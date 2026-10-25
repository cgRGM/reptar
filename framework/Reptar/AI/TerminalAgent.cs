#if !NETWASM
using System.Text;

namespace Reptar.AI;

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
    public string? ResumeSessionId { get; init; }
    public Func<string, string>? TitleNamer { get; init; } = null;
}

/// <summary>
/// The interactive terminal loop behind `bun reptar agent`. Streams tokens with
/// ANSI markdown highlighting, dispatches tool calls in-process through the
/// mounted Reptar AI tools, and persists the conversation when a session store
/// is configured.
/// </summary>
public sealed class TerminalAgent(ReptarApp app, TerminalAgentOptions options, AgentSessionStore? store = null)
{
    private readonly LlmAuthFile _auth = LlmAuthFile.LoadActive();

    private ILlmClient _client = LlmClientFactory.Create(LlmConfig.FromEnvironment(
        options.Provider, options.Model, options.BaseUrl));

    private AgentSessionRow? _session;
    private readonly List<ChatMessage> _history = [];
    private readonly AnsiMarkdown _markdown = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _running = true;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);

        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            if (!_shutdown.IsCancellationRequested)
            {
                Ansi.WriteLine(Ansi.Yellow, "\ninterrupt: cancelling current turn (press ctrl+c again to exit)");
                _shutdown.Cancel();
            }
            else
            {
                _running = false;
                Environment.Exit(0);
            }
        };

        Ansi.Enable();
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

        var interactive = !Console.IsInputRedirected;

        while (_running && !linked.IsCancellationRequested)
        {
            if (interactive)
            {
                Ansi.Write(Ansi.Color(Ansi.Cyan, "\n▸ "));
            }

            string? input;
            try
            {
                input = await Console.In.ReadLineAsync().ConfigureAwait(false);
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

            if (trimmed.StartsWith('/'))
            {
                if (!HandleCommand(trimmed))
                {
                    Ansi.WriteLine(Ansi.Gray, "Bye.");
                    return;
                }
                continue;
            }

            var summary = await AskAsync(trimmed, linked.Token).ConfigureAwait(false);
            if (summary.StopReason == AgentStopReason.Error)
            {
                Ansi.WriteLine(Ansi.Red, $"error: {summary.FinalContent}");
            }
        }
    }

    public async Task<AgentRunSummary> AskAsync(string userText, CancellationToken cancellationToken)
    {
        if (_session is null && store is not null)
            await StartSessionAsync().ConfigureAwait(false);

        if (_history.Count == 0 && _session is not null)
        {
            var title = options.TitleNamer?.Invoke(userText)
                ?? (userText.Length > 70 ? userText[..70] + "…" : userText);
            await store!.RenameSessionAsync(_session.Id, title).ConfigureAwait(false);
            _session = _session with { Title = title };
        }

        _history.Add(new ChatMessage("user", userText));
        if (store is not null && _session is not null)
            await store.AppendMessageAsync(_session.Id, _history[^1]).ConfigureAwait(false);

        var pending = new StringBuilder();
        var steps = 0;

        var summary = await StreamingAgentHarness.RunAsync(
            app,
            _client,
            _history,
            systemPrompt: DefaultSystemPrompt(),
            maxSteps: options.MaxSteps,
            onEvent: evt =>
            {
                switch (evt)
                {
                    case AgentEvent.TurnStarted turn:
                        steps = turn.Step;
                        break;

                    case AgentEvent.TextDelta delta:
                        pending.Append(delta.Text);
                        FlushCompleteLines(pending);
                        break;

                    case AgentEvent.ToolCallStarted call when options.ShowToolCalls:
                        FlushPending(pending);
                        Ansi.WriteLine(Ansi.Color(
                            Ansi.Color(Ansi.Cyan, "→ ") + Ansi.Color(Ansi.Yellow, call.Name) + " ",
                            SummarizeArgs(call.ArgumentsJson)));
                        break;

                    case AgentEvent.ToolCallFinished call when options.ShowToolCalls:
                        var glyph = call.Success ? Ansi.Color(Ansi.Green, "✓") : Ansi.Color(Ansi.Red, "✗");
                        var tail = call.ElapsedMs >= 1000
                            ? $"{call.ElapsedMs / 1000.0:0.0}s"
                            : $"{call.ElapsedMs:0}ms";
                        Ansi.WriteLine(Ansi.Color(
                            glyph + " " + Ansi.Color(Ansi.Yellow, call.Name) + " ",
                            Ansi.Color(Ansi.Gray, tail)));
                        break;
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        FlushPending(pending);

        if (pending.Length == 0 && summary.StopReason is AgentStopReason.Completed or AgentStopReason.MaxSteps)
            Console.WriteLine();

        if (store is not null && _session is not null)
        {
            var last = _history.LastOrDefault(m => m.Role == "assistant");
            if (last is not null)
                await store.AppendMessageAsync(_session.Id, last).ConfigureAwait(false);
        }

        return summary;
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
                _history.AddRange(store.LoadMessagesAsync(target.Id).GetAwaiter().GetResult());
                Ansi.WriteLine(Ansi.Green, $"✓ Resumed {target.Id} — {_history.Count} message(s).");
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

            _client = LlmClientFactory.Create(config);
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
        Ansi.WriteLine(Ansi.Bold + Ansi.Magenta, "  ▄▄▄▄▄  ");
        Ansi.WriteLine(Ansi.Bold + Ansi.Magenta, "  █ ▄▄ █  REPTAR AGENT");
        Ansi.WriteLine(Ansi.Bold + Ansi.Magenta, "  █ ▀▀ █  " +
            Ansi.Reset + Ansi.Color(providerColor, _client.Config.DisplayName));
        Ansi.WriteLine(Ansi.Bold + Ansi.Magenta, "  ▀▀▀▀▀  ");
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
    /// Interactive provider connection: pick a provider, paste the API key
    /// (hidden), optionally override the base URL, verify the connection against
    /// the provider's model list, pick a default model, then save to
    /// ~/.reptar/agent/auth.json and switch the current session to it.
    /// </summary>
    public async Task ConnectAsync(string? providerKey = null)
    {
        Ansi.Rule();
        Ansi.WriteLine(Ansi.Bold + Ansi.Cyan, "connect a provider");

        var selection = providerKey ?? ChooseProviderFromMenu(authMode: true);
        if (selection is null)
        {
            Ansi.WriteLine(Ansi.Gray, "Cancelled.");
            return;
        }

        string? apiKey = null;
        string? baseUrl = null;
        string? defaultModel = null;
        string? keyUrl = null;

        if (selection == "other")
        {
            Ansi.Write(Ansi.Color(Ansi.Cyan, "  provider id") + Ansi.Color(Ansi.Gray, " (e.g. mycompany): "));
            var id = (ReadLineSync() ?? "").Trim().ToLowerInvariant();
            if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                Ansi.WriteLine(Ansi.Red, "Invalid provider id. Use letters, digits, and dashes.");
                return;
            }
            if (LlmProviders.IsKnown(id))
            {
                Ansi.WriteLine(Ansi.Red, $"'{id}' collides with a built-in provider.");
                return;
            }
            selection = id;

            Ansi.Write(Ansi.Color(Ansi.Cyan, "  base url") + Ansi.Color(Ansi.Gray, " (required, e.g. https://api.example.com/v1): "));
            baseUrl = (ReadLineSync() ?? "").Trim();
            if (baseUrl.Length == 0)
            {
                Ansi.WriteLine(Ansi.Red, "Custom providers need a base URL.");
                return;
            }

            Ansi.Write(Ansi.Color(Ansi.Cyan, "  default model") + Ansi.Color(Ansi.Gray, " (required, e.g. my-model): "));
            defaultModel = (ReadLineSync() ?? "").Trim();
            if (defaultModel.Length == 0)
            {
                Ansi.WriteLine(Ansi.Red, "Custom providers need a default model.");
                return;
            }

            var key = ReadSecret("  api key (blank if none):");
            apiKey = key.Length == 0 ? null : key;
        }
        else
        {
            var info = LlmProviders.Find(selection)!;
            keyUrl = info.KeyUrl;
            baseUrl = info.DefaultBaseUrl;

            if (info.RequiresApiKey)
            {
                var key = ReadSecret($"  {info.DisplayName} API key" +
                    (keyUrl is not null ? $"  (from {keyUrl})" : "") + ":");
                if (key.Length == 0)
                {
                    Ansi.WriteLine(Ansi.Yellow, "No key entered — connect cancelled.");
                    return;
                }
                apiKey = key;
            }

            Ansi.Write(Ansi.Color(Ansi.Cyan, $"  base url") +
                Ansi.Color(Ansi.Gray, $" [blank = {baseUrl}]: "));
            var overrideUrl = (ReadLineSync() ?? "").Trim();
            if (overrideUrl.Length > 0)
                baseUrl = overrideUrl;
        }

        // Verify against the provider's model list (opencode-style connection check).
        var trial = LlmConfig.FromEnvironment(selection, model: defaultModel);
        trial = trial with { BaseUrl = baseUrl, ApiKey = apiKey };

        Ansi.Write(Ansi.Color(Ansi.Gray, "  testing connection… "));
        var catalog = await LlmCatalog.ListAsync(trial);

        if (catalog.Ok && catalog.Models.Count > 0)
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Green, $"✓ connected — {catalog.Models.Count} models"));
            defaultModel ??= ChooseDefaultModel(trial, catalog.Models);
        }
        else if (catalog.Ok)
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Green, "✓ connected"));
        }
        else
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Red, "✗ " + catalog.Error));
            Ansi.Write(Ansi.Color(Ansi.Yellow, "  save anyway? [y/N]: "));
            var answer = (ReadLineSync() ?? "").Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Ansi.WriteLine(Ansi.Gray, "Not saved.");
                return;
            }
        }

        _auth.Set(selection, new ProviderCredential(apiKey, baseUrl, defaultModel), makeDefault: true);

        var switched = LlmConfig.FromEnvironment(selection);
        _client = LlmClientFactory.Create(switched);
        Ansi.WriteLine(Ansi.Green,
            $"✓ Saved {selection} → {switched.DisplayName}  ({_auth.Path})");
        Ansi.WriteLine(Ansi.Gray, $"  This provider is now the default for plain 'reptar agent' launches.");
    }

    /// <summary>
    /// Removes a saved credential. Environment-provided keys can't be removed
    /// from here; the hint says which env var is supplying them.
    /// </summary>
    public void Disconnect(string? providerKey = null)
    {
        var target = providerKey ?? ChooseProviderFromMenu(authMode: false);

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
            _client = LlmClientFactory.Create(fallback);
            Ansi.WriteLine(Ansi.Gray, $"  Now using {fallback.DisplayName}.");
        }
    }

    /// <summary>
    /// Lists models from a provider's live catalog and lets the user pick one.
    /// With no argument, uses the current provider (opencode /models).
    /// </summary>
    public async Task SwitchModelFromCatalogAsync(string? providerKey = null)
    {
        var target = providerKey ?? _client.Config.ProviderKey;

        if (target.Length == 0 || target == LlmProviders.Echo)
        {
            Ansi.WriteLine(Ansi.Yellow, "Connect a provider first: /connect");
            return;
        }

        Ansi.Write(Ansi.Color(Ansi.Gray, $"  fetching models for {target}… "));
        var catalog = await LlmCatalog.ListAsync(_client.Config);

        if (!catalog.Ok)
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Red, "✗ " + catalog.Error));
            return;
        }

        if (catalog.Models.Count == 0)
        {
            Ansi.WriteLine(Ansi.Yellow, "Provider returned an empty model list.");
            return;
        }

        Ansi.WriteLine(Ansi.Green, $"{catalog.Models.Count} models");
        var chosen = ChooseDefaultModel(_client.Config, catalog.Models, "  pick a model [number, or blank = current]");

        if (string.IsNullOrEmpty(chosen))
            return;

        var config = LlmConfig.FromEnvironment(target, chosen);
        _client = LlmClientFactory.Create(config);

        var saved = _auth.Get(target);
        if (saved is not null)
        {
            _auth.Set(target, saved with { DefaultModel = chosen }, makeDefault: false);
        }

        Ansi.WriteLine(Ansi.Green, $"✓ Model switched to {config.DisplayName}");
    }

    /// <summary>
    /// Offline status table: every registry provider plus saved custom ones,
    /// with the credential source (env var, saved auth, or not connected).
    /// </summary>
    public void PrintProviders()
    {
        Ansi.Rule();
        Ansi.WriteLine(Ansi.Bold + Ansi.Cyan, "provider status");
        Ansi.WriteLine(Ansi.Gray, $"  auth file: {_auth.Path}");

        var rows = new List<(string Name, string Key, string Status, string Model)>();

        foreach (var info in LlmProviders.Known)
        {
            rows.Add((info.DisplayName, info.Key,
                LlmProviders.FormatStatus(info.Key, _auth),
                _auth.Get(info.Key)?.DefaultModel ?? ""));
        }

        foreach (var (key, credential) in _auth.Credentials)
        {
            if (LlmProviders.IsKnown(key))
                continue;
            rows.Add((key + " (custom)", key, "✓ saved", credential.DefaultModel ?? ""));
        }

        foreach (var (name, key, status, model) in rows)
        {
            var current = key == _client.Config.ProviderKey
                ? Ansi.Color(Ansi.Magenta, " ← current")
                : "";
            var modelText = model.Length > 0 ? Ansi.Color(Ansi.Gray, "  default: " + model) : "";
            Ansi.WriteLine(
                $"  {status,-24} {name}{current}{modelText}");
        }

        Ansi.WriteLine(Ansi.Gray, "  connect: /connect  ·  disconnect: /disconnect  ·  pick model: /models");
        Ansi.Rule();
    }

    private string ChooseDefaultModel(
        LlmConfig config,
        List<string> models,
        string? prompt = null)
    {
        const int shown = 10;
        Ansi.WriteLine(Ansi.Gray, prompt ?? "  default model:");

        for (var i = 0; i < Math.Min(shown, models.Count); i++)
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Gray, $"    {i + 1,2}. ") + models[i]);
        }

        if (models.Count > shown)
            Ansi.WriteLine(Ansi.Color(Ansi.Gray, $"    … {models.Count - shown} more (type any model id)"));

        Ansi.Write(Ansi.Color(Ansi.Cyan, "  choice") +
            Ansi.Color(Ansi.Gray, $" [number or id, blank = {config.Model}]: "));

        var answer = (ReadLineSync() ?? "").Trim();

        if (answer.Length == 0)
            return config.Model;

        if (int.TryParse(answer, out var index) && index >= 1 && index <= models.Count)
            return models[index - 1];

        return models.FirstOrDefault(m => string.Equals(m, answer, StringComparison.OrdinalIgnoreCase))
            ?? config.Model;
    }

    private string? ChooseProviderFromMenu(bool authMode)
    {
        var savedOnly = !authMode;

        Ansi.WriteLine(Ansi.Gray, savedOnly
            ? "  saved providers:"
            : "  providers:");

        var entries = new List<string>();

        if (savedOnly)
        {
            foreach (var key in _auth.Credentials.Keys)
            {
                if (LlmProviders.IsKnown(key) && LlmProviders.CredentialSource(key, _auth).Source == "env")
                    continue;
                entries.Add(key);
            }

            if (entries.Count == 0)
            {
                Ansi.WriteLine(Ansi.Yellow, "  No saved providers to disconnect.");
                return null;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                Ansi.WriteLine(
                    Ansi.Color(Ansi.Cyan, $"    {i + 1,2}. ") +
                    entries[i] +
                    Ansi.Color(Ansi.Gray, "  " + LlmProviders.FormatStatus(entries[i], _auth)));
            }
        }
        else
        {
            foreach (var info in LlmProviders.Known)
            {
                var note = info.Note is null ? "" : Ansi.Color(Ansi.Dim, "  (" + info.Note + ")");
                var status = Ansi.Color(
                    LlmProviders.CredentialSource(info.Key, _auth).Source == "env" ? Ansi.Gray : Ansi.Gray,
                    LlmProviders.FormatStatus(info.Key, _auth));
                Ansi.WriteLine(
                    Ansi.Color(Ansi.Cyan, $"    {entries.Count + 1,2}. ") +
                    info.DisplayName.PadRight(20) + status + note);
                entries.Add(info.Key);
            }

            Ansi.WriteLine(Ansi.Color(Ansi.Cyan, $"    {entries.Count + 1,2}. ") +
                "Other (custom OpenAI-compatible endpoint)");
            entries.Add("other");
        }

        Ansi.Write(Ansi.Color(Ansi.Cyan, "  choice") +
            Ansi.Color(Ansi.Gray, " [number or name, 0 = cancel]: "));

        var answer = (ReadLineSync() ?? "").Trim();

        if (answer.Length == 0 || answer == "0")
            return null;

        if (int.TryParse(answer, out var index) && index >= 1 && index <= entries.Count)
            return entries[index - 1];

        var byName = entries.FirstOrDefault(e =>
            string.Equals(e, answer, StringComparison.OrdinalIgnoreCase));

        return byName is null ? null : byName;
    }

    private static string? ReadLineSync()
    {
        try
        {
            return Console.In.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a secret with echo disabled (asterisks). Falls back to a plain line
    /// when input is piped so the flow is scriptable.
    /// </summary>
    private static string ReadSecret(string prompt)
    {
        Ansi.WriteLine(Ansi.Gray, prompt);

        if (Console.IsInputRedirected)
            return (Console.In.ReadLine() ?? "").Trim();

        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return "";
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (key.KeyChar is '\0' || char.IsControl(key.KeyChar))
                continue;

            secret.Append(key.KeyChar);
            Console.Write('*');
        }
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
