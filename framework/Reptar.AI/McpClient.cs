#if !NETWASM
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>How to reach an MCP server.</summary>
public sealed record McpServerConfig
{
    /// <summary>Server name. Used as the tool namespace.</summary>
    public required string Name { get; init; }

    /// <summary>Executable to launch for a stdio server, for example <c>npx</c>.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables for the child process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Working directory for the child process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long to wait for <c>initialize</c> and <c>tools/list</c>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one <c>tools/call</c> may take.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Tools to skip, by bare server-side name.</summary>
    public IReadOnlyList<string> ExcludeTools { get; init; } = [];

    /// <summary>Launch a stdio server: <c>command</c> plus <c>arguments</c>.</summary>
    public static McpServerConfig Stdio(string name, string command, params string[] arguments) =>
        new() { Name = name, Command = command, Arguments = arguments };

    /// <summary>Launch an npx-hosted server, the usual shape for the MCP ecosystem.</summary>
    public static McpServerConfig Npx(string name, string package, params string[] arguments) =>
        new()
        {
            Name = name,
            Command = "npx",
            Arguments = ["-y", package, .. arguments]
        };
}

/// <summary>Raised when an MCP server returns a JSON-RPC error or the transport fails.</summary>
public sealed class McpException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>
/// A Model Context Protocol client over stdio. Speaks JSON-RPC 2.0 with newline-delimited
/// framing: <c>initialize</c>, <c>notifications/initialized</c>, <c>tools/list</c>, and
/// <c>tools/call</c>.
/// </summary>
/// <remarks>
/// This is the piece that turns "Reptar has five workspace tools" into "Reptar has whatever the
/// MCP ecosystem offers". A server is a child process; its tools become
/// <see cref="AiToolDefinition"/> values named <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, which is
/// pi's convention and makes the origin obvious in a trace.
///
/// Only stdio is implemented. HTTP and OAuth transports are a different shape and were not worth
/// guessing at without a server to test against.
/// </remarks>
public sealed class McpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly McpServerConfig _config;
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();

    private int _nextId = 1;
    private bool _disposed;

    private McpClient(McpServerConfig config, Process process)
    {
        _config = config;
        _process = process;
        _stdin = process.StandardInput.BaseStream;
    }

    /// <summary>Server name, as configured.</summary>
    public string Name => _config.Name;

    /// <summary>Protocol version the server selected, once initialized.</summary>
    public string? ProtocolVersion { get; private set; }

    /// <summary>Server-reported name and version.</summary>
    public string? ServerName { get; private set; }

    /// <summary>True once <c>initialize</c> has completed.</summary>
    public bool IsInitialized => ProtocolVersion is not null;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts the server process and performs the MCP handshake.
    /// </summary>
    public static async Task<McpClient> ConnectAsync(
        McpServerConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new McpException(
                $"MCP server '{config.Name}' has no command. Only stdio servers are supported.");

        var startInfo = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = config.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in config.Arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var (key, value) in config.Environment)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new McpException(
                $"MCP server '{config.Name}' failed to start ({config.Command}): {ex.Message}");
        }

        var client = new McpClient(config, process);
        _ = client.PumpStdoutAsync();
        _ = client.PumpStderrAsync();

        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.HandshakeTimeout);

        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "reptar",
                ["version"] = "1.0"
            }
        }, timeout.Token).ConfigureAwait(false);

        ProtocolVersion = result["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
        ServerName = result["serverInfo"]?["name"]?.GetValue<string>();

        // Required by the spec before any other request.
        await NotifyAsync("notifications/initialized", null, timeout.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Tools
    // ------------------------------------------------------------------

    /// <summary>Lists the server's tools.</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var tools = new List<McpTool>();

        if (result["tools"] is not JsonArray array)
            return tools;

        foreach (var entry in array)
        {
            if (entry is not JsonObject tool)
                continue;

            var name = tool["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (_config.ExcludeTools.Contains(name, StringComparer.Ordinal))
                continue;

            tools.Add(new McpTool(
                name,
                tool["description"]?.GetValue<string>() ?? "",
                tool["inputSchema"] as JsonObject ?? new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }));
        }

        return tools;
    }

    /// <summary>
    /// Calls a tool. Returns the flattened text of its content blocks; a server-reported
    /// <c>isError</c> comes back as a failed <see cref="AiToolResult"/> so the model can react.
    /// </summary>
    public async Task<AiToolResult> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        JsonNode? arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            return AiToolResult.Failure(400, "invalid_json", $"Arguments JSON is invalid: {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.CallTimeout);

        try
        {
            var result = await RequestAsync("tools/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            }, timeout.Token).ConfigureAwait(false);

            var text = FlattenContent(result["content"]);
            var isError = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;

            return isError
                ? AiToolResult.Failure(500, "mcp_tool_error", text)
                : AiToolResult.Ok(200, text);
        }
        catch (OperationCanceledException)
        {
            return AiToolResult.Failure(504, "mcp_timeout",
                $"MCP tool '{toolName}' on server '{Name}' timed out after {_config.CallTimeout.TotalSeconds:0}s.");
        }
        catch (McpException ex)
        {
            return AiToolResult.Failure(502, "mcp_error", ex.Message);
        }
    }

    /// <summary>
    /// Renders the tool list as Reptar <see cref="AiToolDefinition"/> values, named
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.
    /// </summary>
    public async Task<IReadOnlyList<AiToolDefinition>> ToolDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var tools = await ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new List<AiToolDefinition>(tools.Count);

        foreach (var tool in tools)
        {
            definitions.Add(new AiToolDefinition
            {
                Name = NamespacedName(tool.Name),
                Description = $"[MCP {Name}] {tool.Description}".TrimEnd(),
                Method = "POST",
                Path = $"/mcp/{Name}/{tool.Name}",
                Parameters = ToParameterDictionary(tool.InputSchema)
            });
        }

        return definitions;
    }

    /// <summary>
    /// Maps a server tool name into the shared tool namespace. <c>-</c> becomes <c>_</c> so the
    /// result is a valid identifier in codemode scripts too.
    /// </summary>
    public string NamespacedName(string toolName) =>
        $"mcp__{Sanitize(Name)}__{Sanitize(toolName)}";

    private static string Sanitize(string value) => value.Replace('-', '_');

    /// <summary>
    /// Converts a JSON Schema into the plain dictionary <see cref="AiToolDefinition"/> expects.
    /// </summary>
    /// <remarks>
    /// Converted node by node rather than through <c>JsonSerializer</c>: reflection-based
    /// serialization is disabled under Native AOT, so a schema with nested objects would throw at
    /// runtime rather than produce a tool definition.
    /// </remarks>
    private static Dictionary<string, object?> ToParameterDictionary(JsonObject schema) =>
        ToPlainObject(schema) ?? new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>()
        };

    private static Dictionary<string, object?>? ToPlainObject(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;

        var result = new Dictionary<string, object?>(obj.Count, StringComparer.Ordinal);

        foreach (var (key, value) in obj)
            result[key] = ToPlainValue(value);

        return result;
    }

    private static object? ToPlainValue(JsonNode? node) => node switch
    {
        JsonObject obj => ToPlainObject(obj),
        JsonArray array => array.Select(ToPlainValue).ToList(),
        JsonValue value => value.TryGetValue<string>(out var text) ? text
            : value.TryGetValue<bool>(out var flag) ? flag
            : value.TryGetValue<double>(out var number) ? number
            : null,
        _ => null
    };

    private static string FlattenContent(JsonNode? content)
    {
        if (content is not JsonArray array)
            return "";

        var builder = new StringBuilder();

        foreach (var block in array)
        {
            if (block is not JsonObject item)
                continue;

            var type = item["type"]?.GetValue<string>();

            if (type == "text" && item["text"]?.GetValue<string>() is { } text)
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(text);
            }
            else if (type is "image" or "audio")
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append('[').Append(type).Append(": ")
                    .Append(item["mimeType"]?.GetValue<string>() ?? "unknown").Append(']');
            }
            else if (type == "resource")
            {
                var resource = item["resource"] as JsonObject;
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(resource?["text"]?.GetValue<string>() ?? "[resource]");
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // JSON-RPC
    // ------------------------------------------------------------------

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate) _pending[id] = completion;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };

        if (parameters is not null)
            request["params"] = parameters;

        await WriteAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _pending.Remove(id);
        }
    }

    private Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };

        if (parameters is not null)
            notification["params"] = parameters;

        return WriteAsync(notification, cancellationToken);
    }

    private async Task WriteAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(Json) + "\n");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PumpStdoutAsync()
    {
        var stdout = _process.StandardOutput.BaseStream;
        var buffer = new byte[16 * 1024];
        var pending = new List<byte>(8 * 1024);

        try
        {
            while (true)
            {
                var read = await stdout.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r');
                        pending.Clear();
                        if (line.Length > 0)
                            Dispatch(line);
                        continue;
                    }

                    pending.Add(buffer[i]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // server exited
        }
        finally
        {
            FailAllPending($"MCP server '{Name}' exited");
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                    Console.Error.WriteLine($"[mcp:{Name}] {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // server exited
        }
    }

    private void Dispatch(string line)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null)
            return;

        // A response carries an id and either result or error. Notifications have no id.
        if (message["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
            return;

        TaskCompletionSource<JsonObject>? completion;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out completion))
                return;
        }

        if (message["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var parsed) ? parsed : 0;
            completion.TrySetException(new McpException(
                error["message"]?.GetValue<string>() ?? "MCP error", code));
            return;
        }

        completion.TrySetResult(message["result"] as JsonObject ?? new JsonObject());
    }

    private void FailAllPending(string reason)
    {
        TaskCompletionSource<JsonObject>[] pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in pending)
            completion.TrySetException(new McpException(reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _stdin.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (!_process.WaitForExit(3_000))
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        FailAllPending($"MCP server '{Name}' closed");
        _process.Dispose();
        _writeGate.Dispose();
        await ValueTask.CompletedTask;
    }
}

