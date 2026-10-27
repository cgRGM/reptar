#if !NETWASM
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Reptar.AI;

/// <summary>
/// A modal view drawn over the chat transcript. There are no tabs: pi's shape is one scrolling
/// conversation plus pickers that appear when you ask for them, and that is what this mirrors.
/// </summary>
public enum TermOverlay
{
    None = 0,

    /// <summary>Saved session picker (<c>/sessions</c>).</summary>
    Sessions = 1,

    /// <summary>Model catalog and provider connection (<c>/model</c>, <c>/connect</c>).</summary>
    Providers = 2,

    /// <summary>The settings menu (<c>/settings</c>).</summary>
    Settings = 3,

    /// <summary>The session tree, for moving the leaf between branches (<c>/tree</c>).</summary>
    Tree = 4,

    /// <summary>Which models <c>Ctrl+P</c> cycles through (<c>/scoped-models</c>).</summary>
    ScopedModels = 5
}

/// <summary>
/// Pure C# 13 immediate-mode TUI engine for Reptar Agent.
/// Double-buffered 2D frame rendering, Alternate Screen Buffer lifecycle,
/// Pi-style task plan trees, Copilot-style tabbed workspace, Codex-style
/// ambient dinosaur watermark and docked prompt card, and inline floating
/// autocomplete for '/' commands and '@' file mentions.
/// </summary>
public sealed class ReptarTerm : IDisposable
{
    private readonly ReptarApp _app;
    private TerminalAgentOptions _options;
    private readonly AgentSessionStore? _store;

    /// <summary>Tree-backed persistence. Preferred over the flat store when present.</summary>
    private readonly SessionTreeStore? _tree;
    private readonly LlmAuthFile _auth = LlmAuthFile.LoadActive();
    private readonly string _workspaceName;
    private readonly List<string> _workspaceFiles = [];

    private TerminalBuffer _back;
    private TerminalBuffer? _front;

    private int _width;
    private int _height;
    private bool _running = true;
    private bool _inAltScreen;

    private TermOverlay _overlay = TermOverlay.None;
    private string _input = "";
    private int _inputCursor = 0;
    private int _historyIndex = -1;
    private readonly List<string> _history = [];
    private readonly string _historyFile;

    private ILlmClient _client;
    private AgentSessionRow? _session;
    private readonly List<ChatMessage> _messages = [];
    private readonly List<AgentSessionRow> _sessionsList = [];

    // Execution / streaming state.
    //
    // The run executes on a background task and publishes AgentEvent values into
    // _agentEvents. Only the render loop (this thread) mutates the display fields below
    // and _messages, so nothing here needs a lock: the background side owns its own
    // history list and the channel, and the UI side owns everything else. That split is
    // what lets the prompt stay live while the agent works.
    private readonly Channel<AgentEvent> _agentEvents = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Queue<ChatMessage> _steeringQueue = new();
    private readonly object _steeringGate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _isStreaming;
    private bool _dirty = true;
    private string _streamingText = "";
    private string _thinkingText = "";
    private string? _runError;
    private int _scrollOffset;
    private int _persistedCount;
    private double _inputCostPerMillion;
    private double _outputCostPerMillion;
    private int _contextLimit;
    private string _statusNote = "";
    private readonly List<string> _activePlanSteps = [];
    private readonly List<ToolCard> _toolCards = [];

    /// <summary>
    /// Direct shell commands (<c>!cmd</c>). Kept beside the display transcript because the UI
    /// renders them as a styled block while the model needs the structured record.
    /// </summary>
    private readonly List<ChatMessage> _bashHistory = [];

    /// <summary>Prompt templates discovered under .reptar/prompts/.</summary>
    private readonly List<PromptTemplate> _templates = [];

