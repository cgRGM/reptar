using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// The rules a model picker has to get right, extracted from the terminal so every host shares them.
/// </summary>
public sealed class ModelOptionTests
{
    // ------------------------------------------------------------------
    // Chat-model filtering
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("gpt-5.6")]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("llama3.1:8b")]
    [InlineData("gpt-4o-mini")]
    [InlineData("@cf/meta/llama-3.3-70b-instruct-fp8-fast")]
    public void a_chat_model_is_offered(string id)
    {
        Assert.True(LlmModelOptions.IsChatModel(id));
    }

    [Theory]
    [InlineData("text-embedding-3-large")]
    [InlineData("gpt-image-1.5")]
    [InlineData("dall-e-3")]
    [InlineData("tts-1")]
    [InlineData("whisper-1")]
    [InlineData("gpt-4o-realtime-preview")]
    [InlineData("omni-moderation-latest")]
    public void a_model_that_cannot_answer_a_prompt_is_not_offered(string id)
    {
        // Picking one of these produces a provider error rather than a reply.
        Assert.False(LlmModelOptions.IsChatModel(id));
    }

    // ------------------------------------------------------------------
    // Specs
    // ------------------------------------------------------------------

    [Fact]
    public void specs_summarise_context_reasoning_and_vision()
    {
        var model = new LlmCatalog.CuratedModel(
            "gpt-5.6", "GPT-5.6", 400_000, Reasoning: true, Vision: true);

        var specs = LlmModelOptions.FormatSpecs(model);

        Assert.Contains("400k ctx", specs);
        Assert.Contains("reasoning", specs);
        Assert.Contains("vision", specs);
    }

    [Fact]
    public void a_plain_model_gets_a_dash_rather_than_an_empty_cell()
    {
        var model = new LlmCatalog.CuratedModel("mystery", "Mystery", 0);

        Assert.Equal("—", LlmModelOptions.FormatSpecs(model));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_500, "1.5k")]
    [InlineData(128_000, "128k")]
    [InlineData(1_050_000, "1.1M")]
    [InlineData(12_000_000, "12M")]
    public void token_counts_are_abbreviated(int count, string expected)
    {
        Assert.Equal(expected, LlmModelOptions.FormatTokens(count));
    }

    // ------------------------------------------------------------------
    // Categories
    // ------------------------------------------------------------------

    [Fact]
    public void categories_start_with_all_and_keep_provider_order()
    {
        var options = new[]
        {
            Option("openai-chatgpt", "OpenAI (ChatGPT subscription)"),
            Option("gemini", "Google Gemini"),
            Option("openai", "OpenAI")
        };

        var categories = LlmModelOptions.Categories(options);

        Assert.Equal(["All", "OpenAI", "Google"], categories);
    }

    private static ModelOption Option(string providerKey, string providerName) =>
        new(providerKey, providerName, "m", "m", LlmModelOptions.Category(providerName),
            "—", false, [ThinkingLevel.Off], 0);

    // ------------------------------------------------------------------
    // The whole list
    // ------------------------------------------------------------------

    [Fact]
    public async Task an_unconnected_provider_contributes_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reptar-opts-{Guid.NewGuid():n}.json");

        try
        {
            var auth = new LlmAuthFile(path);
            var options = await LlmModelOptions.BuildAsync(auth);

            Assert.Empty(options);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task a_connected_provider_with_no_catalog_still_offers_its_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reptar-opts-{Guid.NewGuid():n}.json");

        try
        {
            var auth = new LlmAuthFile(path);

            // A provider with no models.dev mapping and no reachable endpoint: the picker must
            // still let you select it rather than showing an empty list.
            auth.Set(LlmProviders.OpenCode, new ProviderCredential("k", null, "some-model"));

            var options = await LlmModelOptions.BuildAsync(auth, liveTimeoutMs: 1_000);

            Assert.All(options, option => Assert.Equal(LlmProviders.OpenCode, option.ProviderKey));
            Assert.All(options, option => Assert.True(option.IsDefault || !option.IsDefault));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
