#if !NETWASM
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

public enum AgentStopReason
{
    Completed,
    MaxSteps,
    Cancelled,
    Error
}

public sealed record AgentRunSummary(
    AgentStopReason StopReason,
    string FinalContent,
    int StepsTaken,
    int ToolCallsExecuted,
    string Model,
    double ElapsedMs,
    IReadOnlyList<ChatMessage> History);

/// <summary>
/// Live progress events emitted while <see cref="StreamingAgentHarness.RunAsync"/> executes.
/// Mirrors the observable event surfaces of Pi (message/turn deltas) and Flue
/// (tool lifecycle) so both terminal and web frontends can share one loop.
/// </summary>
public abstract record AgentEvent
{
    public sealed record TurnStarted(int Step) : AgentEvent;
    public sealed record TextDelta(string Text, int Step) : AgentEvent;
    public sealed record ReasoningDelta(string Text, int Step) : AgentEvent;
    public sealed record ToolCallStarted(string Id, string Name, string ArgumentsJson, int Step) : AgentEvent;
    public sealed record ToolCallFinished(string Id, string Name, bool Success, string Result, int Step, double ElapsedMs) : AgentEvent;
    public sealed record TurnFinished(int Step, string? Content) : AgentEvent;
    public sealed record RunFinished(AgentStopReason StopReason, string FinalContent, int Steps, int ToolCalls) : AgentEvent;
    public sealed record RunFailed(string Message) : AgentEvent;
}

/// <summary>
/// A single streaming turn from the model, mirroring <see cref="AgentStep"/>
/// for the non-streaming harness but carrying cumulative deltas.
/// </summary>
public sealed record StreamedTurn(
    string Content,
    IReadOnlyList<AiToolCall> ToolCalls,
    string? FinishReason)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// Streaming, provider-agnostic ReAct loop. Tool calls are dispatched in-process
