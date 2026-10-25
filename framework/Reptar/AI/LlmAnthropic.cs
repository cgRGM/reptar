#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Streaming client for the Anthropic Messages API (content block deltas + tool_use).
/// </summary>
internal sealed class AnthropicLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (Config.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/');

        var (systemText, turns) = SplitSystem(messages);
        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["stream"] = true,
            ["temperature"] = Config.Temperature,
            ["max_tokens"] = Config.MaxTokens > 0 ? Config.MaxTokens : 4096,
            ["messages"] = BuildMessages(turns)
        };

        if (systemText is { Length: > 0 })
            body["system"] = systemText;

        if (tools.Count > 0)
            body["tools"] = LlmHttp.BuildAnthropicTools(tools);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = "2023-06-01"
        };
        if (!string.IsNullOrEmpty(Config.ApiKey))
            headers["x-api-key"] = Config.ApiKey;

        using var response = await LlmHttp.PostJsonAsync(
            $"{baseUrl}/v1/messages", body, headers, Config.Timeout, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        string? finishReason = null;
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
                case "message_delta":
                    if (chunk["delta"]?["stop_reason"] is { } stop)
                        finishReason = LlmHttp.Str(stop);
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

    private static JsonArray BuildMessages(List<ChatMessage> turns)
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
                    array.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = message.Content
                    });
                    break;
            }
        }

        FlushToolResults(array, ref pendingToolResults);
        return array;
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
