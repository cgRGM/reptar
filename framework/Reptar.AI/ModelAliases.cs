#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// A named alias that resolves to a concrete provider/model pair.
/// </summary>
/// <remarks>
/// Ported from pi's virtual models. The problem it solves: you type
/// <c>anthropic/claude-sonnet-4-5</c> fifty times a day, and the good default changes when a new
/// model ships. An alias gives you <c>fast</c> and <c>deep</c> and lets the mapping move.
///
/// The alias is <em>not</em> an abstraction over the provider API — it resolves once, at request
/// time, to a real <see cref="LlmConfig"/>. Nothing downstream knows an alias was involved.
/// </remarks>
public sealed record ModelAlias
{
    /// <summary>The name you type, for example <c>fast</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Provider key, matching <see cref="LlmProviders"/> or a custom id.</summary>
    public required string Provider { get; init; }

    /// <summary>Concrete model id.</summary>
    public required string Model { get; init; }

    /// <summary>Shown in a model picker.</summary>
    public string Description { get; init; } = "";

    /// <summary>Optional override of the provider's base URL.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Resolves to a provider config.</summary>
    public LlmConfig ToConfig(string? apiKey = null) => new()
    {
        ProviderKey = Provider,
        Provider = WireProtocol(Provider),
        Model = Model,
        BaseUrl = BaseUrl,
        ApiKey = apiKey
    };

    /// <summary>
    /// Maps a registry key onto the wire protocol to speak. Every gateway provider
    /// (OpenRouter, Vercel AI Gateway, OpenCode Zen, Cloudflare AI Gateway) and every custom
    /// endpoint uses the OpenAI shape, which is why they all collapse to
    /// <see cref="LlmProvider.OpenAi"/>.
    /// </summary>
    public static LlmProvider WireProtocol(string providerKey) => providerKey switch
    {
        LlmProviders.Anthropic => LlmProvider.Anthropic,
        LlmProviders.Gemini => LlmProvider.Gemini,
        LlmProviders.Ollama => LlmProvider.Ollama,
        LlmProviders.Echo => LlmProvider.Echo,
        _ => LlmProvider.OpenAi
    };
}

/// <summary>
/// The alias registry. Defaults cover the common shapes; projects can add their own.
/// </summary>
public static class ModelAliases
{
    /// <summary>
    /// Aliases that ship with Reptar. These are starting points, not recommendations — they are
    /// meant to be overridden per project.
    /// </summary>
    public static IReadOnlyList<ModelAlias> Defaults { get; } =
    [
        new()
        {
            Name = "fast",
            Provider = LlmProviders.OpenAi,
            Model = "gpt-4o-mini",
            Description = "Cheap and quick; good for search, summaries, and small edits."
        },
        new()
        {
            Name = "deep",
            Provider = LlmProviders.Anthropic,
            Model = "claude-sonnet-4-5",
            Description = "Stronger reasoning; use when the task needs planning."
        },
        new()
        {
            Name = "local",
            Provider = LlmProviders.Ollama,
            Model = "qwen2.5-coder",
            Description = "Runs on your machine; no API key, slower."
        },
        new()
        {
            Name = "offline",
            Provider = "echo",
            Model = "echo",
            Description = "Deterministic echo provider; tools still work."
        }
    ];

    /// <summary>
    /// Resolves a model spec, which may be an alias, <c>provider/model</c>, or a bare model id.
    /// </summary>
    /// <param name="spec">What the user typed.</param>
    /// <param name="aliases">Registry to consult. Defaults to <see cref="Defaults"/>.</param>
    /// <param name="apiKey">Key to attach, usually resolved separately.</param>
    /// <param name="spec">What the user typed: an alias, <c>provider/model</c>, or a bare id.</param>
    /// <param name="aliases">Registry to consult. Defaults to <see cref="Defaults"/>.</param>
    /// <param name="apiKey">Key to attach, usually resolved separately.</param>
    /// <param name="provider">
    /// Provider to pair with a bare model id. Settings store provider and model separately, so a
    /// saved <c>{"provider":"ollama","model":"llama3.1:8b"}</c> must resolve without the spec
    /// needing to repeat the provider.
    /// </param>
    public static LlmConfig Resolve(
        string spec,
        IReadOnlyList<ModelAlias>? aliases = null,
        string? apiKey = null,
        string? provider = null)
    {
        var registry = aliases ?? Defaults;

        var alias = registry.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, spec, StringComparison.OrdinalIgnoreCase));

        if (alias is not null)
            return alias.ToConfig(apiKey);

        // provider/model
        var slash = spec.IndexOf('/');
        if (slash > 0 && slash < spec.Length - 1)
        {
            var specProvider = spec[..slash];
            var specModel = spec[(slash + 1)..];

            return new LlmConfig
            {
                ProviderKey = specProvider,
                Provider = ModelAlias.WireProtocol(specProvider),
                Model = specModel,
                ApiKey = apiKey
            };
        }

        // A bare id with an explicit provider: that provider wins over the environment.
        // Delegated rather than hand-built, so the provider's base URL, env key, and saved
        // credential are all resolved — a hand-built config has no BaseUrl, which silently
        // points a local model at api.openai.com.
        if (provider is { Length: > 0 })
        {
            var configured = LlmConfig.FromEnvironment(provider: provider, model: spec);
            return apiKey is null ? configured : configured with { ApiKey = apiKey };
        }

        // Bare model id: keep the provider from the environment.
        var environment = LlmConfig.FromEnvironment(model: spec);
        return apiKey is null ? environment : environment with { ApiKey = apiKey };
    }

    /// <summary>
    /// Merges alias lists, later entries winning on name. Use to layer a project's aliases over
    /// the defaults.
    /// </summary>
    public static IReadOnlyList<ModelAlias> Merge(params IReadOnlyList<ModelAlias>[] lists)
    {
        var byName = new Dictionary<string, ModelAlias>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in lists)
        {
            foreach (var alias in list)
                byName[alias.Name] = alias;
        }

        return [.. byName.Values.OrderBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Renders the alias table for a model picker or <c>--help</c>.</summary>
    public static string Describe(IReadOnlyList<ModelAlias>? aliases = null)
    {
        var registry = aliases ?? Defaults;
        var nameWidth = registry.Count == 0 ? 4 : registry.Max(alias => alias.Name.Length);
        var specWidth = registry.Count == 0 ? 8 : registry.Max(alias => $"{alias.Provider}/{alias.Model}".Length);

        var lines = new List<string>(registry.Count + 1)
        {
            $"{Pad("alias", nameWidth)}  {Pad("model", specWidth)}  description"
        };

        foreach (var alias in registry)
        {
            lines.Add(
                $"{Pad(alias.Name, nameWidth)}  {Pad($"{alias.Provider}/{alias.Model}", specWidth)}  {alias.Description}");
        }

        return string.Join('\n', lines);

        static string Pad(string value, int width) =>
            value.Length >= width ? value : value.PadRight(width);
    }
}
#endif
