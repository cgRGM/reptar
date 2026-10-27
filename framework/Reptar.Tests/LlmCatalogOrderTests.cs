using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Model list ordering. Provider catalogs arrive in no useful order, so the sort is what decides
/// whether the model someone just heard about appears first or is buried under the back catalogue.
/// </summary>
public sealed class LlmCatalogOrderTests
{
    private static List<string> Ordered(params string[] ids)
    {
        var list = ids.ToList();
        list.Sort(LlmCatalog.CompareByRecency);
        return list;
    }

    [Fact]
    public void a_higher_version_comes_first()
    {
        Assert.Equal(["gpt-5.6", "gpt-4o"], Ordered("gpt-4o", "gpt-5.6"));
    }

    [Fact]
    public void a_higher_major_beats_a_higher_minor()
    {
        // 6.0 outranks 5.9, which element-wise comparison gets right and string comparison does not.
        Assert.Equal(["gpt-6-sol", "gpt-5.9"], Ordered("gpt-5.9", "gpt-6-sol"));
    }

    [Fact]
    public void a_minor_version_is_compared_as_a_number_not_a_string()
    {
        // "5.10" sorts before "5.9" as text; as a version it comes after.
        Assert.Equal(["gpt-5.10", "gpt-5.9"], Ordered("gpt-5.9", "gpt-5.10"));
    }

    [Fact]
    public void a_dated_snapshot_comes_before_its_alias()
    {
        Assert.Equal(
            ["gpt-4o-2024-11-20", "gpt-4o"],
            Ordered("gpt-4o", "gpt-4o-2024-11-20"));
    }

    [Fact]
    public void alphabetical_order_would_have_been_wrong()
    {
        var ordered = Ordered("gpt-4o", "gpt-5.6", "gpt-6-sol", "claude-3-5-sonnet");

        // The whole point: the newest model is first, which sorting by name never produced.
        Assert.Equal("gpt-6-sol", ordered[0]);
        Assert.Equal("claude-3-5-sonnet", ordered[^1]);
    }

    [Fact]
    public void ordering_is_stable_for_equal_versions()
    {
        var first = Ordered("model-b-1", "model-a-1");
        var second = Ordered("model-a-1", "model-b-1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void an_id_without_a_version_sorts_last()
    {
        Assert.Equal(["gpt-5", "latest"], Ordered("latest", "gpt-5"));
    }

    [Fact]
    public void the_chatgpt_subscription_browses_the_openai_catalog()
    {
        // Without this mapping the connected provider lists only its default model, which reads
        // as a broken provider rather than a missing catalog entry.
        Assert.True(LlmCatalog.HasCuratedModels(LlmProviders.OpenAiChatGpt));
    }

    [Fact]
    public async Task the_subscription_lists_many_models_newest_first()
    {
        var models = await LlmCatalog.ListCuratedAsync(LlmProviders.OpenAiChatGpt);

        // Skipped when the network is unavailable; the mapping test above still holds.
        if (models.Count == 0)
            return;

        Assert.True(models.Count > 5, $"expected a real catalog, got {models.Count}");

        for (var i = 1; i < models.Count; i++)
        {
            Assert.True(
                LlmCatalog.CompareByRecency(models[i - 1].Id, models[i].Id) <= 0,
                $"{models[i - 1].Id} should not sort before {models[i].Id}");
        }
    }
}
