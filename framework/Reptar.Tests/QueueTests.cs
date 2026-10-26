using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reptar;
using Reptar.Hosting.Lambda;

namespace Reptar.Tests;

public class QueuePortTests
{
    private sealed record DemoPayload(string Message, int Priority);

    [Fact]
    public async Task Default_queue_on_native_dispatches_inline()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var received = new List<string>();

        app.OnQueue("jobs", (ctx, msg) =>
        {
            received.Add(msg.Body);
            return Task.CompletedTask;
        });

        await app.Queues.SendAsync("jobs", "do work");

        var message = Assert.Single(received);
        Assert.Equal("do work", message);
    }

    [Fact]
    public async Task Default_queue_without_handler_completes_silently()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });

        // No OnQueue("jobs") registration: the sync adapter must not throw,
        // it reports the missing consumer through the batch result.
        await app.Queues.SendAsync("jobs", "nobody home");
    }

    [Fact]
    public async Task RetryIn_captures_per_message_delay()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });

        app.OnQueue("jobs", (ctx, msg) =>
        {
            if (msg.Id == "snooze")
                throw new RetryIn(90);

            return Task.CompletedTask;
        });

        var result = await app.HandleQueueBatchAsync(new QueueBatch("b1", "jobs", [
            new QueueMessage("ok", "fine", DateTimeOffset.UtcNow.ToString("O"), 1),
            new QueueMessage("snooze", "later", DateTimeOffset.UtcNow.ToString("O"), 1),
        ]));

        Assert.False(result.Success);
        Assert.Equal(["snooze"], result.FailedMessageIds);

        var retry = Assert.Single(result.Retries);
        Assert.Equal("snooze", retry.MessageId);
        Assert.Equal(90, retry.DelaySeconds);
    }

    [Fact]
    public async Task Queue_batch_result_serializes_camel_case_for_host_bridge()
    {
        var result = new QueueBatchResult(false, ["m1"], [new QueueRetry("m1", 30)]);
        var json = JsonSerializer.Serialize(result, ReptarJson.Options);
        var node = JsonNode.Parse(json);

        Assert.False(node?["success"]?.GetValue<bool>());
        Assert.Equal("m1", node?["failedMessageIds"]?[0]?.GetValue<string>());
        Assert.Equal("m1", node?["retries"]?[0]?["messageId"]?.GetValue<string>());
        Assert.Equal(30, node?["retries"]?[0]?["delaySeconds"]?.GetValue<int>());
    }

    [Fact]
    public async Task Fake_queue_records_and_drains_through_real_pipeline()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var received = new List<string>();
        app.OnQueue("jobs", (ctx, msg) =>
        {
            received.Add(msg.Body);
            return Task.CompletedTask;
        });

        var fake = new FakeQueueClient(app);
        await fake.SendAsync("jobs", "one");
        await fake.SendAsync("jobs", "two", new QueueSendOptions(DelaySeconds: 5));

        Assert.Equal(2, fake.Recorded.Count);
        Assert.Empty(received);

        await fake.DrainAsync();

        Assert.Empty(fake.Recorded);
        Assert.Equal(["one", "two"], received);
    }

    [Fact]
    public async Task Fake_queue_without_app_throws_on_drain()
    {
        var fake = new FakeQueueClient();
        await fake.SendAsync("jobs", "x");

        await Assert.ThrowsAsync<InvalidOperationException>(fake.DrainAsync);
    }
}

