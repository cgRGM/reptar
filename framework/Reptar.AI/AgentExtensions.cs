#if !NETWASM
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>A tool contributed by an extension.</summary>
public sealed record ExtensionTool(
    string Name,
    string Description,
    string ParametersJson,
    ScriptValue Handler,
    string Extension);

/// <summary>A slash command contributed by an extension.</summary>
public sealed record ExtensionCommand(
    string Name,
    string Description,
    ScriptValue Handler,
    string Extension);

/// <summary>A lifecycle handler contributed by an extension.</summary>
public sealed record ExtensionHook(string Event, ScriptValue Handler, string Extension);

/// <summary>
/// One loaded extension: the registrations it made and the interpreter that owns them.
/// </summary>
public sealed class AgentExtension
{
    internal AgentExtension(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>File name without its extension.</summary>
    public string Name { get; }

    /// <summary>Absolute path of the extension file.</summary>
    public string Path { get; }

    /// <summary>Tools registered by this extension, keyed by tool name.</summary>
    public Dictionary<string, ExtensionTool> Tools { get; } = new(StringComparer.Ordinal);

    /// <summary>Commands registered by this extension, keyed by command name.</summary>
    public Dictionary<string, ExtensionCommand> Commands { get; } = new(StringComparer.Ordinal);

    /// <summary>Lifecycle handlers, in registration order.</summary>
    public List<ExtensionHook> Hooks { get; } = [];

    /// <summary>Set when the file failed to load; the extension contributes nothing.</summary>
    public string? Error { get; internal set; }

    /// <summary>The interpreter that owns this extension's closures.</summary>
    internal ScriptInterpreter? Interpreter { get; set; }
}

/// <summary>
/// Loads and runs extensions written in the codemode script dialect.
/// </summary>
/// <remarks>
/// <para>
/// Reptar targets Native AOT, which has no runtime code loading: <c>Assembly.LoadFrom</c> does not
/// exist and there is no JavaScript engine. An extension therefore cannot be an arbitrary module in
/// the host's language. It is a script interpreted in-process, which keeps the same property that
/// matters — registration at load time, handlers called during a run — while remaining AOT-safe.
/// </para>
/// <para>
/// Extensions load from <c>~/.reptar/extensions</c> and <c>&lt;project&gt;/.reptar/extensions</c>,
/// in that order, so a project can override a personal extension by name. Files run once at load
/// time and may only register; the handlers they register run later, on the interpreter that
/// defined them.
/// </para>
/// </remarks>
public sealed class AgentExtensionHost
{
    /// <summary>Upper bound on loaded extensions, so a stray directory cannot stall startup.</summary>
    public const int MaxExtensions = 64;

    /// <summary>Largest extension file accepted, in bytes.</summary>
    public const int MaxFileBytes = 256 * 1024;

    private static readonly string[] HookNames = ["tool_call", "tool_result", "turn_end"];

    private readonly Dictionary<string, ExtensionTool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionCommand> _commands = new(StringComparer.Ordinal);
    private readonly List<ExtensionHook> _hooks = [];
    private readonly List<AiToolDefinition> _definitions = [];
    private readonly List<string> _errors = [];

    private AgentExtensionHost() { }

    /// <summary>Extensions that loaded, in load order.</summary>
    public IReadOnlyList<AgentExtension> Extensions { get; private set; } = [];

    /// <summary>Tools contributed by every extension, as declarations for the model.</summary>
    public IReadOnlyList<AiToolDefinition> Tools => _definitions;

    /// <summary>Slash commands contributed by every extension.</summary>
    public IReadOnlyCollection<ExtensionCommand> Commands => _commands.Values;

    /// <summary>Load failures. An extension that fails does not stop the others.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>True when nothing was loaded, so callers can skip the wiring entirely.</summary>
    public bool IsEmpty => _hooks.Count == 0 && _definitions.Count == 0;

