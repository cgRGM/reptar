#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// How queued steering messages are delivered. Mirrors pi's <c>set_steering_mode</c>.
/// </summary>
public enum AgentQueueMode
{
    /// <summary>Deliver every queued message after the current turn's tool calls.</summary>
    All,

    /// <summary>Deliver one queued message per completed turn.</summary>
    OneAtATime
}

/// <summary>
/// Owns the mutable state of one conversational agent: the transcript, the steering and
/// follow-up queues, and the in-flight run. Modelled on pi's <c>AgentSession</c>, minus the
/// product policy (auth guidance, crash logs, telemetry) that Reptar does not need here.
/// </summary>
/// <remarks>
/// The split matters: <see cref="AgentLoop"/> is a pure scheduler that emits events, and
/// <see cref="AgentSession"/> is the thing a client talks to. That is what lets the terminal,
/// the web SPA, and a JSONL RPC mode share one implementation.
/// </remarks>
public sealed class AgentSession
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _steering = [];
    private readonly List<ChatMessage> _followUp = [];
    private CancellationTokenSource? _runCts;
    private Task<AgentRunSummary>? _runTask;

    public AgentSession(string model = "")
    {
        Model = model;
    }

    /// <summary>Active model identifier, recorded on runs and assistant messages.</summary>
    public string Model { get; set; }

    /// <summary>The live transcript, including tool results.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Deliver all queued steering messages or one per turn.</summary>
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;

    /// <summary>Deliver all queued follow-ups or one per completion.</summary>
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;

    /// <summary>True while a run is in flight.</summary>
    public bool IsStreaming
    {
        get { lock (_gate) return _runTask is { IsCompleted: false }; }
    }

    /// <summary>Snapshot of pending steering text, for client display.</summary>
    public IReadOnlyList<string> PendingSteering
    {
        get { lock (_gate) return _steering.Select(m => m.Content).ToList(); }
    }

    /// <summary>Snapshot of pending follow-up text, for client display.</summary>
    public IReadOnlyList<string> PendingFollowUp
    {
        get { lock (_gate) return _followUp.Select(m => m.Content).ToList(); }
    }

    /// <summary>
    /// Queue a message to be delivered after the current assistant turn finishes its tool
    /// calls, before the next provider request. Use when the user types while the agent runs.
    /// </summary>
    public void Steer(ChatMessage message)
    {
        lock (_gate) _steering.Add(message);
    }

    /// <summary>Queue a message to be delivered only once the agent would otherwise stop.</summary>
    public void FollowUp(ChatMessage message)
    {
        lock (_gate) _followUp.Add(message);
    }

    /// <summary>
    /// Drain both queues and return the text, so a client can restore it into its editor.
    /// This is the interactive Esc behaviour: <c>ClearQueue</c> then <c>Abort</c>.
    /// </summary>
    public (IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) ClearQueue()
    {
        lock (_gate)
        {
            var steering = _steering.Select(m => m.Content).ToList();
            var followUp = _followUp.Select(m => m.Content).ToList();
            _steering.Clear();
            _followUp.Clear();
            return (steering, followUp);
        }
    }

    /// <summary>Cancel the in-flight run. Queued messages survive unless cleared first.</summary>
    public void Abort()
    {
        lock (_gate) _runCts?.Cancel();
    }

    /// <summary>
    /// Append a user message and run the agent to settlement. Returns null when a run is
    /// already in flight — use <see cref="Steer"/> or <see cref="FollowUp"/> in that case.
    /// </summary>
    public Task<AgentRunSummary>? PromptAsync(
        ChatMessage userMessage,
        AgentLoopConfig config,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> stream,
        IReadOnlyList<AiToolDefinition> tools,
        Action<AgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runTask is { IsCompleted: false })
                return null;

            Messages.Add(userMessage);

            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _runCts.Token;

            var effective = config with
            {
                Model = Model,
                GetSteeringMessages = () => Drain(_steering, SteeringMode),
                GetFollowUpMessages = () => Drain(_followUp, FollowUpMode)
            };

            _runTask = AgentLoop.RunAsync(Messages, effective, stream, tools, onEvent, token);
            return _runTask;
        }
    }

    /// <summary>
    /// Awaits the in-flight run, if any. Returns null when nothing is running.
    /// </summary>
    public async Task<AgentRunSummary?> WaitForIdleAsync()
    {
        Task<AgentRunSummary>? task;
        lock (_gate) task = _runTask;

        if (task is null)
            return null;

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ChatMessage> Drain(List<ChatMessage> queue, AgentQueueMode mode)
    {
        if (queue.Count == 0)
            return [];

        if (mode == AgentQueueMode.All)
        {
            var all = queue.ToList();
            queue.Clear();
            return all;
        }

        var first = queue[0];
        queue.RemoveAt(0);
        return [first];
    }
}

/// <summary>
/// Retry policy for transient provider failures (429, 5xx, overloaded). Ported from pi's
/// auto-retry: exponential backoff with jitter, abortable, and reported through callbacks
/// so a client can show a countdown instead of a frozen screen.
/// </summary>
public sealed record AgentRetryPolicy
{
    public int MaxAttempts { get; init; } = 4;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static readonly AgentRetryPolicy Disabled = new() { MaxAttempts = 1 };

    /// <summary>True for status codes worth retrying. 4xx other than 408/409/429 are terminal.</summary>
    public static bool IsTransient(int statusCode) =>
        statusCode is 408 or 409 or 429 or >= 500;

    /// <summary>Backoff for a 1-based attempt index.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var scaled = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, MaxDelay.TotalMilliseconds);
        // Full jitter keeps concurrent agents from synchronising their retries.
        var jittered = capped * (0.5 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(jittered);
    }
}
#endif
