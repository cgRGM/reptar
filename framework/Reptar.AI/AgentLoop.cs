#if !NETWASM
using System.Diagnostics;
using System.Text;

namespace Reptar.AI;

/// <summary>
/// Why an agent run stopped. Mirrors pi's <c>AgentStopReason</c>; <see cref="MaxSteps"/>
/// and <see cref="Cancelled"/> are Reptar extensions for budget and abort paths.
/// </summary>
public enum AgentStopReason
{
    Completed,
    MaxSteps,
    Cancelled,
    Error
}

/// <summary>
/// Terminal state of one low-level agent run.
/// </summary>
public sealed record AgentRunSummary(
    AgentStopReason StopReason,
    string FinalContent,
    int StepsTaken,
    int ToolCallsExecuted,
    string Model,
    double ElapsedMs,
    IReadOnlyList<ChatMessage> History,
    bool Truncated = false)
{
    public bool Success => StopReason == AgentStopReason.Completed;
}

/// <summary>
/// Which content block a <see cref="AgentEvent.MessageUpdate"/> refers to.
/// Matches pi's <c>text_*</c> / <c>thinking_*</c> / <c>toolcall_*</c> families.
/// </summary>
public enum AgentContentKind
{
    Text,
    Thinking,
    ToolCall
}

/// <summary>
/// Events emitted by <see cref="AgentLoop"/>. The vocabulary deliberately mirrors
/// pi's JSONL protocol (<c>agent_start</c>, <c>turn_start</c>, <c>message_start</c>,
/// <c>message_update</c>, <c>message_end</c>, <c>tool_execution_*</c>, <c>turn_end</c>,
/// <c>agent_end</c>, <c>agent_settled</c>) so the C# TUI, the web SPA, and a future
/// JSONL RPC mode can all consume one stream without translation.
/// </summary>
public abstract record AgentEvent
{
    /// <summary>The turn index this event belongs to. Zero for run-level events.</summary>
    public abstract int Step { get; }

    /// <summary>A low-level run started.</summary>
    public sealed record AgentStart(int MaxSteps) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>One assistant turn started. <see cref="Step"/> is 1-based.</summary>
    public sealed record TurnStart(int TurnStep) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>
    /// A message entered the transcript (user, assistant, or tool result).
    /// For assistant messages this fires with an empty partial body before streaming.
    /// </summary>
    public sealed record MessageStart(int TurnStep, ChatMessage Message) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>
    /// An incremental content delta. Clients buffer deltas per
    /// (<see cref="ContentIndex"/>, <see cref="Kind"/>) and replace their buffer with
    /// the authoritative content from <see cref="MessageEnd"/>.
    /// </summary>
    public sealed record MessageUpdate(
        int TurnStep,
        AgentContentKind Kind,
        int ContentIndex,
        string Delta) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>The authoritative final form of a message. Clients should treat this as truth.</summary>
    public sealed record MessageEnd(int TurnStep, ChatMessage Message) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>A tool call began executing.</summary>
    public sealed record ToolExecutionStart(
        int TurnStep,
        string ToolCallId,
        string ToolName,
        string ArgumentsJson) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>
    /// A running tool reported partial output. This is what keeps a long
    /// <c>run_command</c> from looking like a hang.
    /// </summary>
    public sealed record ToolExecutionUpdate(
        int TurnStep,
        string ToolCallId,
        string ToolName,
        string PartialResult) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>A tool call finished. <see cref="IsError"/> is authoritative, not the text.</summary>
    public sealed record ToolExecutionEnd(
        int TurnStep,
        string ToolCallId,
        string ToolName,
        string Result,
        bool IsError,
        double DurationMs) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>
    /// Model-reported token usage for the assistant response just completed.
    /// <see cref="CacheReadTokens"/> is the part of the input served from the provider's prompt
    /// cache, which is what makes caching measurable.
    /// </summary>
    public sealed record UsageReported(
        int TurnStep,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens = 0,
        int CacheWriteTokens = 0) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>One assistant response and the tool results it produced.</summary>
    public sealed record TurnEnd(
        int TurnStep,
        ChatMessage Message,
        IReadOnlyList<ChatMessage> ToolResults) : AgentEvent
    {
        public override int Step => TurnStep;
    }

    /// <summary>
    /// The low-level run ended. Automatic work (retry, compaction recovery, steering,
    /// follow-up) may still start another run — wait for <see cref="AgentSettled"/>.
    /// </summary>
    public sealed record AgentEnd(AgentRunSummary Summary) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>No further automatic work will run for this submission.</summary>
    public sealed record AgentSettled(bool Aborted) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>A provider or transport failure, reported for display alongside the summary.</summary>
    public sealed record AgentError(string Message) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>
    /// A transient provider failure is being retried. Clients show a countdown instead of
    /// a frozen screen.
    /// </summary>
    public sealed record RetryScheduled(int Attempt, double DelayMs, string Reason) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>A retry attempt is starting.</summary>
    public sealed record RetryAttempt(int Attempt) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>Retrying stopped: either it succeeded or the policy gave up.</summary>
    public sealed record RetryFinished(bool Recovered) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>Context compaction started. Matches pi's <c>compaction_start</c>.</summary>
    public sealed record CompactionStart(CompactionReason Reason) : AgentEvent
    {
        public override int Step => 0;
    }

