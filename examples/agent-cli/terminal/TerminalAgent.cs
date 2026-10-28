#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>
/// Resolves the provider config for the CLI. A model spec may be an alias (<c>fast</c>), a
/// <c>provider/model</c> pair, or a bare model id.
/// </summary>
/// <summary>
/// Resolves which provider and model to use, from the most explicit source available.
/// </summary>
/// <remarks>
/// Precedence, highest first:
/// <list type="number">
/// <item>CLI flags (<c>--provider</c>, <c>--model</c>)</item>
/// <item><c>REPTAR_AGENT_PROVIDER</c> / <c>REPTAR_AGENT_MODEL</c></item>
/// <item><c>~/.reptar/settings.json</c></item>
/// <item>provider env keys, then credentials saved by <c>/connect</c>, then offline echo</item>
/// </list>
///
/// Settings sit above the saved-auth default on purpose. Without that, choosing a model in the
/// picker and then restarting would silently fall back to whatever <c>/connect</c> last touched —
/// the file you edited was never consulted.
/// </remarks>
public static class AgentConfigResolver
{
    public static LlmConfig Resolve(TerminalAgentOptions options)
    {
        var envProvider = Environment.GetEnvironmentVariable("REPTAR_AGENT_PROVIDER");
        var envModel = Environment.GetEnvironmentVariable("REPTAR_AGENT_MODEL");
        var envWins = !string.IsNullOrWhiteSpace(envProvider) || !string.IsNullOrWhiteSpace(envModel);

        if (options.Provider is null && options.Model is { Length: > 0 } spec)
        {
            var resolved = ModelAliases.Resolve(spec);

            // A bare model id should keep whatever provider the environment selected, so only
            // alias and provider/model specs bypass the environment lookup.
            if (spec.Contains('/') || ModelAliases.Defaults.Any(a =>
                    string.Equals(a.Name, spec, StringComparison.OrdinalIgnoreCase)))
            {
                return resolved with { Attribution = ClientIdentity.Reptar };
            }
        }

        // Settings supply a default only when nothing more explicit was given.
        if (options.Provider is null && options.Model is null && !envWins &&
            LoadSettings(options.WorkspaceRoot) is { Model: { Length: > 0 } settingsModel } settings)
        {
            var resolved = ModelAliases.Resolve(
                settingsModel,
                provider: settings.Provider);

            if (resolved is not null)
                return resolved with { Attribution = ClientIdentity.Reptar };
        }

        return LlmConfig.FromEnvironment(options.Provider, options.Model, options.BaseUrl)
            with { Attribution = ClientIdentity.Reptar };
    }

