namespace Reptar;

/// <summary>
/// Development and test queue: <see cref="SendAsync"/> executes the
/// registered <see cref="ReptarApp.OnQueue"/> handler inline, through the
/// exact same dispatch path as production runtimes. Unregistered queues and
/// unsupported options are reported honestly instead of silently dropped.
/// This is the default <see cref="ReptarConfig.Queues"/> on native targets;
/// Cloudflare Workers keep the platform binding.
/// </summary>
public sealed class SyncQueueClient : IQueueClient
{
    private readonly Func<ReptarApp> _appAccessor;
    private readonly IQueueObserver? _observer;
    private bool _warnedOptions;

    public SyncQueueClient(ReptarApp app, IQueueObserver? observer = null)
        : this(() => app, observer) { }

    public SyncQueueClient(Func<ReptarApp> appAccessor, IQueueObserver? observer = null)
    {
        _appAccessor = appAccessor;
        _observer = observer;
    }

    public async Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        if (options is not null)
            WarnOptions();

        var app = _appAccessor();
        var message = new QueueMessage(
            Ids.New("job"),
            messageJson,
            DateTimeOffset.UtcNow.ToString("O"),
            1);

        _observer?.Enqueued(queueName, message.Id, options);

        // Reuse the production dispatch path so tests exercise the exact
        // Ctx, error handling, and retry-delay capture of real consumers.
        await app.HandleQueueBatchAsync(
            new QueueBatch(Ids.New("qbatch"), queueName, [message]));
    }

    private void WarnOptions()
    {
        if (_warnedOptions)
            return;

        _warnedOptions = true;
        Console.WriteLine(
            "WARN SyncQueueClient ignores QueueSendOptions (delay/dedup). " +
            "Use a durable adapter (Db/Sqs/AzureStorage/PubSub) for those semantics.");
    }
}

/// <summary>
/// Recording queue for tests: every send is captured for assertions, and
/// <see cref="DrainAsync"/> replays them through the app's real consumer
/// pipeline. Without an app it stays purely recording.
/// </summary>
public sealed class FakeQueueClient : IQueueClient
{
    private readonly ReptarApp? _app;
    private readonly IQueueObserver? _observer;

    public List<(string Queue, string Body, QueueSendOptions? Options)> Recorded { get; } = [];
    public List<QueueBatchResult> Results { get; } = [];

    public FakeQueueClient(ReptarApp? app = null, IQueueObserver? observer = null)
    {
        _app = app;
        _observer = observer;
    }

    public Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        Recorded.Add((queueName, messageJson, options));
        return Task.CompletedTask;
    }

    /// <summary>Replays every recorded send through the app's consumer pipeline.</summary>
    public async Task<QueueBatchResult> DrainAsync()
    {
        if (_app is null)
            throw new InvalidOperationException("FakeQueueClient has no app to drain to.");

        QueueBatchResult last = new(true, []);
        var remaining = new List<(string Queue, string Body, QueueSendOptions? Options)>(Recorded);
        Recorded.Clear();

        foreach (var (queue, body, options) in remaining)
        {
            _observer?.Enqueued(queue, Ids.New("job"), options);
            last = await _app.HandleQueueBatchAsync(
                new QueueBatch(Ids.New("qbatch"), queue, [
                    new QueueMessage(Ids.New("job"), body, DateTimeOffset.UtcNow.ToString("O"), 1),
                ]));
            Results.Add(last);
        }

        return last;
    }
}