    /// <summary>
    /// Directories searched for extensions, least specific first.
    /// </summary>
    public static IReadOnlyList<string> SearchPaths(string? projectRoot)
    {
        var paths = new List<string>();

        var home = Environment.GetEnvironmentVariable("REPTAR_HOME");
        var userRoot = home is { Length: > 0 }
            ? home
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reptar");

        paths.Add(System.IO.Path.Combine(userRoot, "extensions"));

        if (projectRoot is { Length: > 0 })
            paths.Add(System.IO.Path.Combine(projectRoot, ".reptar", "extensions"));

        return paths;
    }

    /// <summary>
    /// Discovers and loads every extension. Never throws: a broken file becomes an entry in
    /// <see cref="Errors"/> so one bad extension cannot stop the others from loading.
    /// </summary>
    public static async Task<AgentExtensionHost> LoadAsync(
        string? projectRoot = null,
        IReadOnlyList<AiToolDefinition>? visibleTools = null,
        CancellationToken cancellationToken = default)
    {
        var host = new AgentExtensionHost();
        var loaded = new List<AgentExtension>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in SearchPaths(projectRoot))
        {
            foreach (var file in EnumerateFiles(directory))
            {
                if (loaded.Count >= MaxExtensions)
                {
                    host._errors.Add($"Stopped after {MaxExtensions} extensions; {file} was not loaded.");
                    break;
                }

                var name = System.IO.Path.GetFileNameWithoutExtension(file);

                // A later directory wins by name, so a project extension can replace a personal one.
                if (!seen.Add(name))
                {
                    loaded.RemoveAll(extension =>
                        string.Equals(extension.Name, name, StringComparison.OrdinalIgnoreCase));
                }

                var extension = await host
                    .LoadFileAsync(file, name, visibleTools, cancellationToken)
                    .ConfigureAwait(false);

                loaded.Add(extension);
            }
        }

        host.Extensions = loaded;

        foreach (var extension in loaded)
        {
            if (extension.Error is { Length: > 0 })
            {
                host._errors.Add($"{extension.Name}: {extension.Error}");
                continue;
            }

            host.Merge(extension);
        }

        return host;
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(file =>
                {
                    var extension = System.IO.Path.GetExtension(file);
                    return extension is ".reptar" or ".csx";
                })
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<AgentExtension> LoadFileAsync(
        string file,
        string name,
        IReadOnlyList<AiToolDefinition>? visibleTools,
        CancellationToken cancellationToken)
    {
        var extension = new AgentExtension(name, file);

        string source;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                extension.Error = $"larger than {MaxFileBytes} bytes";
                return extension;
            }

            source = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            extension.Error = ex.Message;
            return extension;
        }

        // The interpreter is created with the agent's tools so a handler can call them by name,
        // exactly as a codemode script does.
        var interpreter = new ScriptInterpreter(
            (toolName, arguments, token) =>
                AgentToolContext.IsAvailable
                    ? AgentToolContext.ExecuteAsync(toolName, arguments, token)
                    : Task.FromResult(AiToolResult.Failure(
                        409, "no_agent_context", $"'{toolName}' was called outside an agent run.")),
            visibleTools ?? []);

        extension.Interpreter = interpreter;

        InstallRegistration(interpreter, extension);

        var result = await interpreter.RunAsync(source, cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
            extension.Error = result.Error ?? "the extension failed to load";

        return extension;
    }

