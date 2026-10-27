#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// What the agent should start with. Ported from the parts of pi's <c>settings.json</c> schema
/// that Reptar actually acts on.
/// </summary>
/// <remarks>
/// Two files, user then project, project winning field by field:
/// <code>
/// ~/.reptar/settings.json          your defaults
/// &lt;workspace&gt;/.reptar/settings.json   this repository's overrides
/// </code>
/// A project file only loads when the workspace is trusted — it is project-local configuration,
/// and that is exactly the thing trust exists to gate.
///
/// Every field is nullable so "not set here" is distinguishable from "set to the default". That
/// distinction is what makes layering work: the project file can override one field without
/// restating the rest.
/// </remarks>
public sealed record AgentSettings
{
    /// <summary>Model spec: an alias (<c>fast</c>), <c>provider/model</c>, or a bare model id.</summary>
    public string? Model { get; init; }

    public string? Provider { get; init; }

    /// <summary>Thinking level, by wire name (<c>off</c> … <c>max</c>).</summary>
    public string? Thinking { get; init; }

    /// <summary>Queue mode for messages typed while the agent runs.</summary>
    public AgentQueueMode? SteeringMode { get; init; }

    /// <summary>Queue mode for messages queued for after the agent stops.</summary>
    public AgentQueueMode? FollowUpMode { get; init; }

    /// <summary>Ask the provider to cache the static prefix.</summary>
    public bool? PromptCache { get; init; }

    /// <summary>Send one throwaway request to populate the cache before the first turn.</summary>
    public bool? WarmPromptCache { get; init; }

    /// <summary>Hide reasoning blocks in the transcript.</summary>
    public bool? HideThinking { get; init; }

    /// <summary>How many rows the completion dropdown shows.</summary>
    public int? AutocompleteMaxVisible { get; init; }

    public CompactionSettings? Compaction { get; init; }

    public AgentRetryPolicy? Retry { get; init; }

    /// <summary>What to do when a directory with agent config is opened for the first time.</summary>
    public TrustDefault? DefaultProjectTrust { get; init; }

    /// <summary>
    /// Models reachable by cycling. When empty, cycling walks every available model instead —
    /// which is unusable once you have a few hundred, so a short list is the point.
    /// </summary>
    public IReadOnlyList<ScopedModel>? ScopedModels { get; init; }

    /// <summary>Runtime defaults, matching pi's <c>SETTINGS_DEFAULTS</c>.</summary>
    public static AgentSettings Defaults { get; } = new()
    {
        Thinking = "off",
        SteeringMode = AgentQueueMode.OneAtATime,
        FollowUpMode = AgentQueueMode.OneAtATime,
        PromptCache = true,
        WarmPromptCache = true,
        HideThinking = false,
        AutocompleteMaxVisible = 5,
        Compaction = CompactionSettings.Default,
        Retry = new AgentRetryPolicy(),
        DefaultProjectTrust = TrustDefault.Ask
    };

    /// <summary>Fills unset fields from <paramref name="fallback"/>.</summary>
    public AgentSettings WithFallback(AgentSettings fallback) => new()
    {
        Model = Model ?? fallback.Model,
        Provider = Provider ?? fallback.Provider,
        Thinking = Thinking ?? fallback.Thinking,
        SteeringMode = SteeringMode ?? fallback.SteeringMode,
        FollowUpMode = FollowUpMode ?? fallback.FollowUpMode,
        PromptCache = PromptCache ?? fallback.PromptCache,
        WarmPromptCache = WarmPromptCache ?? fallback.WarmPromptCache,
        HideThinking = HideThinking ?? fallback.HideThinking,
        AutocompleteMaxVisible = AutocompleteMaxVisible ?? fallback.AutocompleteMaxVisible,
        Compaction = Compaction ?? fallback.Compaction,
        Retry = Retry ?? fallback.Retry,
        DefaultProjectTrust = DefaultProjectTrust ?? fallback.DefaultProjectTrust,
        ScopedModels = ScopedModels ?? fallback.ScopedModels
    };

    /// <summary>The thinking level this file asks for, parsed and validated.</summary>
    public ThinkingLevel ThinkingLevel => ThinkingLevels.Parse(Thinking) ?? ThinkingLevel.Off;

    /// <summary>Effective compaction thresholds.</summary>
    public CompactionSettings EffectiveCompaction => Compaction ?? CompactionSettings.Default;

    /// <summary>Effective retry policy.</summary>
    public AgentRetryPolicy EffectiveRetry => Retry ?? new AgentRetryPolicy();
}

/// <summary>
/// One entry in the cycling scope: a provider, a model, and optionally the thinking level to use
/// when cycling to it.
/// </summary>
/// <remarks>
/// The per-entry thinking level is what makes a scope useful: the cheap model should not inherit
/// the deep model's reasoning budget, and vice versa.
/// </remarks>
public sealed record ScopedModel(string Provider, string Model, string? Thinking = null);

/// <summary>First-open behaviour for a directory that carries agent configuration.</summary>
public enum TrustDefault
{
    /// <summary>Prompt.</summary>
    Ask,

