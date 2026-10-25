#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Streaming client for the Google Generative Language API
/// (Gemini streamGenerateContent with alt=sse).
/// </summary>
internal sealed class GeminiLlmClient(LlmConfig config) : ILlmClient
{
    public LlmConfig Config { get; } = config;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (Config.BaseUrl ?? "https://generativelanguage.googleapis.com")
            .TrimEnd('/')
            .Replace("/v1beta", "");

        var (systemText, turns) = SplitSystem(messages);
        var contents = BuildContents(turns);

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = Config.Temperature,
                ["maxOutputTokens"] = Config.MaxTokens > 0 ? Config.MaxTokens : 4096
            }
        };

        if (systemText is { Length: > 0 })
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemText })
            };

        if (tools.Count > 0)
            body["tools"] = LlmHttp.BuildGeminiTools(tools);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-goog-api-key"] = Config.ApiKey ?? ""
        };

        using var response = await LlmHttp.PostJsonAsync(
            $"{baseUrl}/v1beta/models/{Config.Model}:streamGenerateContent?alt=sse",
            body, headers, Config.Timeout, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

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

            if (chunk["candidates"] is not JsonArray candidates || candidates.Count == 0)
                continue;
            if (candidates[0] is not JsonObject candidate)
                continue;
            if (candidate["content"] is not JsonObject candidateContent)
                continue;
            if (candidateContent["parts"] is not JsonArray parts)
                continue;

            foreach (var partNode in parts)
            {
                if (partNode is not JsonObject part)
                    continue;

                if (part["text"] is { } text && text.GetValueKind() == JsonValueKind.String)
                {
                    var value = text.GetValue<string>();
                    if (value?.Length > 0)
                        yield return new LlmStreamEvent.TextDelta(value);
                }

                if (part["functionCall"] is JsonObject call)
                {
                    var name = LlmHttp.Str(call["name"]) ?? "";
                    var args = LlmHttp.NormalizeArguments(call["args"]);
                    yield return new LlmStreamEvent.ToolCall(
                        "call_" + Guid.NewGuid().ToString("N")[..8], name, args);
                }
            }
        }

        yield return new LlmStreamEvent.Completed(null);
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

    private static JsonArray BuildContents(List<ChatMessage> turns)
    {
        var array = new JsonArray();

        foreach (var message in turns)
        {
            switch (message.Role)
            {
                case "tool":
                    array.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = message.Name ?? "",
                                ["response"] = new JsonObject { ["result"] = message.Content }
                            }
                        })
                    });
                    break;

                case "assistant" when message.ToolCalls is { Count: > 0 }:
                    var parts = new JsonArray();
                    if (message.Content.Length > 0)
                        parts.Add(new JsonObject { ["text"] = message.Content });
                    foreach (var call in message.ToolCalls)
                    {
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["args"] = JsonNode.Parse(call.Arguments) as JsonObject
                                    ?? new JsonObject()
                            }
                        });
                    }
                    array.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = parts
                    });
                    break;

                case "assistant":
                    array.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content })
                    });
                    break;

                default:
                    array.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content })
                    });
                    break;
            }
        }

        return array;
    }
}
#endif