    /// <summary>
    /// Installs the three registration functions an extension may call while loading.
    /// </summary>
    /// <remarks>
    /// Registration is the whole load-time contract. Anything with a side effect — a process, a
    /// socket, a timer — would run on every invocation that loads extensions, including ones that
    /// never start a session, so the surface is deliberately just these three.
    /// </remarks>
    private static void InstallRegistration(ScriptInterpreter interpreter, AgentExtension extension)
    {
        interpreter.AddGlobal("register_tool", new ScriptNativeFunction("register_tool", 4, (args, _) =>
        {
            var toolName = args[0].ToDisplay();
            if (toolName.Length == 0 || args[3] is not ScriptClosure)
            {
                extension.Error = "register_tool(name, description, parameters, handler) needs a handler function";
                return Task.FromResult(ScriptValue.Null);
            }

            extension.Tools[toolName] = new ExtensionTool(
                toolName,
                args[1].ToDisplay(),
                args[2].ToDisplay() is { Length: > 0 } parameters ? parameters : """{"type":"object"}""",
                args[3],
                extension.Name);

            return Task.FromResult(ScriptValue.Null);
        }));

        interpreter.AddGlobal("register_command", new ScriptNativeFunction("register_command", 3, (args, _) =>
        {
            var commandName = args[0].ToDisplay().TrimStart('/');
            if (commandName.Length == 0 || args[2] is not ScriptClosure)
            {
                extension.Error = "register_command(name, description, handler) needs a handler function";
                return Task.FromResult(ScriptValue.Null);
            }

            extension.Commands[commandName] = new ExtensionCommand(
                commandName, args[1].ToDisplay(), args[2], extension.Name);

            return Task.FromResult(ScriptValue.Null);
        }));

        interpreter.AddGlobal("on", new ScriptNativeFunction("on", 2, (args, _) =>
        {
            var hookName = args[0].ToDisplay();
            if (Array.IndexOf(HookNames, hookName) < 0)
            {
                extension.Error =
                    $"unknown event '{hookName}'; expected one of {string.Join(", ", HookNames)}";
                return Task.FromResult(ScriptValue.Null);
            }

            if (args[1] is not ScriptClosure)
            {
                extension.Error = $"on('{hookName}', handler) needs a handler function";
                return Task.FromResult(ScriptValue.Null);
            }

            extension.Hooks.Add(new ExtensionHook(hookName, args[1], extension.Name));
            return Task.FromResult(ScriptValue.Null);
        }));
    }

    private void Merge(AgentExtension extension)
    {
        foreach (var (name, tool) in extension.Tools)
        {
            if (!_tools.TryAdd(name, tool))
            {
                _errors.Add(
                    $"{extension.Name}: tool '{name}' is already registered by {_tools[name].Extension}");
                continue;
            }

            _definitions.Add(new AiToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = ParseParameters(tool.ParametersJson),
                // Extensions are dispatched by AgentExtensionHost, not routed over HTTP, so these
                // are labels rather than an address.
                Method = "EXTENSION",
                Path = tool.Extension
            });
        }

        foreach (var (name, command) in extension.Commands)
            _commands[name] = command;