/// through <see cref="AiToolExtensions.ExecuteAiToolAsync"/> with Rule validation,
/// and tool results are fed back to the model until it answers or maxSteps hits.
/// </summary>
public static class StreamingAgentHarness
{
    public static async Task<AgentRunSummary> RunAsync(
        ReptarApp app,
        ILlmClient client,
        List<ChatMessage> history,
        IReadOnlyList<AiToolDefinition>? tools = null,
        string? systemPrompt = null,
        int maxSteps = 24,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        if (systemPrompt is { Length: > 0 } && history.All(m => m.Role != "system"))
            history.Insert(0, new ChatMessage("system", systemPrompt));

        return await RunAsync(
            history,
            client.StreamAsync,
            (name, args) => app.ExecuteAiToolAsync(name, args),
            tools ?? app.GetAiTools(onlyExplicit: true),
            maxSteps,
            client.Config.Model,
            onEvent,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AgentRunSummary> RunAsync(
        List<ChatMessage> history,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition> tools,
        int maxSteps,
        string model,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var steps = 0;
        var toolCallsExecuted = 0;

        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onEvent?.Invoke(new AgentEvent.RunFinished(AgentStopReason.Cancelled, "", steps, toolCallsExecuted));
                    return new AgentRunSummary(
                        AgentStopReason.Cancelled, "", steps, toolCallsExecuted, model,
                        stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
                }

                steps++;
                onEvent?.Invoke(new AgentEvent.TurnStarted(steps));

                var turn = await StreamTurnAsync(
                    stream, history, tools, onEvent, steps, cancellationToken).ConfigureAwait(false);

                if (turn.ToolCalls.Count > 0)
                {
                    history.Add(new ChatMessage(
                        Role: "assistant",
                        Content: turn.Content,
                        ToolCalls: turn.ToolCalls));

                    foreach (var call in turn.ToolCalls)
                    {
                        toolCallsExecuted++;
                        onEvent?.Invoke(new AgentEvent.ToolCallStarted(call.Id, call.Name, call.Arguments, steps));

                        var callWatch = System.Diagnostics.Stopwatch.StartNew();
                        var result = await ExecuteToolSafelyAsync(toolExecutor, call).ConfigureAwait(false);
                        callWatch.Stop();

                        onEvent?.Invoke(new AgentEvent.ToolCallFinished(
                            call.Id, call.Name, result.Success, result.ToLlmResponse(), steps,
                            callWatch.Elapsed.TotalMilliseconds));

                        history.Add(new ChatMessage(
                            Role: "tool",
                            Content: result.ToLlmResponse(),
                            Name: call.Name,
                            ToolCallId: call.Id));
                    }

                    onEvent?.Invoke(new AgentEvent.TurnFinished(steps, turn.Content));

                    if (steps >= maxSteps)
                    {
                        onEvent?.Invoke(new AgentEvent.RunFinished(AgentStopReason.MaxSteps, turn.Content, steps, toolCallsExecuted));
                        return new AgentRunSummary(
                            AgentStopReason.MaxSteps,
                            turn.Content.Length > 0
                                ? turn.Content
                                : "Maximum agent steps reached before a final answer.",
                            steps, toolCallsExecuted, model,
                            stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
                    }

                    continue;
                }

                if (turn.Content.Length > 0 || turn.FinishReason is not null)
                {
                    history.Add(new ChatMessage("assistant", turn.Content));
                    onEvent?.Invoke(new AgentEvent.TurnFinished(steps, turn.Content));
                    onEvent?.Invoke(new AgentEvent.RunFinished(AgentStopReason.Completed, turn.Content, steps, toolCallsExecuted));
                    return new AgentRunSummary(
                        AgentStopReason.Completed, turn.Content, steps, toolCallsExecuted, model,
                        stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
                }

                // Model produced nothing at all; treat as completion to avoid infinite loops.
                history.Add(new ChatMessage("assistant", turn.Content));
                onEvent?.Invoke(new AgentEvent.RunFinished(AgentStopReason.Completed, turn.Content, steps, toolCallsExecuted));
                return new AgentRunSummary(
                    AgentStopReason.Completed, turn.Content, steps, toolCallsExecuted, model,
                    stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
            }
        }
        catch (OperationCanceledException)
        {
            onEvent?.Invoke(new AgentEvent.RunFinished(AgentStopReason.Cancelled, "", steps, toolCallsExecuted));
            return new AgentRunSummary(
                AgentStopReason.Cancelled, "", steps, toolCallsExecuted, model,
                stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
        }
        catch (Exception ex)
        {
            var message = ex.InnerException is { } inner
                ? $"{ex.Message}: {inner.Message}"
                : ex.Message;
            onEvent?.Invoke(new AgentEvent.RunFailed(message));
            return new AgentRunSummary(
                AgentStopReason.Error, message, steps, toolCallsExecuted, model,
                stopwatch.Elapsed.TotalMilliseconds, Snapshot(history));
        }
    }

    private static async Task<StreamedTurn> StreamTurnAsync(
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Action<AgentEvent>? onEvent,
        int step,
        CancellationToken cancellationToken)
    {
        var content = new System.Text.StringBuilder();
        var toolArgs = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finishReason = null;

        await foreach (var evt in stream(history, tools, cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmStreamEvent.TextDelta delta:
                    content.Append(delta.Text);
                    onEvent?.Invoke(new AgentEvent.TextDelta(delta.Text, step));
                    break;

                case LlmStreamEvent.ReasoningDelta delta:
                    onEvent?.Invoke(new AgentEvent.ReasoningDelta(delta.Text, step));
                    break;

                case LlmStreamEvent.ToolCallDelta delta:
                    if (!toolArgs.TryGetValue(delta.Index, out var builder))
                        toolArgs[delta.Index] = (delta.Id, delta.Name, new StringBuilder(delta.ArgumentsJson));
                    else
                        builder.Args.Append(delta.ArgumentsJson);
                    break;

                case LlmStreamEvent.ToolCall call:
                    // OpenAI-compatible clients emit argument deltas first and then
                    // a complete call. Merge by call id instead of appending a
                    // duplicate (which would execute the tool twice).
                    var existingIndex = -1;
                    foreach (var (key, entry) in toolArgs)
                    {
                        if (entry.Id is not null && entry.Id == call.Id)
                        {
                            existingIndex = key;
                            break;
                        }
                    }

                    if (existingIndex >= 0)
                        toolArgs[existingIndex] = (call.Id, call.Name, new StringBuilder(call.ArgumentsJson));
                    else
                        toolArgs[toolArgs.Count == 0 ? 0 : toolArgs.Keys.Max() + 1] =
                            (call.Id, call.Name, new StringBuilder(call.ArgumentsJson));
                    break;

                case LlmStreamEvent.Completed done:
                    finishReason = done.FinishReason;
                    break;
            }
        }

        var calls = toolArgs.Values
            .Where(entry => entry.Name is not null)
            .Select(entry => new AiToolCall(
                entry.Id ?? "call_" + Guid.NewGuid().ToString("N")[..8],
                entry.Name!,
                LlmHttp.NormalizeArguments(entry.Args.ToString())))
            .ToList();

        return new StreamedTurn(content.ToString(), calls, finishReason);
    }

    private static async Task<AiToolResult> ExecuteToolSafelyAsync(
        Func<string, string, Task<AiToolResult>> executor,
        AiToolCall call)
    {
        try
        {
            return await executor(call.Name, call.Arguments).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AiToolResult.Failure(500, "tool_execution_error", ex.Message);
        }
    }

    private static IReadOnlyList<ChatMessage> Snapshot(IReadOnlyList<ChatMessage> history) => history;
}
#endif
