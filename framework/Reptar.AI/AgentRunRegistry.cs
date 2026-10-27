#if !NETWASM
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Reptar.AI;

/// <summary>Tracks cancellable, steerable agent runs by host-defined session or job key.</summary>
public static class AgentRunRegistry
{
    internal sealed class ActiveRun
    {
        private readonly object _gate = new();
        private bool _ended;
        private int _cancellations;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal ConcurrentQueue<ChatMessage> Steering { get; } = new();
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        internal bool Steer(string message)
        {
            lock (_gate)
            {
                if (_ended)
                    return false;
                Steering.Enqueue(new ChatMessage(AgentRoles.User, message));
                return true;
            }
        }

        internal bool Abort()
        {
            lock (_gate)
            {
                if (_ended)
                    return false;
                _cancellations++;
            }
            // User cancellation callbacks must never execute under our lifetime lock.
            try
            {
                Cancellation.Cancel();
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _cancellations--;
                    if (_ended && _cancellations == 0)
                        Cancellation.Dispose();
                }
            }
        }

        internal void End()
        {
            lock (_gate)
            {
                if (_ended)
                    return;
                _ended = true;
                if (_cancellations == 0)
                    Cancellation.Dispose();
            }
        }

        internal IReadOnlyList<ChatMessage> Drain()
        {
            var drained = new List<ChatMessage>();
            while (Steering.TryDequeue(out var message))
                drained.Add(message);
            return drained;
        }
    }

    /// <summary>Owns one particular run, so late cleanup cannot remove a newer run.</summary>
    public sealed class RunLease : IDisposable
    {
        private readonly string _key;
        private readonly ActiveRun _run;
        public CancellationToken Token { get; }

        internal RunLease(string key, ActiveRun run)
        {
            _key = key;
            _run = run;
            Token = run.Cancellation.Token;
        }

        public IReadOnlyList<ChatMessage> DrainSteering() => _run.Drain();

        public void Dispose()
        {
            Runs.TryRemove(new KeyValuePair<string, ActiveRun>(_key, _run));
            _run.End();
        }
    }

    private static readonly ConcurrentDictionary<string, ActiveRun> Runs = new(StringComparer.Ordinal);

    /// <summary>Reserves an idle key; never overwrites an active run.</summary>
    public static bool TryBegin(string key, [NotNullWhen(true)] out RunLease? lease)
    {
        var run = new ActiveRun();
        // Capture the token before publishing: End may race immediately after publication.
        var candidate = new RunLease(key, run);
        if (Runs.TryAdd(key, run))
        {
            lease = candidate;
            return true;
        }
        run.End();
        lease = null;
        return false;
    }

    /// <summary>Starts an idle key, throwing if it already has an active run. Prefer a lease for cleanup.</summary>
    public static void Begin(string key, out CancellationToken token)
    {
        if (!TryBegin(key, out var lease))
            throw new InvalidOperationException($"An agent run is already active for '{key}'.");
        token = lease.Token;
    }

    /// <summary>Explicitly removes the current run. Running producers should dispose their own lease instead.</summary>
    public static void End(string key)
    {
        if (Runs.TryRemove(key, out var run))
            run.End();
    }

    public static bool Steer(string key, string message) =>
        Runs.TryGetValue(key, out var run) && run.Steer(message);

    public static bool Abort(string key) => Runs.TryGetValue(key, out var run) && run.Abort();

    public static (bool Running, double ElapsedMs) Status(string key) =>
        Runs.TryGetValue(key, out var run)
            ? (true, (DateTimeOffset.UtcNow - run.StartedAt).TotalMilliseconds)
            : (false, 0);

    public static IReadOnlyList<ChatMessage> DrainSteering(string key) =>
        Runs.TryGetValue(key, out var run) ? run.Drain() : [];

    public static int ActiveCount => Runs.Count;
}
#endif