public class CronExprTests
{
    [Theory]
    [InlineData("*", true)]
    [InlineData("*/5 * * * *", true)]   // minute 0
    public void Matches_basics(string expression, bool expected)
    {
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, CronExpr.Matches(expression, at));
    }

    [Fact]
    public void Step_expression_misses_off_step_minutes()
    {
        var at = new DateTimeOffset(2026, 10, 26, 9, 3, 0, TimeSpan.Zero);
        Assert.False(CronExpr.Matches("*/5 * * * *", at));
    }

    [Fact]
    public void Weekday_window_fires_only_on_weekdays_at_hour()
    {
        var fridayNoon = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var saturdayNoon = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var friday1201 = new DateTimeOffset(2026, 10, 23, 12, 1, 0, TimeSpan.Zero);

        Assert.True(CronExpr.Matches("0 12 * * 1-5", fridayNoon));
        Assert.False(CronExpr.Matches("0 12 * * 1-5", saturdayNoon));
        Assert.False(CronExpr.Matches("0 12 * * 1-5", friday1201));
    }

    [Fact]
    public void Sunday_accepts_both_zero_and_seven()
    {
        var sunday = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

        Assert.True(CronExpr.Matches("0 8 * * 0", sunday));
        Assert.True(CronExpr.Matches("0 8 * * 7", sunday));
    }

    [Fact]
    public void Day_dom_dow_or_rule_when_both_restricted()
    {
        // "0 0 1 * 1": midnight on the 1st OR any Monday.
        var mondayTheFifth = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var tuesdayTheSixth = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        Assert.True(CronExpr.Matches("0 0 1 * 1", mondayTheFifth));
        Assert.False(CronExpr.Matches("0 0 1 * 1", tuesdayTheSixth));
    }

    [Fact]
    public void Lists_match_any_entry()
    {
        var fifteenth = new DateTimeOffset(2026, 10, 15, 4, 30, 0, TimeSpan.Zero);
        var sixteenth = new DateTimeOffset(2026, 10, 16, 4, 30, 0, TimeSpan.Zero);

        Assert.True(CronExpr.Matches("30 4 1,15 * *", fifteenth));
        Assert.False(CronExpr.Matches("30 4 1,15 * *", sixteenth));
    }

    [Fact]
    public void Six_field_expression_matches_seconds()
    {
        var atTenSeconds = new DateTimeOffset(2026, 10, 26, 9, 0, 10, TimeSpan.Zero);
        var atSevenSeconds = new DateTimeOffset(2026, 10, 26, 9, 0, 7, TimeSpan.Zero);

        Assert.True(CronExpr.Matches("*/10 * * * * *", atTenSeconds));
        Assert.False(CronExpr.Matches("*/10 * * * * *", atSevenSeconds));
        Assert.True(CronExpr.HasSeconds("*/10 * * * * *"));
        Assert.False(CronExpr.HasSeconds("*/5 * * * *"));
    }
}

public class DbQueueTests
{
    private static DbQueueClient CreateQueue(IDbClient db, DbQueueOptions? options = null) =>
        new(db, options);

    [Fact]
    public async Task Enqueue_receive_ack_completes_the_lifecycle()
    {
        var db = Db.Sqlite(":memory:");
        var queue = CreateQueue(db);
        await queue.EnsureCreatedAsync();

        await queue.SendAsync("orders", "{\"id\":1}");
        await queue.SendAsync("orders", "{\"id\":2}");

        var batch = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(batch);
        Assert.Equal(2, batch.Messages.Count);
        Assert.Equal(1, batch.Messages[0].Attempts);
        Assert.Equal("{\"id\":1}", batch.Messages[0].Body);

        await queue.AckAsync("orders", batch);

        Assert.Null(await queue.ReceiveAsync("orders", 10));
    }

    [Fact]
    public async Task Failed_message_retries_with_backoff_and_dead_letters()
    {
        var db = Db.Sqlite(":memory:");
        var queue = CreateQueue(db, new DbQueueOptions { MaxAttempts = 2, BaseBackoffSeconds = 0 });
        await queue.EnsureCreatedAsync();

        await queue.SendAsync("orders", "boom");

        var first = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(first);

        await queue.RetryAsync("orders", first, new Dictionary<string, int>());

        // Zero backoff: the row is due again immediately with attempts bumped.
        var second = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(second);
        Assert.Equal(2, second.Messages[0].Attempts);

        // Second failure exhausts MaxAttempts=2: dead, never received again.
        await queue.RetryAsync("orders", second, new Dictionary<string, int>());
        Assert.Null(await queue.ReceiveAsync("orders", 10));
    }

    [Fact]
    public async Task RetryIn_delay_postpones_visibility()
    {
        var db = Db.Sqlite(":memory:");
        var queue = CreateQueue(db, new DbQueueOptions { BaseBackoffSeconds = 0 });
        await queue.EnsureCreatedAsync();

        await queue.SendAsync("orders", "snooze");
        var batch = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(batch);

        await queue.RetryAsync("orders", batch, new Dictionary<string, int>
        {
            [batch.Messages[0].Id] = 60,
        });

        // 60s delay: not due yet.
        Assert.Null(await queue.ReceiveAsync("orders", 10));
    }

