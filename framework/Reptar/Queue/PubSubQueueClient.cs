using System.Text.Json.Nodes;

namespace Reptar;

public sealed record PubSubOptions(
    string ProjectId,
    string Topic,
    string Subscription,
    Func<CancellationToken, Task<string>> TokenProvider,
    int MaxPullMessages = 50,
    int AckDeadlineSeconds = 10)
{
    /// <summary>
    /// Token provider from a static env token (GOOGLE_OAUTH_ACCESS_TOKEN) —
    /// useful for benches and short-lived deployments. For production, pass a
    /// provider that exchanges a service-account JWT for an access token.
    /// </summary>
    public static Func<CancellationToken, Task<string>> EnvironmentToken(IEnvironment? env = null)
    {
        var environment = env ?? new SystemEnvironment();
        return _ => Task.FromResult(
            environment.Get("GOOGLE_OAUTH_ACCESS_TOKEN")
            ?? throw new InvalidOperationException(
                "Pub/Sub requires GOOGLE_OAUTH_ACCESS_TOKEN or a custom TokenProvider."));
    }
}

/// <summary>
/// Google Cloud Pub/Sub adapter over the REST API with a pluggable bearer
/// token — portable across every Reptar target (no signing, pure JSON).
/// Pub/Sub has no publish delay (use Cloud Tasks or the DB queue for that)
/// and dedup requires an exactly-once subscription; both limitations are
/// reported with one-time warnings instead of silent no-ops.
/// </summary>
public sealed class PubSubQueueClient : IQueueClient, IQueueConsumer
{
    private readonly PubSubOptions _options;
    private readonly IHttpClient _http;
    private readonly IQueueObserver? _observer;
    private readonly Dictionary<string, string> _ackIds = new(StringComparer.Ordinal);
    private bool _warnedDelay;
    private bool _warnedDedup;

    public PubSubQueueClient(PubSubOptions options, IHttpClient http, IQueueObserver? observer = null)
    {
        _options = options;
        _http = http;
        _observer = observer;
    }

    public static PubSubQueueClient FromEnvironment(
        IHttpClient http,
        IEnvironment? environment = null,
        IQueueObserver? observer = null)
    {
        var env = environment ?? new SystemEnvironment();
        var project = env.Get("GOOGLE_CLOUD_PROJECT") ?? env.Get("GOOGLE_PROJECT_ID")
            ?? throw new InvalidOperationException(
                "Pub/Sub requires GOOGLE_CLOUD_PROJECT or GOOGLE_PROJECT_ID.");

        var topic = env.Get("PUBSUB_TOPIC")
            ?? throw new InvalidOperationException("Pub/Sub requires PUBSUB_TOPIC.");

        var subscription = env.Get("PUBSUB_SUBSCRIPTION")
            ?? throw new InvalidOperationException("Pub/Sub requires PUBSUB_SUBSCRIPTION.");

        return new PubSubQueueClient(
            new PubSubOptions(project, topic, subscription, PubSubOptions.EnvironmentToken(env)),
            http,
            observer);
    }

    public async Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        if (options?.DelaySeconds is > 0)
            WarnDelay();
        if (options?.DedupId is { Length: > 0 })
            WarnDedup();

