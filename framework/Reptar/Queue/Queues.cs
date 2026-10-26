namespace Reptar;

/// <summary>
/// Queue adapter entrypoints, mirroring the <see cref="Db"/> and
/// <see cref="ReptarApi"/> style: <c>Queues = Queues.Db(db)</c> in
/// <c>ReptarConfig</c>. Cloudflare Workers resolve to the platform Queues
/// binding automatically; these factories configure explicit adapters.
/// </summary>
public static class Queues
{
    /// <summary>Inline execute for dev/tests. The native default.</summary>
    public static IQueueClient Sync(ReptarApp app, IQueueObserver? observer = null) =>
        new SyncQueueClient(app, observer);

    /// <summary>Recording queue for the portable test suite.</summary>
    public static FakeQueueClient Fake(ReptarApp? app = null, IQueueObserver? observer = null) =>
        new(app, observer);

    /// <summary>
    /// Durable queue in the app's own SQL database (SQLite, PostgreSQL,
    /// MySQL) — the single-node VPS default. Remember to call
    /// <c>await client.EnsureCreatedAsync()</c> (the Kestrel poller does it
    /// automatically) or register <see cref="DbQueueClient.Table"/> in the
    /// app schema.
    /// </summary>
    public static DbQueueClient Db(
        IDbClient db,
        DbQueueOptions? options = null,
        IClock? clock = null,
        IQueueObserver? observer = null) =>
        new(db, options, clock, observer);

#if !NETWASM
    /// <summary>Amazon SQS producer/consumer. Config: AWS_REGION,
    /// AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, [AWS_SESSION_TOKEN],
    /// SQS_QUEUE_URL.</summary>
    public static SqsQueueClient Sqs(SqsOptions options, IHttpClient? http = null, IQueueObserver? observer = null) =>
        new(options, http ?? new DefaultHttpClient(), observer);

    public static SqsQueueClient SqsFromEnvironment(
        IHttpClient? http = null,
        IEnvironment? environment = null,
        IQueueObserver? observer = null) =>
        SqsQueueClient.FromEnvironment(http ?? new DefaultHttpClient(), environment, observer);

    /// <summary>Azure Queue Storage producer/consumer. Config:
    /// AZURE_STORAGE_ACCOUNT, AZURE_STORAGE_KEY.</summary>
    public static AzureStorageQueueClient AzureStorage(
        StorageQueueOptions options,
        IHttpClient? http = null,
        IQueueObserver? observer = null) =>
        new(options, http ?? new DefaultHttpClient(), observer);

    public static AzureStorageQueueClient AzureStorageFromEnvironment(
        IHttpClient? http = null,
        IEnvironment? environment = null,
        IQueueObserver? observer = null) =>
        AzureStorageQueueClient.FromEnvironment(http ?? new DefaultHttpClient(), environment, observer);

    /// <summary>
    /// GCP Pub/Sub producer/consumer — portable to every target, including
    /// Workers (pure JSON + bearer token). Config: GOOGLE_CLOUD_PROJECT,
    /// PUBSUB_TOPIC, PUBSUB_SUBSCRIPTION, GOOGLE_OAUTH_ACCESS_TOKEN.
    /// </summary>
    public static PubSubQueueClient PubSub(
        PubSubOptions options,
        IHttpClient? http = null,
        IQueueObserver? observer = null) =>
        new(options, http ?? new DefaultHttpClient(), observer);

    public static PubSubQueueClient PubSubFromEnvironment(
        IHttpClient? http = null,
        IEnvironment? environment = null,
        IQueueObserver? observer = null) =>
        PubSubQueueClient.FromEnvironment(http ?? new DefaultHttpClient(), environment, observer);
#endif
}