    [Fact]
    public async Task Send_delay_hides_message_until_due()
    {
        var db = Db.Sqlite(":memory:");
        var queue = CreateQueue(db);
        await queue.EnsureCreatedAsync();

        await queue.SendAsync("orders", "later", new QueueSendOptions(DelaySeconds: 3600));
        Assert.Null(await queue.ReceiveAsync("orders", 10));

        await queue.SendAsync("orders", "now");
        var batch = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(batch);
        Assert.Equal("now", batch.Messages[0].Body);
    }

    [Fact]
    public async Task Dedup_skips_second_pending_send()
    {
        var db = Db.Sqlite(":memory:");
        var queue = CreateQueue(db);
        await queue.EnsureCreatedAsync();

        await queue.SendAsync("orders", "{\"n\":1}", new QueueSendOptions(DedupId: "order-1"));
        await queue.SendAsync("orders", "{\"n\":1}", new QueueSendOptions(DedupId: "order-1"));

        var batch = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(batch);
        Assert.Single(batch.Messages);

        await queue.AckAsync("orders", batch);

        // Completed: the dedup window no longer applies.
        await queue.SendAsync("orders", "{\"n\":1}", new QueueSendOptions(DedupId: "order-1"));
        var again = await queue.ReceiveAsync("orders", 10);
        Assert.NotNull(again);
        Assert.Single(again.Messages);
    }
}

public class AwsSigV4Tests
{
    /// <summary>
    /// The complete published AWS walkthrough: GET iam ListUsers with the
    /// example credentials at 20150830T123600Z. If the final signature
    /// matches, every upstream step (canonical request, string-to-sign, key
    /// derivation) matched exactly.
    /// </summary>
    [Fact]
    public void Produces_the_published_aws_signature()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded; charset=utf-8",
        };

        var authorization = AwsSigV4.Sign(
            "GET",
            "iam",
            "us-east-1",
            "iam.amazonaws.com",
            "/",
            "Action=ListUsers&Version=2010-05-08",
            headers,
            [],
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            sessionToken: null,
            new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));

        Assert.Equal("20150830T123600Z", headers["x-amz-date"]);
        Assert.Contains("SignedHeaders=content-type;host;x-amz-date", authorization);
        Assert.Contains(
            "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
            authorization);
    }

    [Fact]
    public void Signing_key_matches_the_published_derivation()
    {
        var key = AwsSigV4.SigningKey(
            "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            "20150830",
            "us-east-1",
            "iam");

        Assert.Equal(
            "c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9",
            QueueCrypto.Hex(key));
    }

    [Fact]
    public void Uri_encode_follows_the_aws_profile()
    {
        Assert.Equal("a%20b%2Fc%2Bd~", AwsSigV4.UriEncode("a b/c+d~"));
    }
}

public class SqsAdapterTests
{
    private sealed class RecordingHttpClient : IHttpClient
    {
        public List<(string Url, string Method, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];
        public Func<string, (int Status, string Body)> Respond { get; set; } = _ => (200, "<SendMessageResponse></SendMessageResponse>");

