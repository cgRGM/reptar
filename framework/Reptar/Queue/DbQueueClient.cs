namespace Reptar;

public sealed record DbQueueOptions(
    int MaxAttempts = 25,
    int BatchSize = 50,
    int BaseBackoffSeconds = 15,
    int MaxBackoffSeconds = 900,
    int CompletedRetentionMinutes = 60);

/// <summary>
/// Durable queue stored in the application's own database (SQLite,
/// PostgreSQL, or MySQL) — no new infrastructure, jobs commit transactionally
/// alongside app data. Single-node by design: one poller claims due rows,
/// failed jobs return with exponential backoff, exhausted jobs land in the
/// 'dead' state. Multi-node deployments should use a platform adapter
/// (SQS, Azure Storage Queues, Pub/Sub) or external coordination.
/// </summary>
public sealed class DbQueueClient : IQueueClient, IQueueConsumer
{
    public const string JobsTable = "reptar_jobs";

    /// <summary>
    /// The jobs table expressed as a regular <see cref="TableDef"/> so
    /// migrations, <c>reptar inspect</c>, and the doctor treat it like any
    /// application table.
    /// </summary>
    public static TableDef Table() => ReptarApi.DefineTable(
        JobsTable,
        ReptarApi.Col.Text("id").PrimaryKey(),
        ReptarApi.Col.Text("queue").NotNull().Indexed(),
        ReptarApi.Col.Text("body").NotNull(),
        ReptarApi.Col.Text("state").NotNull().Indexed(),
        ReptarApi.Col.Text("visible_at").NotNull().Indexed(),
        ReptarApi.Col.Int("attempts").NotNull(),
        ReptarApi.Col.Int("max_attempts").NotNull(),
        ReptarApi.Col.Text("dedup_key"),
        ReptarApi.Col.Text("last_error"),
        ReptarApi.Col.Text("created_at").NotNull(),
        ReptarApi.Col.Text("completed_at"));

    private readonly IDbClient _db;
    private readonly DbQueueOptions _options;
    private readonly IClock _clock;
    private readonly IQueueObserver? _observer;

    public DbQueueClient(
        IDbClient db,
        DbQueueOptions? options = null,
        IClock? clock = null,
        IQueueObserver? observer = null)
    {
        _db = db;
        _options = options ?? new DbQueueOptions();
        _clock = clock ?? new SystemClock();
        _observer = observer;
    }

    public Task EnsureCreatedAsync() => _db.EnsureCreatedAsync(Table());

    public async Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        var dedupKey = options?.DedupId is { Length: > 0 } dedup
            ? $"{queueName}:{dedup}"
            : null;

        if (dedupKey is not null)
        {
            var exists = await _db.ExistsAsync(
                Table(),
                "state = 'pending' and dedup_key = @dedupKey",
                new { dedupKey });
            if (exists)
                return;
        }

        var delaySeconds = options?.DelaySeconds ?? 0;
        var id = Ids.New("job");
        var now = _clock.IsoNow();

        await _db.ExecuteAsync(
            $"""
             insert into {JobsTable}
                 (id, queue, body, state, visible_at, attempts, max_attempts, dedup_key, created_at)
             values
                 (@id, @queue, @body, 'pending', @visibleAt, 0, @maxAttempts, @dedupKey, @createdAt)
             """,
            new
            {
                id,
                queue = queueName,
                body = messageJson,
                visibleAt = _clock.IsoIn(TimeSpan.FromSeconds(delaySeconds)),
                maxAttempts = options?.AttemptsHint ?? _options.MaxAttempts,
                dedupKey,
                createdAt = now,
            });

        _observer?.Enqueued(queueName, id, options);
    }

    public async Task<QueueBatch?> ReceiveAsync(string queue, int maxMessages, CancellationToken ct = default)
    {
        var rows = await _db.QueryMappedAsync(
            $"""
             select id, body, attempts, created_at
             from {JobsTable}
             where queue = @queue and state = 'pending' and visible_at <= @now
             order by created_at
             limit @max
             """,
            new { queue, now = _clock.IsoNow(), max = Math.Min(maxMessages, _options.BatchSize) },
            row => new JobRow(
                Convert.ToString(row["id"]) ?? "",
                Convert.ToString(row["body"]) ?? "",
                ToInt(row["attempts"]),
                Convert.ToString(row["created_at"]) ?? ""));

        if (rows.Count == 0)
            return null;

        var messages = new List<QueueMessage>(rows.Count);
        foreach (var row in rows)
            messages.Add(new QueueMessage(row.Id, row.Body, row.CreatedAt, row.Attempts + 1));

        return new QueueBatch(Ids.New("qbatch"), queue, messages);
    }

    public async Task AckAsync(string queue, QueueBatch batch, CancellationToken ct = default)
    {
        foreach (var message in batch.Messages)
        {
            await _db.ExecuteAsync(
                $"update {JobsTable} set state = 'completed', completed_at = @completedAt where id = @id",
                new { id = message.Id, completedAt = _clock.IsoNow() });

            _observer?.Completed(queue, message.Id);
        }

        await SweepCompletedAsync();
    }

    public async Task RetryAsync(
        string queue,
        QueueBatch batch,
        IReadOnlyDictionary<string, int> retryDelaySeconds,
        CancellationToken ct = default)
    {
        foreach (var message in batch.Messages)
        {
            var row = await _db.QueryMappedAsync(
                $"select attempts, max_attempts, last_error from {JobsTable} where id = @id",
                new { id = message.Id },
                r => (Attempts: ToInt(r["attempts"]), MaxAttempts: ToInt(r["max_attempts"]),
                    LastError: Convert.ToString(r["last_error"])));

            if (row.Count == 0)
                continue;

            var attempts = row[0].Attempts;
            var maxAttempts = row[0].MaxAttempts;

            if (attempts + 1 >= maxAttempts)
            {
                await _db.ExecuteAsync(
                    $"update {JobsTable} set state = 'dead', attempts = @attempts, last_error = @lastError where id = @id",
                    new { attempts = attempts + 1, id = message.Id, lastError = "max attempts reached" });
                _observer?.DeadLettered(queue, message.Id, attempts + 1);
                continue;
            }

            var delaySeconds = retryDelaySeconds.TryGetValue(message.Id, out var requested) && requested > 0
                ? requested
                : BackoffSeconds(attempts);

            await _db.ExecuteAsync(
                $"update {JobsTable} set attempts = @attempts, visible_at = @visibleAt, last_error = @lastError where id = @id",
                new
                {
                    attempts = attempts + 1,
                    id = message.Id,
                    visibleAt = _clock.IsoIn(TimeSpan.FromSeconds(delaySeconds)),
                    lastError = $"attempt {attempts + 1} failed",
                });

            _observer?.Retried(queue, message.Id, attempts + 1, delaySeconds);
        }
    }

    private int BackoffSeconds(int attempts)
    {
        // Exponential from the base, capped; attempts start at 0 for the
        // first failure, so the first backoff equals the base delay.
        var exp = Math.Min(attempts, 10);
        var seconds = (long)_options.BaseBackoffSeconds * (1L << exp);
        return (int)Math.Min(seconds, _options.MaxBackoffSeconds);
    }

    private async Task SweepCompletedAsync()
    {
        var cutoff = _clock.IsoIn(TimeSpan.FromMinutes(-_options.CompletedRetentionMinutes));
        await _db.ExecuteAsync(
            $"delete from {JobsTable} where state = 'completed' and completed_at < @cutoff",
            new { cutoff });
    }

    private static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    private sealed record JobRow(string Id, string Body, int Attempts, string CreatedAt);
}