        _hooks.AddRange(extension.Hooks);
    }

    private static Dictionary<string, object?> ParseParameters(string json)
    {
        var fallback = new Dictionary<string, object?> { ["type"] = "object" };

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return fallback;
        }

        // Converted by hand: JsonSerializer with object? needs reflection, which Native AOT strips.
        return ToParameterMap(node) as Dictionary<string, object?> ?? fallback;
    }

    private static object? ToParameterMap(JsonNode? node) => node switch
    {
        JsonObject obj => obj.ToDictionary(
            entry => entry.Key,
            entry => ToParameterMap(entry.Value),
            StringComparer.Ordinal),
        JsonArray array => array.Select(ToParameterMap).ToList(),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        _ => null
    };

    /// <summary>
    /// Runs an extension tool. Returns null when no extension claims the name, so the caller can
    /// fall through to its own executor.
    /// </summary>
    public async Task<AiToolResult?> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            return null;

        var interpreter = Extensions
            .FirstOrDefault(extension => string.Equals(extension.Name, tool.Extension, StringComparison.Ordinal))
            ?.Interpreter;

        if (interpreter is null)
        {
            return AiToolResult.Failure(
                500, "extension_unloaded", $"'{tool.Extension}' registered '{toolName}' but is not loaded.");
        }

        var arguments = ParseArguments(argumentsJson);
        var context = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["tool"] = ScriptValue.Of(toolName),
            ["arguments"] = arguments
        });

        try
        {
            var result = await interpreter
                .InvokeHandlerAsync(tool.Handler, [arguments, context], cancellationToken)
                .ConfigureAwait(false);

            return ToToolResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A handler that throws becomes a failed tool result, never a crashed run.
            return AiToolResult.Failure(500, "extension_error", $"{tool.Extension}: {ex.Message}");
        }
    }

    /// <summary>Runs an extension command by name. Returns null when no extension claims it.</summary>
    public async Task<ScriptValue?> RunCommandAsync(
        string commandName,
        IReadOnlyList<ScriptValue> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_commands.TryGetValue(commandName.TrimStart('/'), out var command))
            return null;

        var interpreter = Extensions
            .FirstOrDefault(extension => string.Equals(extension.Name, command.Extension, StringComparison.Ordinal))
            ?.Interpreter;

        if (interpreter is null)
            return null;

        // Bound as one array so a handler reads `args[0]`, matching how a tool handler reads
        // `args.field`. Binding the values positionally would make `args` the first argument.
        return await interpreter
            .InvokeHandlerAsync(command.Handler, [ScriptValue.Of(arguments)], cancellationToken)
            .ConfigureAwait(false);
    }

    private static ScriptValue ParseArguments(string json)
    {
        try
        {
            return ScriptValue.FromJson(json is { Length: > 0 } ? json : "{}");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Converts a handler return value into a tool result.
    /// </summary>
    /// <remarks>
    /// A string is the model-facing text. An object may carry <c>content</c> and <c>error</c>, so a
    /// handler can report a failure without throwing — which matters because throwing loses the
    /// partial output the model would need to recover.
    /// </remarks>
    private static AiToolResult ToToolResult(ScriptValue value)
    {
        if (value is ScriptObject obj)
        {
            // ScriptNull.ToDisplay() is the string "null", so a missing field reads as present.
            // Every lookup here has to test the type, not the rendered text.
            var error = obj.Member("error");
            if (error is not ScriptNull && error.ToDisplay() is { Length: > 0 } message)
                return AiToolResult.Failure(400, "extension_error", message);

            var content = obj.Member("content");
            return AiToolResult.Ok(200, content is ScriptNull ? "" : content.ToDisplay());
        }

        return value is ScriptNull ? AiToolResult.Ok(200, "") : AiToolResult.Ok(200, value.ToDisplay());
    }

    /// <summary>
    /// Wraps a loop config so extension hooks run inside it.
    /// </summary>
    /// <remarks>
    /// The existing hooks are composed rather than replaced: a host that already set
    /// <c>BeforeToolCall</c> keeps it, and both run. Extension hooks run first for gating so an
    /// extension's refusal cannot be overridden by a later allow, and last for results so an
    /// extension sees what the host produced.
    /// </remarks>
    public AgentLoopConfig Apply(AgentLoopConfig config)
    {
        if (IsEmpty)
            return config;

        var innerBefore = config.BeforeToolCall;
        var innerAfter = config.AfterToolCall;
        var innerFinish = config.FinishTurn;

        return config with
        {
            BeforeToolCall = async (context, cancellationToken) =>
            {
                var decision = await RunToolCallHooksAsync(context, cancellationToken).ConfigureAwait(false);

                // Fail closed: an extension that blocked the call wins over any later allow.
                if (decision is { Block: true })
                    return decision;

                return innerBefore is null
                    ? AgentToolDecision.Allow
                    : await innerBefore(context, cancellationToken).ConfigureAwait(false);
            },

            AfterToolCall = async (context, result, isError, cancellationToken) =>
            {
                var fromHost = innerAfter is null
                    ? null
                    : await innerAfter(context, result, isError, cancellationToken).ConfigureAwait(false);

                var effective = fromHost ?? result;
                return await RunToolResultHooksAsync(context, effective, isError, cancellationToken)
                    .ConfigureAwait(false);
            },

            FinishTurn = async (turn, cancellationToken) =>
            {
                var fromHost = innerFinish is null
                    ? AgentTurnDecision.Default
                    : await innerFinish(turn, cancellationToken).ConfigureAwait(false);

                await RunTurnEndHooksAsync(turn, cancellationToken).ConfigureAwait(false);
                return fromHost;
            }
        };
    }

    private async Task<AgentToolDecision?> RunToolCallHooksAsync(
        AgentToolCallContext context,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            if (hook.Event != "tool_call")
                continue;

            var interpreter = FindInterpreter(hook);
            if (interpreter is null)
                continue;

            var payload = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["tool"] = ScriptValue.Of(context.Call.Name),
                ["arguments"] = ScriptValue.Of(context.ArgumentsJson)
            });

            ScriptValue outcome;
            try
            {
                outcome = await interpreter
                    .InvokeHandlerAsync(hook.Handler, [payload], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A gate that cannot decide must not silently allow the call.
                return new AgentToolDecision(
                    Block: true,
                    Reason: $"{hook.Extension} could not decide whether to allow '{context.Call.Name}': {ex.Message}");
            }

            if (outcome is ScriptObject result && result.Member("block").Truthy)
            {
                var why = result.Member("reason");
                var reason = why is not ScriptNull && why.ToDisplay() is { Length: > 0 } text
                    ? text
                    : $"{hook.Extension} blocked '{context.Call.Name}'";

                return new AgentToolDecision(Block: true, Reason: reason);
            }
        }

        return null;
    }

    private async Task<AiToolResult?> RunToolResultHooksAsync(
        AgentToolCallContext context,
        AiToolResult result,
        bool isError,
        CancellationToken cancellationToken)
    {
        AiToolResult? replaced = null;

        foreach (var hook in _hooks)
        {
            if (hook.Event != "tool_result")
                continue;

            var interpreter = FindInterpreter(hook);
            if (interpreter is null)
                continue;

            var current = replaced ?? result;
            var payload = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["tool"] = ScriptValue.Of(context.Call.Name),
                ["content"] = ScriptValue.Of(current.Content ?? ""),
                ["error"] = ScriptValue.Of(current.ErrorMessage ?? ""),
                ["isError"] = ScriptValue.Of(isError)
            });

            try
            {
                var outcome = await interpreter
                    .InvokeHandlerAsync(hook.Handler, [payload], cancellationToken)
                    .ConfigureAwait(false);

                // Only an object with `content` replaces the result; anything else observes.
                if (outcome is ScriptObject changed && changed.Member("content") is not ScriptNull)
                    replaced = AiToolResult.Ok(current.StatusCode, changed.Member("content").ToDisplay());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A redaction that fails must not destroy the tool's output; keep the original.
            }
        }

        return replaced;
    }

    private async Task RunTurnEndHooksAsync(AgentTurnResult turn, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            if (hook.Event != "turn_end")
                continue;

            var interpreter = FindInterpreter(hook);
            if (interpreter is null)
                continue;

            var payload = new ScriptObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["stopReason"] = ScriptValue.Of(turn.Message.ToolCalls is { Count: > 0 } ? "tool_use" : "stop"),
                ["toolCalls"] = ScriptValue.Of((double)(turn.Message.ToolCalls?.Count ?? 0))
            });

            try
            {
                await interpreter
                    .InvokeHandlerAsync(hook.Handler, [payload], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Notification only; a failing observer must not end the run.
            }
        }
    }

    private ScriptInterpreter? FindInterpreter(ExtensionHook hook) =>
        Extensions
            .FirstOrDefault(extension =>
                string.Equals(extension.Name, hook.Extension, StringComparison.Ordinal))
            ?.Interpreter;
}
#endif
