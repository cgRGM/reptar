#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Streaming client for the Anthropic Messages API (content block deltas + tool_use).
/// </summary>
public sealed class AnthropicLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (Config.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/');

        var (systemText, turns) = SplitSystem(messages);
        var cache = Config.PromptCache;

        // Thinking shares the response ceiling with the answer, so the ceiling is raised to fit
        // the budget and the budget is capped to leave room to actually reply.
        var (maxTokens, thinkingBudget) = ThinkingLevels.AdjustMaxTokens(
            Config.MaxTokens > 0 ? Config.MaxTokens : null,
            Config.MaxOutputTokens > 0 ? Config.MaxOutputTokens : DefaultMaxTokens,
            Config.Thinking);

        // A ceiling larger than the remaining context is rejected outright, so fit it to what is
        // actually left. The thinking budget is re-derived from the clamped ceiling.
        var contextTokens = ContextCompaction.EstimateContextTokens(messages);
        maxTokens = ThinkingLevels.ClampToContext(maxTokens, contextTokens, Config.ContextWindow);

        if (thinkingBudget > 0 && maxTokens <= thinkingBudget)
        {
            thinkingBudget = Math.Min(
                thinkingBudget,
                Math.Max(0, maxTokens - ThinkingLevels.MinAnswerTokens));
        }
        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["stream"] = true,
            ["temperature"] = Config.Temperature,
            ["max_tokens"] = maxTokens,
            ["messages"] = BuildMessages(turns, cache)
        };

        // Three breakpoints, which is what Anthropic's cache is designed around:
        //   1. the system prompt (static for the session)
        //   2. the last tool definition (static for the session)
        //   3. the last content block of the last message (the conversation so far)
        // Without (3) every turn re-bills the entire history at full input price; with it, a
        // long session pays 0.1x for everything it already sent.
        if (thinkingBudget > 0)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = thinkingBudget
            };

            // Temperature is incompatible with extended thinking.
            body.Remove("temperature");
        }

        if (systemText is { Length: > 0 })
            body["system"] = LlmHttp.BuildAnthropicSystem(systemText, cache);

        if (tools.Count > 0)
        {
            body["tools"] = cache
                ? LlmHttp.BuildAnthropicToolsCached(tools)
                : LlmHttp.BuildAnthropicTools(tools);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = "2023-06-01"
        };
        if (!string.IsNullOrEmpty(Config.ApiKey))
            headers["x-api-key"] = Config.ApiKey;

        ClientIdentity.Apply(headers, Config.ProviderKey, baseUrl, Config.Attribution, Config.SessionId);

        using var response = await LlmHttp.PostJsonAsync(
            $"{baseUrl}/v1/messages", body, headers, Config.Timeout, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        string? finishReason = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        var toolBuilders = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();

        await foreach (var frame in SseParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data is null)
                continue;

            JsonObject chunk;
            try
            {
                chunk = LlmHttp.ParseObject(frame.Data);
            }
            catch (LlmException)
            {
                continue;
            }

            var type = chunk["type"] is { } t ? t.GetValue<string>() : null;

            switch (type)
            {
                case "message_start":
                    if (chunk["message"]?["usage"] is JsonObject startUsage)
                    {
                        if (startUsage["input_tokens"] is { } inputUsage)
                            inputTokens = inputUsage.GetValue<int>();
                        if (startUsage["cache_read_input_tokens"] is { } cacheRead)
                            cacheReadTokens = cacheRead.GetValue<int>();
                        if (startUsage["cache_creation_input_tokens"] is { } cacheWrite)
                            cacheWriteTokens = cacheWrite.GetValue<int>();
                    }
                    break;

                case "message_delta":
                    if (chunk["delta"]?["stop_reason"] is { } stop)
                        finishReason = LlmHttp.Str(stop);
                    if (chunk["usage"]?["output_tokens"] is { } outputUsage)
                        outputTokens = outputUsage.GetValue<int>();
                    break;

                case "content_block_start":
                    if (chunk["content_block"] is JsonObject block &&
                        block["type"]?.GetValue<string>() == "tool_use")
                    {
                        var index = chunk["index"]?.GetValue<int>() ?? 0;
                        toolBuilders[index] = (
                            LlmHttp.Str(block["id"]),
                            LlmHttp.Str(block["name"]),
                            new StringBuilder());
                    }
                    break;

                case "content_block_delta":
                    if (chunk["delta"] is not JsonObject delta)
                        break;

                    var index2 = chunk["index"]?.GetValue<int>() ?? 0;
                    var deltaType = delta["type"]?.GetValue<string>();

                    if (deltaType == "text_delta" && delta["text"] is { } text)
                    {
                        var value = text.GetValue<string>();
                        if (value?.Length > 0)
                            yield return new LlmStreamEvent.TextDelta(value);
                    }
                    else if (deltaType == "thinking_delta" && delta["thinking"] is { } think)
                    {
                        var value = think.GetValue<string>();
                        if (value?.Length > 0)
                            yield return new LlmStreamEvent.ReasoningDelta(value);
                    }
                    else if (deltaType == "input_json_delta" && delta["partial_json"] is { } partial)
                    {
                        var value = partial.GetValue<string>() ?? "";
                        if (toolBuilders.TryGetValue(index2, out var builder))
                        {
                            builder.Args.Append(value);
                            yield return new LlmStreamEvent.ToolCallDelta(
                                index2, builder.Id, builder.Name, value);
                        }
                    }
                    break;
            }
        }

        if (inputTokens + outputTokens > 0)
            yield return new LlmStreamEvent.Usage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);

        foreach (var (_, value) in toolBuilders)
        {
            if (value.Name is not null)
                yield return new LlmStreamEvent.ToolCall(
                    value.Id ?? "call_" + Guid.NewGuid().ToString("N")[..8],
                    value.Name,
                    LlmHttp.NormalizeArguments(value.Args.ToString()));
        }

        yield return new LlmStreamEvent.Completed(finishReason);
    }

    private static (string? System, List<ChatMessage> Turns) SplitSystem(IReadOnlyList<ChatMessage> messages)
    {
        string? systemText = null;
        var turns = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (message.Role == "system")
                systemText = string.IsNullOrEmpty(systemText) ? message.Content : systemText + "\n" + message.Content;
            else
                turns.Add(message);
        }

        return (systemText, turns);
    }

    /// <summary>
    /// Ceiling used when the model's real one is unknown. Deliberately small: an over-large
    /// ceiling is a provider 400, while an under-large one only caps the answer.
    /// </summary>
    private const int DefaultMaxTokens = 8_192;

    private static JsonArray BuildMessages(List<ChatMessage> turns, bool cache)
    {
        // Anthropic requires strictly alternating user/assistant turns and
        // tool results to be grouped into the following user turn.
        var array = new JsonArray();
        JsonArray? pendingToolResults = null;

        foreach (var message in turns)
        {
            switch (message.Role)
            {
                case "tool":
                    (pendingToolResults ??= new JsonArray()).Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Content
                    });
                    break;

                case "assistant" when message.ToolCalls is { Count: > 0 }:
                    FlushToolResults(array, ref pendingToolResults);

                    var assistant = new JsonObject { ["role"] = "assistant" };
                    var contentArray = new JsonArray();

                    if (message.Content.Length > 0)
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = message.Content
                        });

                    foreach (var call in message.ToolCalls)
                    {
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = JsonNode.Parse(call.Arguments) as JsonObject
                                ?? new JsonObject()
                        });
                    }

                    assistant["content"] = contentArray;
                    array.Add(assistant);
                    break;

                case "assistant":
                    FlushToolResults(array, ref pendingToolResults);
                    array.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content
                    });
                    break;

                default:
                    FlushToolResults(array, ref pendingToolResults);
                    if (message.Images is { Count: > 0 })
                    {
                        var blocks = new JsonArray();
                        if (message.Content.Length > 0)
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = message.Content
                            });
                        foreach (var image in message.Images)
                        {
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = image.MimeType,
                                    ["data"] = image.Base64Data
                                }
                            });
                        }
                        array.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = blocks
                        });
                    }
                    else
                    {
                        array.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = message.Content
                        });
                    }
                    break;
            }
        }

        FlushToolResults(array, ref pendingToolResults);

        if (cache)
            MarkLastBlockCacheable(array);

        return array;
    }

    /// <summary>
    /// Puts a cache breakpoint on the last content block of the last message, so the whole
    /// conversation prefix is cached. Anthropic allows four breakpoints per request; the system
    /// prompt and the tool block use two, leaving this one for history.
    /// </summary>
    public static void MarkLastBlockCacheable(JsonArray messages)
    {
        if (messages.Count == 0)
            return;

        if (messages[^1] is not JsonObject last)
            return;

        var role = last["role"]?.GetValue<string>();
        if (role is not ("user" or "assistant"))
            return;

        switch (last["content"])
        {
            case JsonArray blocks when blocks.Count > 0 && blocks[^1] is JsonObject block:
                // Only block kinds Anthropic accepts cache_control on.
                var type = block["type"]?.GetValue<string>();
                if (type is "text" or "image" or "tool_result" or "tool_use")
                    block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                break;

            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0:
                last["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                });
                break;
        }
    }

    private static void FlushToolResults(JsonArray target, ref JsonArray? pending)
    {
        if (pending is null || pending.Count == 0)
            return;

        target.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = pending
        });
        pending = null;
    }
}
#endif
