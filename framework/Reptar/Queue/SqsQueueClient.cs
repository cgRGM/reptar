#if !NETWASM
using System.Text;

namespace Reptar;

public sealed record SqsOptions(
    string? DefaultQueueUrl,
    string Region,
    string AccessKey,
    string SecretKey,
    string? SessionToken = null,
    int ReceiveVisibilitySeconds = 60,
    int LongPollSeconds = 10,
    Dictionary<string, string>? QueueUrls = null)
{
    public string ResolveQueueUrl(string queueName)
    {
        if (QueueUrls is not null &&
            QueueUrls.TryGetValue(queueName, out var mapped) &&
            mapped.Length > 0)
        {
            return mapped;
        }

        if (DefaultQueueUrl is { Length: > 0 })
            return DefaultQueueUrl;

        throw new InvalidOperationException(
            $"No SQS queue URL configured for queue '{queueName}'. " +
            "Set SqsOptions.DefaultQueueUrl or add it to SqsOptions.QueueUrls.");
    }
}

/// <summary>
/// Amazon SQS adapter over the zero-dependency HTTP + SigV4 path: the same
/// producer surface on any runtime, and <see cref="IQueueConsumer"/> for the
/// Kestrel poller. Retries map to visibility-timeout extensions; exhausted
/// messages follow the queue's redrive policy (maxReceiveCount → DLQ).
/// </summary>
public sealed class SqsQueueClient : IQueueClient, IQueueConsumer
{
    private const string ApiVersion = "2012-11-05";

    private readonly SqsOptions _options;
    private readonly IHttpClient _http;
    private readonly IQueueObserver? _observer;
    private readonly Dictionary<string, string> _receipts = new(StringComparer.Ordinal);
    private bool _warnedDelay;

    public SqsQueueClient(SqsOptions options, IHttpClient http, IQueueObserver? observer = null)
    {
        _options = options;
        _http = http;
        _observer = observer;
    }

    public static SqsQueueClient FromEnvironment(
        IHttpClient http,
        IEnvironment? environment = null,
        IQueueObserver? observer = null)
    {
        var env = environment ?? new SystemEnvironment();
        var region = env.Get("AWS_REGION") ?? env.Get("AWS_DEFAULT_REGION")
            ?? throw new InvalidOperationException(
                "SQS requires AWS_REGION or AWS_DEFAULT_REGION.");

        var accessKey = env.Get("AWS_ACCESS_KEY_ID")
            ?? throw new InvalidOperationException("SQS requires AWS_ACCESS_KEY_ID.");

        var secretKey = env.Get("AWS_SECRET_ACCESS_KEY")
            ?? throw new InvalidOperationException("SQS requires AWS_SECRET_ACCESS_KEY.");

        return new SqsQueueClient(
            new SqsOptions(
                env.Get("SQS_QUEUE_URL") ?? env.Get("AWS_SQS_QUEUE_URL"),
                region,
                accessKey,
                secretKey,
                env.Get("AWS_SESSION_TOKEN")),
            http,
            observer);
    }

    public Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        var form = new StringBuilder()
            .Append("Action=SendMessage")
            .Append("&Version=").Append(ApiVersion)
            .Append("&QueueUrl=").Append(AwsSigV4.UriEncode(_options.ResolveQueueUrl(queueName)))
            .Append("&MessageBody=").Append(AwsSigV4.UriEncode(messageJson));

        if (options?.DelaySeconds is { } delay)
        {
            if (delay > 900)
                WarnDelay();
            form.Append("&DelaySeconds=").Append(Math.Min(delay, 900));
        }

        if (options?.DedupId is { Length: > 0 } dedup)
            form.Append("&MessageDeduplicationId=").Append(AwsSigV4.UriEncode(dedup));

