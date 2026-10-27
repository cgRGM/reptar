#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Hand-written JSON for <see cref="ChatMessage"/>, for storage and checkpointing.
/// </summary>
/// <remarks>
/// Deliberately not <c>JsonSerializer</c>: the framework targets Native AOT, where
/// reflection-based serialization is disabled and throws at runtime. This bit the settings store,
/// which silently returned defaults for months, so message persistence gets an explicit mapping
/// rather than a second chance at the same failure.
/// </remarks>
public static class AgentMessageJson
{
    /// <summary>Serializes one message.</summary>
    public static JsonObject ToJson(ChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (message.Name is { Length: > 0 })
            node["name"] = message.Name;

        if (message.ToolCallId is { Length: > 0 })
            node["toolCallId"] = message.ToolCallId;

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            var array = new JsonArray();
            foreach (var call in calls)
            {
                array.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments
                });
            }
            node["toolCalls"] = array;
        }

        if (message.Images is { Count: > 0 } images)
        {
            var array = new JsonArray();
            foreach (var image in images)
            {
                array.Add(new JsonObject
                {
                    ["mimeType"] = image.MimeType,
                    ["data"] = image.Base64Data
                });
            }
            node["images"] = array;
        }

        if (message.Usage is { } usage)
        {
            node["usage"] = new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens,
                ["cacheRead"] = usage.CacheReadTokens,
                ["cacheWrite"] = usage.CacheWriteTokens
            };
        }

        return node;
    }

    /// <summary>Serializes a transcript.</summary>
    public static string SerializeList(IReadOnlyList<ChatMessage> messages) =>
        new JsonArray([.. messages.Select(message => (JsonNode)ToJson(message))]).ToJsonString();

    /// <summary>Parses one message, returning null when the payload is unreadable.</summary>
    public static ChatMessage? FromJson(JsonNode? node)
    {
        if (node is not JsonObject message)
            return null;

        IReadOnlyList<AiToolCall>? calls = null;
        if (message["toolCalls"] is JsonArray callArray && callArray.Count > 0)
        {
            var list = new List<AiToolCall>(callArray.Count);
            foreach (var item in callArray)
            {
                if (item is not JsonObject call)
                    continue;

                list.Add(new AiToolCall(
                    call["id"]?.GetValue<string>() ?? "",
                    call["name"]?.GetValue<string>() ?? "",
                    call["arguments"]?.GetValue<string>() ?? "{}"));
            }

            calls = list.Count > 0 ? list : null;
        }

        IReadOnlyList<AgentImage>? images = null;
        if (message["images"] is JsonArray imageArray && imageArray.Count > 0)
        {
            var list = new List<AgentImage>(imageArray.Count);
            foreach (var item in imageArray)
            {
                if (item is not JsonObject image)
                    continue;

                list.Add(new AgentImage(
                    image["mimeType"]?.GetValue<string>() ?? "image/png",
                    image["data"]?.GetValue<string>() ?? ""));
            }

            images = list.Count > 0 ? list : null;
        }

        AgentUsage? usage = null;
        if (message["usage"] is JsonObject usageNode)
        {
            usage = new AgentUsage(
                ReadInt(usageNode["input"]),
                ReadInt(usageNode["output"]),
                ReadInt(usageNode["cacheRead"]),
                ReadInt(usageNode["cacheWrite"]));
        }

        return new ChatMessage(
            message["role"]?.GetValue<string>() ?? AgentRoles.User,
            message["content"]?.GetValue<string>() ?? "",
            message["name"]?.GetValue<string>(),
            message["toolCallId"]?.GetValue<string>(),
            calls,
            images,
            usage);
    }

    /// <summary>Parses a transcript, skipping entries it cannot read.</summary>
    public static List<ChatMessage> DeserializeList(string? json)
    {
        var messages = new List<ChatMessage>();

        if (string.IsNullOrWhiteSpace(json))
            return messages;

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return messages;
        }

        if (array is null)
            return messages;

        foreach (var node in array)
        {
            if (FromJson(node) is { } message)
                messages.Add(message);
        }

        return messages;
    }

    private static int ReadInt(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<int>(out var direct) => direct,
        JsonValue value when value.TryGetValue<double>(out var real) => (int)real,
        _ => 0
    };
}
#endif
