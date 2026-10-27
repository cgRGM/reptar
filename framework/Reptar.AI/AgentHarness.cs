#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// Convenience entry point for running an agent against a <see cref="ReptarApp"/> and an
/// <see cref="ILlmClient"/>. This is a thin binding layer over <see cref="AgentLoop"/>:
/// it resolves the app's registered AI tools, wires the in-process tool dispatcher, and
/// applies the system prompt. All loop semantics (streaming, tool lifecycle, steering,
/// truncation guard, hooks) live in <see cref="AgentLoop"/>.
/// </summary>
public static class StreamingAgentHarness
{
    /// <summary>
    /// Runs the agent loop until the model answers, the step budget is exhausted, or the
    /// token is cancelled.
    /// </summary>
    /// <param name="app">App whose registered AI tools are exposed to the model.</param>
    /// <param name="client">Provider client. Its <c>StreamAsync</c> is the transport.</param>
    /// <param name="history">
    /// Conversation transcript. Mutated in place: assistant messages, tool results, and any
    /// steering/follow-up messages are appended so callers can persist from the same list.
    /// </param>
    /// <param name="tools">Tool definitions. Defaults to the app's explicit AI tools.</param>
    /// <param name="systemPrompt">Prepended as a system message when the transcript has none.</param>
    /// <param name="maxSteps">Hard ceiling on assistant turns.</param>
    /// <param name="onEvent">Streaming event sink.</param>
    /// <param name="configure">Optional further loop configuration (hooks, queues, parallelism).</param>
    /// <param name="cancellationToken">Aborts the run between turns and between tool calls.</param>
    public static async Task<AgentRunSummary> RunAsync(
        ReptarApp app,
        ILlmClient client,
        List<ChatMessage> history,
        IReadOnlyList<AiToolDefinition>? tools = null,
        string? systemPrompt = null,
        int maxSteps = 24,
        Action<AgentEvent>? onEvent = null,
        Func<AgentLoopConfig, AgentLoopConfig>? configure = null,
        CancellationToken cancellationToken = default)
    {
        if (systemPrompt is { Length: > 0 } && history.All(m => m.Role != "system"))
            history.Insert(0, new ChatMessage("system", systemPrompt));

        var config = new AgentLoopConfig
        {
            Model = client.Config.Model,
            MaxSteps = maxSteps,
            ToolExecutor = (invocation, _) => app.ExecuteAiToolAsync(invocation.ToolName, invocation.ArgumentsJson),
            // run_command must not overlap other tool calls that touch the same workspace.
            SequentialTools = new HashSet<string>(StringComparer.Ordinal) { "run_command" }
        };

        if (configure is not null)
            config = configure(config);

        return await AgentLoop.RunAsync(
            history,
            config,
            client.StreamAsync,
            tools ?? app.GetAiTools(onlyExplicit: true),
            onEvent,
            cancellationToken).ConfigureAwait(false);
    }

}

/// <summary>
/// Adapters between the loop's <see cref="AgentToolExecutor"/> shape and the older
/// name/args delegate used by <see cref="LoopRunner"/>, <see cref="RequestPortAgent"/>, and
/// background agents. Prefer implementing <see cref="AgentToolExecutor"/> directly in new
/// code: it carries the tool-call id and the partial-output sink.
/// </summary>
public static class AgentToolExecutors
{
    public static AgentToolExecutor FromNameArgs(Func<string, string, Task<AiToolResult>> executor) =>
        (invocation, _) => executor(invocation.ToolName, invocation.ArgumentsJson);

    public static AgentToolExecutor FromNameArgs(
        Func<string, string, CancellationToken, Task<AiToolResult>> executor) =>
        (invocation, cancellationToken) =>
            executor(invocation.ToolName, invocation.ArgumentsJson, cancellationToken);
}
#endif