        return PostAsync(_options.ResolveQueueUrl(queueName), queueName, form.ToString(), options);
    }

    public async Task SendBatchAsync(
        string queueName,
        IReadOnlyList<string> messagesJson,
        QueueSendOptions? options = null)
    {
        var queueUrl = _options.ResolveQueueUrl(queueName);
        var batches = (messagesJson.Count + 9) / 10;

        for (var batchIndex = 0; batchIndex < batches; batchIndex++)
        {
            var form = new StringBuilder()
                .Append("Action=SendMessageBatch")
                .Append("&Version=").Append(ApiVersion)
                .Append("&QueueUrl=").Append(AwsSigV4.UriEncode(queueUrl));

            for (var entry = 0; batchIndex * 10 + entry < messagesJson.Count && entry < 10; entry++)
            {
                var index = batchIndex * 10 + entry;
                form.Append("&SendMessageBatchRequestEntry.")
                    .Append(index + 1).Append(".Id=").Append(index + 1)
                    .Append("&SendMessageBatchRequestEntry.")
                    .Append(index + 1).Append(".MessageBody=")
                    .Append(AwsSigV4.UriEncode(messagesJson[index]));
            }

            await PostAsync(queueUrl, queueName, form.ToString(), options);
        }
    }

    public async Task<QueueBatch?> ReceiveAsync(string queue, int maxMessages, CancellationToken ct = default)
    {
        var queueUrl = _options.ResolveQueueUrl(queue);
        var form =
            "Action=ReceiveMessage" +
            $"&Version={ApiVersion}" +
            $"&QueueUrl={AwsSigV4.UriEncode(queueUrl)}" +
            $"&MaxNumberOfMessages={Math.Clamp(maxMessages, 1, 10)}" +
            $"&WaitTimeSeconds={Math.Clamp(_options.LongPollSeconds, 0, 20)}" +
            $"&VisibilityTimeout={Math.Clamp(_options.ReceiveVisibilitySeconds, 0, 43200)}" +
            "&AttributeName.1=All";

        var response = await PostFormAsync(queueUrl, form);
        var xml = response.Body;

        var blocks = MiniXml.Blocks(xml, "Message");
        if (blocks.Count == 0)
            return null;

        var messages = new List<QueueMessage>(blocks.Count);
        foreach (var block in blocks)
        {
            var id = MiniXml.Tag(block, "MessageId");
            var body = MiniXml.Tag(block, "Body");
            var receipt = MiniXml.Tag(block, "ReceiptHandle");
            var attributes = MiniXml.Attributes(block, "Attribute");
            attributes.TryGetValue("ApproximateReceiveCount", out var receiveCount);

            if (id is null || body is null)
                continue;

            if (receipt is not null)
                _receipts[id] = receipt;

            messages.Add(new QueueMessage(
                id,
                body,
                MiniXml.Tag(block, "SentTimestamp") ?? DateTimeOffset.UtcNow.ToString("O"),
                int.TryParse(receiveCount, out var attempts) ? attempts : 1));
        }

        return messages.Count == 0
            ? null
            : new QueueBatch(Ids.New("qbatch"), queue, messages);
    }

    public async Task AckAsync(string queue, QueueBatch batch, CancellationToken ct = default)
    {
        var queueUrl = _options.ResolveQueueUrl(queue);
        var form = new StringBuilder()
            .Append("Action=DeleteMessageBatch")
            .Append("&Version=").Append(ApiVersion)
            .Append("&QueueUrl=").Append(AwsSigV4.UriEncode(queueUrl));

        var index = 0;
        foreach (var message in batch.Messages)
        {
            if (!_receipts.TryGetValue(message.Id, out var receipt))
                continue;

            _receipts.Remove(message.Id);
            _observer?.Completed(queue, message.Id);

            index++;
            form.Append("&DeleteMessageBatchRequestEntry.")
                .Append(index).Append(".Id=").Append(AwsSigV4.UriEncode(message.Id))
                .Append("&DeleteMessageBatchRequestEntry.")
                .Append(index).Append(".ReceiptHandle=").Append(AwsSigV4.UriEncode(receipt));
        }

        if (index > 0)
            await PostFormAsync(queueUrl, form.ToString());
    }

    public async Task RetryAsync(
        string queue,
        QueueBatch batch,
        IReadOnlyDictionary<string, int> retryDelaySeconds,
        CancellationToken ct = default)
    {
        var queueUrl = _options.ResolveQueueUrl(queue);
        var form = new StringBuilder()
            .Append("Action=ChangeMessageVisibilityBatch")
            .Append("&Version=").Append(ApiVersion)
            .Append("&QueueUrl=").Append(AwsSigV4.UriEncode(queueUrl));

        var index = 0;
        foreach (var message in batch.Messages)
        {
            if (!_receipts.TryGetValue(message.Id, out var receipt))
                continue;

            var delay = retryDelaySeconds.TryGetValue(message.Id, out var requested) && requested > 0
                ? requested
                : _options.ReceiveVisibilitySeconds;

            index++;
            form.Append("&ChangeMessageVisibilityBatchRequestEntry.")
                .Append(index).Append(".Id=").Append(AwsSigV4.UriEncode(message.Id))
                .Append("&ChangeMessageVisibilityBatchRequestEntry.")
                .Append(index).Append(".ReceiptHandle=").Append(AwsSigV4.UriEncode(receipt))
                .Append("&ChangeMessageVisibilityBatchRequestEntry.")
                .Append(index).Append(".VisibilityTimeout=").Append(Math.Min(delay, 43200));
        }

        if (index > 0)
            await PostFormAsync(queueUrl, form.ToString());

        foreach (var message in batch.Messages)
        {
            if (_receipts.Remove(message.Id))
                _observer?.Retried(queue, message.Id, message.Attempts,
                    retryDelaySeconds.TryGetValue(message.Id, out var d) ? d : _options.ReceiveVisibilitySeconds);
        }
    }

    private async Task PostAsync(
        string queueUrl,
        string queueName,
        string form,
        QueueSendOptions? options)
    {
        var response = await PostFormAsync(queueUrl, form);
        var messageId = MiniXml.Tag(response.Body, "MessageId");
        _observer?.Enqueued(queueName, messageId ?? Ids.New("job"), options);
    }

    private async Task<HttpResponseData> PostFormAsync(string queueUrl, string form)
    {
        var host = $"sqs.{_options.Region}.amazonaws.com";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
        };

        var authorization = AwsSigV4.Sign(
            "POST",
            "sqs",
            _options.Region,
            host,
            "/",
            "",
            headers,
            Encoding.UTF8.GetBytes(form),
            _options.AccessKey,
            _options.SecretKey,
            _options.SessionToken,
            DateTimeOffset.UtcNow);

        headers["Authorization"] = authorization;

        var response = await _http.FetchAsync($"https://{host}/", "POST", headers, form);

        if (response.Status >= 400)
        {
            var code = MiniXml.Tag(response.Body, "Code") ?? $"HTTP {response.Status}";
            var message = MiniXml.Tag(response.Body, "Message") ?? response.Body;
            throw new InvalidOperationException($"SQS request failed ({code}): {message}");
        }

        return response;
    }

    private void WarnDelay()
    {
        if (_warnedDelay)
            return;

        _warnedDelay = true;
        Console.WriteLine("WARN SQS delays are capped at 15 minutes (900s); larger delays are clamped.");
    }
}
#endif
