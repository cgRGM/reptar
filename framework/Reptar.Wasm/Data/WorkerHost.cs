using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reptar;

/// <summary>
/// The request envelope shared by every NetWasm Worker entrypoint. Keeping it
/// in the framework means apps only provide <see cref="WorkerEntrypoint"/> glue
/// and their own JSON context; they do not hand-write host plumbing.
/// </summary>
public sealed record HostRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body,
    string RequestId);

public sealed record HostResponse(
    int Status,
    Dictionary<string, string> Headers,
    string Body,
    string[] SetCookies);

public sealed record HostQueueMessage(
    string Id,
    string Body,
    string Timestamp,
    int Attempts);

public sealed record HostQueueBatch(
    string BatchId,
    string Queue,
    List<HostQueueMessage> Messages);

public sealed record HostQueueRetry(string MessageId, int DelaySeconds);

public sealed record HostQueueResult(
    bool Success,
    string[] FailedMessageIds,
    List<HostQueueRetry>? Retries = null);

public sealed record HostScheduledEvent(
    string Cron,
    string ScheduleId,
    string ScheduledTime);

public sealed record HostScheduledResult(
    bool Success,
    string[] Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HostQueueBatch))]
[JsonSerializable(typeof(HostQueueMessage))]
[JsonSerializable(typeof(List<HostQueueMessage>))]
[JsonSerializable(typeof(HostQueueResult))]
[JsonSerializable(typeof(HostQueueRetry))]
[JsonSerializable(typeof(List<HostQueueRetry>))]
[JsonSerializable(typeof(HostScheduledEvent))]
[JsonSerializable(typeof(HostScheduledResult))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = WorkerJsonContext.Default,
        };
}

/// <summary>
/// Safe, source-generated bridge from the Worker host into a <see cref="ReptarApp"/>.
/// The host calls the exported <c>handle(string)</c>; this helper guarantees
/// that exactly one response is sent, malformed envelopes become 400s, and
/// unhandled exceptions become 500s instead of a permanently pending Promise.
/// </summary>
public static class WorkerEntrypoint
{
    private const string InternalErrorBody =
        "{\"code\":\"internal_error\",\"message\":\"Something went wrong.\"}";