/// <summary>One tool advertised by an MCP server.</summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// Mounts MCP servers as Reptar AI tools. This is the one-call integration:
/// <code>
/// var mcp = await McpTools.ConnectAsync([McpServerConfig.Npx("files", "@modelcontextprotocol/server-filesystem", "/tmp")]);
/// app.UseMcpTools(mcp);
/// </code>
/// </summary>
public sealed class McpToolSource : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, (McpClient Client, string ToolName)> _routes =
        new(StringComparer.Ordinal);
    private readonly List<AiToolDefinition> _definitions = [];

    /// <summary>Every tool from every connected server.</summary>
    public IReadOnlyList<AiToolDefinition> Tools => _definitions;

    /// <summary>Connected servers, for status display.</summary>
    public IReadOnlyList<McpClient> Clients => _clients;

    /// <summary>Connects each server and collects its tools.</summary>
    public static async Task<McpToolSource> ConnectAsync(
        IEnumerable<McpServerConfig> servers,
        Action<string>? onWarning = null,
        CancellationToken cancellationToken = default)
    {
        var source = new McpToolSource();

        foreach (var config in servers)
        {
            try
            {
                var client = await McpClient.ConnectAsync(config, cancellationToken).ConfigureAwait(false);
                source._clients.Add(client);

                var definitions = await client.ToolDefinitionsAsync(cancellationToken).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < definitions.Count; i++)
                {
                    source._definitions.Add(definitions[i]);
                    source._routes[definitions[i].Name] = (client, tools[i].Name);
                }
            }
            catch (Exception ex)
            {
                // One bad server must not take the agent down with it.
                onWarning?.Invoke($"MCP server '{config.Name}' unavailable: {ex.Message}");
            }
        }

        return source;
    }

    /// <summary>Dispatches a namespaced tool name back to its server.</summary>
    public async Task<AiToolResult> CallAsync(
        string namespacedName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (!_routes.TryGetValue(namespacedName, out var route))
            return AiToolResult.Failure(404, "tool_not_found", $"No MCP tool named '{namespacedName}'.");

        return await route.Client
            .CallToolAsync(route.ToolName, argumentsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Registers each MCP tool as an in-process Reptar route so the standard
    /// <c>ExecuteAiToolAsync</c> dispatcher reaches it, and returns the definitions to merge into
    /// the agent's tool list.
    /// </summary>
    public IReadOnlyList<AiToolDefinition> Mount(ReptarApp app)
    {
        foreach (var definition in _definitions)
        {
            var name = definition.Name;

            app.Post($"/mcp/{name}", async ctx =>
            {
                var node = WorkspaceTools.ReadJsonNode(ctx);
                var argumentsJson = node?.ToJsonString() ?? "{}";

                var result = await CallAsync(name, argumentsJson).ConfigureAwait(false);

                return result.Success
                    ? ctx.JsonRaw(result.Content ?? "{}")
                    : ctx.Error(result.StatusCode, result.ErrorCode ?? "mcp_error", result.ErrorMessage ?? "failed");
            })
            .WithName(name)
            .WithSummary(definition.Description)
            .AsAiTool(name, definition.Description);
        }

        return _definitions;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);

        _clients.Clear();
        _routes.Clear();
        _definitions.Clear();
    }
}
#endif