    /// <summary>Context compaction finished, successfully or not.</summary>
    public sealed record CompactionEnd(
        CompactionReason Reason,
        int TokensBefore,
        int EstimatedTokensAfter,
        string? Error) : AgentEvent
    {
        public override int Step => 0;
    }
}

/// <summary>
/// One tool invocation handed to an <see cref="AgentToolExecutor"/>. Tools that can
/// produce incremental output call <see cref="OnUpdate"/>; the loop turns each call
/// into an <see cref="AgentEvent.ToolExecutionUpdate"/>.
/// </summary>
public readonly record struct AgentToolInvocation(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    Action<string>? OnUpdate);

/// <summary>
/// Executes one tool call. The loop never lets an exception escape; a thrown error
/// becomes an <c>isError</c> tool result so the model can recover.
/// </summary>
public delegate Task<AiToolResult> AgentToolExecutor(
    AgentToolInvocation invocation,
    CancellationToken cancellationToken);

/// <summary>Context handed to the tool-call hooks.</summary>
public sealed record AgentToolCallContext(
    int Step,
    AiToolCall Call,
    string ArgumentsJson);

/// <summary>Result of <see cref="AgentLoopConfig.BeforeToolCall"/>.</summary>
public sealed record AgentToolDecision(bool Block = false, string? Reason = null, bool Terminate = false)
{
    public static readonly AgentToolDecision Allow = new();
}

/// <summary>The completed turn handed to <see cref="AgentLoopConfig.FinishTurn"/>.</summary>
public sealed record AgentTurnResult(
    int Step,
    ChatMessage Message,
    IReadOnlyList<ChatMessage> ToolResults,
    IReadOnlyList<ChatMessage> History);

/// <summary>Result of <see cref="AgentLoopConfig.FinishTurn"/>.</summary>
public sealed record AgentTurnDecision(bool End = false, bool Continue = false)
{
    public static readonly AgentTurnDecision Default = new();
    public static readonly AgentTurnDecision Stop = new(End: true);
    public static readonly AgentTurnDecision Next = new(Continue: true);
}

/// <summary>
/// Snapshot returned by <see cref="AgentLoopConfig.PrepareNextTurn"/>. This is the
/// hook where compaction, model switching, and context edits land between turns.
/// </summary>
public sealed record AgentNextTurn(
    IReadOnlyList<ChatMessage>? Messages = null,
    string? Model = null,
    IReadOnlyList<ChatMessage>? ContextMessages = null);

/// <summary>
/// Everything the loop needs from the product layer. Modelled on pi's
/// <c>AgentLoopConfig</c>: the loop itself knows nothing about sessions, files,
/// or the terminal, which is what makes it testable and reusable across clients.
/// </summary>
public sealed record AgentLoopConfig
{
    /// <summary>Model identifier recorded on the run summary and assistant messages.</summary>
    public string Model { get; init; } = "";

    /// <summary>Hard ceiling on assistant turns. Default 24.</summary>
    public int MaxSteps { get; init; } = 24;

    /// <summary>Tool dispatcher. Required when tools are supplied.</summary>
    public AgentToolExecutor? ToolExecutor { get; init; }

    /// <summary>
    /// Run independent tool calls from one assistant message concurrently.
    /// Individual tools can opt out through <see cref="SequentialTools"/>.
    /// </summary>
    public bool ParallelToolExecution { get; init; } = true;

    /// <summary>Tool names that must run one at a time (pi's <c>executionMode: "sequential"</c>).</summary>
    public IReadOnlySet<string> SequentialTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gate a tool call before it executes. Return <see cref="AgentToolDecision.Block"/> to refuse.</summary>
    public Func<AgentToolCallContext, CancellationToken, Task<AgentToolDecision>>? BeforeToolCall { get; init; }

    /// <summary>Rewrite or annotate a tool result after execution. Return null to keep it.</summary>
    public Func<AgentToolCallContext, AiToolResult, bool, CancellationToken, Task<AiToolResult?>>? AfterToolCall { get; init; }

    /// <summary>
    /// Flattens stored roles into provider roles at the request boundary. Defaults to
    /// <see cref="AgentMessages.ConvertToLlm"/>, which turns <c>bashExecution</c> and summary
    /// messages into user text and drops context-excluded ones. Override only to change that
    /// policy; the contract is "never throw, return a safe list".
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm { get; init; }

    /// <summary>
    /// Transform the transcript before it is converted for the provider. Use for
    /// pruning, injection, or compaction of the request view only.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext { get; init; }

