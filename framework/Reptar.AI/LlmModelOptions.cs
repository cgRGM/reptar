#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// One selectable model, flattened for display.
/// </summary>
/// <remarks>
/// A host's picker needs the same facts in the same order: which provider, which model, what it
/// costs in context, and which thinking levels it accepts. Building that in each host means each
/// host gets the filtering and the ordering slightly wrong.
/// </remarks>
public sealed record ModelOption(
    string ProviderKey,
    string ProviderName,
    string ModelId,
    string DisplayName,
    string Category,
    string Specs,
    bool Reasoning,
    IReadOnlyList<ThinkingLevel> Thinking,
    long ContextTokens)
{
    /// <summary>True when this entry is the provider's default rather than a known model.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// Builds the list of models a host should offer.
/// </summary>
/// <remarks>
/// The rules here are the ones that are easy to get wrong in a picker:
/// <list type="bullet">
/// <item>Only connected providers contribute. Offering a model you have no credential for means
/// picking it, getting a 401, and having to work out why.</item>
/// <item>Only chat models are listed. An embedding or text-to-speech model in a picker produces a
/// provider error rather than a reply.</item>
/// <item>Newest first, because provider catalogs arrive in no useful order.</item>
/// </list>
/// </remarks>
public static class LlmModelOptions
{
    /// <summary>How long to wait for a provider's own model list when no catalog is known.</summary>
    public const int DefaultLiveTimeoutMs = 4_000;

    /// <summary>
    /// Every selectable model across connected providers, newest first within each provider.
    /// </summary>
    public static async Task<IReadOnlyList<ModelOption>> BuildAsync(
        LlmAuthFile auth,
        int liveTimeoutMs = DefaultLiveTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        var options = new List<ModelOption>();

        foreach (var provider in LlmProviders.Known)
        {
            if (!LlmProviders.IsConnected(provider.Key, auth))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var models = await ResolveAsync(provider, auth, liveTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            var category = Category(provider.DisplayName);

            if (models.Count == 0)
            {
                // Offline or unlisted: offer the default so the provider is still selectable.
                options.Add(new ModelOption(
                    provider.Key,
                    provider.DisplayName,
                    provider.DefaultModel,
                    provider.DefaultModel,
                    category,
                    "default",
                    false,
                    [ThinkingLevel.Off],
                    0)
                {
                    IsDefault = true
                });

                continue;
            }

            foreach (var model in models)
            {
                if (!IsChatModel(model.Id))
                    continue;

                options.Add(new ModelOption(
                    provider.Key,
                    provider.DisplayName,
                    model.Id,
                    model.Name,
                    category,
                    FormatSpecs(model),
                    model.Reasoning,
                    model.SupportedThinking,
                    model.ContextTokens));
            }
        }

        return options;
    }

    /// <summary>
    /// Distinct category labels, in provider order, with <c>All</c> first.
    /// </summary>
    public static IReadOnlyList<string> Categories(IEnumerable<ModelOption> options)
    {
        var categories = new List<string> { "All" };

        foreach (var option in options)
        {
            if (!categories.Contains(option.Category, StringComparer.Ordinal))
                categories.Add(option.Category);
        }

        return categories;
    }

    private static async Task<List<LlmCatalog.CuratedModel>> ResolveAsync(
        ProviderInfo provider,
        LlmAuthFile auth,
        int liveTimeoutMs,
        CancellationToken cancellationToken)
    {
        List<LlmCatalog.CuratedModel> models;

        try
        {
            models = await LlmCatalog.ListCuratedAsync(provider.Key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            models = [];
        }

        if (models.Count > 0)
            return models;

        // No curated catalog means this provider's ids are unknown — a gateway, or one models.dev
        // does not cover. Ask the provider: it knows its own names, including any routing prefix
        // that a borrowed catalog would get wrong.
        try
        {
            var live = await LlmCatalog
                .ListAsync(LlmConfig.FromEnvironment(provider.Key), liveTimeoutMs)
                .ConfigureAwait(false);

            if (live.Ok && live.Models.Count > 0)
            {
                return [.. live.Models.Select(id => new LlmCatalog.CuratedModel(id, id, 0))];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falls through to the provider default.
        }

        return [];
    }

    /// <summary>
    /// Whether a model id names a chat model.
    /// </summary>
    /// <remarks>
    /// Excludes the families that cannot answer a prompt: embeddings, rerankers, image generation,
    /// speech, transcription, moderation, and the audio-only realtime models.
    /// </remarks>
    public static bool IsChatModel(string modelId)
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

    /// <summary>The short capability line a picker shows next to a model.</summary>
    public static string FormatSpecs(LlmCatalog.CuratedModel model)
    {
        var specs = new List<string>();

        if (model.ContextTokens > 0)
            specs.Add($"{FormatTokens((int)Math.Min(int.MaxValue, model.ContextTokens))} ctx");

        if (model.Reasoning)
            specs.Add("reasoning");

        if (model.Vision)
            specs.Add("vision");

        return specs.Count > 0 ? string.Join(" · ", specs) : "—";
    }

    /// <summary>Abbreviates a token count: 1.0k, 128k, 1.1M.</summary>
    public static string FormatTokens(int count)
    {
        if (count < 1_000)
            return count.ToString();

        if (count < 10_000)
            return $"{count / 1_000.0:0.0}k";

        if (count < 1_000_000)
            return $"{Math.Round(count / 1_000.0)}k";

        if (count < 10_000_000)
            return $"{count / 1_000_000.0:0.0}M";

        return $"{Math.Round(count / 1_000_000.0)}M";
    }

    /// <summary>The grouping label a picker uses, from the provider's display name.</summary>
    public static string Category(string providerDisplayName) =>
        providerDisplayName.Split(' ')[0];
}
#endif