    /// <summary>
    /// User settings, or null when none exist. Project settings are deliberately not read here:
    /// resolving a default model should not depend on a trust decision.
    /// </summary>
    private static AgentSettings? LoadSettings(string workspaceRoot)
    {
        try
        {
            return AgentSettingsStore.Load(workspaceRoot, trusted: false).Settings;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record TerminalAgentOptions
{
    public string WorkspaceRoot { get; init; } = ".";
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public int MaxSteps { get; init; } = 24;
    public bool ShowToolCalls { get; init; } = true;

    /// <summary>Show reasoning blocks. pi hides them behind a setting.</summary>
    public bool ShowThinking { get; init; } = true;
    public string? ResumeSessionId { get; init; }
    public Func<string, string>? TitleNamer { get; init; } = null;
}

/// <summary>
/// The interactive terminal loop behind `bun reptar agent`. Streams tokens with
/// ANSI markdown highlighting, dispatches tool calls in-process through the
/// mounted Reptar AI tools, and persists the conversation when a session store
/// is configured.
/// </summary>
public sealed class TerminalAgent(
    ReptarApp app,
    SessionTreeStore? tree,
    TerminalAgentOptions options,
    AgentSessionStore? store = null)
{
    private readonly LlmAuthFile _auth = LlmAuthFile.LoadActive();
    private readonly WorkspaceRoot _workspace =
        new(new WorkspaceToolOptions { RootPath = options.WorkspaceRoot });
    private readonly TerminalEditor _editor = new();
    private readonly TerminalScreen _screen = new();
    private readonly List<string> _workspaceFiles = [];
    private DateTime _fileCacheAt = DateTime.MinValue;

    private int _sessionInputTokens;
    private int _sessionOutputTokens;
    private double _sessionCost;
    private long? _contextLimit;
    private double _inputCostPerMillion;
    private double _outputCostPerMillion;

    private ILlmClient _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(options));

    private AgentSessionRow? _session;
    private int _persistedCount;
    private readonly List<ChatMessage> _history = [];
    private readonly AnsiMarkdown _markdown = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _turnCts;
    private bool _running = true;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;

            // First interrupt aborts the turn and returns to the editor (pi behaviour).
            // Only when nothing is running does it mean quit.
            var turn = Volatile.Read(ref _turnCts);
            if (turn is not null && !turn.IsCancellationRequested)
            {
                Ansi.WriteLine(Ansi.Yellow, "\ninterrupt: cancelling current turn (press ctrl+c again to exit)");
                turn.Cancel();
                return;
            }

            _running = false;
            _shutdown.Cancel();
        };

        Ansi.Enable();

        if (!Console.IsInputRedirected && !Console.IsOutputRedirected && Ansi.Enabled &&
            !string.Equals(Environment.GetEnvironmentVariable("REPTAR_CLI_MODE"), "line", StringComparison.OrdinalIgnoreCase))
        {
            using var term = new ReptarTerm(app, options, store, tree);
            await term.RunAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = RefreshModelInfoAsync(_client.Config);
        _screen.Enter();
        PrintBanner();

        if (store is not null)
        {
            await store.EnsureCreatedAsync().ConfigureAwait(false);

            if (options.ResumeSessionId is { Length: > 0 } resumeId)
            {
                var found = await store.FindSessionAsync(resumeId).ConfigureAwait(false);
                if (found is null)
                {
                    Ansi.WriteLine(Ansi.Yellow, $"No session '{resumeId}' found. Starting a new session.");
                    await StartSessionAsync().ConfigureAwait(false);
                }
                else
                {
                    _session = found;
                    _history.AddRange(await store.LoadMessagesAsync(found.Id).ConfigureAwait(false));
                    Ansi.WriteLine(Ansi.Gray,
                        $"Resumed session {found.Id} — {_history.Count} message(s) restored.");
                }
            }
            else
            {
                await StartSessionAsync().ConfigureAwait(false);
            }
        }

        ConfigureEditor();

        try
        {
            await RunPromptLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _screen.Dispose();
            TerminalInput.Deactivate();
        }
    }

    private async Task RunPromptLoopAsync(CancellationToken linkedToken)
    {
        while (_running && !linkedToken.IsCancellationRequested)
        {
            string? input;
            try
            {
                input = _editor.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                break;
            }

            if (input is null)
                break;

            var trimmed = input.Trim();
            if (trimmed.Length == 0)
                continue;

            // "!command" — pi-style shell passthrough: run locally, show output,
            // never send to the model.
            if (trimmed.StartsWith('!'))
            {
                await RunShellPassthroughAsync(trimmed[1..].Trim()).ConfigureAwait(false);
                continue;
            }

            if (trimmed.StartsWith('/'))
            {
                if (!HandleCommand(trimmed))
                {
                    Ansi.WriteLine(Ansi.Gray, "Bye.");
                    return;
                }
                continue;
            }

            var clipboardImage = _editor.ClipboardImage;
            var summary = await AskWithMentionsAsync(trimmed, linkedToken, clipboardImage).ConfigureAwait(false);
            _editor.ClearClipboardImage();
            _screen.PaintFooter(RenderStatusBar());

            if (summary.StopReason == AgentStopReason.Error)
            {
                Ansi.WriteLine(Ansi.Red, $"error: {summary.FinalContent}");
            }
        }
    }

    /// <summary>
    /// Creates the client for a config and refreshes pricing/context metadata
    /// from models.dev in the background (best-effort).
    /// </summary>
    private void SwitchClient(LlmConfig config)
    {
        _client = LlmClientFactory.Create(config);
        _ = RefreshModelInfoAsync(config);
    }

    private async Task RefreshModelInfoAsync(LlmConfig config)
    {
        try
        {
            var info = await LlmCatalog.GetModelInfoAsync(config.ProviderKey, config.Model).ConfigureAwait(false);
            if (info is not null)
            {
                _contextLimit = info.ContextTokens;
                _inputCostPerMillion = info.InputCostPerMillion;
                _outputCostPerMillion = info.OutputCostPerMillion;
            }
        }
        catch
        {
            // models.dev unreachable — bar hides the pricing sections
        }
    }

    private void ConfigureEditor()
    {
        _editor.CommandSuggestions = prefix =>
        {
            var commands = new (string Name, string Description)[]
            {
                ("/help", "list commands"),
                ("/reset", "start a fresh conversation"),
                ("/history", "show the full conversation"),
                ("/tools", "list mounted AI tools"),
                ("/model", "switch model, e.g. /model anthropic/claude-sonnet-4-5"),
                ("/models", "pick from a provider's model catalog"),
                ("/connect", "connect a provider (key saved to ~/.reptar/agent/auth.json)"),
                ("/disconnect", "remove saved credentials for a provider"),
                ("/providers", "show provider connection status"),
                ("/sessions", "list saved sessions"),
                ("/session", "resume a session: /session <id>"),
                ("/new", "new named session: /new <title>"),
                ("/exit", "quit")
            };

            return commands
                .Where(command =>
                    command.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ||
                    command.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(command => new TerminalEditor.Suggestion(
                    command.Name,
                    command.Name,
                    command.Description))
                .ToList();
        };

        _editor.FileSuggestions = prefix =>
        {
            var files = WorkspaceFileList();

            return files
                .Where(file => file.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => ScoreFileMatch(file, prefix))
                .ThenBy(file => file.Length)
                .Take(8)
                .Select(file => new TerminalEditor.Suggestion(file, file))
                .ToList();
        };

        _editor.StatusBar = RenderStatusBar;
        _editor.PaintFooter = _screen.Active ? _screen.PaintFooter : null;
        _editor.HistoryPath = HistoryFilePath();
        _editor.LoadHistory();
    }

    private static int ScoreFileMatch(string file, string prefix)
    {
        var name = file.Split('/')[^1];
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private List<string> WorkspaceFileList()
    {
        if ((DateTime.UtcNow - _fileCacheAt).TotalSeconds < 3)
            return _workspaceFiles;

        try
        {
            var ignored = new HashSet<string>(
                new WorkspaceToolOptions().IgnoredDirectories,
                StringComparer.OrdinalIgnoreCase);

            var result = new List<string>();
            var queue = new Queue<string>([""]);
            var cap = 4_000;

            while (queue.Count > 0 && result.Count < cap)
            {
                var dir = queue.Dequeue();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(
                                 Path.Combine(_workspace.Root, dir)))
                    {
                        if (result.Count >= cap)
                            break;

                        var name = Path.GetFileName(entry);
                        var relative = dir.Length == 0 ? name : dir + "/" + name;

                        if (Directory.Exists(entry))
                        {
                            if (!ignored.Contains(name) && !name.StartsWith('.'))
                                queue.Enqueue(relative);
                            continue;
                        }

                        result.Add(relative);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // skip unreadable directories
                }
            }

            _workspaceFiles.Clear();
            _workspaceFiles.AddRange(result);
            _fileCacheAt = DateTime.UtcNow;
        }
        catch
        {
            // unreadable workspace — no file suggestions, no crash
        }

        return _workspaceFiles;
    }

    /// <summary>
    /// Runs a shell command outside the model and records it in the transcript.
    /// </summary>
    /// <remarks>
    /// <c>!cmd</c> is a bash-execution message: the model did not decide to run it, so it is not
    /// a tool result, but it *is* conversation context — otherwise you cannot ask a follow-up
    /// question about output you just saw. <c>!!cmd</c> keeps it on screen and out of context.
    /// </remarks>
    private async Task RunShellPassthroughAsync(string command)
    {
        if (command.Length == 0)
            return;

        var excludeFromContext = command.StartsWith('!');
        if (excludeFromContext)
            command = command[1..].TrimStart();

        Ansi.WriteLine(Ansi.Color(Ansi.Gray, $"{(excludeFromContext ? "!!" : "!")} {command}"));
        var result = await WorkspaceTools.RunCommandAsync(_workspace, command, 30_000).ConfigureAwait(false);

        if (result.TimedOut)
            Ansi.WriteLine(Ansi.Yellow, "  timed out after 30s");

        if (result.Stdout.Length > 0)
            Ansi.WriteLine(Ansi.Color(Ansi.Dim, Indent(result.Stdout)));
        if (result.Stderr.Length > 0)
            Ansi.WriteLine(Ansi.Color(Ansi.Yellow, Indent(result.Stderr)));
        if (result.Stdout.Length == 0 && result.Stderr.Length == 0)
            Ansi.WriteLine(Ansi.Color(Ansi.Gray, $"  exit {result.ExitCode}"));

        var message = AgentMessages.BashExecution(
            command,
            result.Stdout,
            result.Stderr,
            result.ExitCode,
            cancelled: result.TimedOut,
            excludeFromContext: excludeFromContext);

        _history.Add(message);

        await PersistAsync(message).ConfigureAwait(false);

        if (excludeFromContext)
            Ansi.WriteLine(Ansi.Gray, "  (kept out of model context)");

        static string Indent(string text) =>
            string.Join("\n", text.Split('\n').Select(l => "  " + l));
    }

    /// <summary>
    /// Sends the user's message with any "@path" mentions resolved: text files
    /// are inlined as context blocks, images attach as vision content.
    /// </summary>
    private async Task<AgentRunSummary> AskWithMentionsAsync(
        string text,
        CancellationToken cancellationToken,
        AgentImage? clipboardImage = null)
    {
        var images = new List<AgentImage>();
        if (clipboardImage is not null)
        {
            images.Add(clipboardImage);
            Ansi.WriteLine(Ansi.Green, "  attached clipboard image");
        }
        var attached = new StringBuilder();

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith('@'))
                continue;

            var path = token[1..].TrimEnd('.', ',', '!', '?', ';', ':');
            if (path.Length == 0)
                continue;

            string resolved;
            try
            {
                resolved = _workspace.Resolve(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                Ansi.WriteLine(Ansi.Yellow, $"  skipped @{path}: {ex.Message}");
                continue;
            }

            if (!File.Exists(resolved))
            {
                Ansi.WriteLine(Ansi.Yellow, $"  skipped @{path}: file not found");
                continue;
            }

            var extension = Path.GetExtension(resolved).ToLowerInvariant();
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
                    var mime = extension == ".png" ? "image/png"
                        : extension == ".gif" ? "image/gif"
                        : extension == ".webp" ? "image/webp"
                        : "image/jpeg";
                    images.Add(new AgentImage(mime, Convert.ToBase64String(bytes)));
                    Ansi.WriteLine(Ansi.Green, $"  attached image @{path}");
                }
                catch (IOException ex)
                {
                    Ansi.WriteLine(Ansi.Yellow, $"  skipped @{path}: {ex.Message}");
                }
                continue;
            }

            if (new FileInfo(resolved).Length > 100 * 1024)
            {
                Ansi.WriteLine(Ansi.Yellow, $"  skipped @{path}: larger than 100 KB (use read_file)");
                continue;
            }

            var content = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (content.Length > 8_000)
                content = content[..8_000] + "\n…[truncated]";
            attached.Append("\n\n[file: ").Append(path).Append(']').Append('\n')
                .Append(content);
        }

