using System.Diagnostics;
using System.Text.Json.Nodes;
using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="AgentLoop"/> — the pi-parity guarantees that the
/// legacy harness did not provide: authoritative message ends, streamed tool output,
/// parallel tool execution, the truncation guard, steering, hooks, and settle ordering.
/// </summary>
public sealed class AgentLoopTests
{
    private static AiToolDefinition Tool(string name) => new()
    {
        Name = name,
        Description = $"{name} test tool",
        Parameters = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = new Dictionary<string, object?>() },
        Method = "POST",
        Path = $"/test/{name}"
    };

    private static IReadOnlyList<LlmStreamEvent> Events(params LlmStreamEvent[] events) => events;

    private static Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> Streams(
        params IReadOnlyList<LlmStreamEvent>[] calls) => new ScriptedEventLlmClient(calls).StreamAsync;

    // ------------------------------------------------------------------
    // Message lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_emits_authoritative_message_start_update_and_end_for_assistant_text()
    {
        var events = new List<AgentEvent>();
        var history = new List<ChatMessage>();

        var summary = await AgentLoop.RunAsync(
            history,
            new AgentLoopConfig { Model = "test", MaxSteps = 4 },
            Streams(ScriptedEvents.TextEvents("Hello world")),
            [],
            events.Add);

        Assert.Equal(AgentStopReason.Completed, summary.StopReason);
        Assert.Equal("Hello world", summary.FinalContent);

        var assistantStart = Assert.Single(events.OfType<AgentEvent.MessageStart>());
        Assert.Equal("assistant", assistantStart.Message.Role);

        var update = Assert.Single(events.OfType<AgentEvent.MessageUpdate>());
        Assert.Equal(AgentContentKind.Text, update.Kind);
        Assert.Equal("Hello world", update.Delta);

        var assistantEnd = Assert.Single(events.OfType<AgentEvent.MessageEnd>());
        Assert.Equal("Hello world", assistantEnd.Message.Content);

        // The authoritative end carries the complete text, not a delta.
        Assert.Equal("Hello world", history[^1].Content);
    }

    [Fact]
    public async Task loop_emits_agent_settled_after_agent_end()
    {
        var events = new List<AgentEvent>();

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig { Model = "test", MaxSteps = 2 },
            Streams(ScriptedEvents.TextEvents("done")),
            [],
            events.Add);

        var types = events.Select(e => e.GetType().Name).ToList();
        Assert.Equal("AgentStart", types[0]);
        Assert.Contains("AgentEnd", types);
        Assert.Equal("AgentSettled", types[^1]);
        Assert.True(
            types.IndexOf("AgentEnd") < types.IndexOf("AgentSettled"),
            "agent_settled must follow agent_end");
    }

    [Fact]
    public async Task loop_stops_at_max_steps_without_running_forever()
    {
        var history = new List<ChatMessage>();
        var toolRuns = 0;

        var summary = await AgentLoop.RunAsync(
            history,
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = (_, _) =>
                {
                    toolRuns++;
                    return Task.FromResult(AiToolResult.Ok(200, "ok"));
                }
            },
            Streams(ScriptedEvents.ToolCallEvents("c1", "loop_tool", "{}")),
            [Tool("loop_tool")]);

        Assert.Equal(AgentStopReason.MaxSteps, summary.StopReason);
        Assert.Equal(2, toolRuns);
    }

    // ------------------------------------------------------------------
    // Parallel tool execution
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_runs_independent_tool_calls_concurrently()
    {
        LlmStreamEvent[] calls =
        [
            new LlmStreamEvent.ToolCall("c1", "slow", "{}"),
            new LlmStreamEvent.ToolCall("c2", "slow", "{}"),
            new LlmStreamEvent.ToolCall("c3", "slow", "{}"),
            new LlmStreamEvent.Completed("tool_use")
        ];

        var started = 0;
        var allStarted = new TaskCompletionSource();

        var watch = Stopwatch.StartNew();
        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = async (_, _) =>
                {
                    if (Interlocked.Increment(ref started) == 3)
                        allStarted.TrySetResult();

                    // Each call parks until all three are in flight; sequential execution
                    // would deadlock and the assertion below would fail on timeout.
                    await Task.WhenAny(allStarted.Task, Task.Delay(5000));
                    return AiToolResult.Ok(200, "ok");
                }
            },
            Streams(Events(calls), ScriptedEvents.TextEvents("all three finished")),
            [Tool("slow")]);

        watch.Stop();

        Assert.Equal(3, summary.ToolCallsExecuted);
        Assert.True(watch.ElapsedMilliseconds < 4000, $"tool calls did not overlap ({watch.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task loop_runs_declared_sequential_tools_one_at_a_time()
    {
        var active = 0;
        var maxConcurrent = 0;

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                SequentialTools = new HashSet<string>(StringComparer.Ordinal) { "serial" },
                ToolExecutor = async (_, _) =>
                {
                    var now = Interlocked.Increment(ref active);
                    InterlockedMax(ref maxConcurrent, now);
                    await Task.Delay(30);
                    Interlocked.Decrement(ref active);
                    return AiToolResult.Ok(200, "ok");
                }
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "serial", "{}"),
                    new LlmStreamEvent.ToolCall("c2", "serial", "{}"),
                    new LlmStreamEvent.Completed("tool_use")),
                ScriptedEvents.TextEvents("done")),
            [Tool("serial")]);

        Assert.Equal(2, summary.ToolCallsExecuted);
        Assert.Equal(1, maxConcurrent);
    }

    // ------------------------------------------------------------------
    // Truncation guard
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_refuses_to_execute_tool_calls_from_a_length_truncated_response()
    {
        var executed = 0;

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = (_, _) =>
                {
                    executed++;
                    return Task.FromResult(AiToolResult.Ok(200, "ran"));
                }
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "danger", """{"cmd":"rm -rf /tmp/x"}"""),
                    new LlmStreamEvent.Completed("length")),
                ScriptedEvents.TextEvents("re-issued correctly")),
            [Tool("danger")]);

        Assert.Equal(0, executed);
        Assert.True(summary.Truncated);

        var end = Assert.Single(
            summary.History.Where(m => m.Role == "tool"),
            m => m.ToolCallId == "c1");
        Assert.Contains("output token limit", end.Content);
    }

    // ------------------------------------------------------------------
    // Steering and follow-up queues
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_delivers_steering_message_before_the_next_request()
    {
        var steering = new Queue<ChatMessage>();
        steering.Enqueue(new ChatMessage("user", "actually check the tests"));
        var requests = new List<IReadOnlyList<ChatMessage>>();

        var client = new ScriptedEventLlmClient(
            [
                new LlmStreamEvent.ToolCall("c1", "noop", "{}"),
                new LlmStreamEvent.Completed("tool_use")
            ],
            ScriptedEvents.TextEvents("done"));

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 4,
                ToolExecutor = (_, _) => Task.FromResult(AiToolResult.Ok(200, "ok")),
                GetSteeringMessages = () => steering.Count > 0 ? [steering.Dequeue()] : []
            },
            (messages, tools, ct) =>
            {
                requests.Add([.. messages]);
                return client.StreamAsync(messages, tools, ct);
            },
            [Tool("noop")]);

        // The steering message must appear in the second request, not after the run.
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests[1], m => m.Content == "actually check the tests");
    }

    [Fact]
    public async Task loop_delivers_follow_up_only_after_the_agent_would_stop()
    {
        var followUp = new Queue<ChatMessage>();
        followUp.Enqueue(new ChatMessage("user", "now summarize"));
        var requests = new List<IReadOnlyList<ChatMessage>>();

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.TextEvents("first answer"),
            ScriptedEvents.TextEvents("second answer"));

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 4,
                GetFollowUpMessages = () => followUp.Count > 0 ? [followUp.Dequeue()] : []
            },
            (messages, tools, ct) =>
            {
                requests.Add([.. messages]);
                return client.StreamAsync(messages, tools, ct);
            },
            []);

        Assert.Equal(2, requests.Count);
        Assert.Contains(requests[1], m => m.Content == "now summarize");
        Assert.Equal("second answer", summary.FinalContent);
    }

    // ------------------------------------------------------------------
    // Tool hooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_blocks_a_tool_call_through_the_before_hook()
    {
        var executed = 0;
        var toolResults = new List<string>();

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 3,
                ToolExecutor = (_, _) =>
                {
                    executed++;
                    return Task.FromResult(AiToolResult.Ok(200, "ran"));
                },
                BeforeToolCall = (ctx, _) => Task.FromResult(
                    ctx.Call.Name == "write_file"
                        ? new AgentToolDecision(Block: true, Reason: "user declined")
                        : AgentToolDecision.Allow)
            },
            Streams(Events(
                new LlmStreamEvent.ToolCall("c1", "write_file", "{}"),
                new LlmStreamEvent.Completed("tool_use"),
                new LlmStreamEvent.TextDelta("understood"),
                new LlmStreamEvent.Completed("stop"))),
            [Tool("write_file")]);

        Assert.Equal(0, executed);

        var result = summary.History.First(m => m.Role == "tool");
        Assert.Contains("user declined", result.Content);
        toolResults.Add(result.Content);
        Assert.Single(toolResults);
    }

    [Fact]
    public async Task loop_lets_the_after_hook_rewrite_a_tool_result()
    {
        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = (_, _) => Task.FromResult(AiToolResult.Ok(200, "raw")),
                AfterToolCall = (_, _, _, _) => Task.FromResult<AiToolResult?>(
                    AiToolResult.Ok(200, "redacted"))
            },
            Streams(Events(
                new LlmStreamEvent.ToolCall("c1", "read_file", "{}"),
                new LlmStreamEvent.Completed("tool_use"))),
            [Tool("read_file")]);

        var result = summary.History.First(m => m.Role == "tool");
        Assert.Equal("redacted", result.Content);
    }

    // ------------------------------------------------------------------
    // Streaming tool output
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_forwards_partial_tool_output_as_tool_execution_updates()
    {
        var updates = new List<string>();
        var events = new List<AgentEvent>();

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = (invocation, _) =>
                {
                    invocation.OnUpdate?.Invoke("line 1");
                    invocation.OnUpdate?.Invoke("line 2");
                    return Task.FromResult(AiToolResult.Ok(200, "line 1\nline 2"));
                }
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "run_command", "{}"),
                    new LlmStreamEvent.Completed("tool_use")),
                ScriptedEvents.TextEvents("command finished")),
            [Tool("run_command")],
            evt =>
            {
                events.Add(evt);
                if (evt is AgentEvent.ToolExecutionUpdate update)
                    updates.Add(update.PartialResult);
            });

        Assert.Equal(["line 1", "line 2"], updates);

        var start = Assert.Single(events.OfType<AgentEvent.ToolExecutionStart>());
        Assert.Equal("run_command", start.ToolName);

        var end = Assert.Single(events.OfType<AgentEvent.ToolExecutionEnd>());
        Assert.False(end.IsError);
        Assert.True(end.DurationMs >= 0);
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_reports_cancelled_and_emits_settled_when_the_token_fires()
    {
        using var cts = new CancellationTokenSource();
        var events = new List<AgentEvent>();

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 5,
                ToolExecutor = (_, _) =>
                {
                    cts.Cancel();
                    return Task.FromResult(AiToolResult.Ok(200, "ok"));
                }
            },
            Streams(Events(
                new LlmStreamEvent.ToolCall("c1", "noop", "{}"),
                new LlmStreamEvent.Completed("tool_use"))),
            [Tool("noop")],
            events.Add,
            cts.Token);

        Assert.Equal(AgentStopReason.Cancelled, summary.StopReason);
        var settled = Assert.Single(events.OfType<AgentEvent.AgentSettled>());
        Assert.True(settled.Aborted);
    }


    // ------------------------------------------------------------------
    // JSONL protocol surface
    // ------------------------------------------------------------------

    [Fact]
    public void event_json_uses_pis_record_names_and_field_casing()
    {
        var start = new AgentEvent.ToolExecutionStart(2, "call_1", "read_file", """{"path":"a.txt"}""");
        var startJson = JsonNode.Parse(start.ToJson())!.AsObject();

        Assert.Equal("tool_execution_start", startJson["type"]!.GetValue<string>());
        Assert.Equal("call_1", startJson["toolCallId"]!.GetValue<string>());
        Assert.Equal("read_file", startJson["toolName"]!.GetValue<string>());
        Assert.Equal("a.txt", startJson["args"]!["path"]!.GetValue<string>());

        var delta = new AgentEvent.MessageUpdate(1, AgentContentKind.Text, 0, "hi");
        var deltaJson = JsonNode.Parse(delta.ToJson())!.AsObject();
        Assert.Equal("message_update", deltaJson["type"]!.GetValue<string>());
        Assert.Equal("text_delta", deltaJson["assistantMessageEvent"]!["type"]!.GetValue<string>());
        Assert.Equal("hi", deltaJson["assistantMessageEvent"]!["delta"]!.GetValue<string>());

        var settled = new AgentEvent.AgentSettled(Aborted: true);
        Assert.Equal("agent_settled", settled.ToNode()["type"]!.GetValue<string>());
        Assert.True(settled.ToNode()["aborted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task event_stream_round_trips_as_one_jsonl_record_per_event()
    {
        var records = new List<string>();

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig { Model = "test", MaxSteps = 2 },
            Streams(ScriptedEvents.TextEvents("done")),
            [],
            evt => records.Add(evt.ToJson()));

        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            Assert.DoesNotContain('\n', record);
            var parsed = JsonNode.Parse(record);
            Assert.NotNull(parsed);
            Assert.NotNull(parsed!["type"]);
        }

        Assert.Equal("agent_start", JsonNode.Parse(records[0])!["type"]!.GetValue<string>());
        Assert.Equal("agent_settled", JsonNode.Parse(records[^1])!["type"]!.GetValue<string>());
    }


    // ------------------------------------------------------------------
    // Provider retry
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_retries_transient_provider_failures_and_recovers()
    {
        var attempts = 0;
        var events = new List<AgentEvent>();

        IAsyncEnumerable<LlmStreamEvent> Flaky(
            IReadOnlyList<ChatMessage> _,
            IReadOnlyList<AiToolDefinition> __,
            CancellationToken ___)
        {
            attempts++;
            return attempts == 1 ? Throw429() : ToAsync(ScriptedEvents.TextEvents("recovered"));
        }

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                Retry = new AgentRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }
            },
            Flaky,
            [],
            events.Add);

        Assert.Equal(2, attempts);
        Assert.Equal(AgentStopReason.Completed, summary.StopReason);
        Assert.Equal("recovered", summary.FinalContent);
        Assert.Single(events.OfType<AgentEvent.RetryScheduled>());
        Assert.Single(events.OfType<AgentEvent.RetryAttempt>());
        Assert.True(events.OfType<AgentEvent.RetryFinished>().Single().Recovered);
    }

    [Fact]
    public async Task loop_does_not_retry_a_terminal_provider_error()
    {
        var attempts = 0;

        IAsyncEnumerable<LlmStreamEvent> BadRequest(
            IReadOnlyList<ChatMessage> _,
            IReadOnlyList<AiToolDefinition> __,
            CancellationToken ___)
        {
            attempts++;
            return Throw400();
        }

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                Retry = new AgentRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }
            },
            BadRequest,
            []);

        Assert.Equal(1, attempts);
        Assert.Equal(AgentStopReason.Error, summary.StopReason);
    }

    [Fact]
    public async Task loop_stops_retrying_after_the_attempt_budget()
    {
        var attempts = 0;

        IAsyncEnumerable<LlmStreamEvent> AlwaysOverloaded(
            IReadOnlyList<ChatMessage> _,
            IReadOnlyList<AiToolDefinition> __,
            CancellationToken ___)
        {
            attempts++;
            return Throw429();
        }

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                Retry = new AgentRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }
            },
            AlwaysOverloaded,
            []);

        Assert.Equal(3, attempts);
        Assert.Equal(AgentStopReason.Error, summary.StopReason);
    }

    private static async IAsyncEnumerable<LlmStreamEvent> ToAsync(IReadOnlyList<LlmStreamEvent> events)
    {
        await Task.Yield();
        foreach (var streamEvent in events)
            yield return streamEvent;
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Throw429()
    {
        await Task.Yield();
        throw new LlmException(429, "rate limited");
#pragma warning disable CS0162 // unreachable, required for an iterator body
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Throw400()
    {
        await Task.Yield();
        throw new LlmException(400, "bad request");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }


    // ------------------------------------------------------------------
    // Nested tool calls
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_tool_can_call_another_tool_through_the_same_hooks()
    {
        var innerRan = 0;
        var approvals = new List<string>();

        var summary = await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 3,
                ToolExecutor = async (invocation, ct) =>
                {
                    if (invocation.ToolName == "outer")
                    {
                        var nested = await AgentToolContext.ExecuteAsync(
                            "inner", """{"path":"a.txt"}""", ct);
                        return nested.Success
                            ? AiToolResult.Ok(200, "outer saw: " + nested.Content)
                            : nested;
                    }

                    innerRan++;
                    return AiToolResult.Ok(200, "inner-ok");
                },
                BeforeToolCall = (ctx, _) =>
                {
                    approvals.Add(ctx.Call.Name);
                    return Task.FromResult(AgentToolDecision.Allow);
                }
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "outer", "{}"),
                    new LlmStreamEvent.Completed("tool_use")),
                ScriptedEvents.TextEvents("done")),
            [Tool("outer"), Tool("inner")]);

        Assert.Equal(1, innerRan);
        Assert.Equal(["outer", "inner"], approvals);
        Assert.Contains("outer saw: inner-ok", summary.History.First(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task a_nested_call_is_blocked_by_the_same_approval_policy()
    {
        var innerRan = 0;

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = async (invocation, ct) =>
                {
                    if (invocation.ToolName == "outer")
                    {
                        return await AgentToolContext.ExecuteAsync(
                            "write_file", """{"path":"x"}""", ct);
                    }

                    innerRan++;
                    return AiToolResult.Ok(200, "should not run");
                },
                BeforeToolCall = (ctx, _) => Task.FromResult(
                    ctx.Call.Name == "write_file"
                        ? new AgentToolDecision(Block: true, Reason: "read-only session")
                        : AgentToolDecision.Allow)
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "outer", "{}"),
                    new LlmStreamEvent.Completed("tool_use")),
                ScriptedEvents.TextEvents("done")),
            [Tool("outer"), Tool("write_file")]);

        Assert.Equal(0, innerRan);
    }

    [Fact]
    public async Task nested_calls_are_depth_limited()
    {
        var results = new List<AiToolResult>();

        await AgentLoop.RunAsync(
            [],
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = async (invocation, ct) =>
                {
                    if (invocation.ToolName == "recurse")
                    {
                        var nested = await AgentToolContext.ExecuteAsync("recurse", "{}", ct);
                        results.Add(nested);
                        return nested;
                    }

                    return AiToolResult.Ok(200, "leaf");
                }
            },
            Streams(
                Events(
                    new LlmStreamEvent.ToolCall("c1", "recurse", "{}"),
                    new LlmStreamEvent.Completed("tool_use")),
                ScriptedEvents.TextEvents("done")),
            [Tool("recurse")]);

        // The recursion terminates rather than overflowing the stack, and every level sees the
        // refusal because the innermost failure propagates back up through each enclosing call.
        Assert.Equal(AgentToolContext.MaxDepth + 1, results.Count);
        Assert.All(results, result => Assert.Equal("nested_depth_exceeded", result.ErrorCode));
    }

    [Fact]
    public async Task calling_a_nested_tool_outside_a_run_fails_cleanly()
    {
        var result = await AgentToolContext.ExecuteAsync("read_file", "{}");

        Assert.False(result.Success);
        Assert.Equal("no_agent_context", result.ErrorCode);
        Assert.False(AgentToolContext.IsAvailable);
    }

    [Fact]
    public async Task run_tool_call_applies_the_after_hook()
    {
        var result = await AgentLoop.RunToolCallAsync(
            new AiToolCall("c1", "read_file", "{}"),
            new AgentLoopConfig
            {
                Model = "test",
                ToolExecutor = (_, _) => Task.FromResult(AiToolResult.Ok(200, "raw")),
                AfterToolCall = (_, _, _, _) => Task.FromResult<AiToolResult?>(AiToolResult.Ok(200, "redacted"))
            });

        Assert.Equal("redacted", result.Content);
    }

    private static void InterlockedMax(ref int target, int value)

    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