    /// <summary>
    /// Poll for messages the user typed while the run was active. Delivered after the
    /// current assistant turn finishes its tool calls, before the next provider call.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>>? GetSteeringMessages { get; init; }

    /// <summary>Poll for messages queued for after the agent would otherwise stop.</summary>
    public Func<IReadOnlyList<ChatMessage>>? GetFollowUpMessages { get; init; }

    /// <summary>Decide whether the run ends or continues after a completed turn.</summary>
    public Func<AgentTurnResult, CancellationToken, Task<AgentTurnDecision>>? FinishTurn { get; init; }

    /// <summary>Runs before each turn after the first. Compaction and model switching live here.</summary>
    public Func<AgentTurnResult, CancellationToken, Task<AgentNextTurn?>>? PrepareNextTurn { get; init; }

    /// <summary>Emit a run-finished event when the step budget is exhausted. Default true.</summary>
    public bool FailOnMaxSteps { get; init; }

    /// <summary>
    /// Retry policy for transient provider failures (429, 5xx, overloaded). Default is
    /// disabled so tests and offline providers stay deterministic.
    /// </summary>
    public AgentRetryPolicy Retry { get; init; } = AgentRetryPolicy.Disabled;

    /// <summary>Status codes treated as transient when the policy is enabled.</summary>
    public Func<int, bool>? IsTransientStatus { get; init; }
}

/// <summary>
/// Ambient sink so a tool body registered as a normal Reptar route can stream partial
/// output without changing every tool signature. <see cref="AgentLoop"/> pushes a sink
/// for the duration of each tool call; tools call <see cref="Report"/>.
/// </summary>
public static class AgentToolStreaming
{
    private static readonly AsyncLocal<Action<string>?> Current = new();

    /// <summary>Emit a partial-output chunk for the tool call currently executing, if any.</summary>
    public static void Report(string partialResult)
    {
        if (string.IsNullOrEmpty(partialResult))
            return;
        Current.Value?.Invoke(partialResult);
    }

    /// <summary>True when the current tool call has a listener.</summary>
    public static bool IsObserved => Current.Value is not null;

    internal static void Push(Action<string>? sink) => Current.Value = sink;

    internal static void Pop() => Current.Value = null;
}

/// <summary>
/// Lets a running tool call other tools through the same path the model uses, so
/// <see cref="AgentLoopConfig.BeforeToolCall"/> and <see cref="AgentLoopConfig.AfterToolCall"/>
/// apply transitively. A nested call that would need approval is blocked exactly like a
/// model-issued one — otherwise a "search" tool could quietly do what "write" was denied.
/// </summary>
/// <remarks>
/// Ambient rather than a parameter so existing tool signatures do not change: a tool body calls
/// <see cref="ExecuteAsync"/> with a name and a JSON argument string. Nesting is depth-limited so
/// a tool that calls itself cannot recurse forever.
/// </remarks>
public static class AgentToolContext
{
    /// <summary>Maximum nesting depth. A call beyond this returns a failed result.</summary>
    public const int MaxDepth = 4;

    private static readonly AsyncLocal<NestedFrame?> Current = new();

    private sealed record NestedFrame(
        AgentLoopConfig Config,
        Action<AgentEvent>? Emit,
        int Depth,
        List<NestedCall> Calls);

    /// <summary>True when a tool is running inside an agent loop.</summary>
    public static bool IsAvailable => Current.Value is not null;

    /// <summary>How deep the current nesting is. 0 at the top level.</summary>
    public static int Depth => Current.Value?.Depth ?? 0;

    /// <summary>
    /// Calls another tool. Returns a failed result rather than throwing when no loop is active,
    /// so a tool that is also reachable over HTTP keeps working.
    /// </summary>
    public static async Task<AiToolResult> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var frame = Current.Value;

        if (frame is null)
        {
            return AiToolResult.Failure(
                409, "no_agent_context",
                $"'{toolName}' was called outside an agent run, so it cannot dispatch nested tools.");
        }

        if (frame.Depth >= MaxDepth)
        {
            return AiToolResult.Failure(
                508, "nested_depth_exceeded",
                $"Nested tool calls are limited to {MaxDepth} levels; '{toolName}' would exceed that.");
        }

        var call = new AiToolCall(
            "nested_" + Guid.NewGuid().ToString("N")[..8], toolName, argumentsJson);

        var previous = Current.Value;
        Current.Value = frame with { Depth = frame.Depth + 1 };

