#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Streaming client for OpenAI-compatible Chat Completions endpoints
/// (OpenAI, OpenRouter, Vercel AI Gateway, and local Ollama in OpenAI mode).
/// </summary>
public class OpenAiLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (Config.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        var isOllama = Config.Provider == LlmProvider.Ollama;
        var endpoint = isOllama ? $"{baseUrl}/v1/chat/completions" : $"{baseUrl}/chat/completions";

        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["stream"] = true,
            ["temperature"] = Config.Temperature,
            ["messages"] = BuildMessages(messages)
        };

        if (Config.MaxTokens > 0)
            body["max_tokens"] = Config.MaxTokens;

        // Reasoning models take an effort hint rather than a token budget. Sending it to a model
        // that does not reason is a 400, so it is only set when a level was asked for.
        if (ThinkingLevels.EffortFor(Config.Thinking) is { Length: > 0 } effort)
            body["reasoning_effort"] = effort;

        if (tools.Count > 0)
            body["tools"] = LlmHttp.BuildOpenAiTools(tools);

        // Request the token accounting chunk so the status bar can show usage.
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Config.ApiKey))
            headers["authorization"] = "Bearer " + Config.ApiKey;

        ClientIdentity.Apply(headers, Config.ProviderKey, baseUrl, Config.Attribution, Config.SessionId);

        using var response = await LlmHttp.PostJsonAsync(
            endpoint, body, headers, Config.Timeout, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        string? finishReason = null;
        var promptTokens = 0;
        var completionTokens = 0;
        var toolBuilders = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();

        await foreach (var frame in SseParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data is null or "[DONE]")
                continue;

            JsonObject chunk;
            try
            {
                chunk = LlmHttp.ParseObject(frame.Data);
            }
            catch (LlmException)
            {
                continue; // skip keep-alives and malformed frames
            }

            var choices = chunk["choices"] as JsonArray;
            var choice = choices?.Count > 0 ? choices[0] as JsonObject : null;
            if (choice is null)
                continue;

            if (choice["finish_reason"] is { } done && done.GetValueKind() == JsonValueKind.String)
                finishReason = done.GetValue<string>();

            if (chunk["usage"] is JsonObject usage)
            {
                promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
                completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? completionTokens;
            }

            var delta = choice["delta"] as JsonObject;
            if (delta is null)
                continue;

            if (delta["content"] is { } content && content.GetValueKind() == JsonValueKind.String)
            {
                var text = content.GetValue<string>();
                if (text?.Length > 0)
                    yield return new LlmStreamEvent.TextDelta(text);
            }

            if (delta["reasoning_content"] is { } reasoning && reasoning.GetValueKind() == JsonValueKind.String)
            {
                var text = reasoning.GetValue<string>();
                if (text?.Length > 0)
                    yield return new LlmStreamEvent.ReasoningDelta(text);
            }

            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var callNode in calls)
                {
                    if (callNode is not JsonObject call)
                        continue;

                    var index = call["index"] is { } idx ? idx.GetValue<int>() : 0;
                    var id = call["id"] is { } cid ? LlmHttp.Str(cid) : null;
                    var fn = call["function"] as JsonObject;
                    var name = fn?["name"] is { } nm ? LlmHttp.Str(nm) : null;
                    var args = fn?["arguments"] is { } ar ? LlmHttp.Str(ar) ?? "" : "";

                    if (!toolBuilders.TryGetValue(index, out var builder))
                    {
                        builder = (id, name, new StringBuilder());
                        toolBuilders[index] = builder;
                    }
                    else
                    {
                        if (id is not null && builder.Id is null)
                            builder = (id, builder.Name, builder.Args);
                        if (name is not null && builder.Name is null)
                            builder = (builder.Id, name, builder.Args);
                        toolBuilders[index] = builder;
                    }

                    builder.Args.Append(args);
                    yield return new LlmStreamEvent.ToolCallDelta(index, builder.Id, builder.Name, args);
                }
            }
        }

        if (promptTokens + completionTokens > 0)
            yield return new LlmStreamEvent.Usage(promptTokens, completionTokens);

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

    public static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "system":
                    array.Add(new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = message.Content
                    });
                    break;

                case "assistant" when message.ToolCalls is { Count: > 0 }:
                    var assistantWithTools = new JsonObject { ["role"] = "assistant" };
                    if (message.Content.Length > 0)
                        assistantWithTools["content"] = message.Content;
                    else
                        assistantWithTools["content"] = null;

                    var toolCalls = new JsonArray();
                    foreach (var call in message.ToolCalls)
                    {
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments
                            }
                        });
                    }
                    assistantWithTools["tool_calls"] = toolCalls;
                    array.Add(assistantWithTools);
                    break;

                case "assistant":
                    array.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content
                    });
                    break;

                case "tool":
                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = message.ToolCallId,
                        ["content"] = message.Content
                    });
                    break;

                default:
                    if (message.Images is { Count: > 0 })
                    {
                        var content = new JsonArray();
                        if (message.Content.Length > 0)
                            content.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = message.Content
                            });
                        foreach (var image in message.Images)
                        {
                            content.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject
                                {
                                    ["url"] = $"data:{image.MimeType};base64,{image.Base64Data}"
                                }
                            });
                        }
                        array.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = content
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

        return array;
    }
}

/// <summary>
/// Ollama uses the OpenAI-compatible wire format with a local base URL.
/// </summary>
internal sealed class OllamaLlmClient : OpenAiLlmClient
{
    public OllamaLlmClient(LlmConfig config) : base(config) { }
}
#endif
