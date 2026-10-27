#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>Whether a directory's project-local resources may be loaded.</summary>
public enum TrustDecision
{
    /// <summary>Not decided yet. Treat as untrusted until the user says otherwise.</summary>
    Unknown,

    /// <summary>Load project settings, prompt templates, and skills.</summary>
    Trusted,

    /// <summary>Ignore project-local resources; use user-level ones only.</summary>
    Untrusted
}

/// <summary>
/// Decides whether project-local resources may be loaded, and remembers the answer.
/// </summary>
/// <remarks>
/// Ported from pi's <c>core/project-trust.ts</c>. The threat is concrete: an agent that reads
/// <c>.reptar/settings.json</c>, <c>.reptar/prompts/*.md</c>, or <c>.agents/skills/*</c> from a
/// directory it did not create will happily follow instructions someone else wrote. Cloning a
/// repo and running the agent should not execute that repo's configuration.
///
/// The rule is therefore: <b>nothing project-local loads until the directory has been trusted</b>,
/// and trust is granted per absolute path, stored in the user's config, never in the project.
///
/// Trust is a property of the path, not of the content. If a trusted directory's config changes,
/// it stays trusted — otherwise every <c>git pull</c> would re-prompt, and people would approve
/// without reading.
/// </remarks>
public sealed class ProjectTrustStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _storePath;
    private readonly Dictionary<string, TrustDecision> _decisions;

    private ProjectTrustStore(string storePath, Dictionary<string, TrustDecision> decisions)
    {
        _storePath = storePath;
        _decisions = decisions;
    }

    /// <summary>Default location: <c>~/.reptar/trust.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".reptar", "trust.json");

    /// <summary>
    /// Loads the trust store. A missing or corrupt file yields an empty store rather than an
    /// error: an unreadable store must fail closed, and an empty store is closed.
    /// </summary>
    public static ProjectTrustStore Load(string? path = null)
    {
        var storePath = path ?? DefaultPath;
        var decisions = new Dictionary<string, TrustDecision>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(storePath) &&
                JsonNode.Parse(File.ReadAllText(storePath)) is JsonObject root &&
                root["trusted"] is JsonArray trusted)
            {
                foreach (var entry in trusted)
                {
                    if (entry?.GetValue<string>() is { Length: > 0 } directory)
                        decisions[Normalize(directory)] = TrustDecision.Trusted;
                }
            }

            if (File.Exists(storePath) &&
                JsonNode.Parse(File.ReadAllText(storePath)) is JsonObject root2 &&
                root2["untrusted"] is JsonArray untrusted)
            {
                foreach (var entry in untrusted)
                {
                    if (entry?.GetValue<string>() is { Length: > 0 } directory)
                        decisions[Normalize(directory)] = TrustDecision.Untrusted;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fail closed: no decisions means nothing project-local loads.
        }

        return new ProjectTrustStore(storePath, decisions);
    }

    /// <summary>
    /// The decision for a directory. <see cref="TrustDecision.Unknown"/> means the user has not
    /// been asked yet.
    /// </summary>
    public TrustDecision Get(string directory) =>
        _decisions.TryGetValue(Normalize(directory), out var decision)
            ? decision
            : TrustDecision.Unknown;

    /// <summary>True only for an explicit grant. Unknown is not trust.</summary>
    public bool IsTrusted(string directory) => Get(directory) == TrustDecision.Trusted;

    /// <summary>Records a decision and persists it.</summary>
    public async Task SetAsync(string directory, TrustDecision decision, CancellationToken cancellationToken = default)
    {
        var key = Normalize(directory);

        if (decision == TrustDecision.Unknown)
            _decisions.Remove(key);
        else
            _decisions[key] = decision;

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a grant.</summary>
    public Task RevokeAsync(string directory, CancellationToken cancellationToken = default) =>
        SetAsync(directory, TrustDecision.Untrusted, cancellationToken);

    /// <summary>Every remembered decision, for a settings screen.</summary>
    public IReadOnlyDictionary<string, TrustDecision> Decisions => _decisions;

    /// <summary>
    /// True when the directory contains project-local agent resources that would be loaded.
    /// Useful for deciding whether to prompt at all.
    /// </summary>
    public static bool HasProjectResources(string directory)
    {
        var root = Path.GetFullPath(directory);

        return Directory.Exists(Path.Combine(root, ".reptar"))
            || Directory.Exists(Path.Combine(root, ".agents", "skills"))
            || File.Exists(Path.Combine(root, "AGENTS.md"));
    }

    /// <summary>
    /// Resolves whether project-local resources may load, prompting once when undecided.
    /// </summary>
    /// <param name="directory">Project root.</param>
    /// <param name="ask">
    /// Called only when the decision is unknown. Return true to trust. A null delegate means
    /// "never prompt", which leaves everything untrusted — the right default for CI.
    /// </param>
    public async Task<bool> ResolveAsync(
        string directory,
        Func<string, Task<bool>>? ask = null,
        CancellationToken cancellationToken = default)
    {
        var existing = Get(directory);

        if (existing == TrustDecision.Trusted)
            return true;
        if (existing == TrustDecision.Untrusted)
            return false;

        // Nothing project-local to protect: no need to bother the user.
        if (!HasProjectResources(directory))
        {
            await SetAsync(directory, TrustDecision.Trusted, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (ask is null)
            return false;

        var trusted = await ask(directory).ConfigureAwait(false);
        await SetAsync(
            directory,
            trusted ? TrustDecision.Trusted : TrustDecision.Untrusted,
            cancellationToken).ConfigureAwait(false);

        return trusted;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var trusted = new JsonArray();
        var untrusted = new JsonArray();

        foreach (var (directory, decision) in _decisions)
        {
            if (decision == TrustDecision.Trusted)
                trusted.Add(directory);
            else if (decision == TrustDecision.Untrusted)
                untrusted.Add(directory);
        }

        var payload = new JsonObject
        {
            ["version"] = 1,
            ["trusted"] = trusted,
            ["untrusted"] = untrusted
        };

        var directoryPath = Path.GetDirectoryName(_storePath);
        if (directoryPath is { Length: > 0 })
            Directory.CreateDirectory(directoryPath);

        await File.WriteAllTextAsync(
            _storePath,
            payload.ToJsonString(Json),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Canonical form for comparison: absolute, no trailing separator, case-insensitive on
    /// Windows. Two spellings of the same directory must not be two trust decisions.
    /// </summary>
    private static string Normalize(string directory)
    {
        var full = Path.GetFullPath(directory);
        var trimmed = full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }
}
#endif
