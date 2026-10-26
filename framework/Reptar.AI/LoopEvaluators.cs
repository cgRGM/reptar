#if !NETWASM
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// G5 — The verdict a <see cref="LoopEvaluator"/> returns after each agent
/// run: stop, or continue with feedback for the next iteration.
/// </summary>
public abstract record LoopEvaluation
{
    public sealed record Stop(string? Reason = null) : LoopEvaluation;
    public sealed record Continue(string Feedback) : LoopEvaluation;

    public static LoopEvaluation StopLoop(string? reason = null) => new Stop(reason);
    public static LoopEvaluation ContinueWith(string feedback) => new Continue(feedback);
}

/// <summary>
/// Evaluates an agent run inside an autonomous loop: after each
/// <see cref="StreamingAgentHarness"/> run, decide whether the loop should
/// stop or re-invoke the agent with feedback. Implementations should be cheap
/// and deterministic; expensive judges (LLM-as-judge) are opt-in.
/// </summary>
public interface LoopEvaluator
{
    ValueTask<LoopEvaluation> EvaluateAsync(AgentRunSummary lastRun, CancellationToken cancellationToken = default);
}

/// <summary>
/// Built-in evaluators, all zero-LLM-cost. Mirrors the Microsoft Agent
/// Framework's LoopEvaluator set (CompletionMarker, TodoCompletion,
/// Delegate), with the same safety rules:
///
///  - <see cref="LoopRunner"/> enforces a hard <c>MaxIterations</c> cap.
///  - <see cref="CompletionMarkerLoopEvaluator"/> stops when a fixed marker
///    string appears in the last response and otherwise feeds back a
///    self-contained instruction that works even with a fresh context.
///  - <see cref="TodoCompletionLoopEvaluator"/> stops when the todo module has
///    no remaining items (and skips evaluation entirely while a plan module is
///    still in 'plan' mode).
/// </summary>
public static class LoopEvaluators
{
    /// <summary>
    /// Stops the loop once the completion marker appears in the last
    /// response; otherwise continues with feedback that echoes the marker and
    /// (optionally) the last response, so it also works when the next
    /// iteration starts from a fresh context.
    /// </summary>
    public static LoopEvaluator CompletionMarker(
        string completionMarker,
        string? feedbackTemplate = null)
    {
        return new DelegateLoopEvaluator((run, _) =>
        {
            if (run.FinalContent.Contains(completionMarker, StringComparison.Ordinal))
            {
                return new LoopEvaluation.Stop("completion-marker");
            }

            var feedback = (feedbackTemplate ??
                    "Continue working on the request. When you have fully completed the task, end your response with the marker '{completion_marker}' to indicate completion.")
                .Replace("{completion_marker}", completionMarker, StringComparison.Ordinal)
                .Replace("{last_response}", run.FinalContent, StringComparison.Ordinal);

            return new LoopEvaluation.Continue(feedback);
        });
    }

    /// <summary>
    /// Stops when the todo module for the given session has no incomplete
    /// items. If a plan/execute module is provided and the session is still
    /// in plan mode, the loop always continues (the agent is mid-planning).
    /// </summary>
    public static LoopEvaluator TodoCompletion(
        string sessionId,
        TodoModule todos,
        PlanExecuteModule? modes = null)
    {
        return new DelegateLoopEvaluator((run, _) =>
        {
            if (modes is not null && modes.CurrentMode(sessionId) == PlanExecuteModule.PlanMode)
            {
                return new LoopEvaluation.Continue(
                    "Still in plan mode. Ask the user for approval to switch to execute mode (mode_set 'execute').");
            }

            var remaining = todos.Items.Where(item => !item.IsComplete).ToList();
            if (remaining.Count == 0)
            {
                return new LoopEvaluation.Stop("all-todos-complete");
            }

            var pendingList = string.Join("\n", remaining.Select(item => $"- {item.Title}"));
            return new LoopEvaluation.Continue(
                $"Todos still pending — keep working and mark them complete as you finish:\n{pendingList}");
        });
    }

    /// <summary>
    /// An evaluator from a plain predicate — for host-defined stop conditions.
    /// </summary>
    public static LoopEvaluator Delegate(Func<AgentRunSummary, LoopEvaluation> evaluate) =>
        new DelegateLoopEvaluator((run, _) => evaluate(run));
}

internal sealed class DelegateLoopEvaluator(Func<AgentRunSummary, CancellationToken, LoopEvaluation> evaluate)
    : LoopEvaluator
{
    public ValueTask<LoopEvaluation> EvaluateAsync(
        AgentRunSummary lastRun, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(evaluate(lastRun, cancellationToken));
}

/// <summary>
/// The result of a <see cref="LoopRunner"/> execution.
/// </summary>
public sealed record LoopRunResult(
    int Iterations,
    AgentRunSummary LastRun,
    string StopReason,
    List<ChatMessage> History);

/// <summary>
/// G5 — Runs an agent to completion, then re-invokes it with feedback until
/// the evaluator says stop or the hard <c>MaxIterations</c> cap hits. The cap
/// is non-negotiable: an autonomous loop never runs unbounded.
/// </summary>
public static class LoopRunner
{
    public const int DefaultMaxIterations = 10;

    public static async Task<LoopRunResult> RunAsync(
        List<ChatMessage> history,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<string, string, Task<AiToolResult>> toolExecutor,
        IReadOnlyList<AiToolDefinition> tools,
        LoopEvaluator evaluator,
        int maxStepsPerRun = 10,
        string model = "agent",
        int maxIterations = DefaultMaxIterations,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveMax = Math.Max(1, maxIterations);
        var lastRun = (AgentRunSummary?)null;

        for (var iteration = 1; iteration <= effectiveMax; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastRun = await StreamingAgentHarness.RunAsync(
                history, stream, toolExecutor, tools,
                maxStepsPerRun, model, onEvent, cancellationToken).ConfigureAwait(false);

            // The agent failed or was cancelled inside the harness — don't
            // loop a dead run.
            if (lastRun.StopReason is AgentStopReason.Error or AgentStopReason.Cancelled)
            {
                return new LoopRunResult(iteration, lastRun, lastRun.StopReason.ToString().ToLowerInvariant(), history);
            }

            var verdict = await evaluator.EvaluateAsync(lastRun, cancellationToken).ConfigureAwait(false);

            if (verdict is LoopEvaluation.Stop stop)
            {
                return new LoopRunResult(
                    iteration, lastRun, stop.Reason ?? "evaluator-stop", history);
            }

            if (iteration == effectiveMax)
            {
                break;
            }

            if (verdict is LoopEvaluation.Continue withFeedback)
            {
                history.Add(new ChatMessage("user", withFeedback.Feedback));
            }
        }

        return new LoopRunResult(
            effectiveMax, lastRun!, "max-iterations", history);
    }
}
#endif
