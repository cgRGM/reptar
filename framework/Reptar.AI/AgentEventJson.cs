#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Serializes <see cref="AgentEvent"/> values to the JSONL record shapes pi uses for its
/// JSON and RPC modes. One event becomes one JSON object with a <c>type</c> discriminator,
/// so a TypeScript client, a Python script, or a shell pipeline can consume the agent
/// stream without linking Reptar.
/// </summary>
/// <remarks>
/// Field names follow pi deliberately (<c>tool_execution_start</c>,
/// <c>partialResult</c>, <c>durationMs</c>, <c>assistantMessageEvent</c>) so an existing
/// pi-aware client keeps working. Reptar-only events (<see cref="AgentEvent.AgentError"/>)
/// use the same conventions.
/// </remarks>
public static class AgentEventJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Renders one event as a single-line JSON object.</summary>
    public static string ToJson(this AgentEvent @event) =>
        ToNode(@event).ToJsonString(Options);

    /// <summary>Renders one event as a JSON node, for callers that compose larger records.</summary>
    public static JsonObject ToNode(this AgentEvent @event)
    {
        var node = new JsonObject { ["type"] = TypeName(@event) };

        switch (@event)
        {
            case AgentEvent.AgentStart start:
                node["maxSteps"] = start.MaxSteps;
                break;

            case AgentEvent.TurnStart turn:
                node["step"] = turn.Step;
                break;

            case AgentEvent.MessageStart message:
                node["step"] = message.Step;
                node["message"] = MessageNode(message.Message);
                break;

            case AgentEvent.MessageUpdate update:
                node["step"] = update.Step;
                // Matches pi's nested assistantMessageEvent shape so delta buffers written
                // against pi's wire format work unchanged.
                node["assistantMessageEvent"] = new JsonObject
                {
                    ["type"] = update.Kind switch
                    {
                        AgentContentKind.Thinking => "thinking_delta",
                        AgentContentKind.ToolCall => "toolcall_delta",
                        _ => "text_delta"
                    },
                    ["contentIndex"] = update.ContentIndex,
                    ["delta"] = update.Delta
                };
                break;

            case AgentEvent.MessageEnd end:
                node["step"] = end.Step;
                node["message"] = MessageNode(end.Message);
                break;

            case AgentEvent.ToolExecutionStart start:
                node["step"] = start.Step;
                node["toolCallId"] = start.ToolCallId;
                node["toolName"] = start.ToolName;
                node["args"] = ParseOrNull(start.ArgumentsJson);
                break;

            case AgentEvent.ToolExecutionUpdate update:
                node["step"] = update.Step;
                node["toolCallId"] = update.ToolCallId;
                node["toolName"] = update.ToolName;
                node["partialResult"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = update.PartialResult
                    }),
                    ["details"] = new JsonObject()
                };
                break;

            case AgentEvent.ToolExecutionEnd end:
                node["step"] = end.Step;
                node["toolCallId"] = end.ToolCallId;
                node["toolName"] = end.ToolName;
                node["isError"] = end.IsError;
                node["durationMs"] = Math.Round(end.DurationMs, 1);
                node["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = end.Result
                    }),
                    ["details"] = new JsonObject()
                };
                break;

            case AgentEvent.UsageReported usage:
                node["step"] = usage.Step;
                node["usage"] = new JsonObject
                {
                    ["input"] = usage.InputTokens,
                    ["output"] = usage.OutputTokens,
                    ["cacheRead"] = usage.CacheReadTokens,
                    ["cacheWrite"] = usage.CacheWriteTokens,
                    ["totalTokens"] = usage.InputTokens + usage.OutputTokens
                        + usage.CacheReadTokens + usage.CacheWriteTokens
                };
                break;

            case AgentEvent.TurnEnd turn:
                node["step"] = turn.Step;
                node["message"] = MessageNode(turn.Message);
                node["toolResults"] = new JsonArray(
                    [.. turn.ToolResults.Select(r => (JsonNode)MessageNode(r))]);
                break;

            case AgentEvent.AgentEnd end:
                node["stopReason"] = end.Summary.StopReason.ToString().ToLowerInvariant();
                node["finalContent"] = end.Summary.FinalContent;
                node["steps"] = end.Summary.StepsTaken;
                node["toolCalls"] = end.Summary.ToolCallsExecuted;
                node["elapsedMs"] = Math.Round(end.Summary.ElapsedMs, 1);
                node["truncated"] = end.Summary.Truncated;
                break;

            case AgentEvent.AgentSettled settled:
                node["aborted"] = settled.Aborted;
                break;

            case AgentEvent.AgentError error:
                node["message"] = error.Message;
                break;

            case AgentEvent.RetryScheduled scheduled:
                node["attempt"] = scheduled.Attempt;
                node["delayMs"] = Math.Round(scheduled.DelayMs, 1);
                node["reason"] = scheduled.Reason;
                break;

            case AgentEvent.RetryAttempt attempt:
                node["attempt"] = attempt.Attempt;
                break;

            case AgentEvent.RetryFinished finished:
                node["recovered"] = finished.Recovered;
                break;

            case AgentEvent.CompactionStart compaction:
                node["reason"] = Reason(compaction.Reason);
                break;

            case AgentEvent.CompactionEnd compaction:
                node["reason"] = Reason(compaction.Reason);
                node["tokensBefore"] = compaction.TokensBefore;
                node["estimatedTokensAfter"] = compaction.EstimatedTokensAfter;
                node["error"] = compaction.Error;
                break;
        }

        return node;
    }

    /// <summary>
    /// Parses one JSONL record back into an <see cref="AgentEvent"/>. This is the other half of
    /// <see cref="ToJson"/>: a client that links Reptar gets the protocol for free instead of
    /// hand-rolling a decoder that drifts from the server.
    /// </summary>
    /// <param name="line">One record, without the trailing newline.</param>
    /// <param name="event">The decoded event, or null when the record is not an agent event.</param>
    /// <returns>
    /// True for a recognised agent event. False for command responses, malformed JSON, and
    /// unknown event types — callers should ignore those rather than treat them as errors.
    /// </returns>
    public static bool TryParse(string line, out AgentEvent? @event)
    {
        @event = null;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is null)
            return false;

        var type = node["type"]?.GetValue<string>();
        if (type is null or "response")
            return false;

        var step = Int(node["step"]);

        @event = type switch
        {
            "agent_start" => new AgentEvent.AgentStart(Int(node["maxSteps"])),
            "turn_start" => new AgentEvent.TurnStart(step),
            "message_start" => new AgentEvent.MessageStart(step, MessageFrom(node["message"])),
            "message_update" => UpdateFrom(step, node),
            "message_end" => new AgentEvent.MessageEnd(step, MessageFrom(node["message"])),
            "tool_execution_start" => new AgentEvent.ToolExecutionStart(
                step,
                Str(node["toolCallId"]) ?? "",
                Str(node["toolName"]) ?? "",
                node["args"]?.ToJsonString() ?? "{}"),
            "tool_execution_update" => new AgentEvent.ToolExecutionUpdate(
                step,
                Str(node["toolCallId"]) ?? "",
                Str(node["toolName"]) ?? "",
                FirstText(node["partialResult"])),
            "tool_execution_end" => new AgentEvent.ToolExecutionEnd(
                step,
                Str(node["toolCallId"]) ?? "",
                Str(node["toolName"]) ?? "",
                FirstText(node["result"]),
                Bool(node["isError"]),
                Double(node["durationMs"])),
            "usage" => new AgentEvent.UsageReported(
                step,
                Int(node["usage"]?["input"]),
                Int(node["usage"]?["output"]),
                Int(node["usage"]?["cacheRead"]),
                Int(node["usage"]?["cacheWrite"])),
            "turn_end" => new AgentEvent.TurnEnd(
                step,
                MessageFrom(node["message"]),
                MessagesFrom(node["toolResults"])),
            "agent_end" => new AgentEvent.AgentEnd(new AgentRunSummary(
                ParseStopReason(Str(node["stopReason"])),
                Str(node["finalContent"]) ?? "",
                Int(node["steps"]),
                Int(node["toolCalls"]),
                "",
                Double(node["elapsedMs"]),
                [],
                Bool(node["truncated"]))),
            "agent_settled" => new AgentEvent.AgentSettled(Bool(node["aborted"])),
            "error" => new AgentEvent.AgentError(Str(node["message"]) ?? ""),
            "retry_scheduled" => new AgentEvent.RetryScheduled(
                Int(node["attempt"]), Double(node["delayMs"]), Str(node["reason"]) ?? ""),
            "retry_attempt_start" => new AgentEvent.RetryAttempt(Int(node["attempt"])),
            "retry_finished" => new AgentEvent.RetryFinished(Bool(node["recovered"])),
            "compaction_start" => new AgentEvent.CompactionStart(ParseReason(Str(node["reason"]))),
            "compaction_end" => new AgentEvent.CompactionEnd(
                ParseReason(Str(node["reason"])),
                Int(node["tokensBefore"]),
                Int(node["estimatedTokensAfter"]),
                Str(node["error"])),
            _ => null
        };

        return @event is not null;
    }

    private static AgentEvent MessageUpdateFrom(int step, AgentContentKind kind, int index, string delta) =>
        new AgentEvent.MessageUpdate(step, kind, index, delta);

    private static AgentEvent UpdateFrom(int step, JsonObject node)
    {
        var update = node["assistantMessageEvent"] as JsonObject;
        var kind = Str(update?["type"]) switch
        {
            "thinking_delta" => AgentContentKind.Thinking,
            "toolcall_delta" => AgentContentKind.ToolCall,
            _ => AgentContentKind.Text
        };

        return MessageUpdateFrom(step, kind, Int(update?["contentIndex"]), Str(update?["delta"]) ?? "");
    }

    private static ChatMessage MessageFrom(JsonNode? node)
    {
        if (node is not JsonObject message)
            return new ChatMessage("assistant", "");

        IReadOnlyList<AiToolCall>? calls = null;
        if (message["toolCalls"] is JsonArray callArray && callArray.Count > 0)
        {
            var list = new List<AiToolCall>(callArray.Count);
            foreach (var entry in callArray)
            {
                if (entry is not JsonObject call)
                    continue;
                list.Add(new AiToolCall(
                    Str(call["id"]) ?? "",
                    Str(call["name"]) ?? "",
                    call["arguments"]?.ToJsonString() ?? "{}"));
            }
            calls = list.Count > 0 ? list : null;
        }

        AgentUsage? usage = null;
        if (message["usage"] is JsonObject usageNode)
        {
            usage = new AgentUsage(
                Int(usageNode["input"]),
                Int(usageNode["output"]),
                Int(usageNode["cacheRead"]),
                Int(usageNode["cacheWrite"]));
        }

        return new ChatMessage(
            Str(message["role"]) ?? "assistant",
            Str(message["content"]) ?? "",
            Str(message["name"]),
            Str(message["toolCallId"]),
            calls,
            Usage: usage);
    }

    private static IReadOnlyList<ChatMessage> MessagesFrom(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
            return [];

        var list = new List<ChatMessage>(array.Count);
        foreach (var entry in array)
            list.Add(MessageFrom(entry));
        return list;
    }

    private static string FirstText(JsonNode? node)
    {
        if (node is not JsonObject result)
            return "";
        if (result["content"] is not JsonArray content)
            return "";

        var builder = new System.Text.StringBuilder();
        foreach (var block in content)
        {
            if (block is JsonObject obj && Str(obj["text"]) is { } text)
                builder.Append(text);
        }
        return builder.ToString();
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? (int)number : 0;

    private static double Double(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static AgentStopReason ParseStopReason(string? reason) => reason switch
    {
        "maxsteps" => AgentStopReason.MaxSteps,
        "cancelled" => AgentStopReason.Cancelled,
        "error" => AgentStopReason.Error,
        _ => AgentStopReason.Completed
    };

    private static CompactionReason ParseReason(string? reason) => reason switch
    {
        "threshold" => CompactionReason.Threshold,
        "overflow" => CompactionReason.Overflow,
        _ => CompactionReason.Manual
    };

    /// <summary>The wire <c>type</c> for an event, matching pi's snake_case record names.</summary>
    public static string TypeName(AgentEvent @event) => @event switch
    {
        AgentEvent.AgentStart => "agent_start",
        AgentEvent.TurnStart => "turn_start",
        AgentEvent.MessageStart => "message_start",
        AgentEvent.MessageUpdate => "message_update",
        AgentEvent.MessageEnd => "message_end",
        AgentEvent.ToolExecutionStart => "tool_execution_start",
        AgentEvent.ToolExecutionUpdate => "tool_execution_update",
        AgentEvent.ToolExecutionEnd => "tool_execution_end",
        AgentEvent.UsageReported => "usage",
        AgentEvent.TurnEnd => "turn_end",
        AgentEvent.AgentEnd => "agent_end",
        AgentEvent.AgentSettled => "agent_settled",
        AgentEvent.AgentError => "error",
        AgentEvent.RetryScheduled => "retry_scheduled",
        AgentEvent.RetryAttempt => "retry_attempt_start",
        AgentEvent.RetryFinished => "retry_finished",
        AgentEvent.CompactionStart => "compaction_start",
        AgentEvent.CompactionEnd => "compaction_end",
        _ => "unknown"
    };

    private static string Reason(CompactionReason reason) => reason switch
    {
        CompactionReason.Threshold => "threshold",
        CompactionReason.Overflow => "overflow",
        _ => "manual"
    };

    private static JsonObject MessageNode(ChatMessage message)
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
                array.Add((JsonNode)new JsonObject
                {
                    ["type"] = "toolCall",
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = ParseOrNull(call.Arguments)
                });
            }
            node["toolCalls"] = array;
        }

        if (message.Images is { Count: > 0 } images)
        {
            var array = new JsonArray();
            foreach (var image in images)
            {
                array.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = image.Base64Data,
                    ["mimeType"] = image.MimeType
                });
            }
            node["images"] = array;
        }

        return node;
    }

    private static JsonNode? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Truncated or malformed arguments still need to reach the client for display.
            return JsonValue.Create(json);
        }
    }
}
#endif