        public Task<HttpResponseData> FetchAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            string body = "")
        {
            Requests.Add((url, method, new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase), body));
            var (status, responseBody) = Respond(url);
            return Task.FromResult(new HttpResponseData(
                status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                responseBody));
        }

        public Task<HttpBytesResponse> FetchBytesAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            byte[]? body = null,
            string? contentType = null) => throw new NotSupportedException();
    }

    private static SqsQueueClient Create(RecordingHttpClient http) =>
        new(
            new SqsOptions(
                "https://sqs.us-east-1.amazonaws.com/123456789/orders",
                "us-east-1",
                "AKIDEXAMPLE",
                "secret"),
            http);

    [Fact]
    public async Task Send_builds_signed_form_request_with_options()
    {
        var http = new RecordingHttpClient();
        var client = Create(http);

        await client.SendAsync("orders", "{\"n\":1}", new QueueSendOptions(DelaySeconds: 30, DedupId: "order-1"));

        var (url, method, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/", url);
        Assert.Equal("POST", method);
        Assert.StartsWith("Action=SendMessage&Version=2012-11-05&QueueUrl=", body);
        Assert.Contains("&DelaySeconds=30", body);
        Assert.Contains("&MessageDeduplicationId=order-1", body);
        Assert.Equal("application/x-www-form-urlencoded", headers["Content-Type"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", headers["Authorization"]);
        Assert.Contains("SignedHeaders=content-type;host;x-amz-date", headers["Authorization"]);
    }

    [Fact]
    public async Task Receive_parses_messages_and_attempts()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (200, """
                <ReceiveMessageResponse><ReceiveMessageResult>
                  <Message>
                    <MessageId>msg-1</MessageId>
                    <ReceiptHandle>rh-1</ReceiptHandle>
                    <Body>{"job":"sync"}</Body>
                    <Attribute><Name>ApproximateReceiveCount</Name><Value>3</Value></Attribute>
                  </Message>
                </ReceiveMessageResult></ReceiveMessageResponse>
                """),
        };
        var client = Create(http);

        var batch = await client.ReceiveAsync("orders", 10);

        Assert.NotNull(batch);
        var message = Assert.Single(batch.Messages);
        Assert.Equal("msg-1", message.Id);
        Assert.Equal("{\"job\":\"sync\"}", message.Body);
        Assert.Equal(3, message.Attempts);
    }

    [Fact]
    public async Task Ack_sends_delete_batch_with_receipt_handles()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (200, "<ReceiveMessageResponse><ReceiveMessageResult><Message><MessageId>msg-1</MessageId><ReceiptHandle>rh-1</ReceiptHandle><Body>x</Body></Message></ReceiveMessageResult></ReceiveMessageResponse>"),
        };
        var client = Create(http);
        var batch = await client.ReceiveAsync("orders", 10);
        http.Requests.Clear();

        await client.AckAsync("orders", batch!);

        var (_, method, _, body) = Assert.Single(http.Requests);
        Assert.Equal("POST", method);
        Assert.StartsWith("Action=DeleteMessageBatch", body);
        Assert.Contains("ReceiptHandle=rh-1", body);
    }

    [Fact]
    public async Task Retry_extends_visibility_with_requested_delay()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (200, "<ReceiveMessageResponse><ReceiveMessageResult><Message><MessageId>msg-1</MessageId><ReceiptHandle>rh-1</ReceiptHandle><Body>x</Body></Message></ReceiveMessageResult></ReceiveMessageResponse>"),
        };
        var client = Create(http);
        var batch = await client.ReceiveAsync("orders", 10);
        http.Requests.Clear();

        await client.RetryAsync("orders", batch!, new Dictionary<string, int> { ["msg-1"] = 45 });

        var (_, _, _, body) = Assert.Single(http.Requests);
        Assert.StartsWith("Action=ChangeMessageVisibilityBatch", body);
        Assert.Contains("VisibilityTimeout=45", body);
    }

    [Fact]
    public async Task Provider_error_surfaces_as_exception()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (403, "<ErrorResponse><Error><Code>InvalidClientTokenId</Code><Message>bad key</Message></Error></ErrorResponse>"),
        };
        var client = Create(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync("orders", "{}"));

        Assert.Contains("InvalidClientTokenId", error.Message);
    }
}

public class AzureStorageQueueAdapterTests
{
    private sealed class RecordingHttpClient : IHttpClient
    {
        public List<(string Url, string Method, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];
        public Func<string, (int Status, string Body)> Respond { get; set; } = _ => (201, "");

