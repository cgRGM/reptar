#if !NETWASM
using System.Diagnostics;
using System.Text;

namespace Reptar.AI;

/// <summary>
/// Active tab in the ReptarTerm workspace.
/// </summary>
public enum TermTab
{
    Chat = 0,
    Sessions = 1,
    Tools = 2,
    Providers = 3
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
    private readonly TerminalAgentOptions _options;
    private readonly AgentSessionStore? _store;
    private readonly LlmAuthFile _auth = LlmAuthFile.LoadActive();
    private readonly string _workspaceName;
    private readonly List<string> _workspaceFiles = [];

    private TerminalBuffer _back;
    private TerminalBuffer? _front;

    private int _width;
    private int _height;
    private bool _running = true;
    private bool _inAltScreen;

    private TermTab _activeTab = TermTab.Chat;
    private string _input = "";
    private int _inputCursor = 0;
    private int _historyIndex = -1;
    private readonly List<string> _history = [];
    private readonly string _historyFile;

    private ILlmClient _client;
    private AgentSessionRow? _session;
    private readonly List<ChatMessage> _messages = [];
    private readonly List<AgentSessionRow> _sessionsList = [];
    private int _selectedSessionIdx = 0;
    private int _selectedToolIdx = 0;

    // Execution / Streaming state
    private bool _isStreaming = false;
    private string _streamingDelta = "";
    private readonly List<string> _activePlanSteps = [];
    private readonly List<(string Name, string Args, string Status, int Ms)> _activeTools = [];

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
    private static readonly string[] ProviderCategories = ["All", "Google", "Anthropic", "OpenAI", "OpenRouter", "Local"];
    private int _providerCategoryIdx = 0;
    private string _providerFilterText = "";
    private int _selectedCatalogIdx = 0;

    // Fast inline credential input (no modal popups)
    private bool _keyInputActive = false;
    private string _keyInputProvider = "";
    private string? _keyInputModel = null;
    private string _keyInputBuffer = "";

    public sealed record CatalogModelItem(
        string ModelId,
        string ProviderKey,
        string ProviderDisplayName,
        string Category,
        string Specs,
        string Tag);

    private static readonly CatalogModelItem[] CuratedCatalog =
    [
        new("gemini-2.5-flash", LlmProviders.Gemini, "Google Gemini", "Google", "1M context · 65K output · Fast · Multimodal", "fast"),
        new("gemini-2.5-pro", LlmProviders.Gemini, "Google Gemini", "Google", "2M context · 65K output · Deep Reasoning · Code", "reasoning"),
        new("gemini-2.0-flash", LlmProviders.Gemini, "Google Gemini", "Google", "1M context · 65K output · Next-Gen Workhorse", "fast"),
        new("claude-sonnet-4-5", LlmProviders.Anthropic, "Anthropic", "Anthropic", "200K context · 128K output · Flagship Coding", "flagship"),
        new("claude-3-7-sonnet", LlmProviders.Anthropic, "Anthropic", "Anthropic", "200K context · 128K output · Hybrid Reasoning", "flagship"),
        new("claude-3-5-haiku", LlmProviders.Anthropic, "Anthropic", "Anthropic", "200K context · 8K output · Lightning Fast", "fast"),
        new("claude-opus-5-5", LlmProviders.Anthropic, "Anthropic", "Anthropic", "200K context · 128K output · Deep Nuance", "flagship"),
        new("gpt-4o", LlmProviders.OpenAi, "OpenAI", "OpenAI", "128K context · 16K output · Flagship Multimodal", "flagship"),
        new("gpt-4o-mini", LlmProviders.OpenAi, "OpenAI", "OpenAI", "128K context · 16K output · Fast · Low Cost", "fast"),
        new("o3-mini", LlmProviders.OpenAi, "OpenAI", "OpenAI", "200K context · 100K output · STEM & Code Reasoning", "reasoning"),
        new("gpt-4.5-preview", LlmProviders.OpenAi, "OpenAI", "OpenAI", "128K context · 16K output · Creative Synthesis", "flagship"),
        new("deepseek/deepseek-chat", LlmProviders.OpenRouter, "OpenRouter", "OpenRouter", "64K context · 8K output · DeepSeek V3 671B MoE", "open-weights"),
        new("deepseek/deepseek-r1", LlmProviders.OpenRouter, "OpenRouter", "OpenRouter", "64K context · 8K output · DeepSeek R1 Reasoning", "reasoning"),
        new("meta-llama/llama-3.3-70b-instruct", LlmProviders.OpenRouter, "OpenRouter", "OpenRouter", "128K context · 8K output · Open Weights", "open"),
        new("qwen/qwen-2.5-coder-32b-instruct", LlmProviders.OpenRouter, "OpenRouter", "OpenRouter", "128K context · 8K output · Code MoE", "coding"),
        new("llama3.2", LlmProviders.Ollama, "Ollama (local)", "Local", "128K context · 4K output · Local Offline 3B", "local"),
        new("qwen2.5-coder:7b", LlmProviders.Ollama, "Ollama (local)", "Local", "128K context · 8K output · Offline Coding", "local"),
        new("deepseek-r1:8b", LlmProviders.Ollama, "Ollama (local)", "Local", "128K context · 8K output · Local Distilled Reasoning", "local"),
    ];

    // Telemetry
    private int _totalTokensIn = 0;
    private int _totalTokensOut = 0;
    private double _totalCost = 0.0;
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

    private record PaletteCommand(string Id, string Name, string Desc, Action Execute);

