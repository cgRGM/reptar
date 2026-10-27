#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// Reasoning effort for models that support it. Ported from pi's <c>ModelThinkingLevel</c>.
/// </summary>
public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,

    /// <summary>Highest reasoning level. Only exposed when a model declares support.</summary>
    XHigh,

    /// <summary>Unconstrained thinking. Only exposed when a model declares support.</summary>
    Max
}

/// <summary>
/// Thinking-level policy: which levels a model offers, how to clamp a request onto them, and how
/// a level turns into a token budget. A direct port of pi's
/// <c>packages/ai/src/models.ts</c> and <c>api/simple-options.ts</c>.
/// </summary>
/// <remarks>
/// The budget numbers are the interesting part. pi does not send the level name to Anthropic; it
/// sends <c>thinking.budget_tokens</c> and raises <c>max_tokens</c> so the answer still has room.
/// Getting that wrong is how you end up with a model that thinks for 16k tokens and then has no
/// space to reply, so <see cref="AdjustMaxTokens"/> always leaves
/// <see cref="MinAnswerTokens"/> free.
/// </remarks>
public static class ThinkingLevels
{
    /// <summary>Every level, in cycle order. Shift+Tab walks this list.</summary>
    public static readonly IReadOnlyList<ThinkingLevel> All =
    [
        ThinkingLevel.Off,
        ThinkingLevel.Minimal,
        ThinkingLevel.Low,
        ThinkingLevel.Medium,
        ThinkingLevel.High,
        ThinkingLevel.XHigh,
        ThinkingLevel.Max
    ];

    /// <summary>Token budget per level. <c>Off</c> has none.</summary>
    public static readonly IReadOnlyDictionary<ThinkingLevel, int> DefaultBudgets =
        new Dictionary<ThinkingLevel, int>
        {
            [ThinkingLevel.Minimal] = 1_024,
            [ThinkingLevel.Low] = 2_048,
            [ThinkingLevel.Medium] = 8_192,
            [ThinkingLevel.High] = 16_384
        };

    /// <summary>Tokens always left for the answer when a thinking budget shares the ceiling.</summary>
    public const int MinAnswerTokens = 1_024;

    /// <summary>The level a model starts at when nothing has been chosen.</summary>
    public const ThinkingLevel Default = ThinkingLevel.Medium;

    /// <summary>Wire name, matching pi's lowercase identifiers.</summary>
    public static string Name(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        _ => "max"
    };

