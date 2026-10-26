#if !NETWASM
using System.Text;

namespace Reptar;

public sealed record StorageQueueOptions(
    string Account,
    string Key,
    string ApiVersion = "2021-04-10",
    int ReceiveVisibilitySeconds = 60,
    Dictionary<string, string>? QueueNames = null)
{
    /// <summary>Azure queue names are lowercase alphanumerics + dashes.</summary>
    public string ResolveQueue(string queueName)
    {
        if (QueueNames is not null &&
            QueueNames.TryGetValue(queueName, out var mapped) &&
            mapped.Length > 0)
        {
            return mapped;
        }

        return queueName.ToLowerInvariant();
    }
}

/// <summary>
/// Azure Queue Storage adapter over the REST API with SharedKey signing —
/// no Azure SDK dependency, Native AOT safe. Enqueue delay maps to the
/// visibilitytimeout parameter (up to 7 days). Exhausted messages follow the
/// consumer's max-dequeue-count convention: Reptar's poller reports them to
/// the <see cref="IQueueObserver"/> and stops re-receiving once the storage
/// service's own dequeue accounting removes them.
/// </summary>
public sealed class AzureStorageQueueClient : IQueueClient, IQueueConsumer
{
    private readonly StorageQueueOptions _options;
    private readonly IHttpClient _http;
    private readonly IQueueObserver? _observer;
    private readonly Dictionary<string, string> _popReceipts = new(StringComparer.Ordinal);
    private bool _warnedDedup;

    public AzureStorageQueueClient(StorageQueueOptions options, IHttpClient http, IQueueObserver? observer = null)
    {
        _options = options;
        _http = http;
        _observer = observer;
    }

    public static AzureStorageQueueClient FromEnvironment(
        IHttpClient http,
        IEnvironment? environment = null,
        IQueueObserver? observer = null)
    {
        var env = environment ?? new SystemEnvironment();
        var account = env.Get("AZURE_STORAGE_ACCOUNT")
            ?? throw new InvalidOperationException("Azure Storage Queues requires AZURE_STORAGE_ACCOUNT.");

        var key = env.Get("AZURE_STORAGE_KEY")
            ?? throw new InvalidOperationException("Azure Storage Queues requires AZURE_STORAGE_KEY.");

        return new AzureStorageQueueClient(new StorageQueueOptions(account, key), http, observer);
    }

    public async Task SendAsync(string queueName, string messageJson, QueueSendOptions? options = null)
    {
        if (options?.DedupId is { Length: > 0 })
            WarnDedup();

        var delay = Math.Min(options?.DelaySeconds ?? 0, 604_800); // 7-day cap
        var path = $"/{_options.ResolveQueue(queueName)}/messages";
        var query = delay > 0 ? $"?visibilitytimeout={delay}&timeout=30" : "?timeout=30";
        var body = Encoding.UTF8.GetBytes(messageJson);

        await RequestAsync("PUT", path, query, body, "text/plain");
        _observer?.Enqueued(queueName, Ids.New("job"), options);
    }

    public async Task<QueueBatch?> ReceiveAsync(string queue, int maxMessages, CancellationToken ct = default)
    {
        var count = Math.Clamp(maxMessages, 1, 32);
        var path = $"/{_options.ResolveQueue(queue)}/messages";
        var query = $"?numofmessages={count}" +
                    $"&visibilitytimeout={Math.Clamp(_options.ReceiveVisibilitySeconds, 1, 604_800)}" +
                    $"&timeout=60";

        var response = await RequestAsync("GET", path, query, null, "text/plain");
        var blocks = MiniXml.Blocks(response.Body, "QueueMessage");
        if (blocks.Count == 0)
            return null;

        var messages = new List<QueueMessage>(blocks.Count);
        foreach (var block in blocks)
        {
            var id = MiniXml.Tag(block, "MessageId");
            var text = MiniXml.Tag(block, "MessageText");
            var popReceipt = MiniXml.Tag(block, "PopReceipt");
            var dequeueCount = MiniXml.Tag(block, "DequeueCount");
            var insertionTime = MiniXml.Tag(block, "InsertionTime");

            if (id is null || text is null)
                continue;

            if (popReceipt is not null)
                _popReceipts[id] = popReceipt;

            messages.Add(new QueueMessage(
                id,
                DecodeMessageText(text),
                insertionTime ?? DateTimeOffset.UtcNow.ToString("O"),
                int.TryParse(dequeueCount, out var attempts) ? attempts : 1));
        }

        return messages.Count == 0
            ? null
            : new QueueBatch(Ids.New("qbatch"), queue, messages);
    }

    public async Task AckAsync(string queue, QueueBatch batch, CancellationToken ct = default)
    {
        var pathPrefix = $"/{_options.ResolveQueue(queue)}/messages";
        foreach (var message in batch.Messages)
        {
            if (!_popReceipts.TryGetValue(message.Id, out var popReceipt))
                continue;

            _popReceipts.Remove(message.Id);
            _observer?.Completed(queue, message.Id);

            await RequestAsync(
                "DELETE",
                pathPrefix,
                $"?popreceipt={Uri.EscapeDataString(popReceipt)}&timeout=30",
                null,
                "text/plain");
        }
    }