    public ReptarTerm(ReptarApp app, TerminalAgentOptions options, AgentSessionStore? store)
    {
        _app = app;
        _options = options;
        _store = store;
        _workspaceName = Path.GetFileName(Path.GetFullPath(options.WorkspaceRoot));

        _width = Math.Max(40, Console.WindowWidth);
        _height = Math.Max(12, Console.WindowHeight);
        _back = new TerminalBuffer(_width, _height);

        _historyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".reptar", "agent", "history");

        _client = LlmClientFactory.Create(LlmConfig.FromEnvironment(
            options.Provider, options.Model, options.BaseUrl));

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
        var category = ProviderCategories[_providerCategoryIdx];
        IEnumerable<CatalogModelItem> list = CuratedCatalog;

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
                    _messages.AddRange(await _store.LoadMessagesAsync(found.Id).ConfigureAwait(false));
                }
            }
            if (_session is null)
            {
                _session = await _store.CreateSessionAsync("New chat").ConfigureAwait(false);
            }
            await RefreshSessionsListAsync().ConfigureAwait(false);
        }

        UpdateStatusRibbon();
        Render();

        while (_running && !cancellationToken.IsCancellationRequested)
        {
            CheckResize();

            var keyRead = TerminalInput.ReadKey();
            if (keyRead is null)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var key = keyRead.Value;

            if (key.Ctrl && key.Key == ConsoleKey.C)
            {
                if (_isStreaming)
                {
                    _isStreaming = false;
                }
                else
                {
                    _running = false;
                    break;
                }
            }
            else if (key.Ctrl && key.Key == ConsoleKey.P)
            {
                _showPalette = !_showPalette;
                _paletteFilter = "";
                _paletteSelectedIdx = 0;
            }
            else if (_showPalette)
            {
                HandlePaletteKey(key);
            }
            else
            {
                await HandleMainKeyAsync(key).ConfigureAwait(false);
            }

            Render();
        }

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
            new("sessions", "/sessions", "View and switch saved sessions", () => { _activeTab = TermTab.Sessions; _input = ""; _inputCursor = 0; }),
            new("tools", "/tools", "Inspect mounted workspace tools", () => { _activeTab = TermTab.Tools; _input = ""; _inputCursor = 0; }),
            new("providers", "/providers", "Inspect provider connectivity & models", () => { OpenModelCatalog(); }),
            new("help", "/help", "Show tips and shortcuts", () => { ShowHelpMessage(); }),
            new("exit", "/exit", "Quit ReptarTerm", () => { _running = false; }),
        };

        var filter = _paletteFilter?.Trim().TrimStart('/') ?? "";
        if (string.IsNullOrWhiteSpace(filter))
            return all;

        return all.Where(c => c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              c.Desc.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ShowHelpMessage()
    {
        _messages.Add(new ChatMessage("assistant",
            "**Reptar Agent Keyboard Shortcuts & Commands:**\n\n" +
            "• `Tab` — Switch workspace views (Chat, Sessions, Tools, Providers) or autocomplete\n" +
            "• `Esc` / `q` — Return to Chat from any tab or cancel active prompt\n" +
            "• `/model` — Open model catalog to browse and switch models across providers\n" +
            "• `/connect` — Connect provider API key (frictionless inline setup with 'o' browser portal)\n" +
            "• `/` — Opens inline command palette (`/model`, `/connect`, `/clear`, `/new`, `/tools`, `/providers`)\n" +
            "• `@` — Mentions files from workspace to attach directly as prompt context\n" +
            "• `Ctrl+P` — Opens global floating command palette with search\n" +
            "• `Ctrl+C` — Cancel current streaming turn (or exit on second press)\n" +
            "• `↑ / ↓` — Recall previous prompt history"));
    }

    private void OpenModelCatalog()
    {
        _activeTab = TermTab.Providers;
        _keyInputActive = false;
        _providerFilterText = "";
        _input = "";
        _inputCursor = 0;
    }

    private void OpenConnectInline(string? providerKey = null, string? targetModel = null)
    {
        _activeTab = TermTab.Providers;
        _keyInputActive = true;
        _keyInputProvider = providerKey ?? _client.Config.ProviderKey;
        _keyInputModel = targetModel ?? _client.Config.Model;
        _keyInputBuffer = "";
        _input = "";
        _inputCursor = 0;
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
        if (_activeTab == TermTab.Providers && _keyInputActive)
        {
            HandleProvidersKey(key);
            return;
        }

        // 1. Escape returns to Chat from any non-Chat tab, or closes open dropdowns
        if (key.Key == ConsoleKey.Escape)
        {
            if (_slashDropdownOpen || _fileDropdownOpen)
            {
                _slashDropdownOpen = false;
                _fileDropdownOpen = false;
                return;
            }

            if (_activeTab != TermTab.Chat)
            {
                _activeTab = TermTab.Chat;
                _input = "";
                _inputCursor = 0;
                return;
            }
        }

        // 2. 'q' or 'Q' returns to Chat from any non-Chat tab
        if (_activeTab != TermTab.Chat && key.Ch is 'q' or 'Q')
        {
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
            return;
        }

        // 3. When slash dropdown or file dropdown is open, route keys to autocomplete
        if (_activeTab == TermTab.Chat && (_slashDropdownOpen || _fileDropdownOpen))
        {
            if (HandleDropdownKey(key))
                return;
        }

        // 4. Tab key handling
        if (key.Key == ConsoleKey.Tab)
        {
            if (_activeTab == TermTab.Chat && _input.Length > 0)
            {
                // Tab triggers autocomplete trigger check if not already open
                CheckInputTriggers();
                return;
            }

            if (_activeTab == TermTab.Providers)
            {
                // In Providers tab, Tab cycles the category tabs (fx style)
                _providerCategoryIdx = (_providerCategoryIdx + 1) % ProviderCategories.Length;
                _selectedCatalogIdx = 0;
                return;
            }

            // Cycle workspace tabs: Chat -> Sessions -> Tools -> Providers -> Chat
            _activeTab = (TermTab)(((int)_activeTab + 1) % 4);
            _input = "";
            _inputCursor = 0;
            return;
        }

        // 5. Quick numbers switch tab when on non-Chat tab OR when Chat prompt is empty
        if ((_activeTab != TermTab.Chat || _input.Length == 0) && key.Ch is >= '1' and <= '4')
        {
            _activeTab = (TermTab)(key.Ch - '1');
            _input = "";
            _inputCursor = 0;
            return;
        }

        switch (_activeTab)
        {
            case TermTab.Chat:
                await HandleChatKeyAsync(key).ConfigureAwait(false);
                break;
            case TermTab.Sessions:
                await HandleSessionsKeyAsync(key).ConfigureAwait(false);
                break;
            case TermTab.Tools:
                HandleToolsKey(key);
                break;
            case TermTab.Providers:
                HandleProvidersKey(key);
                break;
        }
    }

    private bool HandleDropdownKey(TermKey key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _slashDropdownOpen = false;
            _fileDropdownOpen = false;
            return true;
        }

        if (_slashDropdownOpen)
        {
            var commands = GetPaletteCommands();
            if (key.Key == ConsoleKey.UpArrow && commands.Count > 0)
            {
                _slashSelectedIdx = (_slashSelectedIdx - 1 + commands.Count) % commands.Count;
                return true;
            }
            if (key.Key == ConsoleKey.DownArrow && commands.Count > 0)
            {
                _slashSelectedIdx = (_slashSelectedIdx + 1) % commands.Count;
                return true;
            }
            if (key.Key is ConsoleKey.Tab or ConsoleKey.Enter && commands.Count > 0)
            {
                var chosen = commands[_slashSelectedIdx];
                _slashDropdownOpen = false;
                _input = "";
                _inputCursor = 0;
                chosen.Execute();
                return true;
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
            var text = _input.Trim();
            if (text.Length == 0)
                return;

            _history.Add(text);
            AppendHistory(text);
            _historyIndex = -1;
            _input = "";
            _inputCursor = 0;
            _slashDropdownOpen = false;
            _fileDropdownOpen = false;

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
                    _messages.Clear();
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
                    _activeTab = TermTab.Sessions;
                }
                else if (cmdPart is "/tools")
                {
                    _activeTab = TermTab.Tools;
                }
                else if (cmdPart is "/providers")
                {
                    _activeTab = TermTab.Providers;
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

            await SendChatMessageAsync(text).ConfigureAwait(false);
            return;
        }

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
        // Check for slash command trigger at the beginning of prompt
        if (_input.StartsWith('/'))
        {
            _slashDropdownOpen = true;
            _fileDropdownOpen = false;
            _paletteFilter = _input.TrimStart('/');
            _slashSelectedIdx = 0;
            return;
        }
        else
        {
            _slashDropdownOpen = false;
        }

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

    private async Task HandleSessionsKeyAsync(TermKey key)
    {
        if (key.Key == ConsoleKey.Escape || key.Ch is 'q' or 'Q')
        {
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
            return;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            _activeTab = TermTab.Tools;
            _input = "";
            _inputCursor = 0;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow && _sessionsList.Count > 0)
        {
            _selectedSessionIdx = (_selectedSessionIdx - 1 + _sessionsList.Count) % _sessionsList.Count;
        }
        else if (key.Key == ConsoleKey.DownArrow && _sessionsList.Count > 0)
        {
            _selectedSessionIdx = (_selectedSessionIdx + 1) % _sessionsList.Count;
        }
        else if (key.Key == ConsoleKey.Enter && _sessionsList.Count > 0 && _store is not null)
        {
            var target = _sessionsList[_selectedSessionIdx];
            _session = target;
            _messages.Clear();
            _messages.AddRange(await _store.LoadMessagesAsync(target.Id).ConfigureAwait(false));
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
        }
        else if (key.Ch is 'n' or 'N')
        {
            await StartNewSessionAsync().ConfigureAwait(false);
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
        }
        else if (key.Ch is 'd' or 'D' && _sessionsList.Count > 0 && _store is not null)
        {
            var target = _sessionsList[_selectedSessionIdx];
            await _store.DeleteSessionAsync(target.Id).ConfigureAwait(false);
            await RefreshSessionsListAsync().ConfigureAwait(false);
            if (_session?.Id == target.Id)
                await StartNewSessionAsync().ConfigureAwait(false);
        }
    }

    private void HandleToolsKey(TermKey key)
    {
        if (key.Key == ConsoleKey.Escape || key.Ch is 'q' or 'Q')
        {
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
            return;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            _activeTab = TermTab.Providers;
            _input = "";
            _inputCursor = 0;
            return;
        }

        var tools = _app.GetAiTools(onlyExplicit: true);
        if (key.Key == ConsoleKey.UpArrow && tools.Count > 0)
            _selectedToolIdx = (_selectedToolIdx - 1 + tools.Count) % tools.Count;
        else if (key.Key == ConsoleKey.DownArrow && tools.Count > 0)
            _selectedToolIdx = (_selectedToolIdx + 1) % tools.Count;
    }

    private void HandleProvidersKey(TermKey key)
    {
        var models = GetFilteredCatalog();
        if (_selectedCatalogIdx >= models.Count)
            _selectedCatalogIdx = Math.Max(0, models.Count - 1);

        var currentModel = models.Count > 0 ? models[_selectedCatalogIdx] : null;

        // 1. If inline key input is active
        if (_keyInputActive)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _keyInputActive = false;
                _keyInputBuffer = "";
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var p = LlmProviders.Find(_keyInputProvider) ?? LlmProviders.Known[0];
                var modelToSet = _keyInputModel ?? currentModel?.ModelId ?? p.DefaultModel;

                if (!string.IsNullOrWhiteSpace(_keyInputBuffer))
                {
                    _auth.Set(p.Key, new ProviderCredential(
                        ApiKey: _keyInputBuffer.Trim(),
                        BaseUrl: p.DefaultBaseUrl,
                        DefaultModel: modelToSet
                    ));
                }

                _client = LlmClientFactory.Create(LlmConfig.FromEnvironment(p.Key, modelToSet));
                _auth.SetDefault(p.Key);
                UpdateStatusRibbon();
                _keyInputActive = false;
                _keyInputBuffer = "";
                _activeTab = TermTab.Chat;
                _messages.Add(new ChatMessage("assistant", $"Connected to **{p.DisplayName}** using model `{modelToSet}`."));
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

            if ((key.Ch is 'o' or 'O') && _keyInputBuffer.Length == 0)
            {
                var p = LlmProviders.Find(_keyInputProvider);
                var url = p?.KeyUrl ?? (_keyInputProvider == LlmProviders.Ollama ? "ollama.com/download" : null);
                if (url is not null)
                    OpenBrowser(url);
                return;
            }

            if (key.Ch is not '\0' && !char.IsControl(key.Ch))
            {
                _keyInputBuffer += key.Ch;
            }
            return;
        }

        // 2. Normal catalog browsing
        if (key.Key == ConsoleKey.Escape || key.Ch is 'q' or 'Q')
        {
            _activeTab = TermTab.Chat;
            _input = "";
            _inputCursor = 0;
            return;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            _providerCategoryIdx = (_providerCategoryIdx + 1) % ProviderCategories.Length;
            _selectedCatalogIdx = 0;
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            _providerCategoryIdx = (_providerCategoryIdx - 1 + ProviderCategories.Length) % ProviderCategories.Length;
            _selectedCatalogIdx = 0;
            return;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            _providerCategoryIdx = (_providerCategoryIdx + 1) % ProviderCategories.Length;
            _selectedCatalogIdx = 0;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow && models.Count > 0)
        {
            _selectedCatalogIdx = (_selectedCatalogIdx - 1 + models.Count) % models.Count;
            return;
        }

        if (key.Key == ConsoleKey.DownArrow && models.Count > 0)
        {
            _selectedCatalogIdx = (_selectedCatalogIdx + 1) % models.Count;
            return;
        }

        if (key.Ch is 'o' or 'O' && currentModel is not null)
        {
            var p = LlmProviders.Find(currentModel.ProviderKey);
            var url = p?.KeyUrl ?? (currentModel.ProviderKey == LlmProviders.Ollama ? "ollama.com/download" : null);
            if (url is not null)
                OpenBrowser(url);

            _keyInputActive = true;
            _keyInputProvider = currentModel.ProviderKey;
            _keyInputModel = currentModel.ModelId;
            _keyInputBuffer = "";
            return;
        }

        if (key.Ch is 'c' or 'C' && currentModel is not null)
        {
            _keyInputActive = true;
            _keyInputProvider = currentModel.ProviderKey;
            _keyInputModel = currentModel.ModelId;
            _keyInputBuffer = "";
            return;
        }

        if (key.Key == ConsoleKey.Enter && currentModel is not null)
        {
            var (source, _) = LlmProviders.CredentialSource(currentModel.ProviderKey, _auth);
            var isLocal = currentModel.ProviderKey == LlmProviders.Ollama;
            var isConfigured = source != "none" || isLocal;

            if (isConfigured)
            {
                _client = LlmClientFactory.Create(LlmConfig.FromEnvironment(currentModel.ProviderKey, currentModel.ModelId));
                _auth.SetDefault(currentModel.ProviderKey);
                UpdateStatusRibbon();
                _messages.Add(new ChatMessage("assistant", $"Switched active model to **{currentModel.ModelId}** ({currentModel.ProviderDisplayName})."));
                _activeTab = TermTab.Chat;
            }
            else
            {
                _keyInputActive = true;
                _keyInputProvider = currentModel.ProviderKey;
                _keyInputModel = currentModel.ModelId;
                _keyInputBuffer = "";
            }
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_providerFilterText.Length > 0)
            {
                _providerFilterText = _providerFilterText[..^1];
                _selectedCatalogIdx = 0;
            }
            return;
        }

        if (key.Ch is not '\0' && !char.IsControl(key.Ch))
        {
            _providerFilterText += key.Ch;
            _selectedCatalogIdx = 0;
        }
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

        var userMessage = new ChatMessage("user", prompt);
        _messages.Add(userMessage);

        if (_store is not null && _session is not null)
            await _store.AppendMessageAsync(_session.Id, userMessage).ConfigureAwait(false);

        _isStreaming = true;
        _streamingDelta = "";
        _activePlanSteps.Clear();
        _activeTools.Clear();
        Render();

        var assistantText = new StringBuilder();

        try
        {
            var summary = await StreamingAgentHarness.RunAsync(
                _app,
                _client,
                _messages,
                systemPrompt: "You are Reptar Agent, an autonomous coding pair programmer in-process with access to workspace tools.",
                maxSteps: _options.MaxSteps,
                onEvent: evt =>
                {
                    switch (evt)
                    {
                        case AgentEvent.TurnStarted turn:
                            _activePlanSteps.Add($"Step {turn.Step}");
                            Render();
                            break;

                        case AgentEvent.TextDelta delta:
                            assistantText.Append(delta.Text);
                            _streamingDelta = assistantText.ToString();
                            Render();
                            break;

                        case AgentEvent.ToolCallStarted call:
                            _activePlanSteps.Add($"Executing {call.Name}...");
                            _activeTools.Add((call.Name, call.ArgumentsJson, "running", 0));
                            Render();
                            break;

                        case AgentEvent.ToolCallFinished call:
                            if (_activeTools.Count > 0)
                            {
                                var last = _activeTools[^1];
                                _activeTools[^1] = (last.Name, last.Args, call.Success ? "ok" : "bad", (int)call.ElapsedMs);
                            }
                            Render();
                            break;

                        case AgentEvent.UsageReported usage:
                            _totalTokensIn += usage.InputTokens;
                            _totalTokensOut += usage.OutputTokens;
                            break;
                    }
                }
            ).ConfigureAwait(false);

            if (summary.StopReason == AgentStopReason.Error)
            {
                var errMsg = new ChatMessage("assistant",
                    $"⚠️ **Provider Error ({_client.Config.Model}):**\n\n{summary.FinalContent}\n\n*Tip: Run `/connect` to set API credentials or `/model` to switch providers.*");
                _messages.Add(errMsg);
                if (_store is not null && _session is not null)
                    await _store.AppendMessageAsync(_session.Id, errMsg).ConfigureAwait(false);
            }
            else if (_store is not null && _session is not null)
            {
                var last = _messages.LastOrDefault(m => m.Role == "assistant");
                if (last is not null)
                    await _store.AppendMessageAsync(_session.Id, last).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var err = new ChatMessage("assistant", $"⚠️ **Error:** {ex.Message}");
            _messages.Add(err);
            if (_store is not null && _session is not null)
                await _store.AppendMessageAsync(_session.Id, err).ConfigureAwait(false);
        }
        finally
        {
            _isStreaming = false;
            _streamingDelta = "";
            UpdateStatusRibbon();
            Render();
        }
    }

    private async Task StartNewSessionAsync()
    {
        if (_store is null) return;
        _session = await _store.CreateSessionAsync("New chat").ConfigureAwait(false);
        _messages.Clear();
        _input = "";
        await RefreshSessionsListAsync().ConfigureAwait(false);
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
            }
        }
        catch { }
    }

    private void UpdateStatusRibbon()
    {
        var branch = ReadGitBranch(_options.WorkspaceRoot);
        var branchPart = string.IsNullOrEmpty(branch) ? "" : $" ({branch})";
        var modelName = _client.Config.Model;
        var costPart = _totalCost > 0 ? $" · ${_totalCost:F3}" : "";
        _statusRibbon = $"~/{_workspaceName}{branchPart} · {modelName}{costPart} · Tab: switch view / autocomplete · /: commands · @: files";
    }

    /// <summary>
    /// Renders the complete double-buffered frame into _back and emits minimal ANSI diff.
    /// </summary>
    private void Render()
    {
        _back.Clear();

        // 1. Top Header & Tab Strip (GitHub Copilot style with Reptar emerald mark)
        RenderHeaderTabs();

        // 2. Active Tab Body
        switch (_activeTab)
        {
            case TermTab.Chat:
                RenderChatTab();
                break;
            case TermTab.Sessions:
                RenderSessionsTab();
                break;
            case TermTab.Tools:
                RenderToolsTab();
                break;
            case TermTab.Providers:
                RenderProvidersTab();
                break;
        }

        // 3. Docked Bottom Input Card (OpenAI Codex style)
        if (_activeTab == TermTab.Chat)
            RenderDockedInputCard();

        // 4. Status Ribbon (Pi style)
        RenderFooterRibbon();

        // 5. Autocomplete Dropdowns for '/' and '@'
        if (_activeTab == TermTab.Chat && !_showPalette)
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
        if (!_showPalette && _activeTab == TermTab.Chat)
        {
            var promptY = _height - 3;
            caretX = Math.Min(_width - 4, 5 + _inputCursor);
            caretY = promptY;
        }
        else if (_activeTab == TermTab.Providers && _keyInputActive)
        {
            var boxY = _height - 6;
            var keyPromptLen = "❯ Paste API Key: ".Length;
            caretX = Math.Min(_width - 4, 3 + 2 + keyPromptLen + _keyInputBuffer.Length);
            caretY = boxY + 1;
        }

        // Emit diff
        var ansi = _back.RenderDiff(_front, caretX, caretY);
        Console.Write(ansi);

        _front = _back.Clone();
    }

    private void RenderHeaderTabs()
    {
        // Line 0: Tab strip matching website header (apps/web/src/components/header.astro)
        _back.Print(1, 0, "▲ ", fg: "\x1b[38;2;16;185;129m", bold: true);
        _back.Print(3, 0, "reptar agent ", fg: "\x1b[37m", bold: true);
        _back.Print(16, 0, "v0.2", fg: "\x1b[90m");

        var tabs = new (string Label, TermTab Tab)[]
        {
            ("1 Chat", TermTab.Chat),
            ("2 Sessions", TermTab.Sessions),
            ("3 Tools", TermTab.Tools),
            ("4 Providers", TermTab.Providers)
        };

        var x = 23;
        foreach (var (lbl, tab) in tabs)
        {
            var active = _activeTab == tab;
            if (active)
            {
                _back.Print(x, 0, $"[ {lbl} ]", fg: "\x1b[37m", bg: "\x1b[48;2;16;185;129m", bold: true);
                x += lbl.Length + 5;
            }
            else
            {
                _back.Print(x, 0, $"  {lbl}  ", fg: "\x1b[90m");
                x += lbl.Length + 4;
            }
        }

        // Right side: branch & tools
        var branch = ReadGitBranch(_options.WorkspaceRoot);
        if (!string.IsNullOrEmpty(branch))
        {
            var branchStr = $"git:{branch} ";
            _back.Print(_width - branchStr.Length - 1, 0, branchStr, fg: "\x1b[38;2;16;185;129m", dim: true);
        }

        // Horizontal divider line
        _back.FillRect(0, 1, _width, 1, '─', fg: "\x1b[38;2;40;45;65m");
    }

    private void RenderChatTab()
    {
        var viewportTop = 2;
        var viewportBottom = _height - 5;
        var availableHeight = viewportBottom - viewportTop;

        if (_messages.Count == 0 && !_isStreaming)
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

        // Render message turns (ignoring background system prompts)
        var chatMessages = _messages.Where(m => m.Role != "system").ToList();
        var y = viewportTop;
        var startMsgIdx = Math.Max(0, chatMessages.Count - 6);

        for (var i = startMsgIdx; i < chatMessages.Count && y < viewportBottom; i++)
        {
            var msg = chatMessages[i];
            var isUser = msg.Role == "user";
            var prefix = isUser ? "❯ User: " : "⚡ Reptar: ";
            var prefixColor = isUser ? "\x1b[38;2;139;92;246m" : "\x1b[38;2;16;185;129m";

            _back.Print(2, y++, prefix, fg: prefixColor, bold: true);

            var lines = WrapText(msg.Content, _width - 6);
            foreach (var l in lines)
            {
                if (y >= viewportBottom) break;
                _back.Print(4, y++, l, fg: "\x1b[37m");
            }
            y++;
        }

        // Render streaming delta & Pi-style plan tree
        if (_isStreaming)
        {
            if (_activePlanSteps.Count > 0 && y < viewportBottom)
            {
                _back.Print(2, y++, $"● Plan ({_activePlanSteps.Count} steps)", fg: "\x1b[38;2;251;146;60m", bold: true);
                for (var si = 0; si < _activePlanSteps.Count && y < viewportBottom; si++)
                {
                    var isLast = si == _activePlanSteps.Count - 1;
                    var branchGlyph = isLast ? "└─ ◐ " : "├─ ✓ ";
                    _back.Print(4, y++, branchGlyph + _activePlanSteps[si], fg: "\x1b[90m");
                }
            }

            if (_activeTools.Count > 0 && y < viewportBottom)
            {
                var tool = _activeTools[^1];
                var toolBox = $"╭─ ⚙ {tool.Name} ({tool.Ms}ms) ─────────── [ {tool.Status} ] ╮";
                _back.Print(2, y++, toolBox, fg: "\x1b[38;2;16;185;129m");
            }

            if (!string.IsNullOrEmpty(_streamingDelta) && y < viewportBottom)
            {
                _back.Print(2, y++, "⚡ Reptar: ", fg: "\x1b[38;2;16;185;129m", bold: true);
                var streamLines = WrapText(_streamingDelta, _width - 6);
                var takeLines = streamLines.TakeLast(Math.Max(1, viewportBottom - y)).ToList();
                foreach (var sl in takeLines)
                {
                    if (y >= viewportBottom) break;
                    _back.Print(4, y++, sl, fg: "\x1b[37m");
                }
            }
        }
    }

    private void RenderSessionsTab()
    {
        var boxW = Math.Min(90, _width - 4);
        var boxH = Math.Min(20, _height - 6);
        var x = (_width - boxW) / 2;
        var y = 3;

        _back.DrawBox(x, y, boxW, boxH, "Saved Chat Sessions", borderFg: "\x1b[38;2;16;185;129m");
        _back.Print(x + 3, y + 1, "Enter: resume session · d: delete · n: new · Esc/1/q: back to chat · Tab: next tab", fg: "\x1b[90m");

        if (_sessionsList.Count == 0)
        {
            _back.Print(x + 4, y + 4, "No saved sessions yet. Start a chat in Tab 1!", fg: "\x1b[90m");
            return;
        }

        var listY = y + 3;
        for (var i = 0; i < _sessionsList.Count && listY < y + boxH - 2; i++, listY++)
        {
            var s = _sessionsList[i];
            var selected = i == _selectedSessionIdx;
            var ptr = selected ? "❯ " : "  ";
            var fg = selected ? "\x1b[37m" : "\x1b[90m";
            var bg = selected ? "\x1b[48;2;20;35;30m" : "";

            var title = s.Title ?? s.Id;
            var line = $"{ptr}{title}  ({s.CreatedAt:yyyy-MM-dd HH:mm})";
            _back.Print(x + 2, listY, line, fg: fg, bg: bg, bold: selected, maxWidth: boxW - 4);
        }
    }

    private void RenderToolsTab()
    {
        var boxW = Math.Min(96, _width - 4);
        var boxH = Math.Min(20, _height - 6);
        var x = (_width - boxW) / 2;
        var y = 3;

        _back.DrawBox(x, y, boxW, boxH, "Mounted Workspace AI Tools", borderFg: "\x1b[38;2;16;185;129m");

        var tools = _app.GetAiTools(onlyExplicit: true);
        _back.Print(x + 3, y + 1, $"Total {tools.Count} in-process tools mounted at workspace root:", fg: "\x1b[90m");

        var listY = y + 3;
        for (var i = 0; i < tools.Count && listY < y + boxH - 2; i++, listY += 2)
        {
            var t = tools[i];
            var selected = i == _selectedToolIdx;
            var ptr = selected ? "❯ " : "  ";
            var header = $"{ptr}⚙ {t.Name} ({t.Method})";
            _back.Print(x + 2, listY, header, fg: selected ? "\x1b[38;2;16;185;129m" : "\x1b[37m", bold: true);
            _back.Print(x + 6, listY + 1, t.Description, fg: "\x1b[90m", maxWidth: boxW - 8);
        }

        _back.Print(x + 3, y + boxH - 1, "↑/↓: navigate · Esc/1/q: back to chat · Tab: next tab", fg: "\x1b[90m");
    }

    private void RenderProvidersTab()
    {
        var models = GetFilteredCatalog();
        if (_selectedCatalogIdx >= models.Count)
            _selectedCatalogIdx = Math.Max(0, models.Count - 1);

        // 1. Header row with Category Tabs (fx style: "Models 18   [All]   Google   Anthropic   OpenAI   OpenRouter   Local")
        var headerY = 2;
        var headerX = 3;

        _back.Print(headerX, headerY, $"Models {models.Count}", fg: "\x1b[37m", bold: true);
        var curX = headerX + 12;

        for (var i = 0; i < ProviderCategories.Length; i++)
        {
            var cat = ProviderCategories[i];
            var isCurrent = i == _providerCategoryIdx;
            if (isCurrent)
            {
                _back.Print(curX, headerY, $"[{cat}]", fg: "\x1b[1;37m", bg: "\x1b[48;2;16;185;129m", bold: true);
                curX += cat.Length + 4;
            }
            else
            {
                _back.Print(curX, headerY, $" {cat} ", fg: "\x1b[90m");
                curX += cat.Length + 3;
            }
        }

        if (!string.IsNullOrWhiteSpace(_providerFilterText))
        {
            _back.Print(curX + 2, headerY, $"Filter: \"{_providerFilterText}\"", fg: "\x1b[33m");
        }

        // 2. Models list
        var startY = 4;
        var maxRows = _keyInputActive ? Math.Max(4, _height - 14) : Math.Max(6, _height - 8);

        var startIdx = 0;
        if (_selectedCatalogIdx >= maxRows)
            startIdx = _selectedCatalogIdx - maxRows + 1;

        var listY = startY;
        for (var i = startIdx; i < models.Count && listY < startY + maxRows; i++, listY++)
        {
            var m = models[i];
            var isSelected = i == _selectedCatalogIdx;
            var isActiveModel = string.Equals(_client.Config.Model, m.ModelId, StringComparison.OrdinalIgnoreCase);

            var (source, detail) = LlmProviders.CredentialSource(m.ProviderKey, _auth);
            var isLocal = m.ProviderKey == LlmProviders.Ollama;

            string statusText;
            string statusFg;
            if (source == "env")
            {
                statusText = $"✓ env: {detail}";
                statusFg = "\x1b[38;2;16;185;129m";
            }
            else if (source == "saved")
            {
                statusText = "✓ saved";
                statusFg = "\x1b[38;2;16;185;129m";
            }
            else if (isLocal)
            {
                statusText = "✓ local";
                statusFg = "\x1b[38;2;56;189;248m";
            }
            else
            {
                statusText = "○ needs key";
                statusFg = "\x1b[38;2;251;191;36m";
            }

            var activeBadge = isActiveModel ? " [Active]" : "";
            var fullStatus = $"{statusText}{activeBadge}";

            var rowBg = isSelected ? "\x1b[48;2;22;36;45m" : "";
            var ptr = isSelected ? "❯ " : "  ";

            if (isSelected)
            {
                _back.FillRect(2, listY, _width - 4, 1, ' ', bg: rowBg);
            }

            // Column 1: Model ID
            _back.Print(3, listY, $"{ptr}{m.ModelId}",
                fg: isSelected ? "\x1b[1;37m" : (isActiveModel ? "\x1b[38;2;16;185;129m" : "\x1b[37m"),
                bg: rowBg,
                bold: isSelected || isActiveModel);

            // Column 2: Specs
            var col2X = Math.Min(40, _width / 3 + 2);
            _back.Print(col2X, listY, m.Specs,
                fg: "\x1b[38;2;148;163;184m",
                bg: rowBg,
                maxWidth: Math.Max(10, _width - col2X - 28));

            // Column 3: Credential Status
            var col3X = Math.Max(col2X + 38, _width - 26);
            _back.Print(col3X, listY, fullStatus,
                fg: isActiveModel ? "\x1b[1;32m" : statusFg,
                bg: rowBg,
                bold: isSelected || isActiveModel);
        }

        // 3. Info status line
        var infoY = _keyInputActive ? _height - 8 : _height - 3;
        var pInfo = LlmProviders.Find(_client.Config.ProviderKey);
        var activePName = pInfo?.DisplayName ?? _client.Config.ProviderKey;
        _back.Print(3, infoY, $"Storage: ~/.reptar/agent/auth.json · Active: {activePName} ({_client.Config.Model})", fg: "\x1b[90m");

        // 4. Inline Fast Credential Input Card (if active)
        if (_keyInputActive)
        {
            var pTarget = LlmProviders.Find(_keyInputProvider) ?? LlmProviders.Known[0];
            var boxY = _height - 6;
            var boxW = Math.Min(84, _width - 6);
            var boxX = 3;

            _back.DrawShadow(boxX + 2, boxY + 1, boxW, 5);
            _back.DrawBox(boxX, boxY, boxW, 5, $"Connect Provider: {pTarget.DisplayName}",
                borderFg: "\x1b[38;2;16;185;129m", titleFg: "\x1b[1;37m");

            var masked = new string('•', _keyInputBuffer.Length);
            var keyPrompt = "❯ Paste API Key: ";
            _back.Print(boxX + 2, boxY + 1, keyPrompt, fg: "\x1b[33m", bold: true);
            var displayVal = string.IsNullOrEmpty(masked) ? "[ Paste key here or type ]" : masked;
            _back.Print(boxX + 2 + keyPrompt.Length, boxY + 1, displayVal, fg: "\x1b[37m");

            if (!string.IsNullOrEmpty(pTarget.KeyUrl))
            {
                _back.Print(boxX + 2, boxY + 2, $"Press 'o' to open https://{pTarget.KeyUrl} in browser", fg: "\x1b[38;2;56;189;248m");
            }

            _back.Print(boxX + 2, boxY + 3, "Enter: save to auth.json & activate · Esc: cancel", fg: "\x1b[90m");
        }

        // 5. Controls Ribbon at bottom
        var ribbonY = _height - 1;
        if (_keyInputActive)
        {
            _back.Print(2, ribbonY, "Enter: save & activate   o: open portal in browser   Esc: cancel", fg: "\x1b[90m");
        }
        else
        {
            _back.Print(2, ribbonY, "↑↓ navigate   ←→/tab category   enter use   o open portal   c set key   esc chat", fg: "\x1b[90m");
        }
    }

    private void RenderDockedInputCard()
    {
        var cardY = _height - 4;
        var cardW = _width - 2;

        _back.DrawBox(1, cardY, cardW, 3, borderFg: "\x1b[38;2;60;65;95m");

        // Prompt symbol
        _back.Print(3, cardY + 1, "› ", fg: "\x1b[38;2;16;185;129m", bold: true);

        // Clear input area in buffer to eliminate any ghost characters
        _back.FillRect(5, cardY + 1, cardW - 6, 1, ' ');

        if (string.IsNullOrEmpty(_input))
        {
            var placeholder = _isStreaming
                ? "Agent is thinking and running tools…"
                : "Ask Reptar anything… (Type / for commands, @ for files)";
            _back.Print(5, cardY + 1, placeholder, fg: "\x1b[90m");
        }
        else
        {
            _back.Print(5, cardY + 1, _input, fg: "\x1b[37m", maxWidth: cardW - 6);
        }
    }

    private void RenderSlashDropdown()
    {
        var commands = GetPaletteCommands();
        if (commands.Count == 0) return;

        var popW = Math.Min(64, _width - 6);
        var popH = Math.Min(commands.Count + 2, 10);
        var popX = 4;
        var popY = Math.Max(2, _height - 4 - popH);

        _back.DrawShadow(popX + 2, popY + 1, popW, popH);
        _back.DrawBox(popX, popY, popW, popH, "Commands (/)", borderFg: "\x1b[38;2;16;185;129m", titleFg: "\x1b[37m");

        var y = popY + 1;
        for (var i = 0; i < commands.Count && y < popY + popH - 1; i++, y++)
        {
            var c = commands[i];
            var selected = i == _slashSelectedIdx;
            var ptr = selected ? "❯ " : "  ";
            var fg = selected ? "\x1b[37m" : "\x1b[90m";
            var bg = selected ? "\x1b[48;2;20;45;35m" : "";

            var line = $"{ptr}{c.Name,-12} {c.Desc}";
            _back.Print(popX + 2, y, line, fg: fg, bg: bg, bold: selected, maxWidth: popW - 4);
        }
    }

    private void RenderFileDropdown()
    {
        var files = GetMatchingFiles();
        if (files.Count == 0) return;

        var popW = Math.Min(64, _width - 6);
        var popH = Math.Min(files.Count + 2, 10);
        var popX = 4;
        var popY = Math.Max(2, _height - 4 - popH);

        _back.DrawShadow(popX + 2, popY + 1, popW, popH);
        _back.DrawBox(popX, popY, popW, popH, "Workspace Files (@)", borderFg: "\x1b[38;2;16;185;129m", titleFg: "\x1b[37m");

        var y = popY + 1;
        for (var i = 0; i < files.Count && y < popY + popH - 1; i++, y++)
        {
            var f = files[i];
            var selected = i == _fileSelectedIdx;
            var ptr = selected ? "❯ " : "  ";
            var fg = selected ? "\x1b[37m" : "\x1b[90m";
            var bg = selected ? "\x1b[48;2;20;45;35m" : "";

            var line = $"{ptr}@{f}";
            _back.Print(popX + 2, y, line, fg: fg, bg: bg, bold: selected, maxWidth: popW - 4);
        }
    }

    private void RenderFooterRibbon()
    {
        var ribbonY = _height - 1;
        _back.Print(2, ribbonY, _statusRibbon, fg: "\x1b[90m", maxWidth: _width - 4);
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
