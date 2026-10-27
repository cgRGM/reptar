using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="ThinkingLevels"/>: the cycle order, capability filtering, the
/// clamp rule, and the token arithmetic that keeps a thinking budget from eating the answer.
/// </summary>
public sealed class ThinkingLevelsTests
{
    // ------------------------------------------------------------------
    // Order and naming
    // ------------------------------------------------------------------

    [Fact]
    public void the_cycle_order_matches_pis_extended_list()
    {
        Assert.Equal(
            [
                ThinkingLevel.Off,
                ThinkingLevel.Minimal,
                ThinkingLevel.Low,
                ThinkingLevel.Medium,
                ThinkingLevel.High,
                ThinkingLevel.XHigh,
                ThinkingLevel.Max
            ],
            ThinkingLevels.All);
    }

    [Theory]
    [InlineData(ThinkingLevel.Off, "off")]
    [InlineData(ThinkingLevel.Minimal, "minimal")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.XHigh, "xhigh")]
    [InlineData(ThinkingLevel.Max, "max")]
    public void wire_names_round_trip(ThinkingLevel level, string name)
    {
        Assert.Equal(name, ThinkingLevels.Name(level));
        Assert.Equal(level, ThinkingLevels.Parse(name));
    }

    [Fact]
    public void parsing_is_case_insensitive_and_accepts_aliases()
    {
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.Parse("HIGH"));
        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.Parse("none"));
        Assert.Equal(ThinkingLevel.Max, ThinkingLevels.Parse("maximum"));
        Assert.Null(ThinkingLevels.Parse("turbo"));
        Assert.Null(ThinkingLevels.Parse(null));
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Fact]
    public void a_model_that_does_not_reason_offers_only_off()
    {
        Assert.Equal([ThinkingLevel.Off], ThinkingLevels.Supported(supportsReasoning: false));
    }

    [Fact]
    public void a_reasoning_model_offers_the_standard_levels()
    {
        var levels = ThinkingLevels.Supported(supportsReasoning: true);

        Assert.Equal(
            [
                ThinkingLevel.Off,
                ThinkingLevel.Minimal,
                ThinkingLevel.Low,
                ThinkingLevel.Medium,
                ThinkingLevel.High
            ],
            levels);
        Assert.DoesNotContain(ThinkingLevel.XHigh, levels);
    }

    [Fact]
    public void extended_levels_only_appear_when_the_model_declares_them()
    {
        var levels = ThinkingLevels.Supported(supportsReasoning: true, supportsExtended: true);

        Assert.Contains(ThinkingLevel.XHigh, levels);
        Assert.Contains(ThinkingLevel.Max, levels);
    }

    // ------------------------------------------------------------------
    // Clamp
    // ------------------------------------------------------------------

    [Fact]
    public void a_supported_level_is_returned_unchanged()
    {
        var available = ThinkingLevels.Supported(supportsReasoning: true);

        Assert.Equal(ThinkingLevel.Medium, ThinkingLevels.Clamp(ThinkingLevel.Medium, available));
    }

    [Fact]
    public void an_unsupported_high_level_clamps_down_to_the_nearest_supported()
    {
        var available = ThinkingLevels.Supported(supportsReasoning: true);

        // xhigh is not offered, so it falls to high.
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.Clamp(ThinkingLevel.XHigh, available));
    }

    [Fact]
    public void clamping_on_a_non_reasoning_model_always_yields_off()
    {
        var available = ThinkingLevels.Supported(supportsReasoning: false);

        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.Clamp(ThinkingLevel.High, available));
    }

    // ------------------------------------------------------------------
    // Cycle
    // ------------------------------------------------------------------

    [Fact]
    public void cycling_advances_and_wraps()
    {
        var available = ThinkingLevels.Supported(supportsReasoning: true);

        Assert.Equal(ThinkingLevel.Minimal, ThinkingLevels.Cycle(ThinkingLevel.Off, available));
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.Cycle(ThinkingLevel.Medium, available));
        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.Cycle(ThinkingLevel.High, available));
    }

    [Fact]
    public void cycling_a_single_level_model_is_a_no_op()
    {
        var available = ThinkingLevels.Supported(supportsReasoning: false);

        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.Cycle(ThinkingLevel.Off, available));
    }

    // ------------------------------------------------------------------
    // Budgets
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ThinkingLevel.Minimal, 1_024)]
    [InlineData(ThinkingLevel.Low, 2_048)]
    [InlineData(ThinkingLevel.Medium, 8_192)]
    [InlineData(ThinkingLevel.High, 16_384)]
    public void budgets_match_pis_defaults(ThinkingLevel level, int expected)
    {
        Assert.Equal(expected, ThinkingLevels.BudgetFor(level));
    }

    [Fact]
    public void off_has_no_budget()
    {
        Assert.Equal(0, ThinkingLevels.BudgetFor(ThinkingLevel.Off));
    }

    [Fact]
    public void extended_levels_use_the_high_budget()
    {
        Assert.Equal(ThinkingLevels.BudgetFor(ThinkingLevel.High), ThinkingLevels.BudgetFor(ThinkingLevel.XHigh));
        Assert.Equal(ThinkingLevels.BudgetFor(ThinkingLevel.High), ThinkingLevels.BudgetFor(ThinkingLevel.Max));
    }

    // ------------------------------------------------------------------
    // Token arithmetic
    // ------------------------------------------------------------------

    [Fact]
    public void with_thinking_off_the_ceiling_is_unchanged()
    {
        var (maxTokens, budget) = ThinkingLevels.AdjustMaxTokens(4_096, 64_000, ThinkingLevel.Off);

        Assert.Equal(4_096, maxTokens);
        Assert.Equal(0, budget);
    }

    [Fact]
    public void the_ceiling_grows_to_fit_the_budget()
    {
        var (maxTokens, budget) = ThinkingLevels.AdjustMaxTokens(4_096, 64_000, ThinkingLevel.Medium);

        Assert.Equal(4_096 + 8_192, maxTokens);
        Assert.Equal(8_192, budget);
    }

    [Fact]
    public void a_null_base_uses_the_model_ceiling()
    {
        var (maxTokens, budget) = ThinkingLevels.AdjustMaxTokens(null, 32_000, ThinkingLevel.High);

        Assert.Equal(32_000, maxTokens);
        Assert.Equal(16_384, budget);
    }

    [Fact]
    public void the_ceiling_never_exceeds_the_model_limit()
    {
        var (maxTokens, _) = ThinkingLevels.AdjustMaxTokens(60_000, 64_000, ThinkingLevel.High);

        Assert.Equal(64_000, maxTokens);
    }

    [Fact]
    public void the_budget_leaves_room_for_an_answer_when_the_ceiling_is_tight()
    {
        // A 2 000-token ceiling cannot hold a 8 192-token thinking budget plus a reply, so the
        // budget is squeezed to leave MinAnswerTokens free.
        var (maxTokens, budget) = ThinkingLevels.AdjustMaxTokens(2_000, 2_000, ThinkingLevel.Medium);

        Assert.Equal(2_000, maxTokens);
        Assert.Equal(2_000 - ThinkingLevels.MinAnswerTokens, budget);
    }

    [Fact]
    public void the_budget_never_goes_negative()
    {
        var (_, budget) = ThinkingLevels.AdjustMaxTokens(100, 100, ThinkingLevel.High);

        Assert.Equal(0, budget);
    }

    // ------------------------------------------------------------------
    // Context clamp
    // ------------------------------------------------------------------

    [Fact]
    public void the_ceiling_is_left_alone_when_the_window_is_unknown()
    {
        Assert.Equal(8_192, ThinkingLevels.ClampToContext(8_192, 150_000, contextWindow: 0));
    }

    [Fact]
    public void the_ceiling_is_clamped_to_the_space_left_minus_the_safety_margin()
    {
        // 200k window, 180k already used, 4 096 held back => 15 904 available.
        var clamped = ThinkingLevels.ClampToContext(64_000, 180_000, 200_000);

        Assert.Equal(200_000 - 180_000 - ThinkingLevels.ContextSafetyTokens, clamped);
    }

    [Fact]
    public void a_ceiling_that_already_fits_is_unchanged()
    {
        Assert.Equal(4_096, ThinkingLevels.ClampToContext(4_096, 10_000, 200_000));
    }

    [Fact]
    public void a_full_context_never_produces_a_zero_or_negative_ceiling()
    {
        Assert.Equal(ThinkingLevels.MinMaxTokens, ThinkingLevels.ClampToContext(8_192, 200_000, 200_000));
    }

    // ------------------------------------------------------------------
    // Provider mapping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ThinkingLevel.Off, null)]
    [InlineData(ThinkingLevel.Minimal, "minimal")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.XHigh, "high")]
    [InlineData(ThinkingLevel.Max, "high")]
    public void openai_effort_mapping(ThinkingLevel level, string? expected)
    {
        Assert.Equal(expected, ThinkingLevels.EffortFor(level));
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    public void anthropic_effort_mapping(ThinkingLevel level, string expected)
    {
        Assert.Equal(expected, ThinkingLevels.EffortForAnthropic(level));
    }

    [Fact]
    public void the_config_carries_a_thinking_level()
    {
        Assert.Equal(ThinkingLevel.Off, new LlmConfig().Thinking);
        Assert.Equal(ThinkingLevel.High, new LlmConfig { Thinking = ThinkingLevel.High }.Thinking);
    }
}
