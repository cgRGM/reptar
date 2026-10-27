using System.Text.Json.Nodes;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the models.dev <c>reasoning_options</c> parser. This is what decides which
/// levels Shift+Tab can offer, so getting it wrong either hides the control or offers a level the
/// provider rejects.
/// </summary>
public sealed class ReasoningOptionsTests
{
    private static LlmCatalog.CuratedModel Parse(JsonNode? reasoningOptions, bool reasoning = true)
    {
        var method = typeof(LlmCatalog).GetMethod(
            "ParseReasoningOptions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [reasoningOptions, reasoning]);
        Assert.NotNull(result);

        var tuple = (ValueTuple<IReadOnlyList<ThinkingLevel>?, string?>)result!;
        return new LlmCatalog.CuratedModel(
            "test", "Test", 100_000,
            Reasoning: reasoning,
            Thinking: tuple.Item1,
            ReasoningKind: tuple.Item2);
    }

    [Fact]
    public void a_non_reasoning_model_offers_only_off()
    {
        var model = Parse(null, reasoning: false);

        Assert.Equal([ThinkingLevel.Off], model.SupportedThinking);
    }

    [Fact]
    public void an_effort_list_becomes_exactly_those_levels()
    {
        var model = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "effort",
            ["values"] = new JsonArray("low", "medium", "high")
        }));

        Assert.Equal(
            [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High],
            model.SupportedThinking);
        Assert.Equal("effort", model.ReasoningKind);
    }

    [Fact]
    public void off_is_only_offered_when_the_provider_accepts_none()
    {
        var withNone = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "effort",
            ["values"] = new JsonArray("none", "low", "high")
        }));

        Assert.Contains(ThinkingLevel.Off, withNone.SupportedThinking);

        var withoutNone = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "effort",
            ["values"] = new JsonArray("low", "high")
        }));

        Assert.DoesNotContain(ThinkingLevel.Off, withoutNone.SupportedThinking);
    }

    [Fact]
    public void extended_levels_appear_only_when_the_provider_lists_them()
    {
        var model = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "effort",
            ["values"] = new JsonArray("none", "low", "medium", "high", "xhigh", "max")
        }));

        Assert.Contains(ThinkingLevel.XHigh, model.SupportedThinking);
        Assert.Contains(ThinkingLevel.Max, model.SupportedThinking);
    }

    [Fact]
    public void a_toggle_model_offers_off_and_high()
    {
        var model = Parse(new JsonArray(new JsonObject { ["type"] = "toggle" }));

        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.High], model.SupportedThinking);
        Assert.Equal("toggle", model.ReasoningKind);
    }

    [Fact]
    public void a_budget_tokens_model_offers_the_standard_ladder()
    {
        var model = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "budget_tokens",
            ["min"] = 1024,
            ["max"] = 32000
        }));

        Assert.Equal(ThinkingLevels.Supported(true), model.SupportedThinking);
        Assert.Equal("budget_tokens", model.ReasoningKind);
    }

    [Fact]
    public void a_reasoning_model_with_no_options_falls_back_to_the_standard_ladder()
    {
        var model = Parse(null);

        Assert.Equal(ThinkingLevels.Supported(true), model.SupportedThinking);
        Assert.Null(model.ReasoningKind);
    }

    [Fact]
    public void an_empty_effort_list_falls_back_rather_than_offering_nothing()
    {
        var model = Parse(new JsonArray(new JsonObject
        {
            ["type"] = "effort",
            ["values"] = new JsonArray()
        }));

        Assert.NotEmpty(model.SupportedThinking);
    }

    [Fact]
    public void the_first_recognised_option_wins()
    {
        var model = Parse(new JsonArray(
            new JsonObject { ["type"] = "unknown" },
            new JsonObject { ["type"] = "toggle" }));

        Assert.Equal("toggle", model.ReasoningKind);
    }
}
