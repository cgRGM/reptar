#if !NETWASM
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

/// <summary>
/// C# client for <see cref="RpcMode"/>. Spawns a Reptar agent server as a child process and
/// drives it over JSONL, so the same agent implementation can back an in-process TUI, a remote
/// IDE plugin, or a CI job.
/// </summary>
/// <remarks>
/// The protocol contract this enforces, in the order clients get it wrong:
/// <list type="bullet">
/// <item>Split records on LF only. <see cref="StreamReader.ReadLineAsync()"/> also treats
/// U+2028/U+2029 as line breaks, and those are legal inside JSON strings, so this reads bytes
/// and splits on <c>\n</c> itself.</item>
/// <item>Correlate by <c>id</c>, never by order — commands are handled asynchronously.</item>
/// <item>A successful <c>prompt</c> means accepted. Wait for <see cref="WaitForSettledAsync"/>;
/// retries, compaction, and steering can all follow <c>agent_end</c>.</item>
/// <item>stdout is protocol only; stderr is diagnostics.</item>
/// </list>
/// </remarks>
public sealed class RpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TaskCompletionSource _settledSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextId = 1;
    private bool _disposed;

    private RpcClient(Process process, Stream stdin)
    {
        _process = process;
        _stdin = stdin;
    }

    /// <summary>Fires for every agent event the server emits.</summary>
    public event Action<AgentEvent>? EventReceived;

    /// <summary>Fires for each line the server writes to stderr.</summary>
    public event Action<string>? DiagnosticReceived;

    /// <summary>True once the child process has exited.</summary>
    public bool HasExited => _process.HasExited;

    // ------------------------------------------------------------------
    // Startup
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts the agent server. Defaults to <c>bun reptar agent --mode rpc</c> rooted at the
    /// repository, which is what the examples use.
    /// </summary>
    public static RpcClient Start(
        string? workspace = null,
        string? provider = null,
        string? model = null,
        string? session = null,
        string? repositoryRoot = null,
        IReadOnlyList<string>? command = null)
    {
        var root = repositoryRoot ?? FindRepositoryRoot();

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var argv = command ?? ["bun", "scripts/reptar.ts", "agent", "--mode", "rpc"];
        startInfo.FileName = argv[0];
        for (var i = 1; i < argv.Count; i++)
            startInfo.ArgumentList.Add(argv[i]);

        if (workspace is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(workspace);
        }

        if (provider is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(provider);
        }

        if (model is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
        }

        if (session is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(session);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        var client = new RpcClient(process, process.StandardInput.BaseStream);
        _ = client.PumpStdoutAsync();
        _ = client.PumpStderrAsync();
        return client;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "reptar.ts")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    // ------------------------------------------------------------------
    // Framing
    // ------------------------------------------------------------------

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
                    var value = buffer[i];

                    // LF is the only record boundary. U+2028/U+2029 stay inside the payload.
                    if (value == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r');
                        pending.Clear();
                        if (line.Length > 0)
                            Dispatch(line);
                        continue;
                    }

                    pending.Add(value);
                }
            }

            if (pending.Count > 0)
                Dispatch(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // server exited mid-stream
        }
        finally
        {
            FailPending(new InvalidOperationException("RPC server exited before responding"));
            _events.Writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                    DiagnosticReceived?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // server exited
        }
    }

    private void Dispatch(string line)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            DiagnosticReceived?.Invoke($"[client] unparseable record: {Clip(line)}");
            return;
        }

        if (node is null)
            return;

        var type = node["type"]?.GetValue<string>();

        if (type == "response")
        {
            var id = node["id"]?.GetValue<string>();
            if (id is null)
                return;

            lock (_gate)
            {
                if (_pending.Remove(id, out var waiter))
                    waiter.TrySetResult(node);
            }
            return;
        }

        if (!AgentEventJson.TryParse(line, out var agentEvent) || agentEvent is null)
            return;

        EventReceived?.Invoke(agentEvent);
        _events.Writer.TryWrite(agentEvent);

        if (agentEvent is AgentEvent.AgentSettled)
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                previous = _settled;
                _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult();
        }
    }

    private void FailPending(Exception error)
    {
        TaskCompletionSource<JsonObject>[] waiters;
        lock (_gate)
        {
            waiters = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetException(error);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends a command and waits for its response. Throws when the server reports failure.
    /// </summary>
    public async Task<JsonObject?> SendAsync(
        JsonObject command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextId).ToString();
        command["id"] = id;

        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending[id] = waiter;

        var payload = Encoding.UTF8.GetBytes(command.ToJsonString(WriteOptions) + "\n");
        await _stdin.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

        var response = await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var success = response["success"] is JsonValue flag && flag.TryGetValue<bool>(out var ok) && ok;
        if (!success)
        {
            var error = response["error"]?.GetValue<string>() ?? "unknown error";
            throw new RpcCommandException(command["type"]?.GetValue<string>() ?? "unknown", error);
        }

        return response["data"] as JsonObject;
    }

    public Task<JsonObject?> GetStateAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "get_state" }, ct);

    public Task<JsonObject?> GetMessagesAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "get_messages" }, ct);

    public Task<JsonObject?> GetSessionStatsAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "get_session_stats" }, ct);

    public Task<JsonObject?> GetLastAssistantTextAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "get_last_assistant_text" }, ct);

    /// <summary>
    /// Submits a prompt. Returns once the server has <em>accepted</em> it — use
    /// <see cref="PromptAndWaitAsync"/> when you want the finished turn.
    /// </summary>
    public Task<JsonObject?> PromptAsync(
        string message,
        string? streamingBehavior = null,
        IReadOnlyList<AgentImage>? images = null,
        CancellationToken ct = default)
    {
        var command = new JsonObject
        {
            ["type"] = "prompt",
            ["message"] = message
        };

        if (streamingBehavior is { Length: > 0 })
            command["streamingBehavior"] = streamingBehavior;

        if (images is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (var image in images)
            {
                array.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = image.Base64Data,
                    ["mimeType"] = image.MimeType
                });
            }
            command["images"] = array;
        }

        return SendAsync(command, ct);
    }

    /// <summary>Queues a message delivered after the current turn's tool calls.</summary>
    public Task<JsonObject?> SteerAsync(string message, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "steer", ["message"] = message }, ct);

    /// <summary>Queues a message delivered once the agent would otherwise stop.</summary>
    public Task<JsonObject?> FollowUpAsync(string message, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "follow_up", ["message"] = message }, ct);

    public Task<JsonObject?> AbortAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "abort" }, ct);

    public Task<JsonObject?> ClearQueueAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "clear_queue" }, ct);

    public Task<JsonObject?> CompactAsync(string? instructions = null, CancellationToken ct = default) =>
        SendAsync(new JsonObject
        {
            ["type"] = "compact",
            ["customInstructions"] = instructions
        }, ct);

    public Task<JsonObject?> SetModelAsync(string provider, string modelId, CancellationToken ct = default) =>
        SendAsync(new JsonObject
        {
            ["type"] = "set_model",
            ["provider"] = provider,
            ["modelId"] = modelId
        }, ct);

    public Task<JsonObject?> SetSteeringModeAsync(string mode, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "set_steering_mode", ["mode"] = mode }, ct);

    public Task<JsonObject?> SetAutoCompactionAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "set_auto_compaction", ["enabled"] = enabled }, ct);

    public Task<JsonObject?> SetAutoRetryAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "set_auto_retry", ["enabled"] = enabled }, ct);

    public Task<JsonObject?> NewSessionAsync(string? name = null, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "new_session", ["name"] = name }, ct);

    /// <summary>Runs a shell command in the server's workspace and adds it to the transcript.</summary>
    public Task<JsonObject?> BashAsync(string command, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "bash", ["command"] = command }, ct);

    // ------------------------------------------------------------------
    // Streaming helpers
    // ------------------------------------------------------------------

    /// <summary>Reads agent events as they arrive. Completes when the server exits.</summary>
    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken ct = default) =>
        _events.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Resolves when the agent has no further automatic work. Call this <em>before</em> prompting
    /// to avoid missing a fast completion.
    /// </summary>
    public Task WaitForSettledAsync(CancellationToken ct = default)
    {
        Task pending;
        lock (_gate) pending = _settled.Task;
        return pending.WaitAsync(ct);
    }

    /// <summary>
    /// Prompts and waits for settlement, forwarding assistant text to <paramref name="onText"/>.
    /// Returns the terminal <see cref="AgentEvent.AgentEnd"/>.
    /// </summary>
    public async Task<AgentEvent.AgentEnd> PromptAndWaitAsync(
        string message,
        Action<string>? onText = null,
        CancellationToken ct = default)
    {
        // Subscribe before prompting: a fast completion can otherwise beat the listener.
        var settled = WaitForSettledAsync(ct);
        AgentEvent.AgentEnd? final = null;

        void Listener(AgentEvent evt)
        {
            switch (evt)
            {
                case AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta:
                    onText?.Invoke(delta.Delta);
                    break;
                case AgentEvent.AgentEnd end:
                    final = end;
                    break;
            }
        }

        EventReceived += Listener;
        try
        {
            await PromptAsync(message, ct: ct).ConfigureAwait(false);
            await settled.ConfigureAwait(false);
        }
        finally
        {
            EventReceived -= Listener;
        }

        return final ?? throw new InvalidOperationException("agent settled without an agent_end event");
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(new JsonObject { ["type"] = "shutdown" }, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // server may already be gone
        }

        try
        {
            await _stdin.FlushAsync().ConfigureAwait(false);
            _stdin.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (!_process.WaitForExit(5_000))
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        _process.Dispose();
        _events.Writer.TryComplete();
    }

    private static string Clip(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}

/// <summary>A command the server rejected. Distinct from a transport failure.</summary>
public sealed class RpcCommandException(string command, string message)
    : Exception($"RPC command '{command}' failed: {message}")
{
    public string Command { get; } = command;
}
#endif