        var message = new ChatMessage("user",
            text + (attached.Length > 0 ? attached.ToString() : string.Empty),
            Images: images.Count > 0 ? images : null);

        return await AskAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private string? RenderStatusBar()
    {
        var bar = new System.Text.StringBuilder("  ");

        // cwd + git branch (pi-style footer start)
        bar.Append(Ansi.Color(Ansi.Bold, ShortenPath(options.WorkspaceRoot)));

        var branch = GitBranch();
        if (branch is { Length: > 0 })
            bar.Append(Ansi.Color(Ansi.Gray, $" ({branch})"));

        bar.Append("  ·  ").Append(Ansi.Color(Ansi.Magenta, _client.Config.DisplayName));

        var tokens = _sessionInputTokens + _sessionOutputTokens;
        if (tokens > 0)
        {
            bar.Append("  ·  ").Append(Ansi.Color(Ansi.Cyan, FormatTokens(tokens)));
            if (_contextLimit is > 0)
                bar.Append(Ansi.Color(Ansi.Gray, $"/{(double)_contextLimit / 1000:0}k ctx"));
            if (_sessionCost > 0)
                bar.Append("  ·  ").Append(Ansi.Color(Ansi.Green, $"${_sessionCost:0.###}"));
        }

        bar.Append(Ansi.Color(Ansi.Gray, "  ·  ctrl+k palette · ctrl+p model"));

        return bar.ToString();
    }