    /// <summary>Parses a wire name. Returns null for anything unrecognised.</summary>
    public static ThinkingLevel? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "off" or "none" or "disabled" => ThinkingLevel.Off,
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" or "med" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "xhigh" or "extra-high" => ThinkingLevel.XHigh,
        "max" or "maximum" => ThinkingLevel.Max,
        _ => null
    };

    /// <summary>
    /// The levels a model offers. A non-reasoning model offers only <see cref="ThinkingLevel.Off"/>.
    /// </summary>
    /// <param name="supportsReasoning">Whether the model reasons at all.</param>
    /// <param name="supportsExtended">
    /// Whether the model exposes <see cref="ThinkingLevel.XHigh"/> and <see cref="ThinkingLevel.Max"/>.
    /// pi only surfaces those two when the model explicitly maps them.
    /// </param>
    public static IReadOnlyList<ThinkingLevel> Supported(bool supportsReasoning, bool supportsExtended = false)
    {
        if (!supportsReasoning)
            return [ThinkingLevel.Off];

        if (supportsExtended)
            return All;

        return
        [
            ThinkingLevel.Off,
            ThinkingLevel.Minimal,
            ThinkingLevel.Low,
            ThinkingLevel.Medium,
            ThinkingLevel.High
        ];
    }

    /// <summary>
    /// Moves a request onto the nearest level the model actually offers: the closest available
    /// level at or above, else the closest below.
    /// </summary>
    public static ThinkingLevel Clamp(ThinkingLevel requested, IReadOnlyList<ThinkingLevel> available)
    {
        if (available.Count == 0)
            return ThinkingLevel.Off;

        if (available.Contains(requested))
            return requested;

        var requestedIndex = IndexOf(requested);

        for (var i = requestedIndex; i < All.Count; i++)
        {
            if (available.Contains(All[i]))
                return All[i];
        }

        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (available.Contains(All[i]))
                return All[i];
        }

        return available[0];
    }

    /// <summary>The next level in cycle order, wrapping around.</summary>
    public static ThinkingLevel Cycle(ThinkingLevel current, IReadOnlyList<ThinkingLevel> available)
    {
        if (available.Count <= 1)
            return available.Count == 0 ? ThinkingLevel.Off : available[0];

        var index = -1;
        for (var i = 0; i < available.Count; i++)
        {
            if (available[i] == current)
            {
                index = i;
                break;
            }
        }

        return available[(index + 1) % available.Count];
    }

    /// <summary>Token budget for a level. <c>XHigh</c> and <c>Max</c> use the <c>High</c> budget.</summary>
    public static int BudgetFor(ThinkingLevel level) =>
        DefaultBudgets.TryGetValue(ClampReasoning(level), out var budget) ? budget : 0;

    /// <summary><c>XHigh</c> and <c>Max</c> collapse to <c>High</c> for budget purposes.</summary>
    public static ThinkingLevel ClampReasoning(ThinkingLevel level) =>
        level is ThinkingLevel.XHigh or ThinkingLevel.Max ? ThinkingLevel.High : level;

    /// <summary>
    /// Computes the response ceiling and thinking budget for one request.
    /// </summary>
    /// <param name="baseMaxTokens">The caller's answer budget, or null for the model's own limit.</param>
    /// <param name="modelMaxTokens">The model's response ceiling.</param>
    /// <param name="level">Requested thinking level.</param>
    /// <returns>
    /// <c>MaxTokens</c> to send, and <c>ThinkingBudget</c> to send as
    /// <c>thinking.budget_tokens</c> (0 when thinking is off).
    /// </returns>
    public static (int MaxTokens, int ThinkingBudget) AdjustMaxTokens(
        int? baseMaxTokens,
        int modelMaxTokens,
        ThinkingLevel level)
    {
        var clamped = ClampReasoning(level);
        var budget = BudgetFor(clamped);

        if (budget == 0)
            return (baseMaxTokens ?? modelMaxTokens, 0);

        // Thinking shares the response ceiling with the answer, so the ceiling has to grow.
        var maxTokens = baseMaxTokens is null
            ? modelMaxTokens
            : Math.Min(baseMaxTokens.Value + budget, modelMaxTokens);

        if (maxTokens <= budget)
            budget = Math.Min(budget, Math.Max(0, maxTokens - MinAnswerTokens));

        return (maxTokens, budget);
    }

    /// <summary>
    /// The <c>reasoning_effort</c> string an OpenAI-compatible endpoint expects.
    /// </summary>
    public static string? EffortFor(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => null,
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "high",
        _ => "high"
    };

    /// <summary>
    /// The adaptive-thinking effort name an Anthropic model expects, when it uses effort rather
    /// than a token budget.
    /// </summary>
    public static string EffortForAnthropic(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        _ => "high"
    };

    /// <summary>
    /// Tokens held back from the ceiling so the estimate does not have to be exact. pi uses the
    /// same 4 096.
    /// </summary>
    public const int ContextSafetyTokens = 4_096;

    /// <summary>Never send a ceiling below this, even when the context is nearly full.</summary>
    public const int MinMaxTokens = 1;

    /// <summary>
    /// Clamps the response ceiling to the space actually left in the context window.
    /// </summary>
    /// <remarks>
    /// Ported from pi's <c>clampMaxTokensToContext</c>. A request whose <c>max_tokens</c> exceeds
    /// the remaining context is a 400 on every provider, and it is easy to hit: a 64k-output model
    /// on a 200k window with a 180k-token conversation has 20k left, not 64k.
    /// </remarks>
    public static int ClampToContext(int maxTokens, int contextTokens, int contextWindow)
    {
        if (contextWindow <= 0)
            return Math.Max(MinMaxTokens, maxTokens);

        var available = contextWindow - contextTokens - ContextSafetyTokens;
        return Math.Min(maxTokens, Math.Max(MinMaxTokens, available));
    }

    private static int IndexOf(ThinkingLevel level)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == level)
                return i;
        }
        return 0;
    }
}
#endif