        try
        {
            var result = await AgentLoop
                .RunToolCallAsync(call, frame.Config, frame.Emit, cancellationToken)
                .ConfigureAwait(false);

            frame.Calls.Add(new NestedCall(toolName, result.Success, null));
            return result;
        }
        catch (Exception ex)
        {
            frame.Calls.Add(new NestedCall(toolName, false, ex.Message));
            return AiToolResult.Failure(500, "nested_tool_error", ex.Message);
        }
        finally
        {
            Current.Value = previous;
        }
    }

    /// <summary>Nested calls made during the current tool invocation, for tracing.</summary>
    public static IReadOnlyList<NestedCall> Calls => Current.Value?.Calls ?? [];

    /// <summary>
    /// Enters a tool invocation. The depth is inherited from the frame being nested inside, not
    /// reset — otherwise every nested call would look like a top-level one and the depth limit
    /// would never fire.
    /// </summary>
    internal static void Push(AgentLoopConfig config, Action<AgentEvent>? emit)
    {
        var parent = Current.Value;
        Current.Value = new NestedFrame(config, emit, parent?.Depth ?? 0, []);
    }

    internal static void Pop() => Current.Value = null;
}

/// <summary>One tool call a tool made while running.</summary>
public sealed record NestedCall(string ToolName, bool Success, string? Error);

/// <summary>
/// Provider-agnostic streaming agent loop: a faithful port of pi's
/// <c>runLoop</c> (packages/agent/src/agent-loop.ts).
/// </summary>
/// <remarks>
/// Differences from the legacy <see cref="StreamingAgentHarness"/>:
/// <list type="bullet">
/// <item>Emits the pi event vocabulary including <c>message_start/update/end</c> and
/// <c>tool_execution_update</c>, so clients never reconstruct state from deltas.</item>
/// <item>Runs independent tool calls concurrently.</item>
/// <item>Honours <c>stopReason == length</c> by failing the whole tool batch instead of
/// executing arguments that a salvage parser may have silently truncated.</item>
/// <item>Supports steering and follow-up queues, tool-call hooks, and a per-turn
/// <c>prepareNextTurn</c> hook.</item>
/// <item>Emits <c>agent_settled</c> after all automatic work is done.</item>
/// </list>
/// </remarks>
public static class AgentLoop
{
    public static async Task<AgentRunSummary> RunAsync(
        List<ChatMessage> history,
        AgentLoopConfig config,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        IReadOnlyList<AiToolDefinition> tools,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var steps = 0;
        var toolCallsExecuted = 0;
        var lastTurn = default(AgentTurnResult);
        var aborted = false;
        // Sticky: once any turn was cut off by the output token limit, the run summary
        // reports it even if a later turn recovered.
        var truncated = false;
        // Replaces the transcript projection sent to the provider. Compaction sets this so the
        // raw history stays intact for display and export while the model sees a shorter view.
        IReadOnlyList<ChatMessage>? contextOverride = null;

        void Emit(AgentEvent evt) => onEvent?.Invoke(evt);

        Emit(new AgentEvent.AgentStart(config.MaxSteps));

        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    return Settle(Finish(AgentStopReason.Cancelled, lastTurn?.Message.Content ?? "", wasTruncated: false));
                }

                if (steps >= config.MaxSteps)
                {
                    return Settle(Finish(
                        AgentStopReason.MaxSteps,
                        lastTurn is null || lastTurn.Message.Content.Length == 0
                            ? "Maximum agent steps reached before a final answer."
                            : lastTurn.Message.Content,
                        wasTruncated: false));
                }

                // Steering arrives before the provider request, not after the run.
                DeliverQueued(config.GetSteeringMessages, history, steps, Emit);

                IReadOnlyList<ChatMessage> baseContext = contextOverride ?? history;
                var transformed = config.TransformContext is null
                    ? baseContext
                    : await config.TransformContext(baseContext, cancellationToken).ConfigureAwait(false);

                // Flatten stored roles to provider roles exactly once, at the boundary.
                var convert = config.ConvertToLlm ?? DefaultConvertToLlm;
                var context = convert(transformed);

                steps++;
                Emit(new AgentEvent.TurnStart(steps));

                var turn = await StreamTurnAsync(
                    stream, context, tools, steps, config, Emit, cancellationToken).ConfigureAwait(false);

                truncated |= turn.Truncated;

                if (turn.StopReason is AgentTurnStop.Error or AgentTurnStop.Aborted)
                {
                    aborted = turn.StopReason == AgentTurnStop.Aborted;
                    lastTurn = new AgentTurnResult(steps, turn.Message, [], [.. history]);
                    Emit(new AgentEvent.TurnEnd(steps, turn.Message, []));
                    return Settle(Finish(
                        aborted ? AgentStopReason.Cancelled : AgentStopReason.Error,
                        turn.Message.Content,
                        wasTruncated: false));
                }

                history.Add(turn.Message);

                IReadOnlyList<ChatMessage> toolResults = [];
                var terminate = false;