    /// <summary>One rendered tool call: name, arguments, live output, terminal status.</summary>
    private sealed class ToolCard
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Args { get; init; }
        public StringBuilder Output { get; } = new();
        public string Status { get; set; } = "running";
        public int DurationMs { get; set; }
    }

    // Autocomplete dropdowns
    private bool _slashDropdownOpen = false;
    private int _slashSelectedIdx = 0;

    private bool _fileDropdownOpen = false;
    private int _fileSelectedIdx = 0;
    private string _fileQuery = "";

    // Floating overlays
    private bool _showPalette = false;
    private string _paletteFilter = "";
    private int _paletteSelectedIdx = 0;

    // Models & Providers Catalog state (fx style)
    private int _providerCategoryIdx = 0;
    private string _providerFilterText = "";
    private int _selectedCatalogIdx = 0;

    /// <summary>What the provider overlay is currently showing.</summary>
    private enum ProviderStage
    {
        /// <summary>The model list, scoped to configured providers.</summary>
        Models,

        /// <summary>Choose how to sign in: with an account, or with an API key.</summary>
        PickAuthMethod,

        /// <summary>The provider list, filtered to the chosen auth method.</summary>
        PickProvider,

        /// <summary>The API-key prompt for one provider.</summary>
        EnterKey
    }

    private ProviderStage _providerStage = ProviderStage.Models;

    /// <summary>Which auth method the provider list is filtered by.</summary>
    private ProviderAuth _authMethod = ProviderAuth.ApiKey;

    /// <summary>Auth-method selector shown first by <c>/connect</c>.</summary>
    private TermSelector? _authSelector;

    /// <summary>Saved-session picker.</summary>
    private TermSelector? _sessionSelector;

    /// <summary>Model-scope editor for <c>Ctrl+P</c> cycling.</summary>
    private TermSelector? _scopeSelector;
    private int _selectedProviderIdx;

    /// <summary>Searchable model list, built from configured providers only.</summary>
    private TermSelector? _modelSelector;

    /// <summary>Searchable provider list for <c>/connect</c>.</summary>
    private TermSelector? _providerSelector;

    /// <summary>Searchable settings list for <c>/settings</c>.</summary>
    private TermSelector? _settingsSelector;

    /// <summary>The session tree, one row per entry.</summary>
    private TermSelector? _treeSelector;

    /// <summary>Which selector currently owns the composer slot, if any.</summary>
    private TermSelector? ActiveSelector => _overlay switch
    {
        TermOverlay.Providers => _providerStage switch
        {
            ProviderStage.PickAuthMethod => _authSelector,
            ProviderStage.PickProvider => _providerSelector,
            ProviderStage.Models => _modelSelector,
            _ => null
        },
        TermOverlay.Sessions => _sessionSelector,
        TermOverlay.Settings => _settingsSelector,
        TermOverlay.Tree => _treeSelector,
        TermOverlay.ScopedModels => _scopeSelector,
        _ => null
    };

    private bool _keyInputActive = false;
    private string _keyInputProvider = "";
    private string? _keyInputModel = null;
    private string _keyInputBuffer = "";

    /// <summary>Which provider field is being collected, or -1 while collecting the key.</summary>
    private int _keyFieldIndex = -1;

    /// <summary>Values collected so far for the provider being connected.</summary>
    private readonly Dictionary<string, string> _keyFieldValues = new(StringComparer.Ordinal);

    /// <summary>True when Enter was pressed on an empty required field.</summary>
    private bool _keyFieldRequired;

    public sealed record CatalogModelItem(
        string ModelId,
        string ProviderKey,
        string ProviderDisplayName,
        string Category,
        string Specs,
        string Tag,
        IReadOnlyList<ThinkingLevel>? Thinking = null,
        string DisplayName = "")
    {
        public IReadOnlyList<ThinkingLevel> SupportedThinking =>
            Thinking ?? (Tag == "reasoning" ? ThinkingLevels.Supported(true) : [ThinkingLevel.Off]);
    }

    /// <summary>
    /// Models from providers that actually have credentials. Populated by
    /// <see cref="RefreshCatalogAsync"/>; empty until the first refresh completes.
    /// </summary>
    private readonly List<CatalogModelItem> _catalog = [];

    /// <summary>
    /// Provider scope row. Derived from the catalog, so it only ever offers providers you can
    /// actually use.
    /// </summary>
    private List<string> _providerCategories = ["All"];

    private int _totalTokensIn;
    private int _totalTokensOut;
    private int _totalCacheRead;
    private int _totalCacheWrite;
    private double _totalCost;

    /// <summary>
    /// Cache hit rate of the most recent response: cacheRead / (input + cacheRead + cacheWrite).
    /// The session total would drift toward 100% and stop telling you anything about the turn
    /// you just paid for, which is the number that matters.
    /// </summary>
    private double _lastCacheHitRate = -1;
    private bool _autoCompact = true;

    /// <summary>Reasoning effort for the active model. Shift+Tab cycles it.</summary>
    private ThinkingLevel _thinking = ThinkingLevel.Off;

    /// <summary>When the current run started, for the working indicator's timer.</summary>
    private DateTimeOffset _runStartedAt;

    /// <summary>User and project settings in effect for this session.</summary>
    private AgentSettingsStore _settings = AgentSettingsStore.Load(".");

    /// <summary>What was loaded before the first turn, and what was rejected.</summary>
    private AgentResourcesReport? _resources;

    /// <summary>True while the <c>ctrl+o</c> resources panel is expanded.</summary>
    private bool _showResources;
    private string _statusRibbon = "";

    // Official Reptar pixel dinosaur (matching the website header SVG: apps/web/src/components/header.astro)
    private static readonly string[] PixelDinoWatermark =
    [
        "      ████████            ████████████    ",
        "      ████████            ████████████    ",
        "      ████████████    ████████████        ",
        "      ████████████    ████████████        ",
        "          ████████████████████████████    ",
        "          ████████████████████████████    ",
        "          ████████████████████████        ",
        "          ████████████████████████        ",
        "              ████████████████████        ",
        "              ████████████████████        ",
        "          ████████████████████            ",
        "          ████████████████████            ",
        "      ████████████    ████                ",
        "      ████████████    ████                ",
        "  ████████████        ████                ",
        "  ████████████        ████                ",
        "  ████████                                ",
        "  ████████                                "
    ];

    private record PaletteCommand(
        string Id,
        string Name,
        string Desc,
        Action Execute,
        string Hint = "");

    public ReptarTerm(
        ReptarApp app,
        TerminalAgentOptions options,
        AgentSessionStore? store,
        SessionTreeStore? tree = null)
    {
        _app = app;
        _options = options;
        _store = store;
        _tree = tree;
        _workspaceName = Path.GetFileName(Path.GetFullPath(options.WorkspaceRoot));

        _width = Math.Max(40, Console.WindowWidth);
        _height = Math.Max(12, Console.WindowHeight);
        _back = new TerminalBuffer(_width, _height);

        _historyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".reptar", "agent", "history");

        _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(options));

        LoadHistory();
        RefreshWorkspaceFiles();
    }

    private void RefreshWorkspaceFiles()
    {
        try
        {
            var root = Path.GetFullPath(_options.WorkspaceRoot);
            _workspaceFiles.Clear();
            if (!Directory.Exists(root))
                return;

            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0 && _workspaceFiles.Count < 200)
            {
                var dir = queue.Dequeue();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.StartsWith('.') || name.EndsWith(".tmp") || name.EndsWith(".lock"))
                            continue;

                        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                        _workspaceFiles.Add(rel);
                        if (_workspaceFiles.Count >= 200)
                            break;
                    }

                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var dirName = Path.GetFileName(sub);
                        if (dirName.StartsWith('.') ||
                            dirName is "bin" or "obj" or "node_modules" or "dist" or "artifacts" or ".scratch-app" or ".reptar")
                            continue;

                        queue.Enqueue(sub);
                    }
                }
                catch
                {
                    // Ignore inaccessible subdirectories without halting traversal
                }
            }
        }
        catch { }
    }

    private List<CatalogModelItem> GetFilteredCatalog()
    {
        var category = _providerCategories[Math.Clamp(_providerCategoryIdx, 0, _providerCategories.Count - 1)];
        IEnumerable<CatalogModelItem> list = _catalog;

        if (category != "All")
        {
            list = list.Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_providerFilterText))
        {
            var query = _providerFilterText.Trim();
            list = list.Where(m =>
                m.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.ProviderDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.Specs.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.Tag.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return list.ToList();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        EnterAltScreen();
        TerminalInput.Activate();

        if (_store is not null)
        {
            await _store.EnsureCreatedAsync().ConfigureAwait(false);
            if (_options.ResumeSessionId is { Length: > 0 } resumeId)
            {
                var found = await _store.FindSessionAsync(resumeId).ConfigureAwait(false);
                if (found is not null)
                {
                    _session = found;
                    ReplaceTranscript(await LoadBranchAsync(found.Id).ConfigureAwait(false));
                }
            }
            if (_session is null)
            {
                _session = await _store.CreateSessionAsync("New chat").ConfigureAwait(false);
            }
            await RefreshSessionsListAsync().ConfigureAwait(false);
        }

        // Settings load first: they decide the model and thinking level the session starts with.
        // Project-local configuration only applies when the workspace is trusted.
        var trust = ProjectTrustStore.Load();
        var trusted = await trust.ResolveAsync(_options.WorkspaceRoot).ConfigureAwait(false);
        _settings = AgentSettingsStore.Load(_options.WorkspaceRoot, trusted);

        // Templates are project-local content too, so they ride on the same decision.
        if (trusted)
            _templates.AddRange(PromptTemplates.Load(_options.WorkspaceRoot));

        if (_settings.Settings.Thinking is { Length: > 0 } savedThinking &&
            ThinkingLevels.Parse(savedThinking) is { } level)
        {
            _thinking = level;
        }

        _autoCompact = _settings.Settings.EffectiveCompaction.Enabled;

        if (_settings.Settings.Model is { Length: > 0 } savedModel && _options.Model is null)
        {
            _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(
                _options with { Provider = _settings.Settings.Provider, Model = savedModel }));

            UpdateStatusRibbon();
        }

        // The model list is scoped to configured providers, so build it from real credentials
        // before the first frame rather than showing a static table.
        await RefreshCatalogAsync().ConfigureAwait(false);

        _resources = AgentResourcesReport.Gather(
            _options.WorkspaceRoot,
            trusted,
            _settings,
            _templates);

        if (AgentLog.Enabled)
            _messages.Add(new ChatMessage("assistant",
                $"Logging this session to `{AgentLog.FilePath}`."));

        UpdateStatusRibbon();
        Render();
        _ = RefreshPricingAsync();

        while (_running && !cancellationToken.IsCancellationRequested)
        {
            CheckResize();

            // 1. Apply anything the background agent produced.
            DrainAgentEvents();

            // 2. Retire the run when it has settled.
            if (_runTask is { IsCompleted: true })
                await FinishAgentRunAsync().ConfigureAwait(false);

            // 3. Handle one keystroke, if any. Poll with a zero timeout: the default is -1,
            //    which blocks until a key arrives and freezes the whole loop — no spinner, no
            //    streamed text, no timer, until the user types something.
            var keyRead = TerminalInput.ReadKey(timeoutMs: 0);
            if (keyRead is { } key)
            {
                _dirty = true;

                if (key.Ctrl && key.Key == ConsoleKey.C)
                {
                    if (_isStreaming)
                    {
                        AbortAgent();
                    }
                    else
                    {
                        _running = false;
                        break;
                    }
                }
                else if (key.Key == ConsoleKey.Escape && _isStreaming)
                {
                    AbortAgent();
                }
                else if (key.Ctrl && key.Key == ConsoleKey.P && !key.Shift)
                {
                    // pi binds ctrl+p to cycling the scoped models, not to a command palette.
                    CycleModel(forward: true);
                }
                else if (key.Ctrl && key.Shift && key.Key == ConsoleKey.P)
                {
                    CycleModel(forward: false);
                }
                else if (key.Ctrl && key.Key == ConsoleKey.L)
                {
                    OpenModelCatalog();
                    _dirty = true;
                }
                else if (key.Ctrl && key.Key == ConsoleKey.K)
                {
                    _showPalette = !_showPalette;
                    _paletteFilter = "";
                    _paletteSelectedIdx = 0;
                    _dirty = true;
                }
                else if (key.Ctrl && key.Key == ConsoleKey.O)
                {
                    // pi's ctrl+o: expand or collapse the startup resource panel.
                    _showResources = !_showResources;
                    if (_showResources)
                        _resources = AgentResourcesReport.Gather(
                            _options.WorkspaceRoot,
                            _settings.Settings.DefaultProjectTrust != TrustDefault.Never,
                            _settings,
                            _templates);
                    _dirty = true;
                }
                else if (_showPalette)
                {
                    HandlePaletteKey(key);
                }
                else
                {
                    await HandleMainKeyAsync(key).ConfigureAwait(false);
                }
            }

            // 4. Repaint only when something changed. Streaming deltas arrive in bursts,
            //    so this collapses N token events into one frame.
            if (_dirty)
            {
                _dirty = false;
                Render();
            }

            // The working indicator animates, so keep repainting while a run is in flight.
            // Idle frames cost nothing because _dirty stays false.
            var streaming = _isStreaming;
            if (streaming)
                _dirty = true;

            if (keyRead is null)
                await Task.Delay(streaming ? 16 : 40, cancellationToken).ConfigureAwait(false);
        }

        AbortAgent();
        await FinishAgentRunAsync().ConfigureAwait(false);
        ExitAltScreen();
    }

    private void HandlePaletteKey(TermKey key)
    {
        var commands = GetPaletteCommands();
        if (key.Key == ConsoleKey.Escape)
        {
            _showPalette = false;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (commands.Count > 0)
                _paletteSelectedIdx = (_paletteSelectedIdx - 1 + commands.Count) % commands.Count;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            if (commands.Count > 0)
                _paletteSelectedIdx = (_paletteSelectedIdx + 1) % commands.Count;
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (commands.Count > 0 && _paletteSelectedIdx < commands.Count)
            {
                var cmd = commands[_paletteSelectedIdx];
                _showPalette = false;
                _input = "";
                _inputCursor = 0;
                cmd.Execute();
            }
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_paletteFilter.Length > 0)
            {
                _paletteFilter = _paletteFilter[..^1];
                _paletteSelectedIdx = 0;
            }
            return;
        }

        if (key.Ch is not '\0' && !char.IsControl(key.Ch))
        {
            _paletteFilter += key.Ch;
            _paletteSelectedIdx = 0;
        }
    }

    private List<PaletteCommand> GetPaletteCommands()
    {
        var all = new List<PaletteCommand>
        {
            new("model", "/model", "Browse and select model from catalog", () => { OpenModelCatalog(); }),
            new("connect", "/connect", "Connect provider API credentials (fast setup)", () => { OpenConnectInline(); }),
            new("clear", "/clear", "Clear chat in current session", () => { _messages.Clear(); }),
            new("new", "/new", "Start a fresh new session", () => { _ = StartNewSessionAsync(); }),
            new("sessions", "/sessions", "View and switch saved sessions", () => { _overlay = TermOverlay.Sessions; _input = ""; _inputCursor = 0; }),
            new("tools", "/tools", "List the mounted workspace tools", ShowToolsTable),
            new("providers", "/providers", "Show provider connection status", ShowProvidersTable),
            new("compact", "/compact", "Summarize context into a checkpoint now",
                () => { _ = CompactNowAsync(""); }, "[focus]"),
            new("templates", "/templates", "List prompt templates from .reptar/prompts", () =>
            {
                _messages.Add(new ChatMessage("assistant", PromptTemplates.Describe(_templates)));
                _dirty = true;
            }),
            new("tree", "/tree", "Browse the session tree and switch branches", () =>
            {
                _ = OpenTreeAsync();
            }),
            new("fork", "/fork", "Copy the current branch into a new session", () =>
            {
                _ = ForkSessionAsync();
            }),
            new("scoped-models", "/scoped-models", "Choose the models Ctrl+P cycles through", OpenScopedModels),
            new("settings", "/settings", "Open the settings menu", OpenSettingsMenu),
            new("help", "/help", "Show tips and shortcuts", () => { ShowHelpMessage(); }),
            new("exit", "/exit", "Quit ReptarTerm", () => { _running = false; }),
        };

        // Prompt templates appear in the same dropdown as built-ins, so a project command is as
        // discoverable as /help.
        foreach (var template in _templates)
        {
            var captured = template;
            all.Add(new PaletteCommand(
                captured.Name,
                "/" + captured.Name,
                captured.Description,
                () => _ = SendChatMessageAsync(PromptTemplates.Expand(captured, "")),
                captured.ArgumentHint ?? ""));
        }

        var filter = _paletteFilter?.Trim().TrimStart('/') ?? "";
        if (string.IsNullOrWhiteSpace(filter))
            return all;

        // pi's ranking: match on the name or the description, best score first. Typing
        // "summarize" finds /compact, which substring matching on the name would never do.
        return Fuzzy.Rank(
            filter,
            all,
            command => [command.Id, command.Name, command.Desc, command.Hint]);
    }

    /// <summary>
    /// The commands the slash dropdown should show right now, ranked by name.
    /// </summary>
    /// <remarks>
    /// An empty result means the query matched no command name. The caller keeps the list open
    /// and shows "no matches" rather than closing it, because a menu that disappears on a typo is
    /// indistinguishable from a menu that is broken.
    /// </remarks>
    private List<PaletteCommand> SlashMatches()
    {
        if (!SlashCommands.IsCompleting(_input))
            return [];

        var all = GetPaletteCommands();
        var ranked = SlashCommands.Match(
            _input,
            [.. all.Select(command => new SlashCommand(command.Name, command.Hint, command.Desc))]);

        // Map back in the ranked order.
        var byName = all.ToDictionary(command => command.Name, StringComparer.Ordinal);
        return [.. ranked.Select(command => byName[command.Name])];
    }

    /// <summary>True when the dropdown is open, even if nothing matches.</summary>
    private bool SlashListOpen => SlashCommands.IsCompleting(_input);

    private void ShowHelpMessage()
    {
        _messages.Add(new ChatMessage("assistant",
            "**Reptar Agent**\n\n" +
            "**Prompt**\n" +
            "• `Enter` — send · while the agent runs, this queues a *steering* message\n" +
            "• `Esc` / `Ctrl+C` — abort the current turn; `Ctrl+C` again quits\n" +
            "• `↑` / `↓` — prompt history · `PgUp` / `PgDn` — scroll the transcript\n" +
            "• `!cmd` — run a shell command and add it to context · `!!cmd` — run it, keep it out of context\n\n" +
            "**Completion**\n" +
            "• `/` — command list; `Tab` or `Enter` completes the name, then `Enter` submits\n" +
            "• `@` — attach a workspace file (text inlines, images attach as vision)\n" +
            "• `Ctrl+P` — cycle to the next model in scope (`/scoped-models` to choose) \n" +
            "• `Ctrl+L` — open the model picker · `Ctrl+K` — command palette\n" +
            "• `Ctrl+O` — show or hide loaded resources (skills, context, conflicts)\n" +
            "• `Shift+Tab` — cycle reasoning effort (off → minimal → low → medium → high)\n\n" +
            "**Commands**\n" +
            "• `/model` — pick a model (tab filters by provider)\n" +
            "• `/connect` — add a provider key (enter saves, `o` opens the portal)\n" +
            "• `/providers` — credential status table\n" +
            "• `/sessions` — switch session · `/new` — start one · `/clear` — clear this one\n" +
            "• `/tools` — mounted tool list · `/templates` — prompt templates\n" +
            "• `/scoped-models` — choose what `Ctrl+P` cycles through\n" +
            "• `/compact [focus]` — checkpoint the context now\n" +
            "• `/help` — this · `/exit` — quit"));
    }

    private void OpenModelCatalog()
    {
        var alreadyOpen = _overlay == TermOverlay.Providers && _providerStage == ProviderStage.Models;

        _overlay = TermOverlay.Providers;
        _providerStage = ProviderStage.Models;
        _keyInputActive = false;
        _input = "";
        _inputCursor = 0;

        if (_modelSelector is null || _catalog.Count == 0)
            RebuildModelSelector();
        else if (!alreadyOpen)
            _modelSelector.ClearQuery();   // a fresh open starts from the full list
    }

    /// <summary>
    /// <c>/fork</c>: copies the active branch into a new session and switches to it. The original
    /// is untouched, so this is "explore this alternative without losing where I was".
    /// </summary>
    private async Task ForkSessionAsync()
    {
        if (_tree is null || _session is null || _store is null)
        {
            _messages.Add(new ChatMessage("assistant", "Forking is not available."));
            _dirty = true;
            return;
        }

        var title = (_session.Title ?? "session") + " (fork)";
        var fork = await _store.CreateSessionAsync(title).ConfigureAwait(false);

        if (!await _tree.ForkAsync(_session.Id, fork.Id).ConfigureAwait(false))
        {
            await _store.DeleteSessionAsync(fork.Id).ConfigureAwait(false);
            _messages.Add(new ChatMessage("assistant", "Nothing to fork yet."));
            _dirty = true;
            return;
        }

        _session = fork;
        ReplaceTranscript(await _tree.LoadBranchAsync(fork.Id).ConfigureAwait(false));

        await RefreshSessionsListAsync().ConfigureAwait(false);

        _messages.Add(new ChatMessage("assistant",
            $"Forked into **{fork.Id}** ({_messages.Count} messages carried over). " +
            "The original session is untouched."));
        _dirty = true;
    }

    /// <summary>
    /// Builds the tree selector. Each row is one entry, indented by depth, with the active leaf
    /// marked — so the shape of the conversation is visible and a different branch is one Enter
    /// away.
    /// </summary>
    private async Task OpenTreeAsync()
    {
        if (_tree is null || _session is null)
        {
            _messages.Add(new ChatMessage("assistant", "Session tree is not available."));
            _dirty = true;
            return;
        }

        var tree = await _tree.TreeAsync(_session.Id).ConfigureAwait(false);
        var leafId = await _tree.LeafAsync(_session.Id).ConfigureAwait(false);

        if (tree.Count == 0)
        {
            _messages.Add(new ChatMessage("assistant", "This session has no entries yet."));
            _dirty = true;
            return;
        }

        var items = new List<TermSelectorItem>();
        Flatten(tree, 0, "", leafId, items);

        _treeSelector = new TermSelector("Session tree", items, Math.Max(6, ComposerRow - 10))
        {
            Notice = "Enter moves the active leaf — the conversation continues from there.",
            Hint = "Type to search · Enter switch branch · Esc cancel"
        };

        _overlay = TermOverlay.Tree;
        _input = "";
        _inputCursor = 0;
        _dirty = true;
    }

    private static void Flatten(
        IReadOnlyList<SessionTreeNode> nodes,
        int depth,
        string prefix,
        string? leafId,
        List<TermSelectorItem> items)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var last = i == nodes.Count - 1;
            var glyph = depth == 0 ? "●" : last ? "└─" : "├─";

            items.Add(new TermSelectorItem(
                $"{prefix}{glyph} {SummarizeEntry(node.Entry)}",
                node.Entry.Role,
                node.Entry.Id == leafId ? "◀ leaf" : "",
                node.Entry.Id == leafId,
                node.Entry.Id,
                node.Entry.Id));

            Flatten(node.Children, depth + 1, depth == 0 ? "" : prefix + (last ? "   " : "│  "), leafId, items);
        }
    }

    private static string SummarizeEntry(SessionEntryRow entry)
    {
        var text = entry.Content.ReplaceLineEndings(" ").Trim();

        if (text.Length == 0 && entry.ToolCalls is { Length: > 0 })
            text = "(tool call)";

        if (text.Length > 64)
            text = text[..64] + "…";

        var role = entry.Role switch
        {
            AgentRoles.User => "user",
            AgentRoles.Assistant => "assistant",
            AgentRoles.Tool => entry.Name ?? "tool",
            _ => entry.Role
        };

        return $"{role}: {text}";
    }

    /// <summary>
    /// <c>/scoped-models</c>: choose the models <c>Ctrl+P</c> cycles through. Enter toggles a
    /// model in or out of the scope; with nothing in scope, cycling walks every available model,
    /// which stops being useful once a provider offers a few hundred.
    /// </summary>
    /// <summary>Adds or removes one model from the cycling scope and persists it.</summary>
    private async Task ToggleScopeAsync(CatalogModelItem model)
    {
        var current = _settings.Settings.ScopedModels ?? [];
        var existing = current.FirstOrDefault(entry =>
            string.Equals(entry.Provider, model.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Model, model.ModelId, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<ScopedModel> next = existing is null
            ? [.. current, new ScopedModel(model.ProviderKey, model.ModelId)]
            : [.. current.Where(entry => !ReferenceEquals(entry, existing))];

        await _settings.SaveUserAsync(settings => settings with { ScopedModels = next })
            .ConfigureAwait(false);

        OpenScopedModels();
    }

    private void OpenScopedModels()
    {
        var scoped = _settings.Settings.ScopedModels ?? [];
        var inScope = scoped
            .Select(entry => (entry.Provider, entry.Model))
            .ToHashSet();

        var items = _catalog.Select(model => new TermSelectorItem(
            model.ModelId,
            model.ProviderDisplayName,
            inScope.Contains((model.ProviderKey, model.ModelId)) ? "✓ in scope" : "",
            inScope.Contains((model.ProviderKey, model.ModelId)),
            "Enter toggles this model in or out of the Ctrl+P cycle.",
            model));

        if (_scopeSelector is null)
            _scopeSelector = new TermSelector("Models in the Ctrl+P cycle", items, Math.Max(6, ComposerRow - 12));
        else
            _scopeSelector.SetItems(items);

        _scopeSelector.Notice = scoped.Count == 0
            ? "Nothing in scope — Ctrl+P currently walks every available model."
            : $"{scoped.Count} model(s) in scope.";

        _scopeSelector.Hint = "Type to search · Enter toggle · Esc done";

        _overlay = TermOverlay.ScopedModels;
        _input = "";
        _inputCursor = 0;
        _dirty = true;
    }

    private void OpenSettingsMenu()
    {
        var alreadyOpen = _overlay == TermOverlay.Settings;

        _overlay = TermOverlay.Settings;
        _input = "";
        _inputCursor = 0;
        RebuildSettingsSelector();

        if (!alreadyOpen)
            _settingsSelector?.ClearQuery();
    }

    /// <summary>
    /// <c>/connect</c> opens the provider list, matching pi's login flow: choose who you are
    /// connecting to, then supply the credential. <c>/connect anthropic</c> skips the choice.
    /// </summary>
    private void OpenConnectInline(string? providerKey = null, string? targetModel = null)
    {
        _overlay = TermOverlay.Providers;
        _input = "";
        _inputCursor = 0;

        if (providerKey is { Length: > 0 })
        {
            BeginKeyEntry(providerKey, targetModel);
            return;
        }

        _providerStage = ProviderStage.PickAuthMethod;
        _keyInputActive = false;
        _selectedProviderIdx = 0;
        RebuildAuthMethodSelector();
    }

    private void BeginKeyEntry(string providerKey, string? targetModel = null)
    {
        _providerStage = ProviderStage.EnterKey;
        _keyInputActive = true;
        _keyInputProvider = providerKey;
        _keyInputModel = targetModel;
        _keyInputBuffer = "";
        _keyFieldIndex = -1;
        _keyFieldValues.Clear();
    }

    /// <summary>The provider fields still to collect for the one being connected.</summary>
    private IReadOnlyList<ProviderField> PendingFields
    {
        get
        {
            var info = LlmProviders.Find(_keyInputProvider);
            return info?.ExtraFields ?? [];
        }
    }

    /// <summary>What the key prompt is currently asking for.</summary>
    private string CurrentKeyPrompt
    {
        get
        {
            var fields = PendingFields;
            return _keyFieldIndex >= 0 && _keyFieldIndex < fields.Count
                ? fields[_keyFieldIndex].Label
                : "api key";
        }
    }

    /// <summary>
    /// Reads the active branch, importing a flat session into the tree the first time it is
    /// opened so old conversations stay loadable.
    /// </summary>
    private async Task<List<ChatMessage>> LoadBranchAsync(string sessionId)
    {
        if (_tree is null)
            return _store is null ? [] : await _store.LoadMessagesAsync(sessionId).ConfigureAwait(false);

        if (_store is not null)
            await _tree.ImportFlatAsync(sessionId, _store.Messages).ConfigureAwait(false);

        return await _tree.LoadBranchAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>Appends to the tree when available, else to the flat table.</summary>
    private async Task AppendAsync(ChatMessage message)
    {
        if (_session is null)
            return;

        if (_tree is not null)
            await _tree.AppendAsync(_session.Id, message, _store?.PersistFilter).ConfigureAwait(false);
        else if (_store is not null)
            await AppendAsync(message).ConfigureAwait(false);
    }

    /// <summary>Providers in display order, filtered by the chosen auth method.</summary>
    private List<ProviderInfo> ConnectableProviders() => _authMethod == ProviderAuth.Account
        ? [.. LlmProviders.Known.Where(provider => provider.SupportsAccountSignIn)]
        : [.. LlmProviders.Known];

    /// <summary>
    /// Rebuilds the model selector from the live catalog. Only configured providers appear, and
    /// the notice says so, because a model you cannot call is worse than no model.
    /// </summary>
    private void RebuildModelSelector()
    {
        var items = _catalog.Select(model => new TermSelectorItem(
            model.ModelId,
            model.Specs,
            model.ProviderDisplayName,
            IsCurrentModel(model.ModelId),
            $"Model Name: {(model.DisplayName.Length > 0 ? model.DisplayName : model.ModelId)}",
            model));

        // Reuse the instance: a refresh after connecting must not clear what you were typing.
        if (_modelSelector is null)
        {
            _modelSelector = new TermSelector("", items, Math.Max(6, ComposerRow - 12));
        }
        else
        {
            _modelSelector.SetItems(items);
        }

        _modelSelector.Notice = "Only showing models from configured providers. Use /connect to add one.";
        _modelSelector.Hint = "Type to search · Enter to select · Ctrl+S set as default · Esc cancel";
    }

    private bool IsCurrentModel(string modelId) =>
        string.Equals(_client.Config.Model, modelId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first step of <c>/connect</c>, matching pi: choose how you want to sign in, then pick
    /// a provider. The two lists differ because subscription-backed providers are not the same
    /// set as API-key ones.
    /// </summary>
    private void RebuildAuthMethodSelector()
    {
        var accountProviders = LlmProviders.Known.Count(provider => provider.SupportsAccountSignIn);

        var items = new List<TermSelectorItem>
        {
            new("Sign in with an account",
                $"{accountProviders} providers",
                accountProviders == 0 ? "· not available yet" : "",
                _authMethod == ProviderAuth.Account,
                "Use a coding subscription you already pay for.",
                ProviderAuth.Account),
            new("Sign in with an API key",
                $"{LlmProviders.Known.Length} providers",
                "",
                _authMethod == ProviderAuth.ApiKey,
                "Paste a key from the provider's dashboard.",
                ProviderAuth.ApiKey)
        };

        _authSelector = new TermSelector("Select authentication method:", items, Math.Max(4, ComposerRow - 10))
        {
            Hint = "↑↓ navigate · enter select · esc cancel"
        };
    }

    /// <summary>Rebuilds the provider selector with pi's status vocabulary.</summary>
    /// <summary>
    /// The provider list, as pi draws it: a name and whether it is configured. No model column —
    /// the model is a separate decision, and putting it here made the list read like a model
    /// picker with a provider prefix.
    /// </summary>
    private void RebuildProviderSelector()
    {
        var providers = _authMethod == ProviderAuth.Account
            ? LlmProviders.Known.Where(provider => provider.SupportsAccountSignIn)
            : LlmProviders.Known;

        var items = providers.Select(provider =>
        {
            var (source, detail) = LlmProviders.CredentialSource(provider.Key, _auth);
            var saved = _auth.Get(provider.Key);
            var missing = LlmProviders.MissingRequirements(provider, saved, saved?.ApiKey);

            var status = source switch
            {
                "env" when missing.Count == 0 => $"✓ {detail}",
                "saved" when missing.Count == 0 => "✓ stored",
                _ when !provider.RequiresApiKey && missing.Count == 0 => "✓ no key needed",
                _ when missing.Count == 0 => "· not configured",
                _ => $"· needs {string.Join(" + ", missing)}"
            };

            var description = _authMethod == ProviderAuth.Account
                ? "Account sign-in is not implemented yet — use an API key for now."
                : !string.IsNullOrEmpty(provider.KeyUrl)
                    ? $"Get a key at {provider.KeyUrl}"
                    : provider.Note ?? provider.DisplayName;

            return new TermSelectorItem(
                provider.DisplayName,
                "",
                status,
                string.Equals(_client.Config.ProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase),
                description,
                provider);
        });

        if (_providerSelector is null)
            _providerSelector = new TermSelector("Select provider to configure:", items, Math.Max(6, ComposerRow - 12));
        else
            _providerSelector.SetItems(items);

        _providerSelector.Hint = "Type to search · Enter to configure · Esc back";
        _providerSelector.Status = "";
    }

    /// <summary>
    /// The settings menu: a searchable list of the knobs Reptar actually has, with the current
    /// value and a one-line explanation for each.
    /// </summary>
    private void RebuildSettingsSelector()
    {
        var settings = _settings.Settings;

        var items = new List<TermSelectorItem>
        {
            new("Auto-compact", settings.EffectiveCompaction.Enabled ? "true" : "false",
                Description: "Automatically compact context when it gets too large.",
                Value: "compaction"),
            new("Thinking level", ThinkingLevels.Name(_thinking),
                Description: "Reasoning effort. Shift+Tab cycles it without opening this menu.",
                Value: "thinking"),
            new("Prompt cache", settings.PromptCache == true ? "true" : "false",
                Description: "Ask the provider to cache the system prompt and tools.",
                Value: "promptCache"),
            new("Cache warming", settings.WarmPromptCache == true ? "true" : "false",
                Description: "Warm the cache with one cheap request before the first turn.",
                Value: "warmCache"),
            new("Steering mode", settings.SteeringMode == AgentQueueMode.All ? "all" : "one-at-a-time",
                Description: "How many queued messages are delivered per turn while the agent runs.",
                Value: "steering"),
            new("Keep recent tokens", settings.EffectiveCompaction.KeepRecentTokens.ToString("N0"),
                Description: "Conversation kept verbatim when compacting.",
                Value: "keepRecent"),
            new("Reserve tokens", settings.EffectiveCompaction.ReserveTokens.ToString("N0"),
                Description: "Headroom kept free so the next response fits.",
                Value: "reserve"),
            new("Retry attempts", settings.EffectiveRetry.MaxAttempts.ToString(),
                Description: "Retries for a transient provider failure.",
                Value: "retry"),
            new("Autocomplete rows", (settings.AutocompleteMaxVisible ?? 5).ToString(),
                Description: "Rows shown in the command and file dropdowns.",
                Value: "autocomplete"),
            new("Show thinking", _options.ShowThinking ? "true" : "false",
                Description: "Show reasoning blocks in the transcript.",
                Value: "showThinking")
        };

        if (_settingsSelector is null)
            _settingsSelector = new TermSelector("Settings", items, Math.Max(6, ComposerRow - 14));
        else
            _settingsSelector.SetItems(items);

        _settingsSelector.Hint = "Type to search · Enter/Space to change · Esc cancel";
    }

    /// <summary>
    /// Switches to a provider and model, refreshes the catalog so the model list reflects the new
    /// credentials, and closes the overlay.
    /// </summary>
    /// <summary>
    /// Models reachable by cycling: the configured scope, filtered to what is actually available.
    /// An empty scope falls back to every configured model, which is what pi does — but a scope
    /// is the point once a provider offers a few hundred models.
    /// </summary>
    private List<CatalogModelItem> CycleScope()
    {
        var scoped = _settings.Settings.ScopedModels;

        if (scoped is not { Count: > 0 })
            return [.. _catalog];

        var wanted = scoped
            .Select(entry => (entry.Provider, entry.Model))
            .ToHashSet();

        return [.. _catalog.Where(model => wanted.Contains((model.ProviderKey, model.ModelId)))];
    }

    /// <summary>
    /// Ctrl+P: step to the next model in scope, applying that entry's thinking level.
    /// </summary>
    private void CycleModel(bool forward)
    {
        var scope = CycleScope();

        if (scope.Count <= 1)
        {
            _statusNote = scope.Count == 0
                ? "no models available — /connect to add a provider"
                : "only one model in scope — /scoped-models to add more";
            _dirty = true;
            return;
        }

        var current = scope.FindIndex(model =>
            string.Equals(model.ModelId, _client.Config.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(model.ProviderKey, _client.Config.ProviderKey, StringComparison.OrdinalIgnoreCase));

        var next = forward
            ? (current + 1) % scope.Count
            : (current <= 0 ? scope.Count - 1 : current - 1);

        var target = scope[next];

        // A scoped entry may carry its own thinking level; clamp it to what the model supports.
        var requested = _settings.Settings.ScopedModels?
            .FirstOrDefault(entry =>
                string.Equals(entry.Provider, target.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Model, target.ModelId, StringComparison.OrdinalIgnoreCase))
            ?.Thinking;

        var level = ThinkingLevels.Parse(requested) ?? _thinking;

        ActivateProvider(target.ProviderKey, target.ModelId, level);
        _statusNote = $"model {next + 1}/{scope.Count}";
    }

    /// <summary>
    /// Levels the active model offers. A model that does not reason offers only <c>off</c>, which
    /// is why the footer can say "thinking off" instead of hiding the control.
    /// </summary>
    private IReadOnlyList<ThinkingLevel> AvailableThinkingLevels()
    {
        var known = _catalog.FirstOrDefault(m =>
            string.Equals(m.ModelId, _client.Config.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.ProviderKey, _client.Config.ProviderKey, StringComparison.OrdinalIgnoreCase));

        // Unknown model: offer the standard ladder rather than hiding the control, since the
        // provider clamps a level it does not accept. A known model uses its real list, which is
        // what stops Shift+Tab offering xhigh on a model that rejects it.
        return known is null
            ? ThinkingLevels.Supported(true)
            : known.SupportedThinking;
    }

    /// <summary>
    /// Shift+Tab: advance to the next level the model supports, wrapping around. pi binds
    /// <c>shift+tab</c> to <c>app.thinking.cycle</c>.
    /// </summary>
    private void CycleThinking()
    {
        var levels = AvailableThinkingLevels();
        _thinking = ThinkingLevels.Cycle(_thinking, levels);

        // The level lives on the provider config, so the client is rebuilt with it.
        _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(
            _options with
            {
                Provider = _client.Config.ProviderKey,
                Model = _client.Config.Model
            }) with { Thinking = _thinking });

        _ = _settings.SaveThinkingAsync(_thinking);

        UpdateStatusRibbon();
        _dirty = true;
    }

    private void ActivateProvider(string providerKey, string modelId) =>
        ActivateProvider(providerKey, modelId, _thinking);

    /// <summary>
    /// Switches to a provider and model, optionally setting the thinking level at the same time.
    /// Cycling passes the level from the scope entry so a cheap model does not inherit the deep
    /// model's reasoning budget.
    /// </summary>
    private void ActivateProvider(string providerKey, string modelId, ThinkingLevel thinking)
    {
        var provider = LlmProviders.Find(providerKey);
        var display = provider?.DisplayName ?? providerKey;

        _thinking = thinking;

        _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(
            _options with { Provider = providerKey, Model = modelId }) with { Thinking = thinking });

        _auth.SetDefault(providerKey);
        _ = _settings.SaveModelAsync(providerKey, modelId);
        _ = _settings.SaveThinkingAsync(thinking);

        _keyInputActive = false;
        _keyInputBuffer = "";
        _providerStage = ProviderStage.Models;
        _overlay = TermOverlay.None;
        _selectedCatalogIdx = 0;

        UpdateStatusRibbon();
        _messages.Add(new ChatMessage("assistant", $"Connected to **{display}** using `{modelId}`."));
        _dirty = true;

        // The model list is scoped to configured providers, so it has to be rebuilt now.
        _ = RefreshCatalogAsync();
        _ = RefreshPricingAsync();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
        }
        catch { }
    }

    private async Task HandleMainKeyAsync(TermKey key)
    {
        // 0. If in Providers tab with inline key input active, route all input to it
        if (_overlay == TermOverlay.Providers && _keyInputActive)
        {
            HandleProvidersKey(key);
            return;
        }

        // 1. Escape closes an overlay, or dismisses an open dropdown.
        if (key.Key == ConsoleKey.Escape)
        {
            if (_slashDropdownOpen || _fileDropdownOpen)
            {
                _slashDropdownOpen = false;
                _fileDropdownOpen = false;
                return;
            }

            if (_overlay != TermOverlay.None)
            {
                _overlay = TermOverlay.None;
                _input = "";
                _inputCursor = 0;
                return;
            }
        }

        // 2. 'q' or 'Q' returns to Chat from any non-Chat tab
        if (_overlay != TermOverlay.None && key.Ch is 'q' or 'Q')
        {
            _overlay = TermOverlay.None;
            _input = "";
            _inputCursor = 0;
            return;
        }

        // 3. When slash dropdown or file dropdown is open, route keys to autocomplete
        if (_overlay == TermOverlay.None && (_slashDropdownOpen || _fileDropdownOpen))
        {
            if (await HandleDropdownKeyAsync(key).ConfigureAwait(false))
                return;
        }

        // Shift+Tab cycles reasoning effort, as pi binds it.
        if (key.Key == ConsoleKey.Tab && key.Shift && _overlay == TermOverlay.None)
        {
            CycleThinking();
            return;
        }

        // 4. Tab key handling
        if (key.Key == ConsoleKey.Tab)
        {
            if (_overlay == TermOverlay.None && _input.Length > 0)
            {
                // Tab triggers autocomplete trigger check if not already open
                CheckInputTriggers();
                return;
            }

            if (_overlay == TermOverlay.Providers)
            {
                // Inside the provider picker, Tab cycles the category filter.
                _providerCategoryIdx = (_providerCategoryIdx + 1) % _providerCategories.Count;
                _selectedCatalogIdx = 0;
                return;
            }

            // No overlays open: Tab is autocomplete, handled by the chat key path.
            return;
        }

        // A selector owns every key while it is open: search, navigate, choose, cancel.
        if (ActiveSelector is { } selector)
        {
            await HandleSelectorKeyAsync(selector, key).ConfigureAwait(false);
            return;
        }

        switch (_overlay)
        {
            case TermOverlay.None:
                await HandleChatKeyAsync(key).ConfigureAwait(false);
                break;
            case TermOverlay.Providers:
                // Only the key-entry stage reaches here; the list stages are selectors.
                HandleProvidersKey(key);
                break;
        }
    }

    private async Task<bool> HandleDropdownKeyAsync(TermKey key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _slashDropdownOpen = false;
            _fileDropdownOpen = false;
            return true;
        }

        if (_slashDropdownOpen)
        {
            var commands = SlashMatches();

            if (commands.Count == 0)
            {
                // Nothing matched, but the list stays open so the user can keep typing or
                // backspace. Escape is the only thing that closes it here.
                return false;
            }

            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    _slashSelectedIdx = (_slashSelectedIdx - 1 + commands.Count) % commands.Count;
                    return true;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    _slashSelectedIdx = (_slashSelectedIdx + 1) % commands.Count;
                    return true;
                }

                if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter)
                {
                    var chosen = commands[Math.Clamp(_slashSelectedIdx, 0, commands.Count - 1)];
                    _slashDropdownOpen = false;
                    _slashSelectedIdx = 0;

                    // Tab completes into the editor so you can add arguments.
                    _input = chosen.Name + " ";
                    _inputCursor = _input.Length;

                    if (key.Key == ConsoleKey.Enter)
                    {
                        // Enter completes and submits, which is pi's behaviour: accept the
                        // completion, close the list, and fall through to running the command.
                        await SubmitInputAsync(chosen.Name).ConfigureAwait(false);
                    }

                    return true;
                }
            }
        }

        if (_fileDropdownOpen)
        {
            var files = GetMatchingFiles();
            if (key.Key == ConsoleKey.UpArrow && files.Count > 0)
            {
                _fileSelectedIdx = (_fileSelectedIdx - 1 + files.Count) % files.Count;
                return true;
            }
            if (key.Key == ConsoleKey.DownArrow && files.Count > 0)
            {
                _fileSelectedIdx = (_fileSelectedIdx + 1) % files.Count;
                return true;
            }
            if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter && files.Count > 0)
            {
                // pi returns here rather than submitting: attaching a file is a step in composing
                // a prompt, not the end of one.
                var chosen = files[_fileSelectedIdx];
                _fileDropdownOpen = false;

                // Replace current @query with @chosen
                var atIdx = _input.LastIndexOf('@');
                if (atIdx >= 0)
                {
                    _input = _input[..atIdx] + "@" + chosen + " ";
                    _inputCursor = _input.Length;
                }
                return true;
            }
        }

        return false;
    }

    private List<string> GetMatchingFiles()
    {
        if (_workspaceFiles.Count == 0)
            RefreshWorkspaceFiles();

        if (string.IsNullOrWhiteSpace(_fileQuery))
            return _workspaceFiles.Take(8).ToList();

        return _workspaceFiles
            .Where(f => f.Contains(_fileQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.StartsWith(_fileQuery, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(8)
            .ToList();
    }

    private async Task HandleChatKeyAsync(TermKey key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            await SubmitInputAsync(_input).ConfigureAwait(false);
            return;
        }

        await HandleChatEditingKeyAsync(key).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits whatever is in the composer: a shell command, a slash command, or a prompt.
    /// Separated from key handling because pi's Enter accepts an autocompletion and then
    /// <em>falls through to submit</em>, so both paths have to land here.
    /// </summary>
    private async Task SubmitInputAsync(string raw)
    {
        {
            var text = raw.Trim();
            if (text.Length == 0)
                return;

            _history.Add(text);
            AppendHistory(text);
            _historyIndex = -1;
            _input = "";
            _inputCursor = 0;
            _slashDropdownOpen = false;
            _fileDropdownOpen = false;

            // !command runs locally and is recorded as a bash-execution message so the model
            // can reason about output you just looked at. !!command keeps it out of context.
            if (text.StartsWith('!'))
            {
                await RunShellPassthroughAsync(text).ConfigureAwait(false);
                return;
            }

            // A prompt template wins over a built-in command only when its name is not one of
            // the built-ins, so /help and /compact cannot be shadowed by a stray file.
            if (text.StartsWith('/') &&
                PromptTemplates.TryExpand(_templates, text) is { Length: > 0 } expanded &&
                !IsBuiltinCommand(text))
            {
                await SendChatMessageAsync(expanded).ConfigureAwait(false);
                return;
            }

            if (text.StartsWith('/'))
            {
                var cmdPart = text.Split(' ')[0].ToLowerInvariant();
                if (cmdPart is "/palette" or "/p")
                {
                    _showPalette = true;
                    _paletteFilter = "";
                }
                else if (cmdPart is "/clear")
                {
                    // Start a new session rather than blanking the view: a cleared screen that
                    // still sends the old history to the model is a trap.
                    await StartNewSessionAsync().ConfigureAwait(false);
                    _messages.Add(new ChatMessage("assistant", "Cleared. Started a new session."));
                    _dirty = true;
                }
                else if (cmdPart is "/new")
                {
                    await StartNewSessionAsync().ConfigureAwait(false);
                }
                else if (cmdPart is "/model")
                {
                    OpenModelCatalog();
                }
                else if (cmdPart is "/connect")
                {
                    OpenConnectInline();
                }
                else if (cmdPart is "/sessions")
                {
                    await OpenSessionsAsync().ConfigureAwait(false);
                }
                else if (cmdPart is "/tools")
                {
                    ShowToolsTable();
                }
                else if (cmdPart is "/providers")
                {
                    ShowProvidersTable();
                }
                else if (cmdPart is "/compact")
                {
                    await CompactNowAsync(text.Length > cmdPart.Length ? text[cmdPart.Length..].Trim() : "")
                        .ConfigureAwait(false);
                }
                else if (cmdPart is "/templates")
                {
                    _messages.Add(new ChatMessage("assistant", PromptTemplates.Describe(_templates)));
                    _dirty = true;
                }
                else if (cmdPart is "/settings")
                {
                    OpenSettingsMenu();
                }
                else if (cmdPart is "/scoped-models")
                {
                    OpenScopedModels();
                }
                else if (cmdPart is "/tree")
                {
                    await OpenTreeAsync().ConfigureAwait(false);
                }
                else if (cmdPart is "/fork")
                {
                    await ForkSessionAsync().ConfigureAwait(false);
                }
                else if (cmdPart is "/help")
                {
                    ShowHelpMessage();
                }
                else if (cmdPart is "/exit" or "/quit")
                {
                    _running = false;
                }
                return;
            }

            // Quick suggestion trigger [1], [2], [3]
            if (text is "1" or "[1]" && _messages.Count == 0)
                text = "Inspect this workspace and explain the structure, architecture, and current git changes.";
            else if (text is "2" or "[2]" && _messages.Count == 0)
                text = "Run the self-test suite and verify that read_file, write_file, edit_file, and run_command are working.";
            else if (text is "3" or "[3]" && _messages.Count == 0)
                text = "Scan the project routes and minimal API endpoints in this app.";

            // Typing while the agent works steers it instead of blocking or being dropped.
            if (_isStreaming)
            {
                SteerAgent(text);
                return;
            }

            await SendChatMessageAsync(text).ConfigureAwait(false);
            return;
        }
    }

    private async Task HandleChatEditingKeyAsync(TermKey key)
    {
        if (key.Key == ConsoleKey.UpArrow)
        {
            if (_history.Count > 0)
            {
                if (_historyIndex == -1)
                    _historyIndex = _history.Count - 1;
                else if (_historyIndex > 0)
                    _historyIndex--;

                _input = _history[_historyIndex];
                _inputCursor = _input.Length;
                CheckInputTriggers();
            }
            return;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            if (_historyIndex != -1)
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _input = _history[_historyIndex];
                    _inputCursor = _input.Length;
                }
                else
                {
                    _historyIndex = -1;
                    _input = "";
                    _inputCursor = 0;
                }
                CheckInputTriggers();
            }
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (_inputCursor > 0) _inputCursor--;
            return;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            if (_inputCursor < _input.Length) _inputCursor++;
            return;
        }

        if (key.Key == ConsoleKey.Home)
        {
            _inputCursor = 0;
            return;
        }

        if (key.Key == ConsoleKey.End)
        {
            _inputCursor = _input.Length;
            return;
        }

        // Transcript scrollback. PgUp detaches from the tail; PgDn walks back down.
        if (key.Key == ConsoleKey.PageUp)
        {
            _scrollOffset += Math.Max(1, _height / 2);
            return;
        }

        if (key.Key == ConsoleKey.PageDown)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - Math.Max(1, _height / 2));
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_inputCursor > 0 && _input.Length > 0)
            {
                _input = _input.Remove(_inputCursor - 1, 1);
                _inputCursor--;
                CheckInputTriggers();
            }
            return;
        }

        if (key.PastedText is { Length: > 0 } paste)
        {
            _input = _input.Insert(_inputCursor, paste);
            _inputCursor += paste.Length;
            CheckInputTriggers();
            return;
        }

        if (key.Ch is not '\0' && !char.IsControl(key.Ch))
        {
            _input = _input.Insert(_inputCursor, key.Ch.ToString());
            _inputCursor++;
            CheckInputTriggers();
        }
    }

    private void CheckInputTriggers()
    {
        // pi's trigger: a slash at the very start with no space yet. Once you type an argument
        // the list closes and Enter submits, which is what makes the flow feel like one gesture.
        if (SlashCommands.IsCompleting(_input))
        {
            _slashDropdownOpen = true;
            _fileDropdownOpen = false;
            _paletteFilter = SlashCommands.Query(_input);
            _slashSelectedIdx = 0;
            return;
        }

        _slashDropdownOpen = false;

        // Check for @ file mention trigger
        var atIdx = _input.LastIndexOf('@');
        if (atIdx >= 0 && atIdx < _inputCursor)
        {
            var query = _input[(atIdx + 1).._inputCursor];
            if (!query.Contains(' '))
            {
                _fileDropdownOpen = true;
                _fileQuery = query;
                _fileSelectedIdx = 0;
                return;
            }
        }

        _fileDropdownOpen = false;
    }

    /// <summary>
    /// The session picker, searchable like every other list. Sessions are identified by title and
    /// id, so both are searchable.
    /// </summary>
    private async Task OpenSessionsAsync()
    {
        if (_store is null)
        {
            _messages.Add(new ChatMessage("assistant", "Session persistence is not configured."));
            _dirty = true;
            return;
        }

        await RefreshSessionsListAsync().ConfigureAwait(false);

        var items = _sessionsList.Select(session => new TermSelectorItem(
            string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title!,
            session.Id,
            session.Id == _session?.Id ? "◀ current" : "",
            session.Id == _session?.Id,
            $"Created {session.CreatedAt}",
            session));

        if (_sessionSelector is null)
            _sessionSelector = new TermSelector("Sessions", items, Math.Max(6, ComposerRow - 10));
        else
            _sessionSelector.SetItems(items);

        _sessionSelector.Hint = "Type to search · Enter resume · n new · d delete · Esc cancel";

        _overlay = TermOverlay.Sessions;
        _input = "";
        _inputCursor = 0;
        _dirty = true;
    }

    /// <summary>
    /// Keys for any searchable selector: typing filters, arrows move, Enter chooses, Escape backs
    /// out one level.
    /// </summary>
    private async Task HandleSelectorKeyAsync(TermSelector selector, TermKey key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            // Escape clears the query first, then closes — the same two-step pi uses, so a
            // mistyped search does not throw you out of the menu.
            if (selector.IsSearching)
            {
                selector.Backspace();
                while (selector.IsSearching)
                    selector.Backspace();
            }
            else
            {
                CloseOverlay();
            }

            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            selector.Move(-1);
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            selector.Move(1);
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            selector.Backspace();
            _dirty = true;
            return;
        }

        if (key.PastedText is { Length: > 0 } paste)
        {
            selector.Paste(paste);
            _dirty = true;
            return;
        }

        // Two single-key actions that only make sense in the session picker. They are checked
        // before the search so they cannot be swallowed by a query.
        if (selector == _sessionSelector && !selector.IsSearching && key.Ch is 'n' or 'N')
        {
            await StartNewSessionAsync().ConfigureAwait(false);
            CloseOverlay();
            return;
        }

        if (selector == _sessionSelector && !selector.IsSearching && key.Ch is 'd' or 'D')
        {
            if (selector.Selected?.Value is AgentSessionRow target && _store is not null)
            {
                await _store.DeleteSessionAsync(target.Id).ConfigureAwait(false);

                if (_session?.Id == target.Id)
                    await StartNewSessionAsync().ConfigureAwait(false);

                await OpenSessionsAsync().ConfigureAwait(false);
            }
            return;
        }

        // pi's Ctrl+S in the model picker: remember this model as the default without switching
        // to it now. Useful when you are browsing a catalog for next time.
        if (key.Ctrl && key.Key == ConsoleKey.S && selector == _modelSelector)
        {
            if (selector.Selected?.Value is CatalogModelItem defaulted)
            {
                await _settings.SaveModelAsync(defaulted.ProviderKey, defaulted.ModelId)
                    .ConfigureAwait(false);
                selector.Status = $"Default set to {defaulted.ModelId}.";
                _dirty = true;
            }
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            await ActivateSelectionAsync(selector).ConfigureAwait(false);
            return;
        }

        if (key.Ch is not '\0' && !char.IsControl(key.Ch))
        {
            selector.Type(key.Ch);
            _dirty = true;
        }
    }

    /// <summary>
    /// Moves the leaf to an entry and reloads the branch. This is the branch switch: the next
    /// message continues from that point, and the previous continuation stays in the tree.
    /// </summary>
    private async Task SwitchLeafAsync(string entryId)
    {
        if (_tree is null || _session is null)
            return;

        await _tree.SetLeafAsync(_session.Id, entryId).ConfigureAwait(false);

        ReplaceTranscript(await _tree.LoadBranchAsync(_session.Id).ConfigureAwait(false));

        CloseOverlay();

        _messages.Add(new ChatMessage("assistant",
            $"Leaf moved to `{entryId}`. The conversation continues from here " +
            $"({_messages.Count} messages on this branch). `/tree` again to switch back."));
        _dirty = true;
    }

    /// <summary>Adds a hint about ctrl+o to the startup summary once.</summary>
    private void NoteResourcesHint()
    {
        if (_resources is null || !_resources.HasProblems || _showResources)
            return;

        _messages.Add(new ChatMessage("assistant",
            "⚠ Some project resources were rejected. Press `ctrl+o` to see which."));
    }

    private void CloseOverlay()
    {
        _overlay = TermOverlay.None;
        _providerStage = ProviderStage.Models;
        _authMethod = ProviderAuth.ApiKey;
        _keyInputActive = false;
        _keyInputBuffer = "";
    }

    /// <summary>Applies whatever the highlighted row means in the current selector.</summary>
    private async Task ActivateSelectionAsync(TermSelector selector)
    {
        if (selector.Selected is not { } item)
            return;

        if (selector == _scopeSelector && item.Value is CatalogModelItem scopedModel)
        {
            await ToggleScopeAsync(scopedModel).ConfigureAwait(false);
            return;
        }

        if (selector == _sessionSelector && item.Value is AgentSessionRow session)
        {
            _session = session;
            ReplaceTranscript(await LoadBranchAsync(session.Id).ConfigureAwait(false));
            CloseOverlay();
            return;
        }

        if (selector == _authSelector && item.Value is ProviderAuth method)
        {
            _authMethod = method;
            _providerStage = ProviderStage.PickProvider;
            RebuildProviderSelector();
            _dirty = true;
            return;
        }

        if (selector == _providerSelector && item.Value is ProviderInfo provider)
        {
            // Account sign-in is not implemented; say so instead of dropping into a key prompt
            // that would look like the same flow under a different name.
            if (_authMethod == ProviderAuth.Account)
            {
                _providerSelector.Status =
                    $"{provider.DisplayName}: account sign-in is not implemented yet. " +
                    "Go back and choose an API key.";
                _dirty = true;
                return;
            }

            if (!provider.RequiresApiKey)
            {
                ActivateProvider(provider.Key, provider.DefaultModel);
                return;
            }

            BeginKeyEntry(provider.Key);
            _dirty = true;
            return;
        }

        if (selector == _modelSelector && item.Value is CatalogModelItem model)
        {
            var (source, _) = LlmProviders.CredentialSource(model.ProviderKey, _auth);
            var configured = source != "none" || model.ProviderKey == LlmProviders.Ollama;

            if (!configured)
            {
                BeginKeyEntry(model.ProviderKey, model.ModelId);
                _dirty = true;
                return;
            }

            ActivateProvider(model.ProviderKey, model.ModelId);
            return;
        }

        if (selector == _treeSelector && item.Value is string entryId)
        {
            await SwitchLeafAsync(entryId).ConfigureAwait(false);
            return;
        }

        if (selector == _settingsSelector && item.Value is string setting)
        {
            await ToggleSettingAsync(setting).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Flips one setting and persists it. Numeric settings cycle through a short list rather than
    /// asking for input, which keeps the menu a menu.
    /// </summary>
    private async Task ToggleSettingAsync(string setting)
    {
        var current = _settings.Settings;

        switch (setting)
        {
            case "compaction":
                _autoCompact = !_autoCompact;
                await _settings.SaveUserAsync(s => s with
                {
                    Compaction = s.EffectiveCompaction with { Enabled = _autoCompact }
                }).ConfigureAwait(false);
                break;

            case "thinking":
                CycleThinking();
                break;

            case "promptCache":
                await _settings.SaveUserAsync(s => s with { PromptCache = !(s.PromptCache ?? true) })
                    .ConfigureAwait(false);
                break;

            case "warmCache":
                await _settings.SaveUserAsync(s => s with { WarmPromptCache = !(s.WarmPromptCache ?? true) })
                    .ConfigureAwait(false);
                break;

            case "steering":
                var next = current.SteeringMode == AgentQueueMode.All
                    ? AgentQueueMode.OneAtATime
                    : AgentQueueMode.All;
                await _settings.SaveUserAsync(s => s with { SteeringMode = next }).ConfigureAwait(false);
                break;

            case "keepRecent":
                var keep = current.EffectiveCompaction.KeepRecentTokens switch
                {
                    <= 8_000 => 20_000,
                    <= 20_000 => 40_000,
                    <= 40_000 => 80_000,
                    _ => 8_000
                };
                await _settings.SaveUserAsync(s => s with
                {
                    Compaction = s.EffectiveCompaction with { KeepRecentTokens = keep }
                }).ConfigureAwait(false);
                break;

            case "reserve":
                var reserve = current.EffectiveCompaction.ReserveTokens switch
                {
                    <= 8_192 => 16_384,
                    <= 16_384 => 32_768,
                    _ => 8_192
                };
                await _settings.SaveUserAsync(s => s with
                {
                    Compaction = s.EffectiveCompaction with { ReserveTokens = reserve }
                }).ConfigureAwait(false);
                break;

            case "retry":
                var attempts = current.EffectiveRetry.MaxAttempts switch
                {
                    <= 1 => 3,
                    <= 3 => 5,
                    _ => 1
                };
                await _settings.SaveUserAsync(s => s with
                {
                    Retry = s.EffectiveRetry with { MaxAttempts = attempts }
                }).ConfigureAwait(false);
                break;

            case "autocomplete":
                var rows = (current.AutocompleteMaxVisible ?? 5) switch
                {
                    <= 5 => 8,
                    <= 8 => 12,
                    _ => 5
                };
                await _settings.SaveUserAsync(s => s with { AutocompleteMaxVisible = rows })
                    .ConfigureAwait(false);
                break;

            case "showThinking":
                _options = _options with { ShowThinking = !_options.ShowThinking };
                await _settings.SaveUserAsync(s => s with { HideThinking = _options.ShowThinking })
                    .ConfigureAwait(false);
                break;
        }

        // SetItems restores the selection by label, so the list stays where it was.
        RebuildSettingsSelector();
        _dirty = true;
    }

    /// <summary>
    /// One Enter in the key flow: collect the key, then each provider field in turn, then
    /// activate. Each step is a single line, so the flow reads as a short sequence rather than a
    /// form.
    /// </summary>
    private void AdvanceKeyEntry()
    {
        var provider = LlmProviders.Find(_keyInputProvider) ?? LlmProviders.Known[0];
        var fields = PendingFields;

        if (_keyFieldIndex < 0)
        {
            if (!string.IsNullOrWhiteSpace(_keyInputBuffer))
                _keyFieldValues["api_key"] = _keyInputBuffer.Trim();

            _keyInputBuffer = "";
            _keyFieldRequired = false;

            if (fields.Count > 0)
            {
                _keyFieldIndex = 0;
                _dirty = true;
                return;
            }
        }
        else if (_keyFieldIndex < fields.Count)
        {
            var field = fields[_keyFieldIndex];

            if (_keyInputBuffer.Trim().Length == 0)
            {
                _keyFieldRequired = true;
                _dirty = true;
                return;
            }

            _keyFieldValues[field.Name] = _keyInputBuffer.Trim();
            _keyInputBuffer = "";
            _keyFieldRequired = false;
            _keyFieldIndex++;

            if (_keyFieldIndex < fields.Count)
            {
                _dirty = true;
                return;
            }
        }

        var modelToSet = _keyInputModel
            ?? _catalog.FirstOrDefault(m => m.ProviderKey == provider.Key)?.ModelId
            ?? provider.DefaultModel;

        _auth.Set(provider.Key, new ProviderCredential(
            ApiKey: _keyFieldValues.GetValueOrDefault("api_key"),
            BaseUrl: provider.HasUrlTemplate ? null : provider.DefaultBaseUrl,
            DefaultModel: modelToSet,
            Extra: _keyFieldValues
                .Where(pair => pair.Key != "api_key")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));

        _keyFieldIndex = -1;
        ActivateProvider(provider.Key, modelToSet);
    }

    /// <summary>
    /// Keys for the provider overlay's key-entry stage. The list stages are handled by
    /// <see cref="HandleSelectorKeyAsync"/> because they are <see cref="TermSelector"/>s.
    /// </summary>
    private void HandleProvidersKey(TermKey key)
    {
        if (!_keyInputActive)
            return;

        if (key.Key == ConsoleKey.Escape)
        {
            _keyInputActive = false;
            _keyInputBuffer = "";
            _providerStage = ProviderStage.PickProvider;
            RebuildProviderSelector();
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            AdvanceKeyEntry();
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_keyInputBuffer.Length > 0)
                _keyInputBuffer = _keyInputBuffer[..^1];
            return;
        }

        if (key.PastedText is { Length: > 0 } paste)
        {
            _keyInputBuffer += paste.Trim();
            return;
        }

        if ((key.Ch is 'o' or 'O') && _keyInputBuffer.Length == 0 && _keyFieldIndex < 0)
        {
            var provider = LlmProviders.Find(_keyInputProvider);
            var url = provider?.KeyUrl ?? (_keyInputProvider == LlmProviders.Ollama ? "ollama.com/download" : null);
            if (url is not null)
                OpenBrowser(url);
            return;
        }

        if (key.Ch is not '\0' && !char.IsControl(key.Ch))
            _keyInputBuffer += key.Ch;
    }


    private async Task SendChatMessageAsync(string prompt)
    {
        // Resolve any @path mentions to append file contents
        var resolvedPrompt = new StringBuilder(prompt);
        foreach (var token in prompt.Split(' '))
        {
            if (token.StartsWith('@') && token.Length > 1)
            {
                var relPath = token[1..].TrimEnd(',', ';', '!', '?');
                var fullPath = Path.Combine(_options.WorkspaceRoot, relPath);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                        resolvedPrompt.AppendLine($"\n\n```file:{relPath}\n{content}\n```");
                    }
                    catch { }
                }
            }
        }

        var userMessage = new ChatMessage("user", resolvedPrompt.ToString());
        _messages.Add(userMessage);
        _persistedCount = _messages.Count - 1;

        if (_store is not null && _session is not null)
            await AppendAsync(userMessage).ConfigureAwait(false);

        StartAgentRun(userMessage);
    }

    /// <summary>
    /// Launches the agent on a background task and returns immediately, so the prompt
    /// stays responsive: typing while streaming queues a steering message, Esc aborts.
    /// </summary>
    private void StartAgentRun(ChatMessage userMessage)
    {
        AgentLog.Message(AgentRoles.User, userMessage.Content);

        _isStreaming = true;
        _runStartedAt = DateTimeOffset.UtcNow;
        _dirty = true;
        _streamingText = "";
        _thinkingText = "";
        _runError = null;
        _statusNote = "";
        _scrollOffset = 0;
        _activePlanSteps.Clear();
        _toolCards.Clear();
        lock (_steeringGate) _steeringQueue.Clear();

        // Snapshot on this thread; the background loop owns its own list from here on and
        // reports back through MessageEnd events, which we apply to _messages.
        var history = new List<ChatMessage>(_messages.Count + _bashHistory.Count);
        history.AddRange(_messages);
        history.AddRange(_bashHistory);
        var systemPrompt = SystemPrompt();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        var sink = _agentEvents.Writer;

        _runTask = Task.Run(async () =>
        {
            try
            {
                await StreamingAgentHarness.RunAsync(
                    _app,
                    _client,
                    history,
                    systemPrompt: systemPrompt,
                    maxSteps: _options.MaxSteps,
                    onEvent: evt => sink.TryWrite(evt),
                    configure: config => config with
                    {
                        GetSteeringMessages = () =>
                        {
                            lock (_steeringGate)
                            {
                                if (_steeringQueue.Count == 0)
                                    return [];
                                return [_steeringQueue.Dequeue()];
                            }
                        },
                        Retry = new AgentRetryPolicy { MaxAttempts = 3 },
                        PrepareNextTurn = (turn, ct) => CompactIfNeededAsync(turn, sink, ct)
                    },
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sink.TryWrite(new AgentEvent.AgentError(ex.Message));
            }
            finally
            {
                sink.TryWrite(new AgentEvent.AgentSettled(token.IsCancellationRequested));
            }
        });
    }

    /// <summary>
    /// Applies queued agent events to display state. Runs on the render thread only.
    /// </summary>
    private void DrainAgentEvents()
    {
        while (_agentEvents.Reader.TryRead(out var evt))
        {
            _dirty = true;

            switch (evt)
            {
                case AgentEvent.TurnStart turn:
                    _activePlanSteps.Add($"step {turn.Step}");
                    break;

                case AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta:
                    _streamingText += delta.Delta;
                    break;

                case AgentEvent.MessageUpdate { Kind: AgentContentKind.Thinking } delta:
                    _thinkingText += delta.Delta;
                    break;

                case AgentEvent.MessageEnd end when end.Message.Role != "assistant":
                    // Tool results and queued user messages join the transcript here.
                    _messages.Add(end.Message);
                    break;

                case AgentEvent.MessageEnd end:
                    _messages.Add(end.Message);
                    _streamingText = "";
                    _thinkingText = "";
                    break;

                case AgentEvent.ToolExecutionStart start:
                    _toolCards.Add(new ToolCard
                    {
                        Id = start.ToolCallId,
                        Name = start.ToolName,
                        Args = SummarizeArgs(start.ArgumentsJson)
                    });
                    break;

                case AgentEvent.ToolExecutionUpdate update:
                    FindCard(update.ToolCallId)?.Output.Append(update.PartialResult);
                    break;

                case AgentEvent.ToolExecutionEnd end:
                    var card = FindCard(end.ToolCallId);
                    if (card is not null)
                    {
                        card.Status = end.IsError ? "failed" : "ok";
                        card.DurationMs = (int)end.DurationMs;
                        if (card.Output.Length == 0)
                            card.Output.Append(end.Result);
                    }
                    break;

                case AgentEvent.UsageReported usage:
                    _totalTokensIn += usage.InputTokens;
                    _totalTokensOut += usage.OutputTokens;
                    _totalCost +=
                        (usage.InputTokens / 1_000_000.0 * _inputCostPerMillion) +
                        (usage.OutputTokens / 1_000_000.0 * _outputCostPerMillion);
                    break;

                case AgentEvent.AgentError error:
                    _runError = error.Message;
                    break;

                case AgentEvent.RetryScheduled retry:
                    _statusNote = $"provider error, retrying in {retry.DelayMs / 1000.0:0.0}s " +
                        $"(attempt {retry.Attempt})";
                    break;

                case AgentEvent.RetryFinished:
                    _statusNote = "";
                    break;

                case AgentEvent.CompactionStart:
                    _statusNote = "compacting context…";
                    break;

                case AgentEvent.CompactionEnd compaction:
                    _statusNote = compaction.Error is { Length: > 0 }
                        ? $"compaction failed: {compaction.Error}"
                        : $"compacted {compaction.TokensBefore:N0} → {compaction.EstimatedTokensAfter:N0} tokens";
                    break;
            }
        }
    }

    private ToolCard? FindCard(string toolCallId)
    {
        foreach (var card in _toolCards)
        {
            if (card.Id == toolCallId)
                return card;
        }
        return null;
    }

    /// <summary>
    /// Called on the render thread once the background run has finished. Persists the
    /// messages the run produced and clears streaming state.
    /// </summary>
    private async Task FinishAgentRunAsync()
    {
        var task = _runTask;
        _runTask = null;
        _runCts?.Dispose();
        _runCts = null;

        _isStreaming = false;
        _runStartedAt = default;
        _streamingText = "";
        _thinkingText = "";
        _dirty = true;

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // aborted by the user
            }
            catch (Exception ex)
            {
                _runError ??= ex.Message;
            }
        }

        if (_runError is { Length: > 0 } message)
        {
            _messages.Add(new ChatMessage("assistant",
                $"⚠️ **Error ({_client.Config.Model}):**\n\n{message}\n\n" +
                "*Tip: `/connect` to set API credentials, `/model` to switch providers.*"));
            _runError = null;
        }

        if (_store is not null && _session is not null)
        {
            for (var i = Math.Max(0, _persistedCount); i < _messages.Count; i++)
            {
                var stored = _messages[i];
                if (stored.Role is "assistant" or "tool")
                    await AppendAsync(stored).ConfigureAwait(false);
            }
        }

        _persistedCount = _messages.Count;
        UpdateStatusRibbon();
    }

    /// <summary>Queue a message for the running agent (Enter while streaming).</summary>
    private void SteerAgent(string text)
    {
        var message = new ChatMessage("user", text);
        lock (_steeringGate) _steeringQueue.Enqueue(message);
        _messages.Add(new ChatMessage("user", $"(steering) {text}"));
        _dirty = true;
    }

    private static readonly HashSet<string> BuiltinCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/palette", "/p", "/clear", "/new", "/model", "/connect", "/sessions", "/tools",
        "/providers", "/help", "/compact", "/exit", "/quit", "/templates"
    };

    private static bool IsBuiltinCommand(string text) =>
        BuiltinCommands.Contains(text.Split(' ')[0]);

    /// <summary>
    /// Runs a shell command outside the model and records it in the transcript.
    /// </summary>
    private async Task RunShellPassthroughAsync(string input)
    {
        var excludeFromContext = input.StartsWith("!!");
        var command = input.TrimStart('!').Trim();

        if (command.Length == 0)
            return;

        var workspace = new WorkspaceRoot(new WorkspaceToolOptions
        {
            RootPath = _options.WorkspaceRoot,
            AllowRunCommand = true
        });

        _messages.Add(new ChatMessage("assistant", $"**$ {command}**"));
        _dirty = true;
        Render();

        var result = await WorkspaceTools
            .RunCommandAsync(workspace, command, 30_000)
            .ConfigureAwait(false);

        var shown = ToolOutput.Tail(
            string.Concat(result.Stdout, result.Stderr),
            maxLines: 40,
            maxBytes: 8 * 1024);

        _messages.Add(new ChatMessage("assistant",
            "```\n" + (shown.Content.Length > 0 ? shown.Content : "(no output)") + "\n```" +
            (result.TimedOut ? "\n\n_timed out_" : "") +
            (result.ExitCode != 0 ? $"\n\n_exit {result.ExitCode}_" : "")));

        var message = AgentMessages.BashExecution(
            command,
            result.Stdout,
            result.Stderr,
            result.ExitCode,
            cancelled: result.TimedOut,
            excludeFromContext: excludeFromContext);

        // Keep the bash message in the transcript used to seed the next run, without showing
        // the raw record twice in the UI.
        _bashHistory.Add(message);

        if (_store is not null && _session is not null)
            await AppendAsync(message).ConfigureAwait(false);

        _dirty = true;
    }

    /// <summary>
    /// Manual <c>/compact [instructions]</c>. Replaces the older half of the transcript with a
    /// structured checkpoint so a long session can keep going.
    /// </summary>
    private async Task CompactNowAsync(string customInstructions)
    {
        if (_isStreaming)
        {
            _messages.Add(new ChatMessage("assistant", "Cannot compact while the agent is running."));
            _dirty = true;
            return;
        }

        _statusNote = "compacting context…";
        _dirty = true;
        Render();

        var before = ContextCompaction.EstimateContextTokens(_messages);

        var outcome = await ContextCompactor.CompactAsync(
            _messages,
            _client,
            new CompactionSettings(),
            CompactionReason.Manual,
            customInstructions: customInstructions.Length > 0 ? customInstructions : null)
            .ConfigureAwait(false);

        if (outcome is null)
        {
            _statusNote = "";
            _messages.Add(new ChatMessage("assistant",
                before < 4_000
                    ? "Nothing to compact yet — the conversation is still small."
                    : "Compaction failed; the summary was empty or truncated. Context is unchanged."));
            _dirty = true;
            return;
        }

        var (result, projection) = outcome.Value;

        // The TUI keeps the raw transcript for scrollback; only the model-facing projection
        // shrinks. Persisting keeps the session reloadable.
        ReplaceTranscript(projection);

        _statusNote = $"compacted {result.TokensBefore:N0} → {result.EstimatedTokensAfter:N0} tokens";
        _messages.Add(new ChatMessage("assistant",
            $"**Context compacted.** {result.TokensBefore:N0} → {result.EstimatedTokensAfter:N0} tokens " +
            $"({projection.Count} messages retained).\n\n{result.Summary}"));
        _dirty = true;

        if (_store is not null && _session is not null)
        {
            for (var i = Math.Max(0, _persistedCount - projection.Count); i < _messages.Count; i++)
                await AppendAsync(_messages[i]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs between turns. When the live context is close to the model's window, summarizes the
    /// older half and hands the loop a shorter projection — the raw transcript is untouched, so
    /// scrollback and export still show everything.
    /// </summary>
    private async Task<AgentNextTurn?> CompactIfNeededAsync(
        AgentTurnResult turn,
        ChannelWriter<AgentEvent> sink,
        CancellationToken cancellationToken)
    {
        if (!_autoCompact || _contextLimit <= 0)
            return null;

        var settings = new CompactionSettings();
        var contextTokens = ContextCompaction.EstimateContextTokens(turn.History);

        if (!ContextCompaction.ShouldCompact(contextTokens, _contextLimit, settings))
            return null;

        sink.TryWrite(new AgentEvent.CompactionStart(CompactionReason.Threshold));

        var outcome = await ContextCompactor.CompactAsync(
            turn.History,
            _client,
            settings,
            CompactionReason.Threshold,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome is null)
        {
            sink.TryWrite(new AgentEvent.CompactionEnd(
                CompactionReason.Threshold, contextTokens, contextTokens, "summary unavailable"));
            return null;
        }

        var (result, projection) = outcome.Value;
        sink.TryWrite(new AgentEvent.CompactionEnd(
            CompactionReason.Threshold, result.TokensBefore, result.EstimatedTokensAfter, null));

        return new AgentNextTurn(ContextMessages: projection);
    }

    /// <summary>Abort the running agent, mirroring pi's Esc: clear the queue, keep the text.</summary>
    private void AbortAgent()
    {
        _runCts?.Cancel();
        lock (_steeringGate) _steeringQueue.Clear();
        _dirty = true;
    }

    private string SystemPrompt() =>
        "You are Reptar Agent, an autonomous coding pair programmer in-process with access to " +
        "workspace tools (read_file, write_file, edit_file, list_dir, run_command). Read before " +
        "editing, prefer edit_file over rewriting whole files, verify with run_command when a " +
        "build or test can check your work, and answer concisely in Markdown.";

    private async Task StartNewSessionAsync()
    {
        if (_store is null) return;
        _session = await _store.CreateSessionAsync("New chat").ConfigureAwait(false);
        ReplaceTranscript([]);
        _input = "";
        await RefreshSessionsListAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Swaps the visible transcript and resets everything derived from it: scroll position, tool
    /// cards, and the persistence cursor. Anything that replaces the conversation goes through
    /// here so switching sessions cannot leave you scrolled up in a list of another session's
    /// tool cards.
    /// </summary>
    private void ReplaceTranscript(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        _persistedCount = _messages.Count;
        _scrollOffset = 0;
        _toolCards.Clear();
        _activePlanSteps.Clear();
        _streamingText = "";
        _thinkingText = "";
        _dirty = true;
    }

    private async Task RefreshSessionsListAsync()
    {
        if (_store is null) return;
        _sessionsList.Clear();
        _sessionsList.AddRange(await _store.ListSessionsAsync(50).ConfigureAwait(false));
    }

    private void CheckResize()
    {
        try
        {
            var w = Math.Max(40, Console.WindowWidth);
            var h = Math.Max(12, Console.WindowHeight);
            if (w != _width || h != _height)
            {
                _width = w;
                _height = h;
                _back.Resize(_width, _height);
                _front = null; // force full redraw
                _dirty = true;
            }
        }
        catch { }
    }

    private void UpdateStatusRibbon()
    {
        // Line 1 is where you are; line 2 is what it has cost. Mirrors pi's footer.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.GetFullPath(_options.WorkspaceRoot);

        var where = root;
        if (home.Length > 0 && root.StartsWith(home, StringComparison.Ordinal))
        {
            var tail = root[home.Length..].TrimStart(Path.DirectorySeparatorChar);
            where = tail.Length == 0 ? "~" : "~/" + tail;
        }

        var branch = ReadGitBranch(_options.WorkspaceRoot);
        if (!string.IsNullOrEmpty(branch))
            where += $" ({branch})";

        _footerLocation = where;
    }

    private string _footerLocation = "";

    /// <summary>
    /// pi's <c>formatTokens</c>: compact enough for a footer, precise where precision is visible.
    /// </summary>
    private static string FormatTokens(int count)
    {
        if (count < 1_000) return count.ToString();
        if (count < 10_000) return $"{count / 1_000.0:0.0}k";
        if (count < 1_000_000) return $"{Math.Round(count / 1_000.0)}k";
        if (count < 10_000_000) return $"{count / 1_000_000.0:0.0}M";
        return $"{Math.Round(count / 1_000_000.0)}M";
    }

    /// <summary>
    /// Renders the complete double-buffered frame into _back and emits minimal ANSI diff.
    /// </summary>
    private void Render()
    {
        _back.Clear();

        // 1. Header: identity and branch, no tab strip.
        RenderHeader();

        // 2. The conversation, preceded by the resource panel while it is open.
        RenderChatTab();


        // 3. The composer slot. A selector takes it over entirely — pi does not draw the prompt
        //    behind a picker, because the picker *is* the input.
        var selector = ActiveSelector;

        if (selector is not null)
        {
            selector.Render(
                _back,
                _width,
                Layout.SelectorBottom,
                ruleFg: "\x1b[38;2;60;65;95m",
                textFg: "\x1b[37m",
                dimFg: "\x1b[90m",
                accentFg: "\x1b[38;2;16;185;129m",
                selectedBg: "\x1b[48;2;22;36;45m");
        }
        else
        {
            RenderWorkingIndicator();
            RenderComposer();
        }

        // 4. Footer: location, then stats
        RenderFooterRibbon();

        // 5. Autocomplete Dropdowns for '/' and '@'
        if (_overlay == TermOverlay.None && !_showPalette)
        {
            if (_slashDropdownOpen)
                RenderSlashDropdown();
            else if (_fileDropdownOpen)
                RenderFileDropdown();
        }

        // 6. Floating Overlay (Command Palette)
        if (_showPalette)
            RenderFloatingPalette();

        // Compute cursor position for terminal caret
        int caretX = -1, caretY = -1;
        if (!_showPalette && _overlay == TermOverlay.None)
        {
            var promptY = ComposerRow;
            caretX = Math.Min(_width - 4, 5 + _inputCursor);
            caretY = promptY;
        }
        else if (_overlay == TermOverlay.Providers && _providerStage == ProviderStage.EnterKey)
        {
            // The composer is the key field, so the caret sits after the mask.
            caretX = Math.Min(_width - 4, 4 + _keyInputBuffer.Length);
            caretY = ComposerRow;
        }

        // Emit diff
        var ansi = _back.RenderDiff(_front, caretX, caretY);
        Console.Write(ansi);

        _front = _back.Clone();
    }

    /// <summary>
    /// One line of identity: mark, name, version, and the branch. No tab strip — pi's shape is a
    /// single conversation, and pickers appear over it when asked for.
    /// </summary>
    private void RenderHeader()
    {
        _back.Print(1, 0, "▲ ", fg: "\x1b[38;2;16;185;129m", bold: true);
        _back.Print(3, 0, "reptar agent ", fg: "\x1b[37m", bold: true);
        _back.Print(16, 0, "v0.2", fg: "\x1b[90m");

        if (_overlay != TermOverlay.None)
        {
            var title = _overlay switch
            {
                TermOverlay.Sessions => "sessions",
                TermOverlay.Settings => "settings",
                TermOverlay.Tree => "tree",
                TermOverlay.Providers => _providerStage switch
                {
                    ProviderStage.PickAuthMethod => "connect",
                    ProviderStage.PickProvider => "connect",
                    ProviderStage.EnterKey => "connect",
                    _ => "models"
                },
                _ => "menu"
            };

            var label = $"/ {title}";
            _back.Print(23, 0, label, fg: "\x1b[38;2;16;185;129m", bold: true);

            // Two spaces after the label: the two runs used to butt together.
            _back.Print(23 + label.Length + 2, 0, "esc to close", fg: "\x1b[90m");
        }

        var branch = ReadGitBranch(_options.WorkspaceRoot);
        if (!string.IsNullOrEmpty(branch))
        {
            var branchStr = $"git:{branch} ";
            _back.Print(_width - branchStr.Length - 1, 0, branchStr, fg: "\x1b[38;2;16;185;129m", dim: true);
        }

        _back.FillRect(0, 1, _width, 1, '─', fg: "\x1b[38;2;40;45;65m");
    }

    private void RenderChatTab()
    {
        var viewportTop = 2;

        // The transcript stops above whatever owns the bottom of the screen.
        var viewportBottom = Layout.TranscriptBottom;
        var availableHeight = viewportBottom - viewportTop;

        if (_messages.Count == 0 && !_isStreaming && _overlay == TermOverlay.None)
        {
            // Empty State Hero (Official Reptar emerald pixel dinosaur watermark + quick tips)
            var startY = Math.Max(viewportTop, (viewportTop + availableHeight - PixelDinoWatermark.Length - 4) / 2);

            // 1. Watermark in emerald green
            for (var i = 0; i < PixelDinoWatermark.Length && startY + i < viewportBottom; i++)
            {
                _back.PrintCentered(startY + i, PixelDinoWatermark[i], fg: "\x1b[38;2;16;185;129m", dim: true);
            }

            var textY = startY + PixelDinoWatermark.Length + 1;
            if (textY + 3 < viewportBottom)
            {
                _back.PrintCentered(textY, "What can I build for you today?", fg: "\x1b[37m", bold: true);
                _back.PrintCentered(textY + 1, "Reptar Agent runs in-process with direct workspace tools. Zero subprocess lag.", fg: "\x1b[90m");

                var tips = " [1] Inspect Architecture   [2] Verify Tools Self-Test   [3] Scan API Routes ";
                _back.PrintCentered(textY + 2, tips, fg: "\x1b[38;2;16;185;129m");
            }
            return;
        }

        // Build the full transcript as styled lines, then window it. Doing it this way
        // gives real scrollback instead of the previous "last six messages" cap.
        var lines = BuildTranscriptLines();

        var total = lines.Count;
        var viewHeight = Math.Max(1, availableHeight);
        var maxScroll = Math.Max(0, total - viewHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);

        var first = Math.Max(0, total - viewHeight - _scrollOffset);
        var y = viewportTop;

        for (var i = first; i < total && y < viewportBottom; i++)
            lines[i].Draw(_back, ref y, viewportBottom);

        if (_scrollOffset > 0 && viewportBottom - 1 >= viewportTop)
        {
            _back.Print(
                Math.Max(2, _width - 24), viewportBottom - 1,
                $"▲ {_scrollOffset} lines back (PgDn)",
                fg: "\x1b[90m");
        }
    }

    /// <summary>One rendered transcript line: text plus styling, deferred until placement.</summary>
    private readonly record struct TranscriptLine(string Text, string Fg, bool Bold, int Indent = 4)
    {
        public void Draw(TerminalBuffer buffer, ref int y, int limit)
        {
            if (y >= limit)
                return;
            buffer.Print(Indent, y++, Text, fg: Fg, bold: Bold);
        }
    }

    private List<TranscriptLine> BuildTranscriptLines()
    {
        const string textFg = "\x1b[37m";
        const string warnFg = "\x1b[38;2;251;191;36m";
        const string userFg = "\x1b[38;2;139;92;246m";
        const string agentFg = "\x1b[38;2;16;185;129m";
        const string dimFg = "\x1b[90m";
        const string toolFg = "\x1b[38;2;251;146;60m";
        const string errorFg = "\x1b[38;2;248;113;113m";

        var lines = new List<TranscriptLine>(_messages.Count * 4);
        var codeFence = false;

        // pi's startup panel sits above the transcript. Collapsed it is one line; ctrl+o expands
        // it to name every loaded resource and every rejection.
        if (_resources is { } resources)
        {
            if (_showResources)
            {
                foreach (var line in resources.Render(compact: false).Split('\n'))
                {
                    var isHeading = line.StartsWith('`') || line.StartsWith("**");
                    var isProblem = line.Contains("collision", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("shadowed", StringComparison.OrdinalIgnoreCase);

                    lines.Add(new TranscriptLine(
                        line,
                        isProblem ? warnFg : isHeading ? agentFg : dimFg,
                        isHeading,
                        2));
                }
            }
            else
            {
                var summary = resources.Skills.Count + " skills"
                    + (resources.ContextFiles.Count > 0
                        ? " · " + string.Join(", ", resources.ContextFileNames)
                        : "")
                    + (_resources!.HasProblems ? "  ⚠ problems — ctrl+o" : "  · ctrl+o");

                lines.Add(new TranscriptLine(summary, resources.HasProblems ? warnFg : dimFg, false, 2));
            }

            lines.Add(new TranscriptLine("", textFg, false, 0));
        }

        void Emit(string text, string fg = textFg, bool bold = false, int indent = 4)
        {
            foreach (var wrapped in WrapText(text, Math.Max(10, _width - indent - 2)))
                lines.Add(new TranscriptLine(wrapped, fg, bold, indent));
        }

        foreach (var message in _messages)
        {
            switch (message.Role)
            {
                case "user":
                    if (lines.Count > 0) lines.Add(new TranscriptLine("", textFg, false, 0));

                    // The user's turn is a bar down the whole block, not a one-line label. A
                    // header alone left the reply reading as a continuation of the prompt,
                    // because both were white text at the same indent.
                    foreach (var wrapped in WrapText(message.Content, Math.Max(10, _width - 8)))
                        lines.Add(new TranscriptLine("▌ " + wrapped, userFg, false, 2));

                    break;

                case "assistant":
                    if (message.Content.Length == 0) break;
                    if (lines.Count > 0) lines.Add(new TranscriptLine("", textFg, false, 0));

                    // A quiet marker so a reply is identifiable at a glance without a label on
                    // every block; the markdown below it carries the rest.
                    lines.Add(new TranscriptLine("◆", agentFg, false, 2));
                    RenderMarkdown(message.Content, codeFence, Emit, lines, textFg, dimFg);
                    break;

                // Tool payloads are shown through the cards below, so the raw result stays out
                // of the conversation flow.
                case "tool":
                    break;
            }
        }

        if (_toolCards.Count > 0)
        {
            lines.Add(new TranscriptLine("", textFg, false, 0));

            foreach (var card in _toolCards)
            {
                // pi's shape: a header naming the call, a bordered box with the output, then the
                // duration. The border colour is the success signal.
                var glyph = card.Status switch
                {
                    "ok" => "✓",
                    "failed" => "✗",
                    _ => "◐"
                };

                var border = card.Status == "failed" ? errorFg : agentFg;

                lines.Add(new TranscriptLine(
                    $"{glyph} {card.Name}  {card.Args}",
                    card.Status == "failed" ? errorFg : toolFg,
                    true,
                    2));

                var output = card.Output.ToString().TrimEnd();
                if (output.Length > 0)
                {
                    var body = output.Split('\n');
                    var shown = card.Status == "running"
                        ? body.TakeLast(4).ToArray()
                        : body.Take(8).ToArray();

                    var width = Math.Max(20, Math.Min(_width - 8, 110));

                    lines.Add(new TranscriptLine("┌" + new string('─', width - 2) + "┐", border, false, 4));

                    foreach (var bodyLine in shown)
                    {
                        var clipped = Clip(bodyLine, width - 4);
                        lines.Add(new TranscriptLine(
                            "│ " + clipped.PadRight(width - 4) + " │", border, false, 4));
                    }

                    if (body.Length > shown.Length)
                    {
                        var more = $"… {body.Length - shown.Length} more lines";
                        lines.Add(new TranscriptLine(
                            "│ " + more.PadRight(width - 4) + " │", dimFg, false, 4));
                    }

                    lines.Add(new TranscriptLine("└" + new string('─', width - 2) + "┘", border, false, 4));
                }

                if (card.Status != "running")
                {
                    var took = card.DurationMs >= 1000
                        ? $"{card.DurationMs / 1000.0:0.0}s"
                        : $"{card.DurationMs}ms";
                    lines.Add(new TranscriptLine($"Took {took}", dimFg, false, 4));
                }
            }
        }

        if (_isStreaming)
        {
            if (_thinkingText.Length > 0 && _options.ShowThinking)
            {
                lines.Add(new TranscriptLine("", textFg, false, 0));
                lines.Add(new TranscriptLine("◇ thinking", dimFg, false, 2));
                foreach (var thinkingLine in WrapText(_thinkingText, Math.Max(10, _width - 8)).TakeLast(3))
                    lines.Add(new TranscriptLine(thinkingLine, dimFg, false, 4));
            }

            if (_streamingText.Length > 0)
            {
                lines.Add(new TranscriptLine("", textFg, false, 0));
                RenderMarkdown(_streamingText, codeFence, Emit, lines, textFg, dimFg);
            }
        }

        return lines;
    }

    /// <summary>
    /// Minimal inline markdown: headings, bullets, blockquotes, inline code, bold, and
    /// fenced code blocks. Deliberately not a full parser — it only needs to make an
    /// agent transcript scannable.
    /// </summary>
    private static void RenderMarkdown(
        string text,
        bool startsInFence,
        Action<string, string, bool, int> emit,
        List<TranscriptLine> lines,
        string textFg,
        string dimFg)
    {
        const string codeFg = "\x1b[38;2;148;163;184m";
        const string headingFg = "\x1b[97m";

        var inFence = startsInFence;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                var language = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                lines.Add(new TranscriptLine(
                    language.Length > 0 ? $"┄┄ {language} ┄┄" : "┄┄┄┄",
                    dimFg, false, 4));
                continue;
            }

            if (inFence)
            {
                lines.Add(new TranscriptLine("  " + line, codeFg, false, 4));
                continue;
            }

            if (trimmed.Length == 0)
            {
                lines.Add(new TranscriptLine("", textFg, false, 0));
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var body = trimmed[level..].Trim();
                emit(body, level <= 2 ? headingFg : textFg, true, 4);
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                emit("• " + trimmed[2..], textFg, false, 6);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                emit("│ " + trimmed[1..].TrimStart(), dimFg, false, 6);
                continue;
            }

            emit(StripInlineMarkdown(line), textFg, false, 4);
        }
    }

    /// <summary>Strips the inline markers a terminal cannot style per-character.</summary>
    private static string StripInlineMarkdown(string line)
    {
        var builder = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                i++;
                continue;
            }
            if (line[i] == '`')
                continue;
            builder.Append(line[i]);
        }
        return builder.ToString();
    }

    private static string Clip(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";

    /// <summary>
    /// Rebuilds the model catalog from providers that have credentials.
    /// </summary>
    /// <remarks>
    /// pi only lists models for configured providers (<c>available = all.filter(configured)</c>).
    /// Offering a model you cannot call is worse than offering nothing: you pick it, get a 401,
    /// and have to work out that the provider was never connected.
    /// </remarks>
    private async Task RefreshCatalogAsync()
    {
        var items = new List<CatalogModelItem>();
        var categories = new List<string> { "All" };

        foreach (var provider in LlmProviders.Known)
        {
            var (source, _) = LlmProviders.CredentialSource(provider.Key, _auth);
            var usable = source != "none" || !provider.RequiresApiKey;

            if (!usable)
                continue;

            var category = provider.DisplayName.Split(' ')[0];
            if (!categories.Contains(category))
                categories.Add(category);

            List<LlmCatalog.CuratedModel> models;
            try
            {
                models = await LlmCatalog.ListCuratedAsync(provider.Key).ConfigureAwait(false);
            }
            catch
            {
                models = [];
            }

            if (models.Count == 0)
            {
                // Offline or unlisted: offer the provider's default so it is still selectable.
                items.Add(new CatalogModelItem(
                    provider.DefaultModel, provider.Key, provider.DisplayName, category,
                    "default", "default", [ThinkingLevel.Off], provider.DefaultModel));
                continue;
            }

            foreach (var model in models)
            {
                // A model picker that offers embedding, image-generation, and TTS models is
                // noise: picking one produces a confusing provider error rather than a reply.
                if (!IsChatModel(model.Id))
                    continue;

                var specs = new List<string>();
                if (model.ContextTokens > 0)
                    specs.Add($"{FormatTokens((int)Math.Min(int.MaxValue, model.ContextTokens))} ctx");
                if (model.Reasoning)
                    specs.Add("reasoning");
                if (model.Vision)
                    specs.Add("vision");

                items.Add(new CatalogModelItem(
                    model.Id,
                    provider.Key,
                    provider.DisplayName,
                    category,
                    specs.Count > 0 ? string.Join(" · ", specs) : "—",
                    model.Reasoning ? "reasoning" : "chat",
                    model.SupportedThinking,
                    model.Name));
            }
        }

        _catalog.Clear();
        _catalog.AddRange(items);

        _providerCategories = categories;
        if (_providerCategoryIdx >= _providerCategories.Count)
            _providerCategoryIdx = 0;

        // Selection is preserved by SetItems; only clamp when the old index is now out of range.
        if (_selectedCatalogIdx >= _catalog.Count)
            _selectedCatalogIdx = Math.Max(0, _catalog.Count - 1);

        RebuildModelSelector();
        RebuildProviderSelector();
    }

    /// <summary>
    /// Whether a model id names a chat model. Excludes the families that cannot answer a prompt:
    /// embeddings, image generation, speech, and the audio-only realtime models.
    /// </summary>
    private static bool IsChatModel(string modelId)
    {
        var id = modelId.ToLowerInvariant();

        string[] excluded =
        [
            "embedding", "embed-", "-embed", "rerank",
            "-image", "image-", "dall-e", "stable-diffusion", "imagen",
            "tts", "-audio", "audio-", "whisper", "transcribe",
            "realtime", "moderation", "clip", "-vision-preview"
        ];

        return !excluded.Any(token => id.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Pulls per-million pricing from models.dev so the status ribbon can show a running
    /// cost. Best-effort: a failed lookup simply hides the cost segment.
    /// </summary>
    private async Task RefreshPricingAsync()
    {
        try
        {
            var info = await LlmCatalog
                .GetModelInfoAsync(_client.Config.ProviderKey, _client.Config.Model)
                .ConfigureAwait(false);

            if (info is null)
                return;

            _inputCostPerMillion = info.InputCostPerMillion;
            _outputCostPerMillion = info.OutputCostPerMillion;
            _contextLimit = (int)Math.Min(int.MaxValue, info.ContextTokens);
            _dirty = true;
        }
        catch
        {
            // offline: the ribbon hides the cost segment
        }
    }

    private static string SummarizeArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "{}";

        var single = argumentsJson.Replace('\n', ' ');
        return single.Length > 64 ? single[..64] + "…" : single;
    }

    /// <summary>
    /// <c>/tools</c> prints the mounted tool list into the transcript. It is reference
    /// information, not a place you navigate to, so a table in the conversation beats a view.
    /// </summary>
    private void ShowToolsTable()
    {
        var tools = _app.GetAiTools(onlyExplicit: true);

        var builder = new System.Text.StringBuilder();
        builder.Append("**Mounted tools** (").Append(tools.Count).AppendLine(")");
        builder.AppendLine();
        builder.AppendLine("```");
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            builder.Append(tool.Name.PadRight(16)).Append("  ").AppendLine(FirstLine(tool.Description));
        }
        builder.AppendLine("```");

        _messages.Add(new ChatMessage("assistant", builder.ToString()));
        _dirty = true;
    }

    /// <summary>
    /// <c>/providers</c> prints connection status into the transcript: which provider each
    /// credential came from, and whether it is live. Same information the picker shows, without
    /// the mode switch.
    /// </summary>
    private void ShowProvidersTable()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("**Providers**");
        builder.AppendLine();
        builder.AppendLine("```");

        foreach (var provider in LlmProviders.Known)
        {
            var (source, detail) = LlmProviders.CredentialSource(provider.Key, _auth);
            var isActive = string.Equals(
                _client.Config.ProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase);

            var status = source switch
            {
                "env" => $"env: {detail}",
                "saved" => "saved",
                _ => provider.RequiresApiKey ? "needs key" : "local"
            };

            builder.Append((isActive ? "* " : "  "))
                .Append(provider.Key.PadRight(20))
                .Append(status);

            if (isActive)
                builder.Append("   <- active");

            builder.AppendLine();
        }

        builder.AppendLine("```");
        builder.AppendLine();
        builder.Append("Active model: `").Append(_client.Config.DisplayName).AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("`/model` opens the picker · `/connect` adds a key");

        _messages.Add(new ChatMessage("assistant", builder.ToString()));
        _dirty = true;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end > 0 ? text[..(end + 1)] : text;
        return sentence.Length <= 78 ? sentence : sentence[..78] + "…";
    }

    /// <summary>
    /// The model picker, rendered inline above the prompt the way pi does it: a list that grows
    /// upward out of the input line, not a floating dialog. Providers scope the list, so you pick
    /// a provider first and then a model within it.
    /// </summary>
    private void RenderProvidersOverlay()
    {
        var cardTop = ComposerRow;

        if (_providerStage == ProviderStage.EnterKey)
        {
            RenderInlineKeyEntry(cardTop);
            return;
        }

        var models = GetFilteredCatalog();

        if (_selectedCatalogIdx >= models.Count)
            _selectedCatalogIdx = Math.Max(0, models.Count - 1);

        // Build the body top-down, then draw it bottom-up so it hugs the prompt.
        var body = new List<(string Text, string Fg, bool Bold, bool Selected)>();

        // Scope row: the provider filter doubles as the scope selector.
        var scope = new System.Text.StringBuilder("  scope  ");
        for (var i = 0; i < _providerCategories.Count; i++)
        {
            var category = _providerCategories[i];
            var current = i == _providerCategoryIdx;
            scope.Append(current ? $"[{category}]" : $" {category} ").Append(' ');
        }
        body.Add((scope.ToString().TrimEnd(), "\x1b[38;2;148;163;184m", false, false));

        body.Add(("  " + new string('─', Math.Max(8, Math.Min(_width - 6, 74))), "\x1b[38;2;40;45;65m", false, false));

        if (models.Count == 0)
        {
            body.Add(("  no models match this scope", "\x1b[90m", false, false));
        }
        else
        {
            var maxRows = Math.Max(3, cardTop - 8);
            var first = Math.Max(0, Math.Min(_selectedCatalogIdx - maxRows + 1, models.Count - maxRows));

            for (var i = first; i < models.Count && body.Count < maxRows + 3; i++)
            {
                var model = models[i];
                var selected = i == _selectedCatalogIdx;
                var active = string.Equals(_client.Config.Model, model.ModelId, StringComparison.OrdinalIgnoreCase);

                var (source, detail) = LlmProviders.CredentialSource(model.ProviderKey, _auth);
                var status = source switch
                {
                    "env" => $"✓ {detail}",
                    "saved" => "✓ saved",
                    _ => model.ProviderKey == LlmProviders.Ollama ? "✓ local" : "○ needs key"
                };

                var pointer = selected ? "→ " : "  ";
                var name = model.ModelId;
                var nameWidth = Math.Min(34, Math.Max(18, _width / 3));
                var label = name.Length > nameWidth ? name[..(nameWidth - 1)] + "…" : name.PadRight(nameWidth);

                var text = $"  {pointer}{label}  {model.Specs,-22} {status}{(active ? "  ● active" : "")}";

                var fg = active ? "\x1b[38;2;16;185;129m" : selected ? "\x1b[97m" : "\x1b[90m";
                body.Add((text, fg, selected || active, selected));
            }

            if (models.Count > maxRows)
                body.Add(($"  ({_selectedCatalogIdx + 1}/{models.Count})", "\x1b[90m", false, false));
        }

        body.Add(("  ↑↓ select · tab scope · enter use · c set key · esc close", "\x1b[90m", false, false));

        // Bottom-anchored: the last line sits directly above the prompt card.
        var y = cardTop - 1;
        for (var i = body.Count - 1; i >= 0 && y >= 2; i--, y--)
        {
            var (text, fg, bold, selected) = body[i];
            if (selected)
                _back.FillRect(0, y, _width, 1, ' ', bg: "\x1b[48;2;22;36;45m");
            _back.Print(1, y, text, fg: fg, bold: bold, maxWidth: _width - 2);
        }

    }

    /// <summary>
    /// The API-key prompt, inline where the composer normally sits. The key is masked, and the
    /// portal hint sits underneath so you can open the provider's page without leaving the flow.
    /// </summary>
    /// <summary>
    /// The API-key prompt, inline where the composer normally sits. The key is masked, and the
    /// portal hint sits underneath so you can open the provider's page without leaving the flow.
    /// </summary>
    /// <summary>
    /// The provider list <c>/connect</c> opens with. Each row says whether it is already
    /// configured and where the credential came from, so you can see what you are changing.
    /// </summary>
    private void RenderProviderList(int cardTop)
    {
        var providers = ConnectableProviders();
        if (providers.Count == 0)
            return;

        _selectedProviderIdx = Math.Clamp(_selectedProviderIdx, 0, providers.Count - 1);

        var body = new List<(string Text, string Fg, bool Bold, bool Selected)>
        {
            ("  connect a provider", "\x1b[97m", true, false),
            ("  " + new string('─', Math.Max(8, Math.Min(_width - 6, 74))), "\x1b[38;2;40;45;65m", false, false)
        };

        var visible = Math.Max(4, cardTop - 9);
        var first = Math.Max(0, Math.Min(_selectedProviderIdx - visible + 1, providers.Count - visible));

        for (var i = first; i < providers.Count && i < first + visible; i++)
        {
            var provider = providers[i];
            var selected = i == _selectedProviderIdx;
            var (source, detail) = LlmProviders.CredentialSource(provider.Key, _auth);

            var status = source switch
            {
                "env" => $"✓ env: {detail}",
                "saved" => "✓ saved",
                _ => provider.RequiresApiKey ? "○ needs key" : "✓ no key needed"
            };

            var pointer = selected ? "→ " : "  ";
            var name = provider.DisplayName;
            var nameWidth = Math.Min(30, Math.Max(18, _width / 3));
            var label = name.Length > nameWidth ? name[..(nameWidth - 1)] + "…" : name.PadRight(nameWidth);

            var fg = source != "none" ? "\x1b[38;2;16;185;129m" : selected ? "\x1b[97m" : "\x1b[90m";
            body.Add(($"  {pointer}{label}  {status}", fg, selected, selected));
        }

        body.Add(("  ↑↓ select · enter connect · esc close", "\x1b[90m", false, false));

        var y = cardTop - 1;
        for (var i = body.Count - 1; i >= 0 && y >= 2; i--, y--)
        {
            var (text, fg, bold, selected) = body[i];
            if (selected)
                _back.FillRect(0, y, _width, 1, ' ', bg: "\x1b[48;2;22;36;45m");
            _back.Print(1, y, text, fg: fg, bold: bold, maxWidth: _width - 2);
        }
    }

    /// <summary>
    /// The API-key prompt, inline where the composer normally sits. pi swaps the editor for the
    /// login input; this does the same thing within a line-oriented buffer.
    /// </summary>
    private void RenderInlineKeyEntry(int cardTop)
    {
        var provider = LlmProviders.Find(_keyInputProvider) ?? LlmProviders.Known[0];

        // Guidance only: the field itself lives in the composer below, which the key prompt has
        // taken over. Anchored to the card so the two read as one block.
        var fields = PendingFields;
        var onField = _keyFieldIndex >= 0 && _keyFieldIndex < fields.Count;

        var lines = new List<(string Text, string Fg, bool Bold)>
        {
            ($"connect {provider.DisplayName}", "\x1b[97m", true)
        };

        if (fields.Count > 0)
        {
            var step = _keyFieldIndex + 2;   // key is step 1
            lines.Add(($"step {Math.Min(step, fields.Count + 1)}/{fields.Count + 1}: " +
                       (onField ? fields[_keyFieldIndex].Label : "api key"), "\x1b[90m", false));
        }

        if (!string.IsNullOrEmpty(provider.KeyUrl))
            lines.Add(($"o  open https://{provider.KeyUrl}", "\x1b[38;2;56;189;248m", false));
        else if (!provider.RequiresApiKey && fields.Count == 0)
            lines.Add(("this provider needs no key — enter to activate", "\x1b[90m", false));

        // Show the URL that will actually be used, with placeholders filled in.
        if (provider.HasUrlTemplate)
        {
            var preview = LlmProviders.ResolveBaseUrl(provider, null, null);
            var complete = !preview.Contains('{', StringComparison.Ordinal);
            lines.Add((complete ? preview : "url needs the fields above",
                complete ? "\x1b[90m" : "\x1b[38;2;251;191;36m", false));
        }

        if (_keyFieldRequired)
            lines.Add(("this value is required", "\x1b[38;2;248;113;113m", false));

        lines.Add(("enter next · esc back", "\x1b[90m", false));

        var y = cardTop - 1;
        for (var i = lines.Count - 1; i >= 0 && y >= 2; i--, y--)
        {
            var (text, fg, bold) = lines[i];
            _back.Print(2, y, text, fg: fg, bold: bold, maxWidth: _width - 4);
        }
    }

    /// <summary>
    /// The composer, as pi draws it: a bare <c>❯</c> line with no frame. The frame was a
    /// Codex-ism; pi's prompt reads as the last line of the transcript rather than a widget.
    /// </summary>
    private void RenderComposer()
    {
        var row = ComposerRow;
        _back.FillRect(0, row, _width, 1, ' ');

        var enteringKey = _overlay == TermOverlay.Providers && _providerStage == ProviderStage.EnterKey;

        _back.Print(2, row, "❯ ", fg: "\x1b[38;2;16;185;129m", bold: true);

        if (enteringKey)
        {
            // pi replaces the editor with the login input; here the composer becomes the key
            // field, so the prompt has an obvious place to go.
            var onField = _keyFieldIndex >= 0;
            var masked = onField
                ? _keyInputBuffer
                : new string('•', _keyInputBuffer.Length);

            if (masked.Length > 0)
                _back.Print(4, row, masked, fg: "\x1b[97m", maxWidth: _width - 8);
            else
                _back.Print(4, row,
                    onField ? CurrentKeyPrompt + " — input is visible" : "paste the api key — input is hidden",
                    fg: "\x1b[90m");
            return;
        }

        if (string.IsNullOrEmpty(_input))
        {
            var placeholder = _isStreaming
                ? "queued message will steer the run…"
                : "ask reptar anything  ·  / commands  ·  @ files  ·  ! shell";
            _back.Print(4, row, placeholder, fg: "\x1b[90m");
        }
        else
        {
            _back.Print(4, row, _input, fg: "\x1b[37m", maxWidth: _width - 6);
        }
    }

    /// <summary>
    /// Vertical geometry for this frame. Computed once per render so every band agrees about
    /// where the others are.
    /// </summary>
    private TermLayout Layout => new(_height, DropdownRows, ActiveSelector?.Height ?? 0);

    /// <summary>
    /// Rows the slash / file dropdown needs. It sits *between* the composer and the footer, so
    /// the composer moves up by exactly this much — otherwise the list lands on the footer,
    /// which is what made the input appear to cover the list.
    /// </summary>
    private int DropdownRows
    {
        get
        {
            if (_overlay != TermOverlay.None)
                return 0;

            // Cap the band at a third of the screen so a short terminal still shows the
            // transcript and the prompt.
            var cap = Math.Max(3, (_height - 6) / 3);

            if (_slashDropdownOpen)
            {
                // Always reserve at least the rule and one row, so "no matches" has somewhere
                // to be shown instead of the band collapsing and the list vanishing.
                var count = SlashMatches().Count;
                return Math.Min(Math.Max(count, 1), cap) + 2;
            }

            if (_fileDropdownOpen)
            {
                var count = GetMatchingFiles().Count;
                return Math.Min(Math.Max(count, 1), cap) + 2;
            }

            return 0;
        }
    }

    private int FooterStatsRow => Layout.FooterStats;
    private int FooterLocationRow => Layout.FooterLocation;
    private int ComposerRow => Layout.Composer;
    private int WorkingRow => Layout.Working;

    /// <summary>
    /// pi's working indicator: a spinner and a running timer on its own line above the prompt.
    /// Only while a run is in flight, so an idle screen stays quiet.
    /// </summary>
    private void RenderWorkingIndicator()
    {
        var row = WorkingRow;
        _back.FillRect(0, row, _width, 1, ' ');

        if (!_isStreaming)
            return;

        var frames = new[] { "✻", "✽", "✶", "✳", "✢" };
        var frame = frames[(int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 120 % frames.Length)];

        var elapsed = _runStartedAt == default
            ? ""
            : $" ({(DateTimeOffset.UtcNow - _runStartedAt).TotalSeconds:0}s)";

        _back.Print(2, row, $"{frame} Working{elapsed}", fg: "\x1b[38;2;16;185;129m");

        if (_statusNote.Length > 0)
            _back.Print(14, row, "· " + _statusNote, fg: "\x1b[90m", maxWidth: _width - 16);
    }

    /// <summary>
    /// The <c>/</c> dropdown, inline above the prompt. pi's row shape: the command name, its
    /// argument hint, then the description, with <c>→</c> marking the selection.
    /// </summary>
    /// <summary>
    /// The <c>/</c> list, drawn where pi draws it: under the prompt, after a rule. The prompt
    /// stays visible because you are still typing into it, unlike a selector which replaces it.
    /// </summary>
    private void RenderSlashDropdown()
    {
        if (!SlashListOpen)
            return;

        var commands = SlashMatches();

        const string dim = "\x1b[90m";
        const string selectedBg = "\x1b[48;2;20;45;35m";

        var visible = Math.Max(1, DropdownRows - 2);
        var nameWidth = commands.Count == 0 ? 12 : Math.Min(24, commands.Max(c => c.Name.Length));
        var first = Math.Max(0, Math.Min(_slashSelectedIdx - visible + 1, commands.Count - visible));

        // The block sits between the composer and the footer, in rows the layout reserved.
        var y = Layout.DropdownTop;

        // Clear the reserved band so nothing from the previous frame shows through.
        _back.FillRect(0, y, _width, DropdownRows, ' ');

        _back.Print(0, y++, new string('─', Math.Max(4, _width - 2)), fg: "\x1b[38;2;60;65;95m");

        for (var i = first; i < commands.Count && i < first + visible && y < _height - 2; i++, y++)
        {
            var command = commands[i];
            var selected = i == _slashSelectedIdx;

            if (selected)
                _back.FillRect(0, y, _width, 1, ' ', bg: selectedBg);

            _back.Print(2, y, selected ? "→ " : "  ", fg: "\x1b[38;2;16;185;129m", bg: selected ? selectedBg : "");
            _back.Print(4, y, command.Name,
                fg: selected ? "\x1b[97m" : "\x1b[37m", bg: selected ? selectedBg : "", bold: selected);

            var x = 4 + nameWidth + 2;

            if (command.Hint.Length > 0)
            {
                _back.Print(x, y, command.Hint, fg: dim, bg: selected ? selectedBg : "");
                x += command.Hint.Length + 2;
            }

            _back.Print(x, y, command.Desc,
                fg: selected ? "\x1b[37m" : dim,
                bg: selected ? selectedBg : "",
                maxWidth: Math.Max(8, _width - x - 2));
        }

        if (y < FooterLocationRow)
            _back.Print(2, y, $"({_slashSelectedIdx + 1}/{commands.Count})", fg: dim);
    }

    /// <summary>
    /// The <c>@</c> file dropdown, inline above the prompt, same shape as the command list.
    /// </summary>
    private void RenderFileDropdown()
    {
        var files = GetMatchingFiles();

        var visible = Math.Max(1, DropdownRows - 2);

        var first = Math.Max(0, Math.Min(_fileSelectedIdx - visible + 1, Math.Max(0, files.Count - visible)));
        var y = Layout.DropdownTop;

        _back.FillRect(0, y, _width, DropdownRows, ' ');

        _back.Print(0, y++, new string('─', Math.Max(4, _width - 2)), fg: "\x1b[38;2;60;65;95m");

        if (files.Count == 0)
        {
            _back.Print(2, y++, $"no file matches \"{_fileQuery}\"", fg: "\x1b[38;2;251;191;36m");
            _back.Print(2, y, "backspace to widen · esc to dismiss", fg: "\x1b[90m");
            return;
        }

        for (var i = first; i < files.Count && i < first + visible && y < FooterLocationRow; i++, y++)
        {
            var file = files[i];
            var selected = i == _fileSelectedIdx;
            var bg = selected ? "\x1b[48;2;20;45;35m" : "";

            if (selected)
                _back.FillRect(0, y, _width, 1, ' ', bg: bg);

            _back.Print(2, y, selected ? "→ " : "  ", fg: "\x1b[38;2;16;185;129m", bg: bg);
            _back.Print(4, y, "@" + file, fg: selected ? "\x1b[97m" : "\x1b[90m", bg: bg,
                bold: selected, maxWidth: _width - 6);
        }

        if (y < FooterLocationRow)
            _back.Print(2, y, $"({_fileSelectedIdx + 1}/{files.Count})", fg: "\x1b[90m");
    }

    /// <summary>
    /// pi's footer: two dim lines. The first is the working directory and branch; the second is
    /// token totals, cache hit rate, cost, context pressure on the left and the model on the right.
    /// </summary>
    private void RenderFooterRibbon()
    {
        const string dim = "\x1b[90m";

        var locationY = FooterLocationRow;
        var statsY = FooterStatsRow;

        _back.FillRect(0, locationY, _width, 2, ' ');

        _back.Print(2, locationY, _footerLocation.Length > 0 ? _footerLocation : "~", fg: dim, maxWidth: _width - 4);

        var left = new System.Text.StringBuilder();

        if (_totalTokensIn > 0) left.Append('↑').Append(FormatTokens(_totalTokensIn));
        if (_totalTokensOut > 0) left.Append(left.Length > 0 ? " " : "").Append('↓').Append(FormatTokens(_totalTokensOut));
        if (_totalCacheRead > 0) left.Append(left.Length > 0 ? " " : "").Append('R').Append(FormatTokens(_totalCacheRead));
        if (_totalCacheWrite > 0) left.Append(left.Length > 0 ? " " : "").Append('W').Append(FormatTokens(_totalCacheWrite));

        if (_lastCacheHitRate >= 0)
            left.Append(left.Length > 0 ? " " : "").Append("CH").Append(_lastCacheHitRate.ToString("0.0")).Append('%');

        if (_totalCost > 0)
            left.Append(left.Length > 0 ? " " : "").Append('$').Append(_totalCost.ToString("0.000"));

        // Context pressure: the number that decides whether the session survives.
        var used = ContextCompaction.EstimateContextTokens(_messages);
        var percent = _contextLimit > 0 ? used * 100.0 / _contextLimit : 0;

        var contextText = _contextLimit > 0
            ? $"{percent:0.0}%/{FormatTokens(_contextLimit)}{(_autoCompact ? " (auto)" : "")}"
            : $"?/{FormatTokens(used)}";

        var contextColor = percent > 90 ? "\x1b[38;2;248;113;113m"
            : percent > 70 ? "\x1b[38;2;251;191;36m"
            : dim;

        var right = _client.Config.Model;

        if (AvailableThinkingLevels().Count > 1)
        {
            var label = _thinking == ThinkingLevel.Off ? "thinking off" : ThinkingLevels.Name(_thinking);
            right += $" • {label}";
        }

        var rightWithProvider = $"({_client.Config.ProviderName}) {right}";

        var leftText = left.ToString();

        // Fit the right side into what is left: full form, then without the provider, then
        // truncated. A footer that wraps or overflows is worse than one that shortens a name.
        var available = Math.Max(8, _width - 2 - leftText.Length - 3);
        var rightText = rightWithProvider;

        if (rightText.Length > available)
            rightText = right;

        if (rightText.Length > available)
            rightText = rightText[..Math.Max(1, available - 1)] + "…";

        _back.FillRect(0, statsY, _width, 1, ' ');

        var contextX = 2 + (leftText.Length > 0 ? leftText.Length + 1 : 0);
        var rightX = Math.Max(contextX + contextText.Length + 2, _width - rightText.Length - 1);

        if (leftText.Length > 0)
            _back.Print(2, statsY, leftText, fg: dim);

        // The context column gets whatever space the right side does not need.
        var contextWidth = Math.Max(6, rightX - contextX - 1);
        _back.Print(contextX, statsY, contextText, fg: contextColor, maxWidth: contextWidth);

        if (rightX + rightText.Length <= _width)
            _back.Print(rightX, statsY, rightText, fg: dim, maxWidth: rightText.Length + 1);
    }

    private void RenderFloatingPalette()
    {
        var popW = Math.Min(68, _width - 6);
        var popH = 14;
        var popX = (_width - popW) / 2;
        var popY = Math.Max(2, (_height - popH) / 2);

        _back.DrawShadow(popX + 2, popY + 1, popW, popH);
        _back.DrawBox(popX, popY, popW, popH, "Command Palette (Ctrl+P / /)",
            borderFg: "\x1b[38;2;16;185;129m", titleFg: "\x1b[37m");

        // Search input
        _back.Print(popX + 2, popY + 1, "> " + (_paletteFilter.Length > 0 ? _paletteFilter : "Search commands..."),
            fg: _paletteFilter.Length > 0 ? "\x1b[37m" : "\x1b[90m");
        _back.FillRect(popX + 1, popY + 2, popW - 2, 1, '─', fg: "\x1b[38;2;45;50;75m");

        // Commands list
        var commands = GetPaletteCommands();
        var y = popY + 3;

        for (var i = 0; i < commands.Count && y < popY + popH - 1; i++, y++)
        {
            var c = commands[i];
            var selected = i == _paletteSelectedIdx;
            var ptr = selected ? "❯ " : "  ";
            var fg = selected ? "\x1b[37m" : "\x1b[90m";
            var bg = selected ? "\x1b[48;2;20;45;35m" : "";

            var line = $"{ptr}{c.Name,-14} {c.Desc}";
            _back.Print(popX + 2, y, line, fg: fg, bg: bg, bold: selected, maxWidth: popW - 4);
        }
    }

    private void EnterAltScreen()
    {
        if (_inAltScreen) return;
        Console.Write("\x1b[?1049h\x1b[?25l");
        _inAltScreen = true;
    }

    private void ExitAltScreen()
    {
        if (!_inAltScreen) return;
        Console.Write("\x1b[?25h\x1b[?1049l");
        _inAltScreen = false;
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFile))
            {
                var lines = File.ReadAllLines(_historyFile);
                _history.AddRange(lines.TakeLast(200));
            }
        }
        catch { }
    }

    private void AppendHistory(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyFile)!;
            Directory.CreateDirectory(dir);
            File.AppendAllLines(_historyFile, [line]);
        }
        catch { }
    }

    private static string ReadGitBranch(string root)
    {
        try
        {
            var head = Path.Combine(root, ".git", "HEAD");
            if (File.Exists(head))
            {
                var text = File.ReadAllText(head).Trim();
                if (text.StartsWith("ref: refs/heads/"))
                    return text["ref: refs/heads/".Length..];
                if (text.Length > 7)
                    return text[..7];
            }
        }
        catch { }
        return "";
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return result;

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (line.Length <= maxWidth)
            {
                result.Add(line);
                continue;
            }

            var words = line.Split(' ');
            var cur = new StringBuilder();
            foreach (var w in words)
            {
                if (cur.Length + w.Length + 1 > maxWidth)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(w);
            }
            if (cur.Length > 0)
                result.Add(cur.ToString());
        }
        return result;
    }

    public void Dispose()
    {
        ExitAltScreen();
        TerminalInput.Deactivate();
    }
}
#endif