        public Task<HttpResponseData> FetchAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            string body = "")
        {
            Requests.Add((url, method, new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase), body));
            var (status, responseBody) = Respond(url);
            return Task.FromResult(new HttpResponseData(
                status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                responseBody));
        }

        public Task<HttpBytesResponse> FetchBytesAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            byte[]? body = null,
            string? contentType = null) => throw new NotSupportedException();
    }

    private static AzureStorageQueueClient Create(RecordingHttpClient http) =>
        new(new StorageQueueOptions("acct1", "a2V5"), http);

    [Fact]
    public async Task Send_puts_delay_into_visibilitytimeout()
    {
        var http = new RecordingHttpClient();
        var client = Create(http);

        await client.SendAsync("Notifications", "{\"n\":1}", new QueueSendOptions(DelaySeconds: 30));

        var (url, method, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://acct1.queue.core.windows.net/notifications/messages?visibilitytimeout=30&timeout=30", url);
        Assert.Equal("PUT", method);
        Assert.Equal("{\"n\":1}", body);
        Assert.StartsWith("SharedKey acct1:", headers["Authorization"]);
        Assert.Contains("GMT", headers["x-ms-date"]);
    }

    [Fact]
    public async Task Receive_decodes_base64_message_text()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (200, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(
                "<QueueMessagesList><QueueMessage><MessageId>m-1</MessageId><MessageText>" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"job\":\"ok\"}")) +
                "</MessageText><PopReceipt>pop-1</PopReceipt><DequeueCount>2</DequeueCount><InsertionTime>Mon, 26 Oct 2026 00:00:00 GMT</InsertionTime></QueueMessage></QueueMessagesList>"))),
        };
        var client = Create(http);

        var batch = await client.ReceiveAsync("Notifications", 10);

        Assert.NotNull(batch);
        var message = Assert.Single(batch.Messages);
        Assert.Equal("m-1", message.Id);
        Assert.Equal("{\"job\":\"ok\"}", message.Body);
        Assert.Equal(2, message.Attempts);

        http.Requests.Clear();
        await client.AckAsync("Notifications", batch);
        var (ackUrl, ackMethod, _, ackBody) = Assert.Single(http.Requests);
        Assert.Equal("DELETE", ackMethod);
        Assert.Contains("popreceipt=pop-1", ackUrl);
        Assert.Equal("", ackBody);
    }

    [Fact]
    public async Task Retry_updates_visibility_with_pop_receipt()
    {
        var http = new RecordingHttpClient
        {
            Respond = _ => (200, "<QueueMessagesList><QueueMessage><MessageId>m-1</MessageId><MessageText>e30=</MessageText><PopReceipt>pop-1</PopReceipt><DequeueCount>1</DequeueCount></QueueMessage></QueueMessagesList>"),
        };
        var client = Create(http);
        var batch = await client.ReceiveAsync("Notifications", 10);
        http.Requests.Clear();

        await client.RetryAsync("Notifications", batch!, new Dictionary<string, int> { ["m-1"] = 120 });

        var (url, method, _, body) = Assert.Single(http.Requests);
        Assert.Equal("PUT", method);
        Assert.Contains("popreceipt=pop-1", url);
        Assert.Contains("visibilitytimeout=120", url);
        Assert.Equal("", body);
    }
}

public class PubSubAdapterTests
{
    private sealed class RecordingHttpClient : IHttpClient
    {
        public List<(string Url, string Method, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];
        public Func<string, (int Status, string Body)> Respond { get; set; } = _ => (200, "{\"messageIds\":[\"pm-1\"]}");

        public Task<HttpResponseData> FetchAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            string body = "")
        {
            Requests.Add((url, method, new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase), body));
            var (status, responseBody) = Respond(url);
            return Task.FromResult(new HttpResponseData(
                status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                responseBody));
        }

        public Task<HttpBytesResponse> FetchBytesAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            byte[]? body = null,
            string? contentType = null) => throw new NotSupportedException();
    }

    private static PubSubQueueClient Create(RecordingHttpClient http) =>
        new(
            new PubSubOptions(
                "proj-1",
                "orders-topic",
                "orders-sub",
                _ => Task.FromResult("test-token")),
            http);

    [Fact]
    public async Task Send_publishes_base64_data_with_bearer_token()
    {
        var http = new RecordingHttpClient();
        var client = Create(http);

        await client.SendAsync("orders", "{\"n\":1}");

        var (url, method, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://pubsub.googleapis.com/v1/projects/proj-1/topics/orders-topic:publish", url);
        Assert.Equal("POST", method);
        Assert.Equal("Bearer test-token", headers["Authorization"]);

        var published = JsonNode.Parse(body);
        var data = published?["messages"]?[0]?["data"]?.GetValue<string>();
        Assert.Equal("{\"n\":1}", Encoding.UTF8.GetString(Convert.FromBase64String(data ?? "")));
    }

    [Fact]
    public async Task Receive_pulls_acks_and_modifies_deadline()
    {
        var pullBody = "{\"receivedMessages\":[{\"ackId\":\"ack-1\",\"message\":{\"messageId\":\"pm-1\",\"data\":\"" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"job\":\"ok\"}")) +
            "\",\"publishTime\":\"2026-10-26T00:00:00Z\"}}]}";
        var http = new RecordingHttpClient { Respond = url => url.EndsWith(":pull") ? (200, pullBody) : (200, "{}") };
        var client = Create(http);

        var batch = await client.ReceiveAsync("orders", 10);
        Assert.NotNull(batch);
        Assert.Equal("pm-1", batch.Messages[0].Id);
        Assert.Equal("{\"job\":\"ok\"}", batch.Messages[0].Body);
        http.Requests.Clear();

        await client.RetryAsync("orders", batch!, new Dictionary<string, int> { ["pm-1"] = 45 });

        var (_, _, _, body) = Assert.Single(http.Requests);
        Assert.EndsWith(":modifyAckDeadline", http.Requests[0].Url);
        Assert.Contains("\"ackDeadlineSeconds\":45", body);
        Assert.Contains("ack-1", body);
    }
}

