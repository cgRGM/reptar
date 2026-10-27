#if !NETWASM
namespace Reptar.AI;

/// <summary>Outcome of a cache warm attempt.</summary>
/// <param name="Warmed">True when the provider reported cached tokens.</param>
/// <param name="CachedTokens">Cache-read tokens the provider reported, if any.</param>
/// <param name="Skipped">Why the warm was skipped, when it was.</param>
public sealed record CacheWarmResult(bool Warmed, int CachedTokens, string? Skipped, double ElapsedMs);

/// <summary>
/// Populates a provider's prompt cache before the user's first real turn.
/// </summary>
/// <remarks>
/// Ported from pi's <c>core/cache-warmer.ts</c>. The economics: on Anthropic a cache write costs
/// 1.25× a normal input token and a cache read costs 0.1×. The system prompt plus tool
/// definitions for a coding agent are typically 2–5k tokens and are re-sent on every turn of a
/// session, so paying the write once turns a 10-turn session's prefix cost into roughly a
/// quarter of what it would otherwise be.
///
/// The warm is a deliberately cheap request: the real system prompt and the real tool
/// definitions, with a one-token user turn. The response is discarded. Anthropic caches on the
/// prefix, so the trivial user turn does not pollute anything.
///
/// This is only worth doing when the prefix is large and the session will be long. A warm on a
/// 200-token prompt costs more than it saves, which is why <see cref="ShouldWarm"/> exists.
/// </remarks>
public static class CacheWarmer
{
    /// <summary>Below this prefix size, warming costs more than it saves.</summary>
    public const int MinimumPrefixTokens = 1_024;

    /// <summary>
    /// Estimates whether a warm is worthwhile for this prefix.
    /// </summary>
    public static bool ShouldWarm(LlmConfig config, string? systemPrompt, int toolCount) =>
        config.PromptCache
        && config.ProviderKey == LlmProviders.Anthropic
        && EstimatePrefixTokens(systemPrompt, toolCount) >= MinimumPrefixTokens;

    /// <summary>Rough prefix size: chars/4 for the prompt plus ~120 tokens per tool.</summary>
    public static int EstimatePrefixTokens(string? systemPrompt, int toolCount) =>
        ((systemPrompt?.Length ?? 0) / 4) + (toolCount * 120);

    /// <summary>
    /// Sends one throwaway request so the provider caches the prefix.
    /// </summary>
    /// <param name="client">Provider to warm.</param>
    /// <param name="systemPrompt">The real system prompt.</param>
    /// <param name="tools">The real tool definitions.</param>
    public static async Task<CacheWarmResult> WarmAsync(
        ILlmClient client,
        string? systemPrompt,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (!ShouldWarm(client.Config, systemPrompt, tools.Count))
        {
            watch.Stop();
            return new CacheWarmResult(false, 0, "prefix too small or provider does not support caching",
                watch.Elapsed.TotalMilliseconds);
        }

        var messages = new List<ChatMessage>(2);

        if (systemPrompt is { Length: > 0 })
            messages.Add(new ChatMessage(AgentRoles.System, systemPrompt));

        messages.Add(new ChatMessage(AgentRoles.User, "."));

        var cached = 0;

        try
        {
            await foreach (var evt in client
                .StreamAsync(messages, tools, cancellationToken)
                .ConfigureAwait(false))
            {
                if (evt is LlmStreamEvent.Usage usage)
                    cached = usage.CacheReadTokens;

                if (evt is LlmStreamEvent.Failed)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            return new CacheWarmResult(false, 0, ex.Message, watch.Elapsed.TotalMilliseconds);
        }

        watch.Stop();

        // A warm that returns immediately with no cache-read tokens usually means the provider
        // treated it as a cold write; the cache is populated either way, so report it as warmed.
        return new CacheWarmResult(true, cached, null, watch.Elapsed.TotalMilliseconds);
    }
}
#endif