    /// <summary>Trust without asking. Only sane in a controlled environment.</summary>
    Always,

    /// <summary>Never trust. The right default for CI.</summary>
    Never
}

/// <summary>
/// Loads and saves <see cref="AgentSettings"/> from the user and project files.
/// </summary>
public sealed class AgentSettingsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _userPath;
    private readonly string _projectPath;

    private AgentSettingsStore(string userPath, string projectPath, AgentSettings settings)
    {
        _userPath = userPath;
        _projectPath = projectPath;
        Settings = settings;
    }

    /// <summary>The merged settings in effect.</summary>
    public AgentSettings Settings { get; private set; }

    /// <summary>Problems encountered while loading, for a startup diagnostic.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Path of the user-level file.</summary>
    public static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".reptar", "settings.json");

    /// <summary>Path of the project-level file.</summary>
    public static string ProjectPath(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), ".reptar", "settings.json");

    /// <summary>
    /// Loads user settings, then layers the project file over them when the workspace is trusted.
    /// </summary>
    /// <param name="workspaceRoot">Project root.</param>
    /// <param name="trusted">
    /// Whether project-local configuration may load. False skips the project file entirely.
    /// </param>
    /// <param name="userPath">Override for tests.</param>
    /// <param name="projectPath">Override for tests.</param>
    public static AgentSettingsStore Load(
        string workspaceRoot,
        bool trusted = false,
        string? userPath = null,
        string? projectPath = null)
    {
        var warnings = new List<string>();

        var user = Read(userPath ?? UserPath, warnings);
        var settings = (user ?? new AgentSettings()).WithFallback(AgentSettings.Defaults);

        var projectFile = projectPath ?? ProjectPath(workspaceRoot);
        if (trusted && File.Exists(projectFile))
        {
            var project = Read(projectFile, warnings);
            if (project is not null)
                settings = project.WithFallback(settings);
        }

        var store = new AgentSettingsStore(userPath ?? UserPath, projectFile, settings)
        {
            Warnings = warnings
        };

        return store;
    }

    /// <summary>
    /// Writes a field to the user file. Project settings are deliberately not written: the agent
    /// should not modify a repository's configuration behind your back.
    /// </summary>
    public async Task SaveUserAsync(
        Func<AgentSettings, AgentSettings> mutate,
        CancellationToken cancellationToken = default)
    {
        var current = Read(_userPath, []) ?? new AgentSettings();
        current = mutate(current);

        var directory = Path.GetDirectoryName(_userPath);
        if (directory is { Length: > 0 })
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            _userPath,
            JsonSerializer.Serialize(current, WriteOptions),
            cancellationToken).ConfigureAwait(false);

        Settings = current.WithFallback(AgentSettings.Defaults);
    }

    /// <summary>Remembers the model choice so the next start uses it.</summary>
    public Task SaveModelAsync(string provider, string model, CancellationToken cancellationToken = default) =>
        SaveUserAsync(settings => settings with { Provider = provider, Model = model }, cancellationToken);

    /// <summary>Remembers the thinking level.</summary>
    public Task SaveThinkingAsync(ThinkingLevel level, CancellationToken cancellationToken = default) =>
        SaveUserAsync(settings => settings with { Thinking = ThinkingLevels.Name(level) }, cancellationToken);

    private static AgentSettings? Read(string path, List<string> warnings)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return JsonSerializer.Deserialize<AgentSettings>(text, ReadOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must not stop the agent from starting; report and move on.
            warnings.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Renders the effective settings for a <c>/settings</c> view.</summary>
    public string Describe()
    {
        var settings = Settings;
        var lines = new List<string>
        {
            "**Settings**",
            "",
            "```",
            $"model            {settings.Model ?? "(from environment)"}",
            $"thinking         {settings.Thinking ?? "off"}",
            $"prompt cache     {(settings.PromptCache == true ? "on" : "off")}",
            $"cache warming    {(settings.WarmPromptCache == true ? "on" : "off")}",
            $"steering         {Mode(settings.SteeringMode)}",
            $"follow-up        {Mode(settings.FollowUpMode)}",
            $"compaction       {(settings.EffectiveCompaction.Enabled ? "auto" : "off")} " +
            $"(keep {settings.EffectiveCompaction.KeepRecentTokens:N0}, reserve {settings.EffectiveCompaction.ReserveTokens:N0})",
            $"retry            {settings.EffectiveRetry.MaxAttempts} attempts, " +
            $"{settings.EffectiveRetry.BaseDelay.TotalSeconds:0}s base",
            $"autocomplete     {settings.AutocompleteMaxVisible} rows",
            $"project trust    {settings.DefaultProjectTrust?.ToString().ToLowerInvariant() ?? "ask"}",
            "```",
            "",
            $"user file     `{_userPath}`",
            $"project file  `{_projectPath}`"
        };

        if (Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("**Warnings**");
            foreach (var warning in Warnings)
                lines.Add($"- {warning}");
        }

        return string.Join('\n', lines);

        static string Mode(AgentQueueMode? mode) =>
            mode == AgentQueueMode.All ? "all" : "one-at-a-time";
    }
}
#endif