    public async Task RetryAsync(
        string queue,
        QueueBatch batch,
        IReadOnlyDictionary<string, int> retryDelaySeconds,
        CancellationToken ct = default)
    {
        var pathPrefix = $"/{_options.ResolveQueue(queue)}/messages";
        foreach (var message in batch.Messages)
        {
            if (!_popReceipts.TryGetValue(message.Id, out var popReceipt))
                continue;

            var delay = retryDelaySeconds.TryGetValue(message.Id, out var requested) && requested > 0
                ? requested
                : _options.ReceiveVisibilitySeconds;

            await RequestAsync(
                "PUT",
                pathPrefix,
                $"?popreceipt={Uri.EscapeDataString(popReceipt)}" +
                $"&visibilitytimeout={Math.Min(delay, 604_800)}&timeout=30",
                [],
                "text/plain");

            _popReceipts.Remove(message.Id);
            _observer?.Retried(queue, message.Id, message.Attempts, delay);
        }
    }

    private async Task<HttpResponseData> RequestAsync(
        string method,
        string path,
        string query,
        byte[]? body,
        string contentType)
    {
        var host = $"{_options.Account}.queue.core.windows.net";
        var date = Rfc1123Utc(DateTimeOffset.UtcNow);

        // SharedKey string-to-sign, 2015-02-21 and later.
        var canonicalizedHeaders = $"x-ms-date:{date}\nx-ms-version:{_options.ApiVersion}\n";
        var canonicalizedResource = $"/{_options.Account}{path}\n{CanonicalQuery(query)}";

        var stringToSign =
            $"{method}\n" +          // VERB
            "\n" +                  // Content-Encoding
            "\n" +                  // Content-Language
            $"{(body is { Length: > 0 } ? body.Length.ToString() : "")}\n" + // Content-Length
            "\n" +                  // Content-MD5
            $"{contentType}\n" +     // Content-Type
            "\n" +                  // Date (x-ms-date carries it)
            "\n" +                  // If-Modified-Since
            "\n" +                  // If-Match
            "\n" +                  // If-None-Match
            "\n" +                  // If-Unmodified-Since
            "\n" +                  // Range
            $"{canonicalizedHeaders}{canonicalizedResource}";

        var signature = QueueCrypto.Base64(
            QueueCrypto.Hmac(QueueCrypto.Base64Decode(_options.Key), QueueCrypto.Utf8(stringToSign)));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"SharedKey {_options.Account}:{signature}",
            ["Content-Type"] = contentType,
            ["x-ms-date"] = date,
            ["x-ms-version"] = _options.ApiVersion,
        };

        // Raw UTF-8 body (Put Message without the QueueMessage wrapper, API
        // version 2011-08-18+): Get Messages returns it base64-encoded and
        // DecodeMessageText unwraps it.
        var payload = body is { Length: > 0 }
            ? Encoding.UTF8.GetString(body)
            : "";
        var response = await _http.FetchAsync($"https://{host}{path}{query}", method, headers, payload);

        if (response.Status >= 400)
        {
            var code = MiniXml.Tag(response.Body, "Code") ?? $"HTTP {response.Status}";
            var message = MiniXml.Tag(response.Body, "Message") ?? response.Body;
            throw new InvalidOperationException($"Azure Storage Queues request failed ({code}): {message}");
        }

        return response;
    }

    private static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '?')
            return "";

        var parameters = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? Uri.UnescapeDataString(pair) : Uri.UnescapeDataString(pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            (parameters.TryGetValue(name, out var values) ? values : parameters[name] = []).Add(value);
        }

        var builder = new StringBuilder();
        foreach (var (name, values) in parameters)
        {
            builder.Append('\n').Append(name).Append(':').Append(string.Join(",", values));
        }

        return builder.ToString();
    }

    private static string DecodeMessageText(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>
    /// Hand-formatted RFC1123 UTC: deterministic on every target, avoiding
    /// the DateTimeOffset formatting gaps recorded in design.md.
    /// </summary>
    private static string Rfc1123Utc(DateTimeOffset at)
    {
        const string Days = "Sun,Mon,Tue,Wed,Thu,Fri,Sat";
        const string Months = "Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec";

        var day = Days.Split(',')[(int)at.DayOfWeek];
        var month = Months.Split(' ')[at.Month - 1];

        return $"{day}, {at.Day:00} {month} {at.Year:0000} " +
               $"{at.Hour:00}:{at.Minute:00}:{at.Second:00} GMT";
    }

    private void WarnDedup()
    {
        if (_warnedDedup)
            return;

        _warnedDedup = true;
        Console.WriteLine("WARN Azure Storage Queues has no native deduplication; DedupId is ignored.");
    }
}
#endif
