namespace Reptar;

/// <summary>
/// Enqueue options shared across queue providers. Each provider adapter
/// documents which fields it honors; unsupported fields are ignored with a
/// one-time warning. Delay: Cloudflare Queues &lt;=24h, SQS &lt;=15m,
/// Azure Storage Queues &lt;=7d, DB-backed visible_at, Pub/Sub unsupported.
/// Dedup: SQS FIFO, Azure Service Bus, DB-backed; elsewhere advisory.
/// </summary>
public sealed record QueueSendOptions(
    int? DelaySeconds = null,
    string? DedupId = null,
    int? AttemptsHint = null);

/// <summary>
/// Thrown from an <see cref="ReptarApp.OnQueue"/> handlers to retry the
/// message after a specific delay instead of the provider's default
/// backoff. Cloudflare Queues: retry delaySeconds. SQS: visibility timeout.
/// Azure Storage Queues: visibility update. DB-backed: visible_at.
/// </summary>
public sealed class RetryIn : Exception
{
    public TimeSpan Delay { get; }

    public RetryIn(TimeSpan delay)
        : base($"Retry job in {(int)delay.TotalSeconds}s.")
        => Delay = delay;

    public RetryIn(int delaySeconds)
        : this(TimeSpan.FromSeconds(delaySeconds)) { }
}

/// <summary>
/// Per-message retry instruction returned from a consumer batch. Mapped by
/// each runtime: the Workers host calls retry({ delaySeconds }), the native
/// poller extends the lease/visibility window, the DB queue sets visible_at.
/// </summary>
public sealed record QueueRetry(string MessageId, int DelaySeconds);

/// <summary>
/// Pull-side queue port for runtimes that own the scheduling loop
/// (Kestrel, containers, VPS). The DB-backed queue is the built-in
/// implementation; SQS, Azure Storage Queues, and Pub/Sub implement the
/// same contract. Workers never needs it: the platform invokes you.
/// </summary>
public interface IQueueConsumer
{
    /// <summary>Returns up to <paramref name="maxMessages"/> due messages, or null/empty when idle.</summary>
    Task<QueueBatch?> ReceiveAsync(string queue, int maxMessages, CancellationToken ct = default);

    /// <summary>Marks the batch's messages as successfully processed (delete/complete).</summary>
    Task AckAsync(string queue, QueueBatch batch, CancellationToken ct = default);

    /// <summary>Returns the batch's failed messages for retry with per-message delay hints.</summary>
    Task RetryAsync(
        string queue,
        QueueBatch batch,
        IReadOnlyDictionary<string, int> retryDelaySeconds,
        CancellationToken ct = default);
}

/// <summary>
/// Optional queue lifecycle observer. Null by default: zero cost when not
/// configured. Wire OpenTelemetry or metrics over this hook; the framework
/// itself never takes a tracing dependency.
/// </summary>
public interface IQueueObserver
{
    void Enqueued(string queue, string messageId, QueueSendOptions? options) { }
    void Completed(string queue, string messageId) { }
    void Failed(string queue, string messageId, int attempt, Exception error) { }
    void Retried(string queue, string messageId, int attempt, int delaySeconds) { }
    void DeadLettered(string queue, string messageId, int attempts) { }
}