public class AlarmTests
{
    [Fact]
    public async Task OnAlarm_dispatches_through_the_reserved_path()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var ran = 0;
        app.OnAlarm(ctx =>
        {
            ran++;
            return Task.CompletedTask;
        });

        var response = await app.HandleAsync(
            new ReptarRequest { Method = "POST", Path = "/_reptar/alarm" });

        Assert.Equal(200, response.Status);
        Assert.Equal(1, ran);

        // The reserved path is an internal dispatch, not a route: it must
        // stay out of RouteMetadata / OpenAPI / generated contracts.
        Assert.DoesNotContain(app.RouteMetadata, meta => meta.Path == "/_reptar/alarm");
    }

    [Fact]
    public async Task Alarm_failure_returns_500_and_the_app_stays_up()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        app.OnAlarm(_ => throw new InvalidOperationException("boom"));

        var response = await app.HandleAsync(
            new ReptarRequest { Method = "POST", Path = "/_reptar/alarm" });
        Assert.Equal(500, response.Status);

        // Subsequent traffic is unaffected.
        var after = await app.HandleAsync(
            new ReptarRequest { Method = "GET", Path = "/definitely-not-registered" });
        Assert.Equal(404, after.Status);
    }

    [Fact]
    public async Task Without_a_handler_the_reserved_path_is_a_normal_404()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });

        var response = await app.HandleAsync(
            new ReptarRequest { Method = "POST", Path = "/_reptar/alarm" });

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Alarm_context_gets_background_task_draining()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var done = new TaskCompletionSource();
        app.OnAlarm(ctx =>
        {
            ctx.WaitUntil(() =>
            {
                done.TrySetResult();
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });

        var response = await app.HandleAsync(
            new ReptarRequest { Method = "POST", Path = "/_reptar/alarm" });

        Assert.Equal(200, response.Status);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class LambdaSqsTests
{
    private const string SqsEvent = """
        {
          "Records": [
            {
              "messageId": "m-1",
              "body": "{\"job\":\"a\"}",
              "eventSource": "aws:sqs",
              "eventSourceARN": "arn:aws:sqs:us-east-1:123456789:orders",
              "attributes": { "ApproximateReceiveCount": "2", "SentTimestamp": "1729900000000" }
            },
            {
              "messageId": "m-2",
              "body": "{\"job\":\"b\"}",
              "eventSource": "aws:sqs",
              "eventSourceARN": "arn:aws:sqs:us-east-1:123456789:orders",
              "attributes": { "ApproximateReceiveCount": "1" }
            }
          ]
        }
        """;

    [Fact]
    public void Parses_sqs_events_with_queue_name_and_attempts()
    {
        var parsed = LambdaHost.TryParseSqsEvent(SqsEvent, out var batch);

        Assert.True(parsed);
        Assert.Equal("orders", batch.Queue);
        Assert.Equal(2, batch.Messages.Count);
        Assert.Equal("m-1", batch.Messages[0].Id);
        Assert.Equal(2, batch.Messages[0].Attempts);
        Assert.Equal(1, batch.Messages[1].Attempts);
    }

    [Fact]
    public void Ignores_non_sqs_events()
    {
        Assert.False(LambdaHost.TryParseSqsEvent("{\"rawPath\":\"/\",\"requestContext\":{}}", out _));
        Assert.False(LambdaHost.TryParseSqsEvent("{}", out _));
    }

    [Fact]
    public void Formats_partial_batch_failures()
    {
        var success = LambdaHost.FormatSqsBatchResponse(new QueueBatchResult(true, []));
        Assert.Equal("{}", success);

        var failures = LambdaHost.FormatSqsBatchResponse(
            new QueueBatchResult(false, ["m-2"]));
        Assert.Equal("{\"batchItemFailures\":[{\"itemIdentifier\":\"m-2\"}]}", failures);
    }
}