                if (turn.ToolCalls.Count > 0)
                {
                    // A "length" stop means the response was cut off by the token limit, so
                    // every tool call in it may carry truncated arguments. Fail the batch and
                    // let the model re-issue rather than executing a half-formed command.
                    var batch = turn.Truncated
                        ? await FailTruncatedBatchAsync(turn.ToolCalls, steps, Emit, cancellationToken)
                            .ConfigureAwait(false)
                        : await ExecuteToolCallsAsync(
                            turn.ToolCalls, steps, config, Emit, cancellationToken).ConfigureAwait(false);

                    toolResults = batch.Messages;
                    terminate = batch.Terminate;
                    toolCallsExecuted += batch.ExecutedCount;

                    foreach (var result in toolResults)
                    {
                        history.Add(result);
                        Emit(new AgentEvent.MessageStart(steps, result));
                        Emit(new AgentEvent.MessageEnd(steps, result));
                    }
                }

                lastTurn = new AgentTurnResult(steps, turn.Message, toolResults, [.. history]);
                Emit(new AgentEvent.TurnEnd(steps, turn.Message, toolResults));

                var decision = config.FinishTurn is null
                    ? AgentTurnDecision.Default
                    : await config.FinishTurn(lastTurn, cancellationToken).ConfigureAwait(false);

                if (decision.End)
                {
                    return Settle(Finish(
                        AgentStopReason.Completed, turn.Message.Content, truncated));
                }

                var hasMoreWork = terminate is false &&
                    (turn.ToolCalls.Count > 0 || decision.Continue);

                if (hasMoreWork)
                {
                    if (config.PrepareNextTurn is not null)
                    {
                        var next = await config.PrepareNextTurn(lastTurn, cancellationToken)
                            .ConfigureAwait(false);

                        if (next?.ContextMessages is { Count: > 0 } replacement)
                            contextOverride = replacement;

                        if (next?.Messages is { Count: > 0 } prepared)
                        {
                            foreach (var message in prepared)
                            {
                                history.Add(message);
                                Emit(new AgentEvent.MessageStart(steps, message));
                                Emit(new AgentEvent.MessageEnd(steps, message));
                            }
                        }
                    }

                    // Steering queued during tool execution is picked up on the next pass.
                    continue;
                }

                // The agent would stop here. Deliver follow-ups before ending.
                var followUps = config.GetFollowUpMessages?.Invoke() ?? [];
                if (followUps.Count == 0)
                {
                    return Settle(Finish(
                        AgentStopReason.Completed, turn.Message.Content, truncated));
                }

