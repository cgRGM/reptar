using System.Text;
using System.Text.Json;

namespace Reptar.Tests;

public sealed class MailTests
{
    private sealed record MailAck(bool Ok, string? ProviderId, string? Error);

    private sealed class RecordingMailObserver : IMailObserver
    {
        public List<(string Provider, string? ProviderId, double DurationMs)> SentEvents { get; } = [];

        public List<(string Provider, string Error)> FailedEvents { get; } = [];

        public void Sent(string provider, string? providerId, double durationMs) =>
            SentEvents.Add((provider, providerId, durationMs));

        public void Failed(string provider, string error) =>
            FailedEvents.Add((provider, error));
    }

    private sealed class ThrowingMailTransport : IMailClient
    {
        public string Name => "throwing";

        public Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CapturingHttpClient(Func<HttpResponseData>? respond = null) : IHttpClient
    {
        public List<(string Url, string Method, Dictionary<string, string>? Headers, string Body)> Requests { get; } = [];

        private readonly Func<HttpResponseData> _respond =
            respond ?? (() => new HttpResponseData(
                200,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "{\"id\":\"email-123\"}"));

        public Task<HttpResponseData> FetchAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            string body = "")
        {
            Requests.Add((url, method, headers, body));
            return Task.FromResult(_respond());
        }
    }

    private static MailMessage MagicLinkMail(string to = "user@example.com") =>
        new(
            [to],
            "Verify your email",
            Html: "<p><a href=\"https://app.test/verify?token=abc123\">Verify</a></p>",
            Text: "Verify: https://app.test/verify?token=abc123 Your code is 654321.");

    private sealed record MagicLinkTemplate(string Url) : IMailTemplate
    {
        public string Subject => "Sign in to Acme";

        public string Html => $"<p><a href=\"{Url}\">Sign in</a></p>";

        public string? Text => $"Sign in: {Url}";
    }