        var message = new JsonObject { ["data"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson)) };
        if (options?.DedupId is { Length: > 0 } dedup)
            message["attributes"] = new JsonObject { ["dedupId"] = dedup };

        var body = new JsonObject
        {
            ["messages"] = new JsonArray { message },
        };

        var response = await PostAsync(
            $"https://pubsub.googleapis.com/v1/projects/{_options.ProjectId}/topics/{_options.Topic}:publish",
            body.ToJsonString());

        var messageId = JsonNode.Parse(response.Body)?["messageIds"]?[0]?.ToString();
        _observer?.Enqueued(queueName, messageId ?? Ids.New("job"), options);
    }

    public async Task<QueueBatch?> ReceiveAsync(string queue, int maxMessages, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["maxMessages"] = Math.Clamp(maxMessages, 1, _options.MaxPullMessages),
        };

        var response = await PostAsync(
            $"https://pubsub.googleapis.com/v1/projects/{_options.ProjectId}/subscriptions/{_options.Subscription}:pull",
            body.ToJsonString());

        var root = JsonNode.Parse(response.Body);
        var received = root?["receivedMessages"] as JsonArray;
        if (received is null || received.Count == 0)
            return null;

        var messages = new List<QueueMessage>(received.Count);
        foreach (var item in received)
        {
            var ackId = item?["ackId"]?.ToString();
            var inner = item?["message"];
            var id = inner?["messageId"]?.ToString();
            var data = inner?["data"]?.ToString();
            var publishTime = inner?["publishTime"]?.ToString();

            if (ackId is null || id is null || data is null)
                continue;

            _ackIds[id] = ackId;
            messages.Add(new QueueMessage(
                id,
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)),
                publishTime ?? DateTimeOffset.UtcNow.ToString("O"),
                1));
        }

        return messages.Count == 0
            ? null
            : new QueueBatch(Ids.New("qbatch"), queue, messages);
    }

    public async Task AckAsync(string queue, QueueBatch batch, CancellationToken ct = default)
    {
        var ackIds = CollectAckIds(batch.Messages);
        if (ackIds.Count == 0)
            return;

        foreach (var message in batch.Messages)
            _observer?.Completed(queue, message.Id);

        await PostAsync(
            $"https://pubsub.googleapis.com/v1/projects/{_options.ProjectId}/subscriptions/{_options.Subscription}:acknowledge",
            new JsonObject { ["ackIds"] = ToJsonArray(ackIds) }.ToJsonString());
    }

    public async Task RetryAsync(
        string queue,
        QueueBatch batch,
        IReadOnlyDictionary<string, int> retryDelaySeconds,
        CancellationToken ct = default)
    {
        var ackIds = CollectAckIds(batch.Messages);
        if (ackIds.Count == 0)
            return;

        // A zero ackDeadlineSeconds nacks immediately; RetryIn maps to the
        // next redelivery delay, clamped to the 600s API cap.
        var deadline = 0;
        foreach (var message in batch.Messages)
        {
            if (retryDelaySeconds.TryGetValue(message.Id, out var requested))
                deadline = Math.Max(deadline, Math.Min(requested, 600));
        }

        await PostAsync(
            $"https://pubsub.googleapis.com/v1/projects/{_options.ProjectId}/subscriptions/{_options.Subscription}:modifyAckDeadline",
            new JsonObject
            {
                ["ackIds"] = ToJsonArray(ackIds),
                ["ackDeadlineSeconds"] = deadline,
            }.ToJsonString());

        foreach (var message in batch.Messages)
            _observer?.Retried(queue, message.Id, message.Attempts, deadline);
    }

    private List<string> CollectAckIds(IReadOnlyList<QueueMessage> messages)
    {
        var ackIds = new List<string>(messages.Count);
        foreach (var message in messages)
        {
            if (_ackIds.TryGetValue(message.Id, out var ackId))
            {
                ackIds.Add(ackId);
                _ackIds.Remove(message.Id);
            }
        }
        return ackIds;
    }

    private static JsonArray ToJsonArray(List<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private async Task<HttpResponseData> PostAsync(string url, string body)
    {
        var token = await _options.TokenProvider(CancellationToken.None);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {token}",
        };

        var response = await _http.FetchAsync(url, "POST", headers, body);

        if (response.Status >= 400)
            throw new InvalidOperationException(
                $"Pub/Sub request failed (HTTP {response.Status}): {response.Body}");

        return response;
    }

    private void WarnDelay()
    {
        if (_warnedDelay)
            return;

        _warnedDelay = true;
        Console.WriteLine("WARN Pub/Sub has no publish delay; DelaySeconds is ignored (Cloud Tasks is the GCP-native workaround).");
    }

    private void WarnDedup()
    {
        if (_warnedDedup)
            return;

        _warnedDedup = true;
        Console.WriteLine("WARN Pub/Sub dedup requires an exactly-once subscription; DedupId is attached as a message attribute only.");
    }
}