                foreach (var message in followUps)
                {
                    history.Add(message);
                    Emit(new AgentEvent.MessageStart(steps, message));
                    Emit(new AgentEvent.MessageEnd(steps, message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            return Settle(Finish(AgentStopReason.Cancelled, lastTurn?.Message.Content ?? "", wasTruncated: false));
        }
        catch (RequestPortSuspendedException)
        {
            // Request-port suspension is a pause, not a failure; the caller turns it
            // into a resumable checkpoint.
            throw;
        }
        catch (Exception ex)
        {
            var message = ex.InnerException is { } inner ? $"{ex.Message}: {inner.Message}" : ex.Message;
            Emit(new AgentEvent.AgentError(message));
            return Settle(Finish(AgentStopReason.Error, message, wasTruncated: false));
        }

        AgentRunSummary Settle(AgentRunSummary summary)
        {
            Emit(new AgentEvent.AgentEnd(summary));
            Emit(new AgentEvent.AgentSettled(aborted || summary.StopReason == AgentStopReason.Cancelled));
            return summary;
        }

        AgentRunSummary Finish(AgentStopReason reason, string content, bool wasTruncated)
        {
            stopwatch.Stop();
            return new AgentRunSummary(
                reason,
                content,
                steps,
                toolCallsExecuted,
                config.Model,
                stopwatch.Elapsed.TotalMilliseconds,
                [.. history],
                wasTruncated);
        }
    }

    private static readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>> DefaultConvertToLlm =
        messages => AgentMessages.ConvertToLlm(messages);

    /// <summary>
    /// Runs one tool call through the full path — argument validation, the before hook, execution,
    /// the after hook — without emitting events or appending to the transcript.
    /// </summary>
    /// <remarks>
    /// Exported for two callers: tools that need to call other tools (see
    /// <see cref="AgentToolContext"/>), and hosts that want to reuse the approval policy outside a
    /// model turn. Never throws for a tool failure; a thrown error becomes a failed result.
    /// </remarks>
    public static async Task<AiToolResult> RunToolCallAsync(
        AiToolCall call,
        AgentLoopConfig config,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var emit = onEvent ?? (_ => { });
        var outcome = await RunOneAsync(call, 0, config, emit, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    private static void DeliverQueued(
        Func<IReadOnlyList<ChatMessage>>? source,
        List<ChatMessage> history,
        int step,
        Action<AgentEvent> emit)
    {
        if (source is null)
            return;

        var queued = source();
        if (queued.Count == 0)
            return;

        foreach (var message in queued)
        {
            history.Add(message);
            emit(new AgentEvent.MessageStart(step, message));
            emit(new AgentEvent.MessageEnd(step, message));
        }
    }

    private enum AgentTurnStop
    {
        Stop,
        Length,
        ToolUse,
        Error,
        Aborted
    }

    private sealed record StreamedTurn(
        ChatMessage Message,
        IReadOnlyList<AiToolCall> ToolCalls,
        AgentTurnStop StopReason,
        bool Truncated);

    /// <summary>
    /// Streams one assistant response, retrying transient provider failures under
    /// <see cref="AgentLoopConfig.Retry"/>. Each attempt re-emits <c>message_start</c> so a
    /// client knows to discard the partial it buffered for the previous attempt.
    /// </summary>
    private static async Task<StreamedTurn> StreamTurnAsync(
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        IReadOnlyList<ChatMessage> context,
        IReadOnlyList<AiToolDefinition> tools,
        int step,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var retry = config.Retry;
        var isTransient = config.IsTransientStatus ?? AgentRetryPolicy.IsTransient;
        var maxAttempts = Math.Max(1, retry.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
                emit(new AgentEvent.RetryAttempt(attempt));

            // The client sees a message exist before it sees deltas, exactly like pi.
            emit(new AgentEvent.MessageStart(step, new ChatMessage("assistant", "")));

            var attemptResult = await StreamAttemptAsync(
                stream, context, tools, step, emit, cancellationToken).ConfigureAwait(false);

            // Only transient statuses are retried, and only while the budget lasts.
            if (attemptResult.TransientStatus is not { } status
                || !isTransient(status)
                || attempt >= maxAttempts)
            {
                if (attempt > 1)
                    emit(new AgentEvent.RetryFinished(attemptResult.Turn.StopReason != AgentTurnStop.Error));
                return attemptResult.Turn;
            }

            var delay = retry.DelayFor(attempt);
            emit(new AgentEvent.RetryScheduled(attempt, delay.TotalMilliseconds, attemptResult.Failure ?? $"HTTP {status}"));

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return attemptResult.Turn;
            }
        }
    }

    private sealed record StreamAttempt(StreamedTurn Turn, int? TransientStatus, string? Failure);

    private static async Task<StreamAttempt> StreamAttemptAsync(
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        IReadOnlyList<ChatMessage> context,
        IReadOnlyList<AiToolDefinition> tools,
        int step,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var toolArgs = new SortedDictionary<int, ToolCallBuilder>();
        var contentIndex = 0;
        string? finishReason = null;
        string? failure = null;
        int? transientStatus = null;
        var sawText = false;
        var sawThinking = false;
        AgentUsage? usage = null;

        try
        {
            await foreach (var evt in stream(context, tools, cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LlmStreamEvent.TextDelta delta:
                        if (!sawText)
                        {
                            contentIndex = NextIndex(toolArgs, ref sawThinking);
                            sawText = true;
                        }
                        content.Append(delta.Text);
                        emit(new AgentEvent.MessageUpdate(step, AgentContentKind.Text, contentIndex, delta.Text));
                        break;

                    case LlmStreamEvent.ReasoningDelta delta:
                        if (!sawThinking)
                        {
                            contentIndex = NextIndex(toolArgs, ref sawText);
                            sawThinking = true;
                        }
                        emit(new AgentEvent.MessageUpdate(step, AgentContentKind.Thinking, contentIndex, delta.Text));
                        break;

                    case LlmStreamEvent.ToolCallDelta delta:
                        var builder = GetOrAdd(toolArgs, delta.Index);
                        builder.Id ??= delta.Id;
                        builder.Name ??= delta.Name;
                        if (delta.ArgumentsJson.Length > 0)
                        {
                            builder.Arguments.Append(delta.ArgumentsJson);
                            emit(new AgentEvent.MessageUpdate(
                                step, AgentContentKind.ToolCall, delta.Index, delta.ArgumentsJson));
                        }
                        break;

                    case LlmStreamEvent.ToolCall call:
                        // OpenAI-compatible clients emit argument deltas and then a complete
                        // call. Merge by call id instead of appending a duplicate, which would
                        // execute the same tool twice.
                        var existing = toolArgs.FirstOrDefault(kv => kv.Value.Id == call.Id);
                        if (existing.Value is not null)
                        {
                            existing.Value.Name = call.Name;
                            existing.Value.Arguments.Clear();
                            existing.Value.Arguments.Append(call.ArgumentsJson);
                        }
                        else
                        {
                            var index = toolArgs.Count == 0 ? 0 : toolArgs.Keys.Max() + 1;
                            toolArgs[index] = new ToolCallBuilder
                            {
                                Id = call.Id,
                                Name = call.Name,
                                Arguments = new StringBuilder(call.ArgumentsJson)
                            };
                        }
                        break;

                    case LlmStreamEvent.Usage reported:
                        usage = new AgentUsage(
                            reported.InputTokens,
                            reported.OutputTokens,
                            reported.CacheReadTokens,
                            reported.CacheWriteTokens);
                        emit(new AgentEvent.UsageReported(
                            step,
                            reported.InputTokens,
                            reported.OutputTokens,
                            reported.CacheReadTokens,
                            reported.CacheWriteTokens));
                        break;

                    case LlmStreamEvent.Completed done:
                        finishReason = done.FinishReason;
                        break;

                    case LlmStreamEvent.Failed failed:
                        failure = failed.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var aborted = new ChatMessage("assistant", content.ToString(), Usage: usage);
            emit(new AgentEvent.MessageEnd(step, aborted));
            return new StreamAttempt(
                new StreamedTurn(aborted, [], AgentTurnStop.Aborted, false), null, null);
        }
        catch (LlmException ex)
        {
            failure = ex.Message;
            transientStatus = ex.StatusCode;
        }
        catch (Exception ex)
        {
            failure = ex.InnerException is { } inner ? $"{ex.Message}: {inner.Message}" : ex.Message;
        }

        var calls = toolArgs.Values
            .Where(entry => entry.Name is not null)
            .Select(entry => new AiToolCall(
                entry.Id ?? "call_" + Guid.NewGuid().ToString("N")[..8],
                entry.Name!,
                LlmHttp.NormalizeArguments(entry.Arguments.ToString())))
            .ToList();

        var stop = failure is not null
            ? AgentTurnStop.Error
            : MapStop(finishReason, calls.Count > 0);

        var text = failure is not null && content.Length == 0 ? failure : content.ToString();
        var message = new ChatMessage(
            "assistant",
            text,
            ToolCalls: calls.Count > 0 ? calls : null,
            Usage: usage);

        emit(new AgentEvent.MessageEnd(step, message));

        var turn = new StreamedTurn(message, calls, stop, stop == AgentTurnStop.Length);

        return new StreamAttempt(
            turn,
            stop == AgentTurnStop.Error ? transientStatus : null,
            failure);
    }

    private static int NextIndex(SortedDictionary<int, ToolCallBuilder> calls, ref bool otherUsed)
    {
        otherUsed = true;
        return calls.Count == 0 ? 0 : calls.Keys.Max() + 1;
    }

    private static ToolCallBuilder GetOrAdd(SortedDictionary<int, ToolCallBuilder> calls, int index)
    {
        if (!calls.TryGetValue(index, out var builder))
        {
            builder = new ToolCallBuilder();
            calls[index] = builder;
        }
        return builder;
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; init; } = new();
    }

    private static AgentTurnStop MapStop(string? finishReason, bool hasToolCalls) =>
        finishReason?.ToLowerInvariant() switch
        {
            "length" or "max_tokens" or "max_output_tokens" => AgentTurnStop.Length,
            "error" or "failed" => AgentTurnStop.Error,
            "tool_use" or "tool_calls" or "function_call" => AgentTurnStop.ToolUse,
            "aborted" or "cancelled" or "canceled" => AgentTurnStop.Aborted,
            _ => hasToolCalls ? AgentTurnStop.ToolUse : AgentTurnStop.Stop
        };

    private sealed record ExecutedBatch(
        IReadOnlyList<ChatMessage> Messages,
        bool Terminate,
        int ExecutedCount);

    private static async Task<ExecutedBatch> FailTruncatedBatchAsync(
        IReadOnlyList<AiToolCall> calls,
        int step,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>(calls.Count);

        foreach (var call in calls)
        {
            emit(new AgentEvent.ToolExecutionStart(step, call.Id, call.Name, call.Arguments));

            var result = AiToolResult.Failure(
                400,
                "truncated_tool_call",
                $"Tool call '{call.Name}' was not executed: the response hit the output token limit, "
                + "so its arguments may be truncated. Re-issue the tool call with complete arguments.");

            emit(new AgentEvent.ToolExecutionEnd(
                step, call.Id, call.Name, result.ToLlmResponse(), true, 0));

            messages.Add(new ChatMessage(
                Role: "tool",
                Content: result.ToLlmResponse(),
                Name: call.Name,
                ToolCallId: call.Id));
        }

        return new ExecutedBatch(messages, Terminate: false, ExecutedCount: 0);
    }

    private static async Task<ExecutedBatch> ExecuteToolCallsAsync(
        IReadOnlyList<AiToolCall> calls,
        int step,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var sequential = config.ParallelToolExecution is false ||
            calls.Any(call => config.SequentialTools.Contains(call.Name));

        return sequential
            ? await ExecuteSequentialAsync(calls, step, config, emit, cancellationToken).ConfigureAwait(false)
            : await ExecuteParallelAsync(calls, step, config, emit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExecutedBatch> ExecuteSequentialAsync(
        IReadOnlyList<AiToolCall> calls,
        int step,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<AgentToolOutcome>(calls.Count);

        foreach (var call in calls)
        {
            emit(new AgentEvent.ToolExecutionStart(step, call.Id, call.Name, call.Arguments));
            var outcome = await RunOneAsync(call, step, config, emit, cancellationToken).ConfigureAwait(false);
            EmitEnd(step, outcome, emit);
            outcomes.Add(outcome);

            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return Materialize(outcomes, calls.Count);
    }

    private static async Task<ExecutedBatch> ExecuteParallelAsync(
        IReadOnlyList<AiToolCall> calls,
        int step,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        foreach (var call in calls)
            emit(new AgentEvent.ToolExecutionStart(step, call.Id, call.Name, call.Arguments));

        var running = calls
            .Select(call => RunOneAsync(call, step, config, emit, cancellationToken))
            .ToArray();

        var outcomes = await Task.WhenAll(running).ConfigureAwait(false);

        // Tool ends are emitted in assistant-message order, not completion order, so the
        // transcript and the wire stream agree.
        foreach (var outcome in outcomes)
            EmitEnd(step, outcome, emit);

        return Materialize(outcomes, calls.Count);
    }

    private static ExecutedBatch Materialize(IReadOnlyList<AgentToolOutcome> outcomes, int totalCalls)
    {
        var messages = new List<ChatMessage>(outcomes.Count);
        var executed = 0;

        foreach (var outcome in outcomes)
        {
            if (outcome.Executed)
                executed++;

            messages.Add(new ChatMessage(
                Role: "tool",
                Content: outcome.Result.ToLlmResponse(),
                Name: outcome.Call.Name,
                ToolCallId: outcome.Call.Id));
        }

        var terminate = outcomes.Count > 0 && outcomes.Count == totalCalls &&
            outcomes.All(outcome => outcome.Terminate);

        return new ExecutedBatch(messages, terminate, executed);
    }

    private static void EmitEnd(int step, AgentToolOutcome outcome, Action<AgentEvent> emit)
    {
        emit(new AgentEvent.ToolExecutionEnd(
            step,
            outcome.Call.Id,
            outcome.Call.Name,
            outcome.Result.ToLlmResponse(),
            !outcome.Result.Success,
            outcome.DurationMs));
    }

    private sealed record AgentToolOutcome(
        AiToolCall Call,
        AiToolResult Result,
        double DurationMs,
        bool Terminate,
        bool Executed);

    private static async Task<AgentToolOutcome> RunOneAsync(
        AiToolCall call,
        int step,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
    {
        var context = new AgentToolCallContext(step, call, call.Arguments);

        try
        {
            if (config.BeforeToolCall is not null)
            {
                var decision = await config.BeforeToolCall(context, cancellationToken).ConfigureAwait(false);
                if (decision.Block)
                {
                    return new AgentToolOutcome(
                        call,
                        AiToolResult.Failure(403, "tool_blocked", decision.Reason ?? "Tool execution was blocked"),
                        0,
                        decision.Terminate,
                        Executed: false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new AgentToolOutcome(
                    call, AiToolResult.Failure(499, "aborted", "Operation aborted"), 0, false, Executed: false);
            }

            if (config.ToolExecutor is null)
            {
                return new AgentToolOutcome(
                    call,
                    AiToolResult.Failure(501, "no_executor", "No tool executor is configured for this run."),
                    0,
                    false,
                    Executed: false);
            }

            var invocation = new AgentToolInvocation(
                call.Id,
                call.Name,
                call.Arguments,
                partial => emit(new AgentEvent.ToolExecutionUpdate(step, call.Id, call.Name, partial)));

            var watch = Stopwatch.StartNew();
            AiToolResult result;

            AgentToolStreaming.Push(invocation.OnUpdate);
            AgentToolContext.Push(config, emit);
            try
            {
                result = await config.ToolExecutor(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = AiToolResult.Failure(499, "aborted", "Operation aborted");
            }
            catch (RequestPortSuspendedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = AiToolResult.Failure(500, "tool_execution_error", ex.Message);
            }
            finally
            {
                AgentToolContext.Pop();
                AgentToolStreaming.Pop();
            }

            watch.Stop();

            if (config.AfterToolCall is not null)
            {
                var rewritten = await config.AfterToolCall(context, result, !result.Success, cancellationToken)
                    .ConfigureAwait(false);
                if (rewritten is not null)
                    result = rewritten;
            }

            return new AgentToolOutcome(
                call,
                result,
                watch.Elapsed.TotalMilliseconds,
                Terminate: false,
                Executed: true);
        }
        catch (OperationCanceledException)
        {
            return new AgentToolOutcome(
                call, AiToolResult.Failure(499, "aborted", "Operation aborted"), 0, false, Executed: false);
        }
        catch (RequestPortSuspendedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolOutcome(
                call,
                AiToolResult.Failure(500, "tool_execution_error", ex.Message),
                0,
                false,
                Executed: false);
        }
    }
}
#endif