    public static async Task HandleAsync(
        ReptarApp app,
        string requestJson,
        Action<string, string> respond)
    {
        var responded = false;

        void RespondOnce(string requestId, HostResponse response)
        {
            if (responded)
                return;

            responded = true;
            respond(requestId, JsonSerializer.Serialize(response, WorkerJson.Options));
        }

        HostRequest? request = null;

        try
        {
            request = JsonSerializer.Deserialize<HostRequest>(
                requestJson,
                WorkerJson.Options);
        }
        catch (JsonException)
        {
            // Fall through to the 400 response below.
        }

        if (request is null)
        {
            RespondOnce(
                ExtractRequestId(requestJson),
                new HostResponse(400, [], EncodeBody("{}"), []));
            return;
        }

        try
        {
            var body = string.IsNullOrEmpty(request.Body)
                ? []
                : Convert.FromBase64String(request.Body);

            var result = await app.HandleWithBackgroundAsync(new ReptarRequest
            {
                Method = request.Method,
                Path = request.Path,
                Query = request.Query.TrimStart('?'),
                RequestId = request.RequestId,
                Headers = request.Headers,
                Cookies = ParseCookies(request.Headers),
                Body = body,
                BodyStream = new MemoryStream(body, writable: false),
            });

            RespondOnce(
                request.RequestId,
                ToHostResponse(result.Response));

            try
            {
                await result.Background;
            }
            catch (Exception exception)
            {
                // The response is already on the wire. Surface the failure in
                // logs; the host's waitUntil sees the rejected export.
                Console.WriteLine($"ERROR background: {exception}");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ERROR handler: {exception}");
            RespondOnce(
                request.RequestId,
                new HostResponse(500, [], EncodeBody(InternalErrorBody), []));
        }
    }

    /// <summary>
    /// Queue consumer entrypoint: the host's <c>queue()</c> handler passes
    /// <paramref name="batchJson"/> (batchId, queue, messages), and the app's
    /// <see cref="ReptarApp.HandleQueueBatchAsync"/> result flows back through
    /// <paramref name="respondQueue"/> — acks, retries, and RetryIn delays map
    /// onto the Workers queue primitives in the host.
    /// </summary>
    public static async Task HandleQueueAsync(
        ReptarApp app,
        string batchJson,
        Action<string, string> respondQueue)
    {
        var responded = false;

        void RespondOnce(string batchId, HostQueueResult result)
        {
            if (responded)
                return;

            responded = true;
            respondQueue(
                batchId,
                JsonSerializer.Serialize(result, WorkerJson.Options));
        }

        HostQueueBatch? batch = null;
        try
        {
            batch = JsonSerializer.Deserialize<HostQueueBatch>(
                batchJson,
                WorkerJson.Options);
        }
        catch (JsonException)
        {
            // Fall through to the all-failed response below.
        }

        if (batch is null)
        {
            RespondOnce("", new HostQueueResult(false, [], null));
            return;
        }

        try
        {
            var messages = new List<QueueMessage>(batch.Messages.Count);
            foreach (var message in batch.Messages)
            {
                messages.Add(new QueueMessage(
                    message.Id,
                    message.Body,
                    message.Timestamp,
                    message.Attempts));
            }

            var result = await app.HandleQueueBatchAsync(
                new QueueBatch(batch.BatchId, batch.Queue, messages));

            List<HostQueueRetry>? retries = null;
            if (result.Retries is { Count: > 0 })
            {
                retries = new List<HostQueueRetry>(result.Retries.Count);
                foreach (var retry in result.Retries)
                    retries.Add(new HostQueueRetry(retry.MessageId, retry.DelaySeconds));
            }

            RespondOnce(
                batch.BatchId,
                new HostQueueResult(result.Success, result.FailedMessageIds, retries));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ERROR queue: {exception}");
            // Report every message as failed so the host retries the batch.
            var all = new List<string>(batch.Messages.Count);
            foreach (var message in batch.Messages)
                all.Add(message.Id);

            RespondOnce(batch.BatchId, new HostQueueResult(false, all.ToArray(), null));
        }
    }

    /// <summary>
    /// Cron entrypoint: the host's <c>scheduled()</c> handler passes
    /// <paramref name="scheduledJson"/> (cron, scheduleId, scheduledTime) and
    /// resolves its held Promise through <paramref name="respondScheduled"/>.
    /// </summary>
    public static async Task HandleScheduledAsync(
        ReptarApp app,
        string scheduledJson,
        Action<string, string> respondScheduled)
    {
        var responded = false;

        void RespondOnce(string scheduleId, HostScheduledResult result)
        {
            if (responded)
                return;

            responded = true;
            respondScheduled(
                scheduleId,
                JsonSerializer.Serialize(result, WorkerJson.Options));
        }

        HostScheduledEvent? scheduled = null;
        try
        {
            scheduled = JsonSerializer.Deserialize<HostScheduledEvent>(
                scheduledJson,
                WorkerJson.Options);
        }
        catch (JsonException)
        {
            // Fall through to the failed response below.
        }

        if (scheduled is null)
        {
            RespondOnce("", new HostScheduledResult(false, ["Invalid scheduled envelope."]));
            return;
        }

        try
        {
            var result = await app.HandleScheduledAsync(
                new ScheduledEvent(scheduled.Cron, scheduled.ScheduledTime));

            RespondOnce(scheduled.ScheduleId, new HostScheduledResult(result.Success, result.Errors));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ERROR scheduled: {exception}");
            RespondOnce(scheduled.ScheduleId, new HostScheduledResult(false, [exception.Message]));
        }
    }

    private static HostResponse ToHostResponse(ReptarResponse response)
    {
        var headers = new Dictionary<string, string>(response.Headers);

        if (response.ContentType is not null)
            headers["content-type"] = response.ContentType;

        return new HostResponse(
            response.Status,
            headers,
            response.Body is null ? "" : Convert.ToBase64String(response.Body),
            response.SetCookies.ToArray());
    }

    private static string EncodeBody(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static string ExtractRequestId(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);

            return document.RootElement.TryGetProperty(
                "requestId",
                out var requestId)
                ? requestId.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static Dictionary<string, string> ParseCookies(
        Dictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue("cookie", out var header))
            return cookies;

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
                continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (name.Length > 0)
                cookies[name] = value;
        }

        return cookies;
    }
}
