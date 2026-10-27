#if !NETWASM
using System.Collections.Concurrent;

namespace Reptar.AI;

/// <summary>
/// Tracks in-flight agent runs by key so a second request can steer or abort the first.
/// </summary>
/// <remarks>
/// An HTTP handler is stateless: without a registry, a request that arrives while the agent is
/// working has no way to reach the run started by an earlier request. That is the whole difference
/// between a web client that feels like the terminal and one where you can only wait.
///
/// The key is whatever the host already has — a session id, a conversation id, a job id. This
/// class knows nothing about HTTP or sessions; it is a map of cancellable runs with a message
/// queue each.
/// </remarks>
public static class AgentRunRegistry
{
    private sealed class ActiveRun
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentQueue<ChatMessage> Steering { get; } = new();
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, ActiveRun> Runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a run and hands back its cancellation token. Replaces any previous run for the
    /// same key, so a restarted run does not leak the old token.
    /// </summary>
    public static void Begin(string key, out CancellationToken token)
    {
        var run = new ActiveRun();
        Runs[key] = run;
        token = run.Cancellation.Token;
    }

    /// <summary>Removes the run. Call from a finally block.</summary>
    public static void End(string key)
    {
        if (Runs.TryRemove(key, out var run))
            run.Cancellation.Dispose();
    }

    /// <summary>Queues a message for the running agent. False when nothing is running.</summary>
    public static bool Steer(string key, string message)
    {
        if (!Runs.TryGetValue(key, out var run))
            return false;

        run.Steering.Enqueue(new ChatMessage(AgentRoles.User, message));
        return true;
    }

    /// <summary>Aborts the running agent. False when nothing is running.</summary>
    public static bool Abort(string key)
    {
        if (!Runs.TryGetValue(key, out var run))
            return false;

        run.Cancellation.Cancel();
        return true;
    }

    /// <summary>Whether a run is active for the key, and how long it has been going.</summary>
    public static (bool Running, double ElapsedMs) Status(string key) =>
        Runs.TryGetValue(key, out var run)
            ? (true, (DateTimeOffset.UtcNow - run.StartedAt).TotalMilliseconds)
            : (false, 0);

    /// <summary>
    /// Takes every queued steering message. Consumed, not replayed, so a client that steers twice
    /// does not get its message delivered on every subsequent turn.
    /// </summary>
    public static IReadOnlyList<ChatMessage> DrainSteering(string key)
    {
        if (!Runs.TryGetValue(key, out var run))
            return [];

        var drained = new List<ChatMessage>();
        while (run.Steering.TryDequeue(out var message))
            drained.Add(message);

        return drained;
    }

    /// <summary>Number of runs currently registered, for a health endpoint.</summary>
    public static int ActiveCount => Runs.Count;
}
#endif
