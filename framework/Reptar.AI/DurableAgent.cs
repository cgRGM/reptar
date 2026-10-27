#if !NETWASM
using System.Text.Json;
using System.Text.Json.Nodes;
using static Reptar.ReptarApi;

namespace Reptar.AI;

/// <summary>
/// Tables for the durable agent harness. Append-only entries plus small state tables, so the
/// whole conversation is reconstructible from storage after a crash.
/// </summary>
/// <remarks>
/// This is a focused port of pi's <c>@earendil-works/pi-durable</c>. The full package is a
/// 15k-line task scheduler with extensions, code mode, and telemetry; what actually matters —
/// and what is implemented here — is the guarantee:
/// <b>nothing is shown before it is committed, and a crash resumes from the last commit.</b>
/// </remarks>
public static class DurableSchema
{
    public static readonly TableDef Conversations = DefineTable("agent_conversations",
        Col.Text("id").PrimaryKey(),
        Col.Text("title"),
        Col.Text("model").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"));

    /// <summary>
    /// Immutable transcript records. <c>seq</c> is the total order within a conversation;
    /// <c>parent_id</c> exists so branching can be added without a migration.
    /// </summary>
    public static readonly TableDef Entries = DefineTable("agent_entries",
        Col.Text("id").PrimaryKey(),
        Col.Text("conversation_id").NotNull().Indexed(),
        Col.Text("parent_id"),
        Col.Int("seq").NotNull(),
        Col.Text("kind").NotNull(),
        Col.Text("payload").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    /// <summary>
    /// A unit of work handed to a conversation. <c>request_id</c> is unique so a retried submit
    /// returns the original instead of running twice.
    /// </summary>
    public static readonly TableDef Submissions = DefineTable("agent_submissions",
        Col.Text("id").PrimaryKey(),
        Col.Text("conversation_id").NotNull().Indexed(),
        Col.Text("request_id").Unique(),
        Col.Text("status").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("answer"),
        Col.Text("error"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"));

    /// <summary>
    /// The durable state machine. A row in <c>running</c> whose process died is "orphaned" and
    /// is picked up by <see cref="DurableAgentHarness.ResumeAsync"/>.
    /// </summary>
    public static readonly TableDef Tasks = DefineTable("agent_tasks",
        Col.Text("id").PrimaryKey(),
        Col.Text("conversation_id").NotNull().Indexed(),
        Col.Text("submission_id").Indexed(),
        Col.Text("kind").NotNull(),
        Col.Text("status").NotNull(),
        Col.Int("turns_completed").Default(0),
        Col.Int("attempts").Default(0),
        Col.Text("last_entry_id"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef[] All = [Conversations!, Entries!, Submissions!, Tasks!];
}

/// <summary>Lifecycle of a submission.</summary>
public enum DurableSubmissionStatus
{
    Pending,
    Running,
    Done,
    Failed
}

/// <summary>Lifecycle of a durable task. <see cref="Orphaned"/> means the owning process died.</summary>
public enum DurableTaskStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Orphaned
}

public sealed record DurableConversationRow(
    string Id,
    string? Title,
    string Model,
    string CreatedAt,
    string UpdatedAt);

public sealed record DurableEntryRow(
    string Id,
    string ConversationId,
    string? ParentId,
    long Seq,
    string Kind,
    string Payload,
    string CreatedAt);

public sealed record DurableSubmissionRow(
    string Id,
    string ConversationId,
    string? RequestId,
    string Status,
    string Content,
    string? Answer,
    string? Error,
    string CreatedAt,
    string UpdatedAt)
{
    public DurableSubmissionStatus State => Enum.TryParse<DurableSubmissionStatus>(Status, true, out var value)
        ? value
        : DurableSubmissionStatus.Pending;

    public bool IsTerminal => State is DurableSubmissionStatus.Done or DurableSubmissionStatus.Failed;
}

public sealed record DurableTaskRow(
    string Id,
    string ConversationId,
    string? SubmissionId,
    string Kind,
    string Status,
    int TurnsCompleted,
    int Attempts,
    string? LastEntryId,
    string CreatedAt,
    string UpdatedAt)
{
    public DurableTaskStatus State => Enum.TryParse<DurableTaskStatus>(Status, true, out var value)
        ? value
        : DurableTaskStatus.Pending;
}

/// <summary>What a conversation runs with. Stored per conversation, resolved per use.</summary>
public sealed record DurableAgentChoices
{
    public string Model { get; init; } = "";
    public string? SystemPrompt { get; init; }
    public int MaxSteps { get; init; } = 24;
    public string? WorkspaceRoot { get; init; }
}

public sealed record DurableHarnessOptions
{
    public DurableAgentChoices Agent { get; init; } = new();
    public string? Title { get; init; }

    /// <summary>
    /// How many times a submission is retried after a transient failure before it is marked
    /// failed. Orphaned tasks are resumed once per process by default.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Open without the ability to write or run work. A process that only renders a session
    /// another process drives should use this.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// Crash-safe agent harness. Submissions and every completed turn are committed atomically
/// before anything is reported, so reopening the storage continues the work instead of
/// restarting it.
/// </summary>
/// <remarks>
/// The mapping onto Reptar's loop is direct:
/// <list type="bullet">
/// <item><see cref="AgentLoopConfig.FinishTurn"/> is the checkpoint. It fires after every
/// assistant turn plus its tool results, which is exactly the boundary pi-durable commits at.</item>
/// <item>The transcript is rebuilt from <see cref="DurableSchema.Entries"/>, not from memory, so a
/// resumed run sees the same history the model saw before the crash.</item>
/// <item>A task left in <c>running</c> is marked <see cref="DurableTaskStatus.Orphaned"/> on open
/// and re-run by <see cref="ResumeAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class DurableAgentHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbClient _db;
    private readonly ReptarApp _app;
    private readonly DurableHarnessOptions _options;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private DurableAgentHarness(ReptarApp app, DurableHarnessOptions options)
    {
        _app = app;
        _db = app.Db;
        _options = options;
    }

    /// <summary>
    /// Opens the harness: creates the tables and marks any task left running by a previous
    /// process as orphaned so it can be resumed.
    /// </summary>
    public static async Task<DurableAgentHarness> OpenAsync(
        ReptarApp app,
        DurableHarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        var harness = new DurableAgentHarness(app, options);

        await app.Db.EnsureCreatedAsync(DurableSchema.All).ConfigureAwait(false);

        if (!options.ReadOnly)
        {
            await app.Db.ExecuteAsync(
                "UPDATE agent_tasks SET status = @status, updated_at = CURRENT_TIMESTAMP " +
                "WHERE status = @running",
                new { status = nameof(DurableTaskStatus.Orphaned), running = nameof(DurableTaskStatus.Running) })
                .ConfigureAwait(false);

            await app.Db.ExecuteAsync(
                "UPDATE agent_submissions SET status = @pending, updated_at = CURRENT_TIMESTAMP " +
                "WHERE status = @running",
                new { pending = nameof(DurableSubmissionStatus.Pending), running = nameof(DurableSubmissionStatus.Running) })
                .ConfigureAwait(false);
        }

        return harness;
    }

    // ------------------------------------------------------------------
    // Conversations
    // ------------------------------------------------------------------

    /// <summary>Returns the single root conversation, creating it on first use.</summary>
    public async Task<DurableConversationRow> RootAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _db.QuerySingleOrDefaultAsync<DurableConversationRow>(
            "SELECT * FROM agent_conversations ORDER BY created_at LIMIT 1").ConfigureAwait(false);

        if (existing is not null)
            return existing;

        ThrowIfReadOnly();

        var row = new DurableConversationRow(
            NewId("conv"),
            _options.Title ?? "root",
            _options.Agent.Model,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"));

        await _db.ExecuteAsync(
            "INSERT INTO agent_conversations (id, title, model, created_at, updated_at) " +
            "VALUES (@id, @title, @model, @createdAt, @updatedAt)",
            new { id = row.Id, title = row.Title, model = row.Model, createdAt = row.CreatedAt, updatedAt = row.UpdatedAt })
            .ConfigureAwait(false);

        return row;
    }

    /// <summary>Rebuilds the transcript for a conversation, oldest first.</summary>
    public async Task<List<ChatMessage>> TranscriptAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _db.QueryAsync<DurableEntryRow>(
            "SELECT * FROM agent_entries WHERE conversation_id = @id ORDER BY seq",
            new { id = conversationId }).ConfigureAwait(false);

        var messages = new List<ChatMessage>(entries.Count);
        foreach (var entry in entries)
        {
            var message = Deserialize(entry);
            if (message is not null)
                messages.Add(message);
        }

        return messages;
    }

    /// <summary>
    /// Creates a new conversation containing the entries of <paramref name="conversationId"/> up to
    /// and including <paramref name="throughEntryId"/>. The original is untouched, so a fork is
    /// "explore this alternative without losing where I was".
    /// </summary>
    /// <param name="conversationId">Conversation to fork.</param>
    /// <param name="throughEntryId">
    /// Last entry to copy. Null copies the whole conversation, which is a clone.
    /// </param>
    /// <param name="title">Title for the new conversation.</param>
    /// <returns>The new conversation's id, or null when the source or cut point is missing.</returns>
    public async Task<DurableConversationRow?> ForkAsync(
        string conversationId,
        string? throughEntryId = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();

        var source = await _db.QuerySingleOrDefaultAsync<DurableConversationRow>(
            "SELECT * FROM agent_conversations WHERE id = @id",
            new { id = conversationId }).ConfigureAwait(false);

        if (source is null)
            return null;

        long cutSeq;
        if (throughEntryId is null)
        {
            var last = await _db.QuerySingleOrDefaultAsync<SeqRow>(
                "SELECT COALESCE(MAX(seq), 0) AS value FROM agent_entries WHERE conversation_id = @id",
                new { id = conversationId }).ConfigureAwait(false);
            cutSeq = last?.Value ?? 0;
        }
        else
        {
            var entry = await _db.QuerySingleOrDefaultAsync<DurableEntryRow>(
                "SELECT * FROM agent_entries WHERE conversation_id = @id AND id = @entryId",
                new { id = conversationId, entryId = throughEntryId }).ConfigureAwait(false);

            if (entry is null)
                return null;

            cutSeq = entry.Seq;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var fork = new DurableConversationRow(
            NewId("conv"),
            title ?? (source.Title is { Length: > 0 } existing ? existing + " (fork)" : "fork"),
            source.Model,
            now,
            now);

        // The copy and the fork row land together: a crash leaves either the original alone or a
        // complete fork, never a half-copied transcript.
        await _db.TransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(
                "INSERT INTO agent_conversations (id, title, model, created_at, updated_at) " +
                "VALUES (@id, @title, @model, @now, @now)",
                new { id = fork.Id, title = fork.Title, model = fork.Model, now }).ConfigureAwait(false);

            var entries = await tx.QueryAsync<DurableEntryRow>(
                "SELECT * FROM agent_entries WHERE conversation_id = @id AND seq <= @cutSeq ORDER BY seq",
                new { id = conversationId, cutSeq }).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                await tx.ExecuteAsync(
                    "INSERT INTO agent_entries (id, conversation_id, parent_id, seq, kind, payload, created_at) " +
                    "VALUES (@id, @conversationId, @parentId, @seq, @kind, @payload, @createdAt)",
                    new
                    {
                        id = NewId("ent"),
                        conversationId = fork.Id,
                        parentId = (string?)null,
                        seq = entry.Seq,
                        kind = entry.Kind,
                        payload = entry.Payload,
                        createdAt = entry.CreatedAt
                    }).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        return fork;
    }

    /// <summary>Conversations in creation order, for a session picker.</summary>
    public Task<List<DurableConversationRow>> ConversationsAsync() =>
        _db.QueryAsync<DurableConversationRow>(
            "SELECT * FROM agent_conversations ORDER BY created_at");

    // ------------------------------------------------------------------
    // Submissions
    // ------------------------------------------------------------------

    /// <summary>
    /// Durably admits an input. Re-submitting the same <paramref name="requestId"/> returns the
    /// original submission instead of running the work twice.
    /// </summary>
    public async Task<DurableSubmissionRow> SubmitAsync(
        string content,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();

        var conversation = await RootAsync(cancellationToken).ConfigureAwait(false);

        if (requestId is { Length: > 0 })
        {
            var existing = await _db.QuerySingleOrDefaultAsync<DurableSubmissionRow>(
                "SELECT * FROM agent_submissions WHERE request_id = @requestId",
                new { requestId }).ConfigureAwait(false);

            if (existing is not null)
                return existing;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var submission = new DurableSubmissionRow(
            NewId("sub"),
            conversation.Id,
            requestId,
            nameof(DurableSubmissionStatus.Pending),
            content,
            null,
            null,
            now,
            now);

        // The input entry and the submission land together: a crash either admits both or neither.
        await _db.TransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(
                "INSERT INTO agent_submissions (id, conversation_id, request_id, status, content, created_at, updated_at) " +
                "VALUES (@id, @conversationId, @requestId, @status, @content, @now, @now)",
                new
                {
                    id = submission.Id,
                    conversationId = submission.ConversationId,
                    requestId = submission.RequestId,
                    status = submission.Status,
                    content = submission.Content,
                    now
                }).ConfigureAwait(false);

            await AppendEntryAsync(
                tx,
                conversation.Id,
                new ChatMessage(AgentRoles.User, content),
                cancellationToken).ConfigureAwait(false);

            await tx.ExecuteAsync(
                "INSERT INTO agent_tasks (id, conversation_id, submission_id, kind, status, turns_completed, attempts) " +
                "VALUES (@id, @conversationId, @submissionId, @kind, @status, 0, 0)",
                new
                {
                    id = NewId("task"),
                    conversationId = conversation.Id,
                    submissionId = submission.Id,
                    kind = "generation",
                    status = nameof(DurableTaskStatus.Pending)
                }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return submission;
    }

    /// <summary>Reads a submission by id, for waiting after a restart.</summary>
    public Task<DurableSubmissionRow?> SubmissionAsync(string submissionId) =>
        _db.QuerySingleOrDefaultAsync<DurableSubmissionRow>(
            "SELECT * FROM agent_submissions WHERE id = @id", new { id = submissionId });

    /// <summary>All submissions for the root conversation, newest last.</summary>
    public Task<List<DurableSubmissionRow>> SubmissionsAsync(string conversationId) =>
        _db.QueryAsync<DurableSubmissionRow>(
            "SELECT * FROM agent_submissions WHERE conversation_id = @id ORDER BY created_at",
            new { id = conversationId });

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs every pending or orphaned task until the queue is empty. Safe to call on every
    /// start: an empty queue is a no-op.
    /// </summary>
    public async Task<int> ResumeAsync(
        ILlmClient client,
        IReadOnlyList<AiToolDefinition>? tools = null,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();

        var executed = 0;

        while (true)
        {
            var task = await _db.QuerySingleOrDefaultAsync<DurableTaskRow>(
                "SELECT * FROM agent_tasks WHERE status IN (@pending, @orphaned) ORDER BY created_at LIMIT 1",
                new
                {
                    pending = nameof(DurableTaskStatus.Pending),
                    orphaned = nameof(DurableTaskStatus.Orphaned)
                }).ConfigureAwait(false);

            if (task is null)
                return executed;

            if (task.Attempts >= _options.MaxAttempts)
            {
                await FailTaskAsync(task, "attempt budget exhausted").ConfigureAwait(false);
                continue;
            }

            await RunTaskAsync(task, client, tools, onEvent, cancellationToken).ConfigureAwait(false);
            executed++;
        }
    }

    /// <summary>
    /// Submits and runs to completion. The convenient entry point for scripts and tests.
    /// </summary>
    public async Task<DurableSubmissionRow> SubmitAndRunAsync(
        string content,
        ILlmClient client,
        IReadOnlyList<AiToolDefinition>? tools = null,
        string? requestId = null,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var submission = await SubmitAsync(content, requestId, cancellationToken).ConfigureAwait(false);

        if (submission.IsTerminal)
            return submission;

        await ResumeAsync(client, tools, onEvent, cancellationToken).ConfigureAwait(false);

        return await SubmissionAsync(submission.Id).ConfigureAwait(false) ?? submission;
    }

    private async Task RunTaskAsync(
        DurableTaskRow task,
        ILlmClient client,
        IReadOnlyList<AiToolDefinition>? tools,
        Action<AgentEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        // One task at a time: a conversation is busy while a run is going, and serializing here
        // also keeps the SQLite writer from contending with itself.
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _db.ExecuteAsync(
                "UPDATE agent_tasks SET status = @status, attempts = attempts + 1, updated_at = CURRENT_TIMESTAMP " +
                "WHERE id = @id",
                new { status = nameof(DurableTaskStatus.Running), id = task.Id }).ConfigureAwait(false);

            if (task.SubmissionId is { Length: > 0 })
            {
                await _db.ExecuteAsync(
                    "UPDATE agent_submissions SET status = @status, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                    new { status = nameof(DurableSubmissionStatus.Running), id = task.SubmissionId })
                    .ConfigureAwait(false);
            }

            // The transcript comes from storage, so a resumed run sees exactly what the model saw
            // before the crash.
            var history = await TranscriptAsync(task.ConversationId, cancellationToken).ConfigureAwait(false);

            if (_options.Agent.SystemPrompt is { Length: > 0 } prompt && history.All(m => m.Role != AgentRoles.System))
                history.Insert(0, new ChatMessage(AgentRoles.System, prompt));

            var config = new AgentLoopConfig
            {
                Model = client.Config.Model,
                MaxSteps = _options.Agent.MaxSteps,
                ToolExecutor = (invocation, _) =>
                    _app.ExecuteAiToolAsync(invocation.ToolName, invocation.ArgumentsJson),
                SequentialTools = new HashSet<string>(StringComparer.Ordinal) { "run_command" },
                Retry = new AgentRetryPolicy(),
                FinishTurn = async (turn, ct) =>
                {
                    await CheckpointAsync(task, turn, ct).ConfigureAwait(false);
                    return AgentTurnDecision.Default;
                }
            };

            var summary = await AgentLoop.RunAsync(
                history,
                config,
                client.StreamAsync,
                tools ?? _app.GetAiTools(onlyExplicit: true),
                onEvent,
                cancellationToken).ConfigureAwait(false);

            await CompleteTaskAsync(task, summary).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Leave the task running so the next process resumes it.
            throw;
        }
        catch (Exception ex)
        {
            await FailTaskAsync(task, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Commits the turn that just finished. This is the durability boundary: everything the
    /// model and tools produced is stored before the next request goes out.
    /// </summary>
    private async Task CheckpointAsync(
        DurableTaskRow task,
        AgentTurnResult turn,
        CancellationToken cancellationToken)
    {
        var committed = await _db.QueryAsync<DurableEntryRow>(
            "SELECT * FROM agent_entries WHERE conversation_id = @id ORDER BY seq",
            new { id = task.ConversationId }).ConfigureAwait(false);

        var known = committed.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var pending = new List<ChatMessage>();

        // The loop's history is the source of truth; append whatever storage has not seen.
        foreach (var message in turn.History)
        {
            var entryId = EntryIdFor(message);
            if (entryId is not null && known.Contains(entryId))
                continue;
            pending.Add(message);
        }

        if (pending.Count == 0)
            return;

        var lastEntryId = task.LastEntryId;

        await _db.TransactionAsync(async tx =>
        {
            foreach (var message in pending)
            {
                lastEntryId = await AppendEntryAsync(tx, task.ConversationId, message, cancellationToken)
                    .ConfigureAwait(false);
            }

            await tx.ExecuteAsync(
                "UPDATE agent_tasks SET turns_completed = @turns, last_entry_id = @lastEntryId, " +
                "updated_at = CURRENT_TIMESTAMP WHERE id = @id",
                new
                {
                    turns = turn.Step,
                    lastEntryId,
                    id = task.Id
                }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task CompleteTaskAsync(DurableTaskRow task, AgentRunSummary summary)
    {
        var status = summary.StopReason switch
        {
            AgentStopReason.Completed => DurableTaskStatus.Done,
            AgentStopReason.Cancelled => DurableTaskStatus.Orphaned,
            _ => DurableTaskStatus.Failed
        };

        await _db.ExecuteAsync(
            "UPDATE agent_tasks SET status = @status, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            new { status = status.ToString(), id = task.Id }).ConfigureAwait(false);

        if (task.SubmissionId is not { Length: > 0 })
            return;

        if (status == DurableTaskStatus.Done)
        {
            await _db.ExecuteAsync(
                "UPDATE agent_submissions SET status = @status, answer = @answer, updated_at = CURRENT_TIMESTAMP " +
                "WHERE id = @id",
                new
                {
                    status = nameof(DurableSubmissionStatus.Done),
                    answer = summary.FinalContent,
                    id = task.SubmissionId
                }).ConfigureAwait(false);
        }
        else if (status == DurableTaskStatus.Failed)
        {
            await _db.ExecuteAsync(
                "UPDATE agent_submissions SET status = @status, error = @error, updated_at = CURRENT_TIMESTAMP " +
                "WHERE id = @id",
                new
                {
                    status = nameof(DurableSubmissionStatus.Failed),
                    error = summary.FinalContent,
                    id = task.SubmissionId
                }).ConfigureAwait(false);
        }
    }

    private async Task FailTaskAsync(DurableTaskRow task, string error)
    {
        await _db.ExecuteAsync(
            "UPDATE agent_tasks SET status = @status, updated_at = CURRENT_TIMESTAMP WHERE id = @id",
            new { status = nameof(DurableTaskStatus.Failed), id = task.Id }).ConfigureAwait(false);

        if (task.SubmissionId is not { Length: > 0 })
            return;

        await _db.ExecuteAsync(
            "UPDATE agent_submissions SET status = @status, error = @error, updated_at = CURRENT_TIMESTAMP " +
            "WHERE id = @id",
            new
            {
                status = nameof(DurableSubmissionStatus.Failed),
                error,
                id = task.SubmissionId
            }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Entries
    // ------------------------------------------------------------------

    private static async Task<string> AppendEntryAsync(
        IDbClient tx,
        string conversationId,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        var next = await tx.QuerySingleOrDefaultAsync<SeqRow>(
            "SELECT COALESCE(MAX(seq), 0) + 1 AS value FROM agent_entries WHERE conversation_id = @id",
            new { id = conversationId }).ConfigureAwait(false);

        var seq = next?.Value ?? 1;
        var id = EntryIdFor(message) ?? NewId("ent");

        await tx.ExecuteAsync(
            "INSERT INTO agent_entries (id, conversation_id, parent_id, seq, kind, payload) " +
            "VALUES (@id, @conversationId, @parentId, @seq, @kind, @payload)",
            new
            {
                id,
                conversationId,
                parentId = (string?)null,
                seq,
                kind = message.Role,
                payload = SerializeMessage(message)
            }).ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// Stable identity for a message so a checkpoint can tell "already stored" from "new".
    /// Tool results key on their call id; everything else gets a content hash plus role.
    /// </summary>
    private static string? EntryIdFor(ChatMessage message)
    {
        if (message.ToolCallId is { Length: > 0 } callId)
            return "ent_" + Hash($"{message.Role}:{callId}");

        if (message.Role == AgentRoles.User)
            return "ent_" + Hash($"{message.Role}:{message.Content}");

        return null;
    }

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Serializes a message by hand. Reflection-based <c>JsonSerializer</c> is unavailable under
    /// Native AOT, which is a supported target, so the durable payload format is explicit.
    /// </summary>
    private static string SerializeMessage(ChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (message.Name is { Length: > 0 }) node["name"] = message.Name;
        if (message.ToolCallId is { Length: > 0 }) node["toolCallId"] = message.ToolCallId;

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

        return node.ToJsonString();
    }

    private static ChatMessage? Deserialize(DurableEntryRow entry)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(entry.Payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is null)
            return null;

        IReadOnlyList<AiToolCall>? calls = null;
        if (node["toolCalls"] is JsonArray array && array.Count > 0)
        {
            var list = new List<AiToolCall>(array.Count);
            foreach (var item in array)
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

        AgentUsage? usage = null;
        if (node["usage"] is JsonObject usageNode)
        {
            usage = new AgentUsage(
                usageNode["input"]?.GetValue<int>() ?? 0,
                usageNode["output"]?.GetValue<int>() ?? 0,
                usageNode["cacheRead"]?.GetValue<int>() ?? 0,
                usageNode["cacheWrite"]?.GetValue<int>() ?? 0);
        }

        return new ChatMessage(
            node["role"]?.GetValue<string>() ?? AgentRoles.User,
            node["content"]?.GetValue<string>() ?? "",
            node["name"]?.GetValue<string>(),
            node["toolCallId"]?.GetValue<string>(),
            calls,
            Usage: usage);
    }

    private sealed record SeqRow(long Value);

    private static string NewId(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 17)];

    private void ThrowIfReadOnly()
    {
        if (_options.ReadOnly)
            throw new InvalidOperationException(
                "This harness was opened read-only; it cannot write or run tasks.");
    }

    public ValueTask DisposeAsync()
    {
        _runGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif
