#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Streaming client for the OpenAI Responses API (<c>/v1/responses</c>).
/// </summary>
/// <remarks>
/// A separate client because the wire shape is genuinely different from chat completions rather
/// than a variation of it: the transcript is a flat list of typed <c>input</c> items instead of
/// role/content messages, a tool call is its own item type, and the stream is a sequence of
/// <c>response.*</c> events rather than deltas attached to a choice.
///
/// This is the path a ChatGPT subscription uses. The subscription token is scoped to
/// <c>api.openai.com/v1</c> and is not accepted by <c>/chat/completions</c>, so supporting account
/// sign-in means supporting this endpoint.
/// </remarks>
public sealed class OpenAiResponsesLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (Config.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        var endpoint = $"{baseUrl}/responses";

        var (instructions, input) = BuildInput(messages);

        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["input"] = input,
            ["stream"] = true,
            // Server-side storage would tie the conversation to OpenAI's account state; the
            // transcript is ours to keep.
            ["store"] = false
        };

        if (instructions.Length > 0)
            body["instructions"] = instructions.ToString();

        if (Config.MaxTokens > 0)
            body["max_output_tokens"] = Config.MaxTokens;

        if (ThinkingLevels.EffortFor(Config.Thinking) is { Length: > 0 } effort)
            body["reasoning"] = new JsonObject { ["effort"] = effort };

        if (tools.Count > 0)
            body["tools"] = BuildTools(tools);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Config.ApiKey))
            headers["authorization"] = "Bearer " + Config.ApiKey;

        ClientIdentity.Apply(headers, Config.ProviderKey, baseUrl, Config.Attribution, Config.SessionId);

        using var response = await LlmHttp.PostJsonAsync(
            endpoint, body, headers, Config.Timeout, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var evt in ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// Flattens a transcript into the Responses API's input item list.
    /// </summary>
    /// <remarks>
    /// System text becomes <c>instructions</c> rather than an item, assistant tool calls become
    /// sibling <c>function_call</c> items rather than fields on the message, and tool results become
    /// <c>function_call_output</c> items. Getting this wrong is not a formatting difference: the
    /// API rejects a tool call nested inside assistant content.
    /// </remarks>
    private static (StringBuilder Instructions, JsonArray Input) BuildInput(
        IReadOnlyList<ChatMessage> messages)
    {
        var instructions = new StringBuilder();
        var input = new JsonArray();

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case AgentRoles.System:
                    if (instructions.Length > 0)
                        instructions.Append('\n');
                    instructions.Append(message.Content);
                    break;

                case AgentRoles.Tool:
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId ?? "",
                        ["output"] = message.Content
                    });
                    break;

                case AgentRoles.Assistant:
                    if (message.Content.Length > 0)
                    {
                        input.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = message.Content
                            })
                        });
                    }

                    if (message.ToolCalls is { Count: > 0 } calls)
                    {
                        foreach (var call in calls)
                        {
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call",
                                ["call_id"] = call.Id,
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments
                            });
                        }
                    }
                    break;

                default:
                    input.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = message.Content
                        })
                    });
                    break;
            }
        }

        return (instructions, input);
    }

    private static JsonArray BuildTools(IReadOnlyList<AiToolDefinition> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = LlmHttp.ToNode(tool.Parameters),
                // `strict` would require an exact-schema match on every argument; tool schemas
                // built by hand rarely satisfy it, and a rejection reads as a model error.
                ["strict"] = false
            });
        }

        return array;
    }


    /// <summary>
    /// Translates the response event stream into LlmStreamEvent values.
    /// </summary>
    private static async IAsyncEnumerable<LlmStreamEvent> ReadStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var calls = new Dictionary<string, PendingCall>(StringComparer.Ordinal);
        var finished = new List<AiToolCall>();
        var text = new StringBuilder();
        string? stop = null;
        string? failure = null;

        await foreach (var frame in SseParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data is null)
                continue;

            JsonObject? evt;
            try
            {
                evt = JsonNode.Parse(frame.Data) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (evt is null)
                continue;

            var type = evt["type"]?.GetValue<string>() ?? "";

            if (type == "response.output_text.delta")
            {
                var delta = evt["delta"]?.GetValue<string>() ?? "";
                if (delta.Length > 0)
                {
                    text.Append(delta);
                    yield return new LlmStreamEvent.TextDelta(delta);
                }
            }
            else if (type is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta")
            {
                var delta = evt["delta"]?.GetValue<string>() ?? "";
                if (delta.Length > 0)
                    yield return new LlmStreamEvent.ReasoningDelta(delta);
            }
            else if (type == "response.output_item.added")
            {
                if (evt["item"] is JsonObject item &&
                    item["type"]?.GetValue<string>() == "function_call")
                {
                    var itemId = item["id"]?.GetValue<string>() ?? "";
                    calls[itemId] = new PendingCall(
                        item["call_id"]?.GetValue<string>() ?? itemId,
                        item["name"]?.GetValue<string>() ?? "");
                }
            }
            else if (type == "response.function_call_arguments.delta")
            {
                var itemId = evt["item_id"]?.GetValue<string>() ?? "";
                var delta = evt["delta"]?.GetValue<string>() ?? "";

                if (calls.TryGetValue(itemId, out var call) && delta.Length > 0)
                {
                    call.Arguments.Append(delta);
                    yield return new LlmStreamEvent.ToolCallDelta(
                        calls.Keys.ToList().IndexOf(itemId), call.CallId, call.Name, delta);
                }
            }
            else if (type == "response.output_item.done")
            {
                if (evt["item"] is JsonObject item &&
                    item["type"]?.GetValue<string>() == "function_call")
                {
                    var callId = item["call_id"]?.GetValue<string>() ?? "";
                    var name = item["name"]?.GetValue<string>() ?? "";
                    var args = item["arguments"]?.GetValue<string>() ?? "{}";
                    finished.Add(new AiToolCall(callId, name, args));
                    yield return new LlmStreamEvent.ToolCall(callId, name, args);
                }
            }
            else if (type is "response.completed" or "response.incomplete")
            {
                if (evt["response"] is JsonObject result)
                {
                    stop = result["status"]?.GetValue<string>() == "incomplete" ? "length" : "stop";

                    if (result["usage"] is JsonObject usage)
                    {
                        yield return new LlmStreamEvent.Usage(
                            Int(usage["input_tokens"]),
                            Int(usage["output_tokens"]),
                            Int(usage["input_tokens_details"]?["cached_tokens"]));
                    }
                }
            }
            else if (type == "response.failed")
            {
                failure = evt["response"]?["error"]?["message"]?.GetValue<string>()
                    ?? "The provider reported a failed response.";
            }
        }

        if (failure is { Length: > 0 })
            yield return new LlmStreamEvent.Failed(failure);

        yield return new LlmStreamEvent.Completed(finished.Count > 0 ? "tool_use" : stop ?? "stop");
    }

    private sealed class PendingCall(string callId, string name)
    {
        public string CallId { get; } = callId;
        public string Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
    }

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
}
#endif