    [Fact]
    public async Task Template_overloads_render_subject_html_and_text()
    {
        var app = ReptarApp.Create();
        app.Post("/magic", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(
                ["user@example.com"],
                new MagicLinkTemplate("https://app.test/magic?t=xyz"),
                from: "Acme <noreply@acme.com>",
                idempotencyKey: "tok-1");
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();
        await client.PostJsonAsync("/magic", "{}");

        var message = Assert.Single(client.Mail.Sent);
        Assert.Equal("Sign in to Acme", message.Subject);
        Assert.Equal("Acme <noreply@acme.com>", message.From);
        Assert.Equal("https://app.test/magic?t=xyz", client.Mail.FirstLinkMatching("https://app.test/magic"));
    }

    [Fact]
    public async Task Template_background_overload_queues_and_sends()
    {
        var app = ReptarApp.Create();
        app.Post("/magic-bg", async ctx =>
        {
            ctx.Mail.SendInBackground(
                ["bg@example.com"],
                new MagicLinkTemplate("https://app.test/bg?t=1"));
            return ctx.JsonRaw("""{"ok":true}""");
        });

        var client = app.CreateTestClient();
        await client.PostJsonAsync("/magic-bg", "{}");

        var queued = Assert.Single(client.Mail.Queued);
        Assert.Equal("Sign in to Acme", queued.Subject);

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (client.Mail.Sent.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Single(client.Mail.Sent);
    }

    [Fact]
    public async Task Unconfigured_mail_returns_honest_failure()
    {
        var result = await new NoopMailClient().SendAsync(new MailMessage(["user@example.com"], "Hello"));

        Assert.False(result.Ok);
        Assert.Null(result.ProviderId);
        Assert.Contains("Mail is not configured", result.Error);
    }

    [Fact]
    public async Task CreateTestClient_swaps_default_transport_to_memory_and_extracts_links()
    {
        var app = ReptarApp.Create();
        app.Post("/send", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(MagicLinkMail());
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();

        Assert.True(client.Mail.Capturing);
        Assert.Empty(client.Mail.Sent);

        var response = await client.PostJsonAsync("/send", "{}");
        Assert.Equal(200, response.Status);

        var ack = JsonSerializer.Deserialize<MailAck>(
            Encoding.UTF8.GetString(response.Body!), ReptarJson.Options);

        Assert.NotNull(ack);
        Assert.True(ack.Ok);

        var message = Assert.Single(client.Mail.Sent);
        Assert.Equal("Verify your email", message.Subject);

        var same = client.Mail.LastSentTo("user@example.com");
        Assert.Equal(message, same);

        Assert.Equal(
            "https://app.test/verify?token=abc123",
            client.Mail.FirstLinkMatching("https://app.test/verify"));
        Assert.Equal("654321", client.Mail.FirstOtp());
    }

    [Fact]
    public void CreateTestClient_respects_explicit_transport_without_lying()
    {
        var app = ReptarApp.Create(new ReptarConfig { Mail = new ThrowingMailTransport() });
        var client = app.CreateTestClient();

        Assert.False(client.Mail.Capturing);
        Assert.Empty(client.Mail.Sent);

        Assert.Throws<InvalidOperationException>(() => client.Mail.LastSentTo("user@example.com"));
    }

    [Fact]
    public async Task CreateTestClient_reuses_an_already_installed_memory_transport()
    {
        var app = ReptarApp.Create(new ReptarConfig { Mail = Mail.InMemory() });
        var first = app.CreateTestClient();
        var second = app.CreateTestClient();

        app.Post("/send", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(MagicLinkMail());
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        await first.PostJsonAsync("/send", "{}");
        await second.PostJsonAsync("/send", "{}");

        Assert.Equal(2, first.Mail.Sent.Count);
        Assert.Equal(2, second.Mail.Sent.Count);
    }

    [Fact]
    public async Task SendInBackground_records_queued_deterministically()
    {
        var app = ReptarApp.Create();
        app.Post("/send", async ctx =>
        {
            ctx.Mail.SendInBackground(MagicLinkMail("bg@example.com"));
            return ctx.JsonRaw("""{"ok":true}""");
        });

        var client = app.CreateTestClient();
        var response = await client.PostJsonAsync("/send", "{}");

        Assert.Equal(200, response.Status);

        // Queued is captured at WaitUntil registration: deterministic.
        var queued = Assert.Single(client.Mail.Queued);
        Assert.Equal("bg@example.com", queued.To[0]);

        // Sent lands when the background task executes.
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (client.Mail.Sent.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Single(client.Mail.Sent);
    }

    [Fact]
    public async Task Inline_send_emits_server_timing_and_observer_events()
    {
        var observer = new RecordingMailObserver();
        var app = ReptarApp.Create(new ReptarConfig { MailObserver = observer });
        app.Post("/send", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(new MailMessage(["user@example.com"], "Hello"));
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();
        var response = await client.PostJsonAsync("/send", "{}");

        Assert.True(response.Headers.TryGetValue("Server-Timing", out var timing));
        Assert.Contains("mail;dur=", timing);
        Assert.Contains("memory", timing);

        var sent = Assert.Single(observer.SentEvents);
        Assert.Equal("memory", sent.Provider);
        Assert.NotNull(sent.ProviderId);
        Assert.True(sent.DurationMs >= 0);
    }

    [Fact]
    public async Task Transport_failures_and_exceptions_never_throw_and_report_to_observer()
    {
        var observer = new RecordingMailObserver();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Mail = new ThrowingMailTransport(),
            MailObserver = observer,
        });

        app.Post("/send", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(new MailMessage(["user@example.com"], "Hello"));
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();
        var response = await client.PostJsonAsync("/send", "{}");

        var ack = JsonSerializer.Deserialize<MailAck>(
            Encoding.UTF8.GetString(response.Body!), ReptarJson.Options);

        Assert.NotNull(ack);
        Assert.False(ack.Ok);
        Assert.Contains("boom", ack.Error);

        var failed = Assert.Single(observer.FailedEvents);
        Assert.Equal("throwing", failed.Provider);
        Assert.Contains("boom", failed.Error);
    }

    [Fact]
    public async Task Resend_transport_builds_correct_payload_and_headers()
    {
        Environment.SetEnvironmentVariable("RESEND_TEST_KEY", "re_test_123");
        var http = new CapturingHttpClient();
        var transport = new ResendTransport("RESEND_TEST_KEY", defaultFrom: "Acme <noreply@acme.com>", httpClient: http);

        var result = await transport.SendAsync(new MailMessage(
            ["user@example.com", "second@example.com"],
            "Welcome",
            Html: "<h1>Hi</h1>",
            Text: "Hi",
            Cc: ["cc@example.com"],
            Bcc: ["bcc@example.com"],
            ReplyTo: "support@example.com",
            Headers: new Dictionary<string, string> { ["X-Campaign"] = "onboarding" },
            Attachments: [new MailAttachment("data.csv", [1, 2, 3], "text/csv")],
            IdempotencyKey: "idem-123"));

        Assert.True(result.Ok);
        Assert.Equal("email-123", result.ProviderId);

        var (url, method, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.resend.com/emails", url);
        Assert.Equal("POST", method);
        Assert.Equal("Bearer re_test_123", headers!["Authorization"]);
        Assert.Equal("idem-123", headers["Idempotency-Key"]);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("Acme <noreply@acme.com>", root.GetProperty("from").GetString());
        Assert.Equal(2, root.GetProperty("to").GetArrayLength());
        Assert.Equal("user@example.com", root.GetProperty("to")[0].GetString());
        Assert.Equal("Welcome", root.GetProperty("subject").GetString());
        Assert.Equal("<h1>Hi</h1>", root.GetProperty("html").GetString());
        Assert.Equal("support@example.com", root.GetProperty("reply_to")[0].GetString());
        Assert.Equal("cc@example.com", root.GetProperty("cc")[0].GetString());
        Assert.Equal("bcc@example.com", root.GetProperty("bcc")[0].GetString());
        Assert.Equal("onboarding", root.GetProperty("headers").GetProperty("X-Campaign").GetString());

        var attachment = root.GetProperty("attachments")[0];
        Assert.Equal("data.csv", attachment.GetProperty("filename").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), attachment.GetProperty("content").GetString());
        Assert.Equal("text/csv", attachment.GetProperty("content_type").GetString());
    }

    [Fact]
    public async Task Resend_transport_fails_without_api_key()
    {
        Environment.SetEnvironmentVariable("RESEND_MISSING_KEY_TEST", null);
        var transport = new ResendTransport("RESEND_MISSING_KEY_TEST", httpClient: new CapturingHttpClient());

        var result = await transport.SendAsync(new MailMessage(["user@example.com"], "Hello"));

        Assert.False(result.Ok);
        Assert.Contains("RESEND_MISSING_KEY_TEST", result.Error);
    }

    [Fact]
    public async Task Resend_transport_surfaces_provider_errors()
    {
        var http = new CapturingHttpClient(() => new HttpResponseData(
            422,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{\"name\":\"validation_error\",\"message\":\"Invalid from\"}"));
        var transport = new ResendTransport(
            "RESEND_TEST_KEY", defaultFrom: "Acme <noreply@acme.com>", httpClient: http);

        var result = await transport.SendAsync(new MailMessage(
            ["user@example.com"], "Hello", From: "Acme <noreply@acme.com>"));

        Assert.False(result.Ok);
        Assert.Contains("422", result.Error);
        Assert.Contains("Invalid from", result.Error);
    }

    [Fact]
    public async Task Postmark_transport_builds_pascal_case_payload()
    {
        Environment.SetEnvironmentVariable("POSTMARK_TEST_TOKEN", "pm-token");
        var http = new CapturingHttpClient(() => new HttpResponseData(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{\"To\":\"user@example.com\",\"SubmittedAt\":\"now\",\"MessageID\":\"pm-message-id\",\"ErrorCode\":0,\"Message\":\"OK\"}"));
        var transport = new PostmarkTransport("POSTMARK_TEST_TOKEN", messageStream: "alerts", httpClient: http);

        var result = await transport.SendAsync(new MailMessage(
            ["user@example.com"],
            "Hello",
            From: "Acme <noreply@acme.com>",
            Html: "<p>Hi</p>",
            Text: "Hi"));

        Assert.True(result.Ok);
        Assert.Equal("pm-message-id", result.ProviderId);

        var (url, _, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.postmarkapp.com/email", url);
        Assert.Equal("pm-token", headers!["X-Postmark-Server-Token"]);
        Assert.DoesNotContain("Authorization", headers.Keys);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("Acme <noreply@acme.com>", root.GetProperty("From").GetString());
        Assert.Equal("user@example.com", root.GetProperty("To").GetString());
        Assert.Equal("Hello", root.GetProperty("Subject").GetString());
        Assert.Equal("<p>Hi</p>", root.GetProperty("HtmlBody").GetString());
        Assert.Equal("Hi", root.GetProperty("TextBody").GetString());
        Assert.Equal("alerts", root.GetProperty("MessageStream").GetString());
    }

    [Fact]
    public async Task Postmark_transport_reports_error_codes()
    {
        var http = new CapturingHttpClient(() => new HttpResponseData(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{\"To\":\"user@example.com\",\"SubmittedAt\":\"now\",\"MessageID\":null,\"ErrorCode\":406,\"Message\":\"Inactive recipient\"}"));
        var transport = new PostmarkTransport(
            "POSTMARK_TEST_TOKEN", defaultFrom: "Acme <noreply@acme.com>", httpClient: http);

        var result = await transport.SendAsync(new MailMessage(
            ["user@example.com"], "Hello", From: "Acme <noreply@acme.com>"));

        Assert.False(result.Ok);
        Assert.Contains("406", result.Error);
        Assert.Contains("Inactive recipient", result.Error);
    }

    [Fact]
    public async Task MailPit_transport_builds_address_objects_and_capital_html()
    {
        var http = new CapturingHttpClient(() => new HttpResponseData(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "{\"ID\":\"mp-1\"}"));
        var transport = new MailPitTransport("http://localhost:8025", defaultFrom: "Jane <jane@example.com>", httpClient: http);

        var result = await transport.SendAsync(new MailMessage(
            ["User Two <user@example.com>"],
            "Sub",
            Html: "<b>x</b>",
            Bcc: ["bcc@example.com"]));

        Assert.True(result.Ok);

        var (url, _, _, body) = Assert.Single(http.Requests);
        Assert.Equal("http://localhost:8025/api/v1/send", url);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("jane@example.com", root.GetProperty("From").GetProperty("Email").GetString());
        Assert.Equal("Jane", root.GetProperty("From").GetProperty("Name").GetString());
        Assert.Equal("user@example.com", root.GetProperty("To")[0].GetProperty("Email").GetString());
        Assert.Equal("User Two", root.GetProperty("To")[0].GetProperty("Name").GetString());
        Assert.Equal("<b>x</b>", root.GetProperty("HTML").GetString());
        Assert.Equal("Sub", root.GetProperty("Subject").GetString());
        Assert.Equal("bcc@example.com", root.GetProperty("Bcc")[0].GetString());
    }

    [Fact]
    public void Cloudflare_payload_serializes_the_structured_builder_shape()
    {
        var payload = MailMessages.ToCloudflarePayload(
            new MailMessage(
                ["to@example.com"],
                "Verify",
                From: "from@example.com",
                Html: "<p>x</p>",
                Attachments: [new MailAttachment("logo.png", [1, 2], "image/png", "cid:logo")]),
            "from@example.com");

        var json = JsonSerializer.Serialize(payload, MailJsonContext.Default.CloudflareSendRequest);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("from@example.com", root.GetProperty("from").GetString());
        Assert.Equal("to@example.com", root.GetProperty("to")[0].GetString());
        Assert.Equal("Verify", root.GetProperty("subject").GetString());
        Assert.Equal("<p>x</p>", root.GetProperty("html").GetString());
        Assert.False(root.TryGetProperty("replyTo", out _));

        var attachment = root.GetProperty("attachments")[0];
        Assert.Equal("logo.png", attachment.GetProperty("filename").GetString());
        Assert.Equal("image/png", attachment.GetProperty("type").GetString());
        Assert.Equal("inline", attachment.GetProperty("disposition").GetString());
        Assert.Equal("cid:logo", attachment.GetProperty("contentId").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2]), attachment.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Generic_http_transport_posts_neutral_payload_with_custom_auth()
    {
        Environment.SetEnvironmentVariable("MAIL_HTTP_KEY_TEST", "tok");
        var http = new CapturingHttpClient();
        var transport = new HttpMailTransport(
            new MailHttpOptions(
                "https://relay.test/send",
                ApiKeyEnv: "MAIL_HTTP_KEY_TEST",
                AuthHeader: "X-Api-Key",
                AuthPrefix: ""),
            http);

        var result = await transport.SendAsync(new MailMessage(
            ["u@example.com"],
            "S",
            From: "f@example.com",
            IdempotencyKey: "idem-9"));

        Assert.True(result.Ok);

        var (url, _, headers, body) = Assert.Single(http.Requests);
        Assert.Equal("https://relay.test/send", url);
        Assert.Equal("tok", headers!["X-Api-Key"]);
        Assert.Equal("idem-9", headers["Idempotency-Key"]);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("u@example.com", doc.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("S", doc.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public void Mail_scanners_extract_links_and_exact_digit_codes()
    {
        var links = MailScanners.ExtractLinks(
            "go to https://a.test/x?y=1 now, and <a href=\"https://b.test/p\">x</a>, not httpx or https:/bad",
            null);

        Assert.Equal(["https://a.test/x?y=1", "https://b.test/p"], links);

        var otps = MailScanners.ExtractOtps(
            "code 123456 and 12345 (too short) and 1234567 (too long)",
            null);

        Assert.Equal(["123456"], otps);
    }

    [Fact]
    public void EmailAddress_parses_and_formats_mailboxes()
    {
        var named = EmailAddress.Parse("Jane Doe <jane@example.com>");
        Assert.Equal("jane@example.com", named.Address);
        Assert.Equal("Jane Doe", named.Name);
        Assert.Equal("Jane Doe <jane@example.com>", named.ToString());

        var bare = EmailAddress.Parse("bare@example.com");
        Assert.Null(bare.Name);
        Assert.Equal("bare@example.com", bare.ToString());

        Assert.False(EmailAddress.TryParse("not-an-email", out _));
        Assert.False(EmailAddress.TryParse("", out _));
        Assert.Throws<FormatException>(() => EmailAddress.Parse("bad"));
    }

    [Fact]
    public async Task UseMailPreview_serves_html_json_and_clears()
    {
        var app = ReptarApp.Create(new ReptarConfig { Mail = Mail.Console(), Debug = true });
        app.UseMailPreview();
        app.Post("/send", async ctx =>
        {
            var result = await ctx.Mail.SendAsync(MagicLinkMail());
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();
        await client.PostJsonAsync("/send", "{}");

        var html = await client.GetAsync("/_mail");
        Assert.Equal(200, html.Status);
        Assert.Contains("text/html", html.ContentType);
        Assert.Contains("Verify your email", Encoding.UTF8.GetString(html.Body!));

        var json = await client.GetAsync("/_mail/json");
        Assert.Equal(200, json.Status);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(json.Body!));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());

        var message = root.GetProperty("messages")[0];
        Assert.Equal("user@example.com", message.GetProperty("to").GetString());
        Assert.Equal(
            "https://app.test/verify?token=abc123",
            message.GetProperty("links")[0].GetString());
        Assert.Equal("654321", message.GetProperty("otps")[0].GetString());

        var cleared = await client.DeleteAsync("/_mail");
        Assert.Equal(204, cleared.Status);

        var after = await client.GetAsync("/_mail/json");
        using var afterDoc = JsonDocument.Parse(Encoding.UTF8.GetString(after.Body!));
        Assert.Equal(0, afterDoc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UseMailPreview_skips_registration_without_console_transport()
    {
        var app = ReptarApp.Create();
        app.UseMailPreview();

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/_mail");

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public void Mailbox_helpers_throw_clear_errors_when_nothing_matches()
    {
        var app = ReptarApp.Create();
        var client = app.CreateTestClient();

        Assert.Throws<InvalidOperationException>(() => client.Mail.LastSentTo("nobody@example.com"));
        Assert.Throws<InvalidOperationException>(() => client.Mail.FirstLinkMatching("https://none.test"));
        Assert.Throws<InvalidOperationException>(() => client.Mail.FirstOtp());
    }

    [Fact]
    public async Task LastSentTo_returns_the_most_recent_message_for_an_address()
    {
        var app = ReptarApp.Create();
        app.Post("/send", async ctx =>
        {
            var subject = ctx.Req.Header("X-Subject") ?? "first";
            var result = await ctx.Mail.SendAsync(new MailMessage(["user@example.com"], subject));
            return ctx.Json(new MailAck(result.Ok, result.ProviderId, result.Error));
        });

        var client = app.CreateTestClient();

        await client.PostJsonAsync("/send", "{}", new Dictionary<string, string> { ["X-Subject"] = "first" });
        await client.PostJsonAsync("/send", "{}", new Dictionary<string, string> { ["X-Subject"] = "second" });

        Assert.Equal("second", client.Mail.LastSentTo("user@example.com").Subject);
        Assert.Equal(2, client.Mail.Sent.Count);

        client.Mail.Clear();
        Assert.Empty(client.Mail.Sent);
    }
}
