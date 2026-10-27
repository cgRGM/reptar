using System.Text.Json.Nodes;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for Anthropic prompt caching. The breakpoints are the whole feature: without
/// the last-message one, every turn re-bills the entire conversation at full input price.
/// </summary>
public sealed class PromptCacheTests
{
    private static JsonObject Block(string type, string text) => new()
    {
        ["type"] = type,
        ["text"] = text
    };

    private static bool IsCached(JsonNode? node) =>
        node is JsonObject obj &&
        obj["cache_control"] is JsonObject control &&
        control["type"]?.GetValue<string>() == "ephemeral";

    // ------------------------------------------------------------------
    // Breakpoint 1: system prompt
    // ------------------------------------------------------------------

    [Fact]
    public void the_system_prompt_becomes_a_cacheable_block_when_caching_is_on()
    {
        var system = LlmHttp.BuildAnthropicSystem("you are a coding agent", cache: true);

        var array = Assert.IsType<JsonArray>(system);
        var block = Assert.IsType<JsonObject>(array[0]);

        Assert.Equal("you are a coding agent", block["text"]!.GetValue<string>());
        Assert.True(IsCached(block));
    }

    [Fact]
    public void the_system_prompt_stays_a_plain_string_when_caching_is_off()
    {
        var system = LlmHttp.BuildAnthropicSystem("you are a coding agent", cache: false);

        Assert.Equal("you are a coding agent", system!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // Breakpoint 2: last tool definition
    // ------------------------------------------------------------------

    private static AiToolDefinition Tool(string name) => new()
    {
        Name = name,
        Description = $"{name} tool",
        Method = "POST",
        Path = $"/{name}",
        Parameters = new Dictionary<string, object?> { ["type"] = "object" }
    };

    [Fact]
    public void the_last_tool_definition_carries_the_breakpoint()
    {
        var tools = LlmHttp.BuildAnthropicToolsCached([Tool("read_file"), Tool("write_file")]);

        Assert.Equal(2, tools.Count);
        Assert.False(IsCached(tools[0]));
        Assert.True(IsCached(tools[1]));
    }

    [Fact]
    public void caching_tools_does_not_change_their_declarations()
    {
        var plain = LlmHttp.BuildAnthropicTools([Tool("read_file")]);
        var cached = LlmHttp.BuildAnthropicToolsCached([Tool("read_file")]);

        var plainName = plain[0]!["name"]!.GetValue<string>();
        var cachedName = cached[0]!["name"]!.GetValue<string>();

        Assert.Equal(plainName, cachedName);
        Assert.Equal(
            plain[0]!["input_schema"]!.ToJsonString(),
            cached[0]!["input_schema"]!.ToJsonString());
    }

    [Fact]
    public void an_empty_tool_list_is_left_alone()
    {
        Assert.Empty(LlmHttp.BuildAnthropicToolsCached([]));
    }

    // ------------------------------------------------------------------
    // Breakpoint 3: last content block
    // ------------------------------------------------------------------

    [Fact]
    public void the_last_text_block_of_the_last_message_is_cached()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "first" },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(Block("text", "latest question"))
            }
        };

        AnthropicLlmClient.MarkLastBlockCacheable(messages);

        Assert.False(IsCached(messages[0]!["content"]));
        var blocks = Assert.IsType<JsonArray>(messages[1]!["content"]);
        Assert.True(IsCached(blocks[^1]));
    }

    [Fact]
    public void a_plain_string_last_message_is_converted_to_a_cached_block()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hello" }
        };

        AnthropicLlmClient.MarkLastBlockCacheable(messages);

        var blocks = Assert.IsType<JsonArray>(messages[0]!["content"]);
        var block = Assert.IsType<JsonObject>(blocks[0]);
        Assert.Equal("hello", block["text"]!.GetValue<string>());
        Assert.True(IsCached(block));
    }

    [Fact]
    public void a_tool_result_block_can_carry_the_breakpoint()
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = "c1",
                    ["content"] = "file contents"
                })
            }
        };

        AnthropicLlmClient.MarkLastBlockCacheable(messages);

        var blocks = Assert.IsType<JsonArray>(messages[0]!["content"]);
        Assert.True(IsCached(blocks[^1]));
    }

    [Fact]
    public void an_empty_message_list_is_left_alone()
    {
        var messages = new JsonArray();
        AnthropicLlmClient.MarkLastBlockCacheable(messages);

        Assert.Empty(messages);
    }

    [Fact]
    public void a_trailing_assistant_message_can_carry_the_breakpoint()
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(Block("text", "thinking out loud"))
            }
        };

        AnthropicLlmClient.MarkLastBlockCacheable(messages);

        var blocks = Assert.IsType<JsonArray>(messages[0]!["content"]);
        Assert.True(IsCached(blocks[^1]));
    }

    // ------------------------------------------------------------------
    // Policy
    // ------------------------------------------------------------------

    [Fact]
    public void caching_is_on_by_default()
    {
        Assert.True(new LlmConfig().PromptCache);
    }

    [Fact]
    public void a_small_prefix_is_not_worth_warming()
    {
        var config = new LlmConfig { ProviderKey = LlmProviders.Anthropic, PromptCache = true };

        Assert.False(CacheWarmer.ShouldWarm(config, "short prompt", toolCount: 1));
        Assert.True(CacheWarmer.ShouldWarm(config, new string('x', 8_000), toolCount: 6));
    }

    [Fact]
    public void warming_is_skipped_for_providers_that_cache_automatically()
    {
        var config = new LlmConfig { ProviderKey = LlmProviders.OpenAi, PromptCache = true };

        Assert.False(CacheWarmer.ShouldWarm(config, new string('x', 20_000), toolCount: 10));
    }

    [Fact]
    public void warming_is_skipped_when_caching_is_off()
    {
        var config = new LlmConfig { ProviderKey = LlmProviders.Anthropic, PromptCache = false };

        Assert.False(CacheWarmer.ShouldWarm(config, new string('x', 20_000), toolCount: 10));
    }

    [Fact]
    public async Task warming_a_provider_without_cache_support_returns_a_skip_reason()
    {
        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("ignored"));

        var result = await CacheWarmer.WarmAsync(client, "short", []);

        Assert.False(result.Warmed);
        Assert.NotNull(result.Skipped);
    }
}