    /// <summary>
    /// ~/.reptar/agent/history (honoring REPTAR_AGENT_HISTORY), next to auth.json.
    /// </summary>
    private static string HistoryFilePath()
    {
        var configured = Environment.GetEnvironmentVariable("REPTAR_AGENT_HISTORY");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(
            Path.GetDirectoryName(LlmAuthFile.ActivePath) ?? ".",
            "history");
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..];
        return path;
    }

    private string GitBranch()
    {
        try
        {
            var headPath = Path.Combine(_workspace.Root, ".git", "HEAD");
            if (!File.Exists(headPath))
                return "";

            var head = File.ReadAllText(headPath).Trim();
            return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
                ? head["ref: refs/heads/".Length..]
                : head.Length > 8
                    ? head[..8]
                    : head;
        }
        catch
        {
            return "";
        }
    }

    private static string FormatTokens(int count)
    {
        if (count < 1000)
            return count.ToString();
        if (count < 10_000)
            return $"{count / 1000.0:0.#}k";
        return $"{Math.Round(count / 1000.0)}k";
    }

    public async Task<AgentRunSummary> AskAsync(string userText, CancellationToken cancellationToken)
    {
        return await AskAsync(new ChatMessage("user", userText), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRunSummary> AskAsync(ChatMessage userMessage, CancellationToken cancellationToken)
    {
        var userText = userMessage.Content;

        if (_session is null && store is not null)
            await StartSessionAsync().ConfigureAwait(false);

        if (_history.Count == 0 && _session is not null)
        {
            var title = options.TitleNamer?.Invoke(userText)
                ?? (userText.Length > 70 ? userText[..70] + "…" : userText);
            await store!.RenameSessionAsync(_session.Id, title).ConfigureAwait(false);
            _session = _session with { Title = title };
        }

        _history.Add(userMessage);
        await PersistAsync(userMessage).ConfigureAwait(false);
        _persistedCount = _history.Count;

        var pending = new StringBuilder();

        // A fresh per-turn token so Ctrl+C aborts this turn without tearing down the REPL.
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        Volatile.Write(ref _turnCts, turnCts);

        AgentRunSummary summary;
        try
        {
            summary = await StreamingAgentHarness.RunAsync(
                app,
                _client,
                _history,
                systemPrompt: DefaultSystemPrompt(),
                maxSteps: options.MaxSteps,
                onEvent: evt =>
                {
                    switch (evt)
                    {
                        case AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta:
                            pending.Append(delta.Delta);
                            FlushCompleteLines(pending);
                            break;

                        case AgentEvent.UsageReported usage:
                            _sessionInputTokens += usage.InputTokens;
                            _sessionOutputTokens += usage.OutputTokens;
                            _sessionCost +=
                                usage.InputTokens / 1_000_000.0 * _inputCostPerMillion +
                                usage.OutputTokens / 1_000_000.0 * _outputCostPerMillion;
                            break;

                        case AgentEvent.ToolExecutionStart call when options.ShowToolCalls:
                            FlushPending(pending);
                            // Color() takes a code first; build the line from coloured parts.
                            Ansi.WriteLine(
                                Ansi.Color(Ansi.Cyan, "→ ")
                                + Ansi.Color(Ansi.Yellow, call.ToolName)
                                + " "
                                + Ansi.Color(Ansi.Gray, SummarizeArgs(call.ArgumentsJson)));
                            break;

                        case AgentEvent.ToolExecutionEnd call when options.ShowToolCalls:
                            var glyph = call.IsError ? Ansi.Color(Ansi.Red, "✗") : Ansi.Color(Ansi.Green, "✓");
                            var tail = call.DurationMs >= 1000
                                ? $"{call.DurationMs / 1000.0:0.0}s"
                                : $"{call.DurationMs:0}ms";
                            Ansi.WriteLine(
                                glyph
                                + " "
                                + Ansi.Color(Ansi.Yellow, call.ToolName)
                                + " "
                                + Ansi.Color(Ansi.Gray, tail));
                            break;
                    }
                },
                cancellationToken: turnCts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _turnCts, null);
            turnCts.Dispose();
        }

        FlushPending(pending);

        if (pending.Length == 0 && summary.StopReason is AgentStopReason.Completed or AgentStopReason.MaxSteps)
            Console.WriteLine();

        if (summary.StopReason == AgentStopReason.Cancelled)
            Ansi.WriteLine(Ansi.Gray, "cancelled.");

        // Persist everything the run appended: assistant turns and tool results both belong in
        // the tree, and the tree is what /tree and /fork read.
        for (var i = _persistedCount; i < _history.Count; i++)
        {
            var message = _history[i];
            if (message.Role is AgentRoles.Assistant or AgentRoles.Tool)
                await PersistAsync(message).ConfigureAwait(false);
        }

        _persistedCount = _history.Count;

        return summary;
    }

    /// <summary>Appends to the tree when configured, else to the flat table.</summary>
    private async Task PersistAsync(ChatMessage message)
    {
        if (_session is null)
            return;

        if (tree is not null)
            await tree.AppendAsync(_session.Id, message, store?.PersistFilter).ConfigureAwait(false);
        else if (store is not null)
            await PersistAsync(message).ConfigureAwait(false);
    }

    private async Task StartSessionAsync()
    {
        _session = await store!.CreateSessionAsync(
            options.TitleNamer?.Invoke("") ?? $"session {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}");
    }

    private string DefaultSystemPrompt()
    {
        if (options.SystemPrompt is { Length: > 0 } prompt)
            return prompt;

        var root = Path.GetFullPath(options.WorkspaceRoot);
        return $$"""
            You are the Reptar agent, an interactive coding assistant embedded in the Reptar web framework.

            Working directory: {{root}}

            Rules:
            - Use the workspace tools (read_file, write_file, edit_file, list_dir, run_command) to inspect and change files.
            - Read a file before editing it. Prefer edit_file with a unique target string over rewriting whole files.
            - Run commands through run_command when builds or tests can verify your work.
            - Paths are relative to the working directory. Never touch files outside it.
            - Answer concisely in Markdown. Show a unified diff or the commands run when you changed something.
            """;
    }

    private bool HandleCommand(string command)
    {
        var name = command.Split(' ', 2)[0].ToLowerInvariant();
        var argument = command.Contains(' ') ? command[(command.IndexOf(' ') + 1)..].Trim() : "";

        switch (name)
        {
            case "/help" or "/?":
                PrintHelp();
                return true;

            case "/reset" or "/clear":
                _history.Clear();
                if (store is not null)
                {
                    _session = store.CreateSessionAsync(
                        options.TitleNamer?.Invoke("") ?? $"session {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}").GetAwaiter().GetResult();
                }
                Ansi.WriteLine(Ansi.Green, "✓ Conversation cleared" +
                    (_session is null ? "." : $" (new session {_session.Id})."));
                return true;

            case "/history":
                if (_history.Count == 0)
                {
                    Ansi.WriteLine(Ansi.Gray, "(no messages yet)");
                    return true;
                }

                Ansi.Rule();
                foreach (var message in _history)
                {
                    var role = message.Role switch
                    {
                        "user" => Ansi.Color(Ansi.Green, "you      "),
                        "assistant" => Ansi.Color(Ansi.Cyan, "assistant"),
                        "tool" => Ansi.Color(Ansi.Yellow, "tool     "),
                        _ => Ansi.Color(Ansi.Gray, "system   ")
                    };
                    Ansi.WriteLine(role + "  " + FirstLine(message.Content));
                }
                Ansi.Rule();
                return true;

            case "/tools":
                PrintTools();
                return true;

            case "/model":
                SwitchModel(argument);
                return true;

            case "/connect" or "/login":
                ConnectAsync(argument.Length > 0 ? argument : null).GetAwaiter().GetResult();
                return true;

            case "/disconnect" or "/logout":
                Disconnect(argument.Length > 0 ? argument : null);
                return true;

            case "/providers":
                PrintProviders();
                return true;

            case "/models":
                SwitchModelFromCatalogAsync(argument.Length > 0 ? argument : null).GetAwaiter().GetResult();
                return true;

            case "/sessions":
                if (store is null)
                {
                    Ansi.WriteLine(Ansi.Gray, "Session persistence is not configured in this app.");
                    return true;
                }

                var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
                Ansi.Rule();
                foreach (var s in sessions)
                {
                    var marker = s.Id == _session?.Id ? Ansi.Color(Ansi.Green, "● ") : "  ";
                    Ansi.WriteLine(marker + Ansi.Color(Ansi.Gray, s.Id + "  ") + FirstLine(s.Title));
                }
                Ansi.Rule();
                return true;

            case "/session":
                if (store is null || argument.Length == 0)
                {
                    Ansi.WriteLine(Ansi.Gray, "Usage: /session <id>   (see /sessions)");
                    return true;
                }

                var target = store.FindSessionAsync(argument).GetAwaiter().GetResult();
                if (target is null)
                {
                    Ansi.WriteLine(Ansi.Red, $"Session '{argument}' not found.");
                    return true;
                }

                _session = target;
                _history.Clear();
                if (tree is not null)
                    tree.ImportFlatAsync(target.Id, store.Messages).GetAwaiter().GetResult();

                _history.AddRange(tree is not null
                    ? tree.LoadBranchAsync(target.Id).GetAwaiter().GetResult()
                    : store.LoadMessagesAsync(target.Id).GetAwaiter().GetResult());
                Ansi.WriteLine(Ansi.Green, $"✓ Resumed {target.Id} — {_history.Count} message(s).");
                return true;

            case "/tree":
                if (tree is null || _session is null)
                {
                    Ansi.WriteLine(Ansi.Gray, "Session tree is not available.");
                    return true;
                }

                Ansi.WriteLine(Ansi.Gray,
                    tree.DescribeTreeAsync(_session.Id).GetAwaiter().GetResult());
                return true;

            case "/fork":
                if (tree is null || store is null || _session is null)
                {
                    Ansi.WriteLine(Ansi.Gray, "Forking is not available.");
                    return true;
                }

                var forked = store.CreateSessionAsync(
                    (_session.Title ?? "session") + " (fork)").GetAwaiter().GetResult();

                if (!tree.ForkAsync(_session.Id, forked.Id).GetAwaiter().GetResult())
                {
                    store.DeleteSessionAsync(forked.Id).GetAwaiter().GetResult();
                    Ansi.WriteLine(Ansi.Gray, "Nothing to fork yet.");
                    return true;
                }

                _session = forked;
                _history.Clear();
                _history.AddRange(tree.LoadBranchAsync(forked.Id).GetAwaiter().GetResult());
                Ansi.WriteLine(Ansi.Green,
                    $"✓ Forked into {forked.Id} — {_history.Count} message(s) carried over.");
                return true;

            case "/new":
                if (store is null)
                    return true;
                _history.Clear();
                _session = store.CreateSessionAsync(argument.Length > 0 ? argument : "new session").GetAwaiter().GetResult();
                Ansi.WriteLine(Ansi.Green, $"✓ New session {_session.Id}.");
                return true;

            case "/exit" or "/quit" or "/q":
                return false;

            default:
                Ansi.WriteLine(Ansi.Yellow, $"Unknown command '{name}'. Try /help.");
                return true;
        }
    }

    private void SwitchModel(string argument)
    {
        if (argument.Length == 0)
        {
            Ansi.WriteLine(Ansi.Gray, $"Current model: {_client.Config.DisplayName}");
            Ansi.WriteLine(Ansi.Gray, "Usage: /model <provider/model>  ·  /models to pick from the live catalog");
            return;
        }

        try
        {
            string? provider = null;
            string? model = argument;
            var slash = argument.IndexOf('/');
            if (slash > 0)
            {
                provider = argument[..slash];
                model = argument[(slash + 1)..];
            }

            var config = LlmConfig.FromEnvironment(provider, model, options.BaseUrl);
            if (options.ApiKey is { Length: > 0 })
                config = config with { ApiKey = options.ApiKey };

            SwitchClient(config);
            Ansi.WriteLine(Ansi.Green, $"✓ Model switched to {_client.Config.DisplayName}");
        }
        catch (Exception ex)
        {
            Ansi.WriteLine(Ansi.Red, $"Could not switch model: {ex.Message}");
        }
    }

    private void PrintTools()
    {
        var tools = app.GetAiTools(onlyExplicit: true);
        Ansi.Rule();
        Ansi.WriteLine(Ansi.Bold + Ansi.Cyan, $"{tools.Count} tool(s) available");
        foreach (var tool in tools)
        {
            Ansi.WriteLine(
                Ansi.Color(Ansi.Yellow, "  " + tool.Name) +
                Ansi.Color(Ansi.Gray, "  " + FirstLine(tool.Description.Replace('\n', ' '))));
        }
        Ansi.Rule();
    }

    private void PrintBanner()
    {
        var provider = _client.Config.Provider;
        var providerColor = provider switch
        {
            LlmProvider.Echo => Ansi.Gray,
            LlmProvider.Ollama => Ansi.Green,
            _ => Ansi.Cyan
        };

        Ansi.WriteLine();
        Ansi.WriteLine(Ansi.Bold + TerminalUi.Purple("  ▄▄▄▄▄  "));
        Ansi.WriteLine(Ansi.Bold + TerminalUi.Purple("  █ ▄▄ █  ") + Ansi.Bold + "REPTAR AGENT");
        Ansi.WriteLine(Ansi.Bold + TerminalUi.Purple("  █ ▀▀ █  ") +
            Ansi.Reset + Ansi.Color(providerColor, _client.Config.DisplayName));
        Ansi.WriteLine(Ansi.Bold + TerminalUi.Purple("  ▀▀▀▀▀  "));
        Ansi.WriteLine(Ansi.Gray, $"  workspace: {Path.GetFullPath(options.WorkspaceRoot)}");

        if (provider == LlmProvider.Echo)
        {
            Ansi.WriteLine(Ansi.Yellow,
                "  offline echo mode — run /connect to add a provider (or set OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY, OLLAMA_HOST)");
        }

        Ansi.WriteLine(Ansi.Gray, "  type /help for commands, ctrl+c to interrupt");
        Ansi.WriteLine();
    }

    private void PrintHelp()
    {
        Ansi.Rule();
        Ansi.WriteLine(Ansi.Bold, "  commands");
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /reset") + Ansi.Color(Ansi.Gray, "    start a fresh conversation"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /history") + Ansi.Color(Ansi.Gray, "   show the full conversation"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /tools") + Ansi.Color(Ansi.Gray, "     list mounted AI tools"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /model") + Ansi.Color(Ansi.Gray, "     switch model, e.g. /model anthropic/claude-sonnet-4-5"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /models") + Ansi.Color(Ansi.Gray, "    pick from a provider's live model catalog"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /connect") + Ansi.Color(Ansi.Gray, "   connect a provider (API key, saved to ~/.reptar/agent/auth.json)"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /disconnect") + Ansi.Color(Ansi.Gray, "  remove saved credentials for a provider"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /providers") + Ansi.Color(Ansi.Gray, "  show provider connection status"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /sessions") + Ansi.Color(Ansi.Gray, "  list saved sessions"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /session") + Ansi.Color(Ansi.Gray, "   resume a session: /session <id>"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /new") + Ansi.Color(Ansi.Gray, "       new named session: /new <title>"));
        Ansi.WriteLine(Ansi.Color(Ansi.Cyan, "  /exit") + Ansi.Color(Ansi.Gray, "     quit (ctrl+c twice also quits)"));
        Ansi.Rule();
    }


    // ------------------------------------------------------------------
    // Provider connection (pi /login + opencode /connect flow)
    // ------------------------------------------------------------------

    /// <summary>
    /// Interactive provider connection: pick a provider (arrow keys + search),
    /// paste the API key (hidden, validated), optionally override the base
    /// URL, verify the connection under a live spinner, pick a default model
    /// from the live or models.dev catalog, then save to
    /// ~/.reptar/agent/auth.json and switch the session to it.
    /// </summary>
    public async Task ConnectAsync(string? providerKey = null)
    {
        Ansi.Rule();
        Ansi.WriteLine(Ansi.Bold + Ansi.Cyan, "connect a provider");

        var selection = providerKey ?? SelectProviderMenu(authMode: true);
        if (selection is null)
        {
            Ansi.WriteLine(Ansi.Gray, "Cancelled.");
            return;
        }

        string? apiKey = null;
        string? baseUrl = null;
        string? defaultModel = null;

        if (selection == "other")
        {
            var id = TerminalUi.Ask(
                "provider id",
                v => v.Length > 0 &&
                     v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    ? null
                    : "use letters, digits, and dashes");
            if (id is null || id.Length == 0) { Ansi.WriteLine(Ansi.Gray, "Cancelled."); return; }

            id = id.ToLowerInvariant();
            if (LlmProviders.IsKnown(id))
            {
                Ansi.WriteLine(Ansi.Red, $"'{id}' collides with a built-in provider.");
                return;
            }
            selection = id;

            var customUrl = TerminalUi.Ask(
                "base URL (e.g. https://api.example.com/v1)",
                v => v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "must start with http:// or https://");
            if (customUrl is null || customUrl.Length == 0) { Ansi.WriteLine(Ansi.Gray, "Cancelled."); return; }
            baseUrl = customUrl;

            var model = TerminalUi.Ask("default model", v => v.Length > 0 ? null : "required");
            if (model is null || model.Length == 0) { Ansi.WriteLine(Ansi.Gray, "Cancelled."); return; }
            defaultModel = model;

            var key = TerminalUi.Ask("API key (blank if none)", secret: true);
            apiKey = key is { Length: > 0 } ? key : null;
        }
        else
        {
            var info = LlmProviders.Find(selection)!;
            baseUrl = info.DefaultBaseUrl;

            if (info.RequiresApiKey)
            {
                var prompt = $"{info.DisplayName} API key" +
                    (info.KeyUrl is not null ? $"  ({info.KeyUrl})" : "");
                var key = TerminalUi.Ask(prompt, v => v.Length > 0 ? null : "a key is required", secret: true);

                if (key is null || key.Length == 0)
                {
                    Ansi.WriteLine(Ansi.Yellow, "No key entered — connect cancelled.");
                    return;
                }

                apiKey = key;
            }

            if (info.RequiresBaseUrl)
            {
                var url = TerminalUi.Ask(
                    $"base URL ({info.DefaultBaseUrl})",
                    v => v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : "must start with http:// or https://");
                if (url is null || url.Length == 0) { Ansi.WriteLine(Ansi.Gray, "Cancelled."); return; }
                baseUrl = url;
            }
            else
            {
                var url = TerminalUi.Ask(
                    "base URL",
                    v => v.Length == 0 ||
                         v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : "must start with http:// or https://",
                    placeholder: $"blank = {baseUrl}");
                if (url is null) { Ansi.WriteLine(Ansi.Gray, "Cancelled."); return; }
                if (url.Length > 0)
                    baseUrl = url;
            }
        }

        // Wire provider from the registry so the connection test hits the
        // right endpoint; custom ids are only resolvable after being saved.
        var trial = new LlmConfig
        {
            ProviderKey = selection,
            Provider = selection switch
            {
                LlmProviders.Anthropic => LlmProvider.Anthropic,
                LlmProviders.Gemini => LlmProvider.Gemini,
                LlmProviders.Ollama => LlmProvider.Ollama,
                _ => LlmProvider.OpenAi
            },
            Model = defaultModel ?? "model",
            ApiKey = apiKey,
            BaseUrl = baseUrl
        };

        var catalog = await TerminalUi.SpinAsync("testing connection…", async update =>
        {
            var result = await LlmCatalog.ListAsync(trial);
            if (result.Ok)
            {
                var note = result.Models.Count > 0 ? $"{result.Models.Count} models" : "connected";
                await update("✓ " + note);
            }
            return result;
        });

        if (catalog.Ok && catalog.Models.Count > 0)
        {
            Ansi.WriteLine(Ansi.Green, $"✓ connected — {catalog.Models.Count} models");
            defaultModel ??= await PickModelAsync(
                "default model", selection, trial.Model, catalog.Models);
        }
        else if (catalog.Ok)
        {
            Ansi.WriteLine(Ansi.Green, "✓ connected");
        }
        else
        {
            Ansi.WriteLine(Ansi.Red, "✗ " + catalog.Error);
            var answer = TerminalUi.Ask("save anyway? [y/N]");
            if (answer is null || answer.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Ansi.WriteLine(Ansi.Gray, "Not saved.");
                return;
            }
        }

        _auth.Set(selection, new ProviderCredential(apiKey, baseUrl, defaultModel), makeDefault: true);

        var switched = LlmConfig.FromEnvironment(selection);
        SwitchClient(switched);
        Ansi.WriteLine(Ansi.Green,
            $"✓ Saved {selection} → {switched.DisplayName}  ({_auth.Path})");
        Ansi.WriteLine(Ansi.Gray, "  This provider is now the default for plain 'reptar agent' launches.");
    }

    /// <summary>
    /// Removes a saved credential. Environment-provided keys can't be removed
    /// from here; the hint says which env var is supplying them.
    /// </summary>
    public void Disconnect(string? providerKey = null)
    {
        var target = providerKey;

        if (target is null)
        {
            var saved = _auth.Credentials.Keys
                .Where(key => !(LlmProviders.IsKnown(key) && LlmProviders.CredentialSource(key, _auth).Source == "env"))
                .Select(key => new TermSelectorItem(
                    LlmProviders.Find(key)?.DisplayName ?? key,
                    LlmProviders.FormatStatus(key, _auth),
                    Value: key))
                .ToList();

            target = saved.Count == 0
                ? null
                : TerminalUi.Select("select provider to disconnect", saved);
        }

        if (target is null)
        {
            Ansi.WriteLine(Ansi.Gray, "Cancelled.");
            return;
        }

        var (source, detail) = LlmProviders.CredentialSource(target, _auth);

        if (source == "env")
        {
            Ansi.WriteLine(Ansi.Yellow,
                $"{target} is provided by the {detail} environment variable — unset it instead.");
            return;
        }

        if (!_auth.Clear(target))
        {
            Ansi.WriteLine(Ansi.Yellow, $"No saved credentials for {target}.");
            return;
        }

        Ansi.WriteLine(Ansi.Green, $"✓ Removed saved credentials for {target}.");

        if (_client.Config.ProviderKey == target)
        {
            var fallback = LlmConfig.FromEnvironment();
            SwitchClient(fallback);
            Ansi.WriteLine(Ansi.Gray, $"  Now using {fallback.DisplayName}.");
        }
    }

    /// <summary>
    /// Lists models from the provider's live catalog (falling back to the
    /// models.dev curated catalog when the provider endpoint is unavailable)
    /// and switches the session's model. Works pre-connect for curated data.
    /// </summary>
    public async Task SwitchModelFromCatalogAsync(string? providerKey = null)
    {
        var target = providerKey ?? _client.Config.ProviderKey;

        if (target.Length == 0 || target == LlmProviders.Echo)
        {
            Ansi.WriteLine(Ansi.Yellow, "Connect a provider first: /connect");
            return;
        }

        var catalogConfig = target == _client.Config.ProviderKey
            ? _client.Config
            : LlmConfig.FromEnvironment(target);

        (List<LlmCatalog.CuratedModel> curatedList, CatalogResult live) = await TerminalUi.SpinAsync(
            "fetching models…",
            async update =>
            {
                var curatedModels = await LlmCatalog.ListCuratedAsync(target);
                var liveResult = await LlmCatalog.ListAsync(catalogConfig);
                await update(liveResult.Ok
                    ? $"{target} live catalog ({liveResult.Models.Count} models)"
                    : curatedModels.Count > 0
                        ? $"models.dev curated catalog ({curatedModels.Count} models)"
                        : "no catalog reachable");
                return (curatedModels, liveResult);
            });

        var ids = live.Ok && live.Models.Count > 0
            ? live.Models
            : curatedList.Select(m => m.Id).ToList();

        if (ids.Count == 0)
        {
            Ansi.WriteLine(Ansi.Yellow,
                "No model list available. Check the provider connection or set the model explicitly with /model <provider/model>.");
            return;
        }

        var source = live.Ok ? $"{target} live" : "models.dev";
        var chosen = await PickModelAsync(
            $"models ({source})", target, _client.Config.Model, ids, curatedList);

        if (string.IsNullOrEmpty(chosen) || chosen == _client.Config.Model)
            return;

        var config = LlmConfig.FromEnvironment(target, chosen);
        SwitchClient(config);

        var saved = _auth.Get(target);
        if (saved is not null)
        {
            _auth.Set(target, saved with { DefaultModel = chosen }, makeDefault: false);
        }

        Ansi.WriteLine(Ansi.Green, $"✓ Model switched to {config.DisplayName}");
    }

    /// <summary>
    /// Bordered status table: every registry provider plus saved custom ones,
    /// with the credential source (env var, saved auth, or not connected).
    /// </summary>
    public void PrintProviders()
    {
        Ansi.Rule();

        var columns = new List<TerminalUi.Column>
        {
            new("Provider"),
            new("Status"),
            new("Default model"),
            new("Key / URL")
        };

        var rows = new List<IReadOnlyList<string>>();

        void AddRow(string name, string key)
        {
            var (source, detail) = LlmProviders.CredentialSource(key, _auth);

            var status = source switch
            {
                "env" => Ansi.Color(Ansi.Green, "✓ env: " + detail),
                "saved" => Ansi.Color(Ansi.Green, "✓ saved"),
                _ => Ansi.Color(Ansi.Gray, "• not connected")
            };

            var info = LlmProviders.Find(key);
            var model = _auth.Get(key)?.DefaultModel ?? "";
            var hint = source == "env" ? (info?.KeyUrl ?? "") : (info?.KeyUrl ?? "");

            var current = key == _client.Config.ProviderKey
                ? Ansi.Color(ReptarColorKey(), " ← current")
                : "";

            rows.Add([
                name + current,
                status,
                Ansi.Color(Ansi.Gray, model),
                Ansi.Color(Ansi.Gray, hint)
            ]);
        }

        foreach (var info in LlmProviders.Known)
            AddRow(info.DisplayName, info.Key);

        foreach (var key in _auth.Credentials.Keys)
        {
            if (LlmProviders.IsKnown(key))
                continue;
            AddRow(key + " (custom)", key);
        }

        Ansi.WriteLine(Ansi.Bold + Ansi.Cyan, "  provider status");
        Ansi.WriteLine(Ansi.Gray, "  auth file: " + _auth.Path);
        Ansi.WriteLine();
        TerminalUi.Table(columns, rows);
        Ansi.WriteLine(Ansi.Gray, "  connect: /connect  ·  disconnect: /disconnect  ·  pick model: /models");
        Ansi.Rule();
    }

    private string ReptarColorKey() => Ansi.Magenta;

    /// <summary>
    /// Model picker: numbered/arrow-key selection enriched with models.dev
    /// display names ("Claude Opus 4.5") and context windows. Esc/0 keeps the
    /// current/default model.
    /// </summary>
    private async Task<string?> PickModelAsync(
        string title,
        string providerKey,
        string fallback,
        List<string> modelIds,
        List<LlmCatalog.CuratedModel>? curated = null)
    {
        curated ??= await LlmCatalog.ListCuratedAsync(providerKey);

        var byId = curated
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = modelIds
            .Select(id =>
            {
                byId.TryGetValue(id, out var model);
                var label = model?.Name ?? id;
                var detail = model is { ContextTokens: > 0 }
                    ? $"{model.ContextTokens / 1000}k ctx"
                    : null;
                return new TermSelectorItem(label, detail ?? "", Value: id);
            })
            .ToList();

        var chosen = TerminalUi.Select(title, items,
            hint: "↑↓/type to filter · enter select · esc keeps " + fallback);

        return chosen ?? fallback;
    }

    private string? SelectProviderMenu(bool authMode)
    {
        if (!authMode)
        {
            var saved = _auth.Credentials.Keys
                .Where(key => !(LlmProviders.IsKnown(key) &&
                                LlmProviders.CredentialSource(key, _auth).Source == "env"))
                .Select(key => new TermSelectorItem(
                    LlmProviders.Find(key)?.DisplayName ?? key,
                    LlmProviders.FormatStatus(key, _auth),
                    Value: key))
                .ToList();

            if (saved.Count == 0)
            {
                Ansi.WriteLine(Ansi.Yellow, "  No saved providers to disconnect.");
                return null;
            }

            return TerminalUi.Select("select provider to disconnect", saved);
        }

        var items = LlmProviders.Known
            .Select(info => new TermSelectorItem(
                info.DisplayName,
                LlmProviders.FormatStatus(info.Key, _auth) +
                    (info.Note is { Length: > 0 } ? "  ·  " + info.Note : ""),
                Value: info.Key))
            .ToList();

        items.Add(new TermSelectorItem("Other (custom OpenAI-compatible endpoint)", "", Value: "other"));

        return TerminalUi.Select("select a provider to configure", items);
    }

    private void FlushCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            var newline = pending.ToString().IndexOf('\n');
            if (newline < 0)
                return;

            var line = pending.ToString(0, newline);
            pending.Remove(0, newline + 1);
            _markdown.WriteLine(line);
        }
    }

    private void FlushPending(StringBuilder pending)
    {
        if (pending.Length == 0)
            return;
        _markdown.WriteLine(pending.ToString());
        pending.Clear();
    }

    private static string SummarizeArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "{}";

        var single = argumentsJson.Replace('\n', ' ');
        return single.Length > 72 ? single[..72] + "…}" : single;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var newline = text.IndexOf('\n');
        var line = newline < 0 ? text : text[..newline];
        return line.Length > 100 ? line[..100] + "…" : line;
    }
}
#endif
