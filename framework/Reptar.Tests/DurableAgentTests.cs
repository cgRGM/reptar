using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="DurableAgentHarness"/>: the durability guarantees a normal
/// agent loop does not make. The important ones are crash-safe resume, idempotent submits, and
/// "nothing is reported before it is committed".
/// </summary>
public sealed class DurableAgentTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public DurableAgentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-durable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "durable.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A fresh app over the same database file, simulating a process restart.</summary>
    private ReptarApp CreateApp()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(_dbPath) });
        app.UseWorkspaceTools(_root);
        return app;
    }

    private static DurableHarnessOptions Options(string model = "test") => new()
    {
        Agent = new DurableAgentChoices { Model = model, SystemPrompt = "you are a coding agent" },
        Title = "durable test"
    };

    // ------------------------------------------------------------------
    // Tables and conversation
    // ------------------------------------------------------------------

    [Fact]
    public async Task open_creates_the_tables_and_a_single_root_conversation()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var first = await harness.RootAsync();
        var second = await harness.RootAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("durable test", first.Title);
    }

    // ------------------------------------------------------------------
    // Submissions
    // ------------------------------------------------------------------

    [Fact]
    public async Task submit_commits_the_input_entry_and_the_task_atomically()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var submission = await harness.SubmitAsync("what changed?");
        var transcript = await harness.TranscriptAsync(submission.ConversationId);

        Assert.Equal(DurableSubmissionStatus.Pending, submission.State);
        var single = Assert.Single(transcript);
        Assert.Equal(AgentRoles.User, single.Role);
        Assert.Equal("what changed?", single.Content);

        var tasks = await app.Db.QueryAsync<DurableTaskRow>("SELECT * FROM agent_tasks");
        var task = Assert.Single(tasks);
        Assert.Equal(DurableTaskStatus.Pending, task.State);
        Assert.Equal(submission.Id, task.SubmissionId);
    }

    [Fact]
    public async Task resubmitting_the_same_request_id_returns_the_original()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var first = await harness.SubmitAsync("hello", requestId: "greeting-1");
        var second = await harness.SubmitAsync("hello", requestId: "greeting-1");

        Assert.Equal(first.Id, second.Id);

        var all = await app.Db.QueryAsync<DurableSubmissionRow>("SELECT * FROM agent_submissions");
        Assert.Single(all);
    }

    [Fact]
    public async Task a_different_request_id_creates_a_second_submission()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        await harness.SubmitAsync("one", requestId: "a");
        await harness.SubmitAsync("two", requestId: "b");

        var all = await app.Db.QueryAsync<DurableSubmissionRow>("SELECT * FROM agent_submissions");
        Assert.Equal(2, all.Count);
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    [Fact]
    public async Task submit_and_run_completes_and_records_the_answer()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("forty-two"));
        var submission = await harness.SubmitAndRunAsync("what is the answer?", client);

        Assert.Equal(DurableSubmissionStatus.Done, submission.State);
        Assert.Equal("forty-two", submission.Answer);

        var transcript = await harness.TranscriptAsync(submission.ConversationId);
        Assert.Contains(transcript, m => m.Role == AgentRoles.User && m.Content == "what is the answer?");
        Assert.Contains(transcript, m => m.Role == AgentRoles.Assistant && m.Content == "forty-two");
    }

    [Fact]
    public async Task resume_is_a_no_op_when_the_queue_is_empty()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("ok"));
        var executed = await harness.ResumeAsync(client);

        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task resume_runs_a_pending_task_left_by_a_previous_process()
    {
        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("resumed answer"));

        // Process 1: admit the work, then "crash" without running it.
        string submissionId;
        var first = CreateApp();
        await using (var harness = await DurableAgentHarness.OpenAsync(first, Options()))
        {
            var seeded = await harness.SubmitAsync("do the thing");
            submissionId = seeded.Id;
        }

        // Process 2: same database, fresh app.
        var second = CreateApp();
        await using var resumed = await DurableAgentHarness.OpenAsync(second, Options());
        var executed = await resumed.ResumeAsync(client);

        Assert.Equal(1, executed);

        var submission = await resumed.SubmissionAsync(submissionId);
        Assert.NotNull(submission);
        Assert.Equal(DurableSubmissionStatus.Done, submission!.State);
        Assert.Equal("resumed answer", submission.Answer);
    }

    [Fact]
    public async Task open_marks_a_task_left_running_as_orphaned()
    {
        var app = CreateApp();
        string taskId;
        string submissionId;

        await using (var harness = await DurableAgentHarness.OpenAsync(app, Options()))
        {
            var seeded = await harness.SubmitAsync("interrupted work");
            submissionId = seeded.Id;
            var tasks = await app.Db.QueryAsync<DurableTaskRow>("SELECT * FROM agent_tasks");
            taskId = tasks[0].Id;

            await app.Db.ExecuteAsync(
                "UPDATE agent_tasks SET status = @status WHERE id = @id",
                new { status = nameof(DurableTaskStatus.Running), id = taskId });
            await app.Db.ExecuteAsync(
                "UPDATE agent_submissions SET status = @status WHERE id = @id",
                new { status = nameof(DurableSubmissionStatus.Running), id = submissionId });
        }

        var reopened = CreateApp();
        await using var harness2 = await DurableAgentHarness.OpenAsync(reopened, Options());

        var task = await reopened.Db.QuerySingleOrDefaultAsync<DurableTaskRow>(
            "SELECT * FROM agent_tasks WHERE id = @id", new { id = taskId });
        Assert.Equal(DurableTaskStatus.Orphaned, task!.State);

        var submission = await harness2.SubmissionAsync(submissionId);
        Assert.Equal(DurableSubmissionStatus.Pending, submission!.State);
    }

    [Fact]
    public async Task an_orphaned_task_is_resumed_and_finished()
    {
        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("finished after restart"));
        string submissionId;

        var first = CreateApp();
        await using (var harness = await DurableAgentHarness.OpenAsync(first, Options()))
        {
            var seeded = await harness.SubmitAsync("long running work");
            submissionId = seeded.Id;
            await first.Db.ExecuteAsync(
                "UPDATE agent_tasks SET status = @status",
                new { status = nameof(DurableTaskStatus.Running) });
        }

        var second = CreateApp();
        await using var resumed = await DurableAgentHarness.OpenAsync(second, Options());
        await resumed.ResumeAsync(client);

        var submission = await resumed.SubmissionAsync(submissionId);
        Assert.Equal(DurableSubmissionStatus.Done, submission!.State);
        Assert.Equal("finished after restart", submission.Answer);
    }

    // ------------------------------------------------------------------
    // Checkpointing
    // ------------------------------------------------------------------

    [Fact]
    public async Task every_turn_is_committed_so_a_restart_keeps_the_partial_work()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        // Turn 1 calls a tool, turn 2 answers. Both turns must be durable.
        var client = new ScriptedEventLlmClient(
            [
                new LlmStreamEvent.ToolCall("c1", "list_dir", """{"path":"."}"""),
                new LlmStreamEvent.Completed("tool_use")
            ],
            ScriptedEvents.TextEvents("all done"));

        var submission = await harness.SubmitAndRunAsync("list the files", client);

        Assert.Equal(DurableSubmissionStatus.Done, submission.State);

        var transcript = await harness.TranscriptAsync(submission.ConversationId);
        Assert.Contains(transcript, m => m.Role == AgentRoles.User);
        Assert.Contains(transcript, m => m.Role == AgentRoles.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Contains(transcript, m => m.Role == AgentRoles.Tool);
        Assert.Contains(transcript, m => m.Role == AgentRoles.Assistant && m.Content == "all done");

        var task = await app.Db.QuerySingleOrDefaultAsync<DurableTaskRow>(
            "SELECT * FROM agent_tasks WHERE submission_id = @id", new { id = submission.Id });
        Assert.Equal(DurableTaskStatus.Done, task!.State);
        Assert.True(task.TurnsCompleted >= 1);
    }

    [Fact]
    public async Task a_failing_provider_marks_the_submission_failed_rather_than_leaving_it_pending()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var client = new ScriptedEventLlmClient([new LlmStreamEvent.Failed("provider exploded")]);
        var submission = await harness.SubmitAndRunAsync("this will fail", client);

        Assert.Equal(DurableSubmissionStatus.Failed, submission.State);
        Assert.NotNull(submission.Error);
    }

    [Fact]
    public async Task attempt_budget_stops_an_endlessly_failing_task()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, new DurableHarnessOptions
        {
            Agent = new DurableAgentChoices { Model = "test" },
            MaxAttempts = 0
        });

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("never runs"));
        var submission = await harness.SubmitAsync("nope");
        await harness.ResumeAsync(client);

        var stored = await harness.SubmissionAsync(submission.Id);
        Assert.Equal(DurableSubmissionStatus.Failed, stored!.State);
    }

    // ------------------------------------------------------------------
    // Read-only
    // ------------------------------------------------------------------

    [Fact]
    public async Task read_only_harness_refuses_to_submit_or_resume()
    {
        var app = CreateApp();

        await using (var writer = await DurableAgentHarness.OpenAsync(app, Options()))
            await writer.SubmitAsync("seed");

        await using var reader = await DurableAgentHarness.OpenAsync(app, new DurableHarnessOptions
        {
            Agent = new DurableAgentChoices { Model = "test" },
            ReadOnly = true
        });

        // Reading works.
        var root = await reader.RootAsync();
        Assert.NotNull(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SubmitAsync("blocked"));

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ResumeAsync(client));
    }
    // ------------------------------------------------------------------
    // Forks
    // ------------------------------------------------------------------

    [Fact]
    public async Task fork_copies_the_transcript_and_leaves_the_original_alone()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("first answer"));
        var submission = await harness.SubmitAndRunAsync("hello", client);

        var fork = await harness.ForkAsync(submission.ConversationId, title: "alternative");

        Assert.NotNull(fork);
        Assert.NotEqual(submission.ConversationId, fork!.Id);
        Assert.Equal("alternative", fork.Title);

        var original = await harness.TranscriptAsync(submission.ConversationId);
        var copied = await harness.TranscriptAsync(fork.Id);

        Assert.Equal(original.Count, copied.Count);
        Assert.Equal(
            original.Select(m => m.Content),
            copied.Select(m => m.Content));
    }

    [Fact]
    public async Task fork_can_cut_at_a_specific_entry()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.TextEvents("one"),
            ScriptedEvents.TextEvents("two"));

        var first = await harness.SubmitAndRunAsync("first", client);
        await harness.SubmitAndRunAsync("second", client);

        var entries = await app.Db.QueryAsync<DurableEntryRow>(
            "SELECT * FROM agent_entries WHERE conversation_id = @id ORDER BY seq",
            new { id = first.ConversationId });

        Assert.True(entries.Count >= 3);

        // Cut after the first user message: the fork should contain exactly that.
        var fork = await harness.ForkAsync(first.ConversationId, throughEntryId: entries[0].Id);
        var copied = await harness.TranscriptAsync(fork!.Id);

        Assert.Single(copied);
        Assert.Equal("first", copied[0].Content);
    }

    [Fact]
    public async Task forking_a_missing_conversation_returns_null()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        Assert.Null(await harness.ForkAsync("conv_does_not_exist"));
    }

    [Fact]
    public async Task forking_at_a_missing_entry_returns_null()
    {
        var app = CreateApp();
        await using var harness = await DurableAgentHarness.OpenAsync(app, Options());

        var submission = await harness.SubmitAsync("hello");

        Assert.Null(await harness.ForkAsync(submission.ConversationId, throughEntryId: "ent_nope"));
    }

    [Fact]
    public async Task a_read_only_harness_cannot_fork()
    {
        var app = CreateApp();

        string conversationId;
        await using (var writer = await DurableAgentHarness.OpenAsync(app, Options()))
            conversationId = (await writer.SubmitAsync("seed")).ConversationId;

        await using var reader = await DurableAgentHarness.OpenAsync(app, new DurableHarnessOptions
        {
            Agent = new DurableAgentChoices { Model = "test" },
            ReadOnly = true
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ForkAsync(conversationId));
    }
}
