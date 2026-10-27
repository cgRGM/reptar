#if !NETWASM
using System.Data;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// Standard checkpoint TableDef for resumable agent runs (G14 request ports).
/// A checkpoint captures the entire conversation state at the moment an agent
/// asked for external input, so the run can resume hours or days later — even
/// in a fresh process — once a human (or another system) responds.
/// </summary>
public static class AgentRunCheckpointSchema
{
    public static TableDef RunCheckpoints(string tableName = "agent_run_checkpoints") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("id").PrimaryKey(),
            ReptarApi.Col.Text("session_id").NotNull().Indexed(),
            ReptarApi.Col.Text("port").NotNull(),
            ReptarApi.Col.Text("question").NotNull(),
            ReptarApi.Col.Text("state_json").NotNull(),
            ReptarApi.Col.Text("pending_tool_call_id"),
            ReptarApi.Col.Text("pending_tool_name"),
            ReptarApi.Col.Text("pending_tool_args"),
            ReptarApi.Col.Text("status").NotNull().Default("pending"),
            ReptarApi.Col.Text("answer"),
            ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));
}

/// <summary>
/// A persisted run checkpoint. Mapping is manual (zero reflection, Native AOT
/// safe), mirroring <see cref="AgentSessionRow"/>.
/// </summary>
public sealed record AgentCheckpointRow(
    string Id,
    string SessionId,
    string Port,
    string Question,
    string StateJson,
    string? PendingToolCallId,
    string? PendingToolName,
    string? PendingToolArgs,
    string Status,
    string? Answer,
    string CreatedAt) : IReptarEntity
{
    public bool IsPending => Status == "pending";
    public bool IsApproval => Port == "approval";

    static AgentCheckpointRow()
    {
        EntityMapper<AgentCheckpointRow>.DirectMap = Map;
        EntityMapper<AgentCheckpointRow>.Factory = _ => Map;
    }

    public static AgentCheckpointRow Map(IDataRecord record)
    {
        string id = "", sessionId = "", port = "", question = "", stateJson = "", status = "pending", createdAt = "";
        string? pendingToolCallId = null, pendingToolName = null, pendingToolArgs = null, answer = null;

        for (var i = 0; i < record.FieldCount; i++)
        {
            if (record.IsDBNull(i))
                continue;
            switch (Normalize(record.GetName(i)))
            {
                case "id": id = record.GetString(i); break;
                case "sessionid" or "session_id": sessionId = record.GetString(i); break;
                case "port": port = record.GetString(i); break;
                case "question": question = record.GetString(i); break;
                case "statejson" or "state_json": stateJson = record.GetString(i); break;
                case "pendingtoolcallid" or "pending_tool_call_id": pendingToolCallId = record.GetValue(i).ToString(); break;
                case "pendingtoolname" or "pending_tool_name": pendingToolName = record.GetValue(i).ToString(); break;
                case "pendingtoolargs" or "pending_tool_args": pendingToolArgs = record.GetValue(i).ToString(); break;
                case "status": status = record.GetString(i); break;
                case "answer": answer = record.GetValue(i).ToString(); break;
                case "createdat" or "created_at": createdAt = record.GetValue(i).ToString() ?? ""; break;
            }
        }

        return new AgentCheckpointRow(
            id, sessionId, port, question, stateJson, pendingToolCallId, pendingToolName, pendingToolArgs, status, answer, createdAt);
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("SessionId", SessionId),
        ("Port", Port),
        ("Question", Question),
        ("StateJson", StateJson),
        ("PendingToolCallId", PendingToolCallId),
        ("PendingToolName", PendingToolName),
        ("PendingToolArgs", PendingToolArgs),
        ("Status", Status),
        ("Answer", Answer),
        ("CreatedAt", CreatedAt)
    ];

    private static string Normalize(string name) =>
        name.Replace("_", "").ToLowerInvariant();
}

/// <summary>
/// Durable store for run checkpoints over any <see cref="IDbClient"/>
/// (SQLite locally, D1 on Workers).
/// </summary>
public sealed class RunCheckpointStore(IDbClient db, TableDef checkpoints)
{
    public TableDef Checkpoints => checkpoints;

    public Task EnsureCreatedAsync() => db.EnsureCreatedAsync(checkpoints);

    public async Task<AgentCheckpointRow> CreateAsync(
        string sessionId,
        string port,
        string question,
        string stateJson,
        string? pendingToolCallId = null,
        string? pendingToolName = null,
        string? pendingToolArgs = null)
    {
        var row = new AgentCheckpointRow(
            Ids.New("ckpt"),
            sessionId,
            port,
            question,
            stateJson,
            pendingToolCallId,
            pendingToolName,
            pendingToolArgs,
            "pending",
            null,
            DateTimeOffset.UtcNow.ToString("O"));

        await db.InsertAsync(checkpoints, row);
        return row;
    }

    public async Task<AgentCheckpointRow?> GetAsync(string id)
    {
        var rows = await db.ListAsync<AgentCheckpointRow>(
            checkpoints, "where id = @id", ("id", id), limit: 1);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<AgentCheckpointRow?> FindPendingAsync(string sessionId)
    {
        var rows = await db.ListAsync<AgentCheckpointRow>(
            checkpoints,
            "where session_id = @sessionId and status = 'pending' order by rowid desc",
            ("sessionId", sessionId),
            limit: 1);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task AnswerAsync(string id, string answer) =>
        db.ExecuteAsync(
            $"update \"{checkpoints.Name}\" set status = 'answered', answer = @answer where id = @id",
            ("answer", answer),
            ("id", id));
}

/// <summary>
/// Thrown by a request-port tool executor to pause the agent run. Handled by
/// <see cref="RequestPortAgent"/>, never surfaced as a run failure.
/// </summary>
public sealed class RequestPortSuspendedException(string port, string question)
    : Exception($"Agent run suspended on request port '{port}' awaiting input.")
{
    public string Port { get; } = port;
    public string Question { get; } = question;
}

/// <summary>
/// Request-port lifecycle events, alongside the harness's own AgentEvent
/// stream. The web tier renders these as approval / ask-user prompts, and the
/// terminal renders them inline.
/// </summary>
public abstract record RequestPortEvent
{
    public sealed record RunSuspended(string CheckpointId, string Port, string Question) : RequestPortEvent;
    public sealed record RunResumed(string CheckpointId, string Answer) : RequestPortEvent;
}

/// <summary>
/// The outcome of a request-port-aware run: either a completed
/// <see cref="AgentRunSummary"/>, or a suspended checkpoint awaiting input.
/// </summary>
public sealed record RequestPortRunResult(
    bool Completed,
    AgentRunSummary? Summary,
    AgentCheckpointRow? SuspendedCheckpoint)
{
    public static RequestPortRunResult Complete(AgentRunSummary summary) => new(true, summary, null);
    public static RequestPortRunResult Suspended(AgentCheckpointRow checkpoint) => new(false, null, checkpoint);
}

/// <summary>
/// G14 — Request ports / human-in-the-loop. Wraps
/// <see cref="StreamingAgentHarness"/> so an agent can ask a question
/// (<c>ask_user</c> tool), pause, persist a checkpoint, and resume later —
/// possibly in a new process — once the answer arrives.
///
/// Usage:
/// <code>
/// var result = await RequestPortAgent.RunAsync(history, client.StreamAsync, executor, tools, store, sessionId);
/// if (!result.Completed)
///     // surface result.SuspendedCheckpoint.Question to a human
///     // ... days later:
///     await RequestPortAgent.ResumeAsync(store, result.SuspendedCheckpoint.Id, "the answer", client.StreamAsync, executor, tools);
/// </code>
/// </summary>
public static class RequestPortAgent
{
    public const string AskUserToolName = "ask_user";

    /// <summary>
    /// The built-in request-port tool. The agent calls this when it needs
    /// information only a human can provide; the run suspends instead of
    /// executing it.
    /// </summary>
    public static AiToolDefinition AskUserTool { get; } = new()
    {
        Name = AskUserToolName,
        Description = "Pause and ask the user a question. Use this whenever you need information, clarification, or approval that only the human can provide; the run resumes automatically once they answer.",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["question"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] = "The specific question to ask the user. Make it self-contained and, where options exist, list them."
                }
            },
            ["required"] = new List<string> { "question" }
        },
        Method = "post",
        Path = "/agent/ask_user"
    };

    public static async Task<RequestPortRunResult> RunAsync(
        List<ChatMessage> history,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition> tools,
        RunCheckpointStore store,
        string sessionId,
        int maxSteps = 10,
        string model = "agent",
        Action<AgentEvent>? onEvent = null,
        Action<RequestPortEvent>? onPortEvent = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        return await RunCoreAsync(
            history, stream, toolExecutor, tools, store, sessionId,
            maxSteps, model, onEvent, onPortEvent, jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a suspended run: the answer is appended as the pending tool
    /// result, the checkpoint is marked answered, and the agent loop continues.
    /// The conversation is restored from the checkpoint's persisted state, so
    /// resuming in a fresh process works out of the box.
    /// </summary>
    public static async Task<RequestPortRunResult> ResumeAsync(
        RunCheckpointStore store,
        string checkpointId,
        string answer,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition>? tools = null,
        int maxSteps = 10,
        string model = "agent",
        Action<AgentEvent>? onEvent = null,
        Action<RequestPortEvent>? onPortEvent = null,
        JsonSerializerOptions? jsonOptions = null,
        List<ChatMessage>? liveHistory = null,
        Func<string, string, Task<AiToolResult>>? pendingToolExecutor = null,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await store.GetAsync(checkpointId).ConfigureAwait(false);
        if (checkpoint is null)
            throw new InvalidOperationException($"No checkpoint with id '{checkpointId}'.");
        if (!checkpoint.IsPending)
            throw new InvalidOperationException($"Checkpoint '{checkpointId}' was already answered.");

        var options = jsonOptions ?? ReptarJson.Options;
        var history = liveHistory ?? JsonSerializer.Deserialize<List<ChatMessage>>(checkpoint.StateJson, options)
            ?? [];

        // Approval checkpoints: the answer decides whether the pending tool
        // actually runs. On approval the model sees the real tool result;
        // on denial it sees an explicit refusal it can reason about.
        var toolMessageContent = answer;
        if (checkpoint.PendingToolName is { Length: > 0 } pendingTool)
        {
            var approved = answer.Trim().ToLowerInvariant() is
                "approve" or "approved" or "yes" or "y" or "allow" or "ok";

            AiToolResult toolResult;
            if (approved)
            {
                // The human just approved this exact call: run it through the
                // ungated executor so an approval-gate wrapper cannot ask for
                // approval a second time.
                var executor = pendingToolExecutor ?? toolExecutor;
                try
                {
                    toolResult = await executor(pendingTool, checkpoint.PendingToolArgs ?? "{}").ConfigureAwait(false);
                }
                catch (RequestPortSuspendedException)
                {
                    throw; // a nested suspension must not become a tool error
                }
                catch (Exception ex)
                {
                    toolResult = AiToolResult.Failure(500, "tool_execution_error", ex.Message);
                }
            }
            else
            {
                toolResult = AiToolResult.Failure(403, "approval_denied",
                    $"The user declined the '{pendingTool}' tool call.");
            }

            toolMessageContent = toolResult.ToLlmResponse();
        }

        // Deliver the answer as the tool result the model is waiting on.
        history.Add(new ChatMessage(
            Role: "tool",
            Content: toolMessageContent,
            Name: checkpoint.Port,
            ToolCallId: checkpoint.PendingToolCallId ?? "call_resumed"));

        await store.AnswerAsync(checkpoint.Id, answer).ConfigureAwait(false);
        onPortEvent?.Invoke(new RequestPortEvent.RunResumed(checkpoint.Id, answer));

        return await RunCoreAsync(
            history, stream, toolExecutor, tools ?? [], store, checkpoint.SessionId,
            maxSteps, model, onEvent, onPortEvent, jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RequestPortRunResult> RunCoreAsync(
        List<ChatMessage> history,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition> tools,
        RunCheckpointStore store,
        string sessionId,
        int maxSteps,
        string model,
        Action<AgentEvent>? onEvent,
        Action<RequestPortEvent>? onPortEvent,
        JsonSerializerOptions? jsonOptions,
        CancellationToken cancellationToken)
    {
        var options = jsonOptions ?? ReptarJson.Options;
        var allTools = tools.Where(t => t.Name != AskUserToolName).ToList();
        allTools.Add(AskUserTool);

        Func<string, string, Task<AiToolResult>> portAwareExecutor = async (name, argumentsJson) =>
        {
            if (name == AskUserToolName)
            {
                var question = ExtractQuestion(argumentsJson);
                var callId = FindPendingToolCallId(history, AskUserToolName);

                // Persist the run state exactly as the model left it, then
                // unwind so the loop pauses instead of continuing.
                var checkpoint = await store.CreateAsync(
                    sessionId, AskUserToolName, question,
                    JsonSerializer.Serialize(history, options), callId).ConfigureAwait(false);

                onPortEvent?.Invoke(new RequestPortEvent.RunSuspended(checkpoint.Id, AskUserToolName, question));
                throw new RequestPortSuspendedException(AskUserToolName, question);
            }

            return await toolExecutor(name, argumentsJson).ConfigureAwait(false);
        };

        try
        {
            var summary = await AgentLoop.RunAsync(
                history,
                new AgentLoopConfig
                {
                    Model = model,
                    MaxSteps = maxSteps,
                    ToolExecutor = AgentToolExecutors.FromNameArgs(portAwareExecutor),
                    // The ask-user port throws to suspend the run; keep it out of a parallel
                    // batch so no sibling tool call is left mid-flight.
                    SequentialTools = new HashSet<string>(StringComparer.Ordinal) { AskUserToolName }
                },
                stream,
                allTools,
                onEvent,
                cancellationToken).ConfigureAwait(false);
            return RequestPortRunResult.Complete(summary);
        }
        catch (RequestPortSuspendedException)
        {
            var checkpoint = await store.FindPendingAsync(sessionId).ConfigureAwait(false);
            return checkpoint is not null
                ? RequestPortRunResult.Suspended(checkpoint)
                : RequestPortRunResult.Suspended(new AgentCheckpointRow(
                    Id: "unknown", SessionId: sessionId, Port: AskUserToolName,
                    Question: "unknown", StateJson: "[]", PendingToolCallId: null,
                    PendingToolName: null, PendingToolArgs: null, Status: "pending",
                    Answer: null, CreatedAt: DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    private static string ExtractQuestion(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.TryGetProperty("question", out var question) &&
                question.ValueKind == JsonValueKind.String)
            {
                return question.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw text below.
        }

        return argumentsJson;
    }

    private static string FindPendingToolCallId(List<ChatMessage> history, string toolName)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index] is not { Role: "assistant", ToolCalls: { Count: > 0 } calls })
                continue;

            var call = calls.FirstOrDefault(candidate => candidate.Name == toolName);
            if (call is { } pending)
                return pending.Id;
        }

        return "call_ask_user";
    }
}
#endif
