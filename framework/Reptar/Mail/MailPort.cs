using System.Diagnostics;

namespace Reptar;

/// <summary>
/// A parsed RFC 5322-style mailbox: <c>"Name &lt;address@domain&gt;"</c> or a
/// bare address. Manual parsing only — <see cref="System.Text.RegularExpressions.Regex"/>
/// breaks the NetWasm Binaryen optimizer, so mail code stays regex-free.
/// </summary>
public readonly record struct EmailAddress(string Address, string? Name)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";

    /// <summary>Throws <see cref="FormatException"/> when the text is not a mailbox.</summary>
    public static EmailAddress Parse(string? text)
    {
        if (TryParse(text, out var value))
            return value;

        throw new FormatException($"'{text}' is not a valid email address.");
    }

    public static bool TryParse(string? text, out EmailAddress value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        // "Name <addr>" form — the name may be quoted.
        var open = trimmed.LastIndexOf('<');
        var close = trimmed.LastIndexOf('>');

        if (open >= 0 && close > open)
        {
            var address = trimmed[(open + 1)..close].Trim();
            var name = open > 0 ? trimmed[..open].Trim().Trim('"').Trim('\'') : "";

            if (!IsValidAddress(address))
                return false;

            value = new EmailAddress(address, name.Length == 0 ? null : name);
            return true;
        }

        if (!IsValidAddress(trimmed))
            return false;

        value = new EmailAddress(trimmed, null);
        return true;
    }

    private static bool IsValidAddress(string address) =>
        address.Length >= 3 &&
        address.Contains('@') &&
        !address.Contains(' ') &&
        address[0] != '@' &&
        address[^1] != '@';
}

/// <summary>An attachment. Content is raw bytes; transports base64-encode.</summary>
public sealed record MailAttachment(
    string Filename,
    byte[] Content,
    string? ContentType = null,
    string? ContentId = null);

/// <summary>
/// A provider-neutral email. <c>From</c> is resolved at send time from the
/// transport's configured <c>defaultFrom</c> when omitted. Transports build
/// their own wire DTOs from this record — provider vocabulary never leaks
/// into the message itself.
/// </summary>
public sealed record MailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string? From = null,
    string? Html = null,
    string? Text = null,
    IReadOnlyList<string>? Cc = null,
    IReadOnlyList<string>? Bcc = null,
    string? ReplyTo = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<MailAttachment>? Attachments = null,
    string? IdempotencyKey = null);

/// <summary>
/// Delivery outcome. Failures never throw — <c>Error</c> carries the provider
/// reason so callers can log, retry, or surface it without try/catch.
/// </summary>
public readonly record struct MailResult(bool Ok, string? ProviderId, string? Error)
{
    public static MailResult Sent(string? providerId = null) => new(true, providerId, null);

    public static MailResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Compile-time mail template: renders Subject/Html/Text directly from
/// records and raw string literals. Zero template engines — the framework
/// never grows a templating dependency, and templates stay AOT-safe because
/// they are ordinary records.
/// </summary>
public interface IMailTemplate
{
    string Subject { get; }

    string Html { get; }

    string? Text { get; }
}

/// <summary>
/// Optional mail lifecycle hook. Null by default: zero cost when not
/// configured. Wire logging, metrics, or OTel export over this hook — the
/// framework itself never takes a tracing dependency.
/// </summary>
public interface IMailObserver
{
    void Sent(string provider, string? providerId, double durationMs) { }

    void Failed(string provider, string error) { }
}

/// <summary>
/// The mail transport port (the Swoosh adapter behaviour): one send method.
/// Implementations never throw for delivery failures — they return
/// <see cref="MailResult.Failed"/> — and resolve provider credentials
/// lazily so constructing a transport never requires secrets to exist.
/// </summary>
public interface IMailClient
{
    /// <summary>Provider label used by telemetry and Server-Timing ("resend", "postmark", "memory", ...).</summary>
    string Name { get; }

    Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default mail client when none is configured. Reports failure with a clear
/// message instead of silently dropping the email — the same honesty rule as
/// <see cref="SyncQueueClient"/>.
/// </summary>
public sealed class NoopMailClient : IMailClient
{
    public string Name => "none";

    public Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(MailResult.Failed(
            "Mail is not configured. Set Mail = Mail.Resend(...), Mail.Postmark(...), " +
            "Mail.Cloudflare(...), Mail.MailPit(...), Mail.Console(), or Mail.InMemory() in ReptarConfig."));
}

/// <summary>
/// Per-request mail facade returned by <see cref="Ctx.Mail"/>. Adds request
/// telemetry (Server-Timing, <see cref="IMailObserver"/> hooks) and the
/// <see cref="Ctx.WaitUntil"/>-based background dispatch on top of the
/// configured <see cref="IMailClient"/>.
/// </summary>
public readonly struct MailSender
{
    private readonly IMailClient _client;
    private readonly IMailObserver? _observer;
    private readonly Ctx _ctx;

    internal MailSender(IMailClient client, IMailObserver? observer, Ctx ctx)
    {
        _client = client;
        _observer = observer;
        _ctx = ctx;
    }

    /// <summary>The underlying transport, for direct/custom use.</summary>
    public IMailClient Client => _client;

    /// <summary>
    /// Sends inline and reports the duration as a <c>Server-Timing: mail</c>
    /// entry. Delivery failures come back as <see cref="MailResult.Failed"/>
    /// and are never thrown.
    /// </summary>
    public async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var started = Stopwatch.GetTimestamp();
        MailResult result;

        try
        {
            result = await _client.SendAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            result = MailResult.Failed(exception.Message);
        }

        var durationMs = TicksToMs(Stopwatch.GetTimestamp() - started);

        if (result.Ok)
        {
            _observer?.Sent(_client.Name, result.ProviderId, durationMs);
            _ctx.ServerTiming("mail", durationMs, _client.Name);
        }
        else
        {
            _observer?.Failed(_client.Name, result.Error ?? "Unknown mail error.");
        }

        return result;
    }

    /// <summary>Template overload: sends the rendered subject/body to the addresses.</summary>
    public Task<MailResult> SendAsync(
        IReadOnlyList<string> to,
        IMailTemplate template,
        string? from = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        return SendAsync(
            new MailMessage(
                to,
                template.Subject,
                from,
                template.Html,
                template.Text,
                IdempotencyKey: idempotencyKey),
            cancellationToken);
    }

    /// <summary>
    /// Sends after the response without blocking it: wraps the dispatch in
    /// <see cref="Ctx.WaitUntil"/> so the Worker isolate stays open and the
    /// native hosts drain the work. Failures are logged and reported to
    /// <see cref="IMailObserver"/>, never thrown to the caller.
    /// </summary>
    public void SendInBackground(MailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A lambda inside a struct cannot capture `this`; copy the fields it
        // needs into locals before registering the background task.
        var client = _client;
        var observer = _observer;
        var ctx = _ctx;

        // Deterministic test capture: the in-memory sink records the message
        // as queued at registration time, before the background task runs.
        if (client is InMemoryMailTransport recorder)
            recorder.RecordQueued(message);

        ctx.WaitUntil(async () =>
        {
            var started = Stopwatch.GetTimestamp();

            try
            {
                var result = await client.SendAsync(message);
                var durationMs = TicksToMs(Stopwatch.GetTimestamp() - started);

                if (result.Ok)
                {
                    observer?.Sent(client.Name, result.ProviderId, durationMs);
                }
                else
                {
                    observer?.Failed(client.Name, result.Error ?? "Unknown mail error.");
                    ctx.Log.Error($"[mail:{client.Name}] background send failed: {result.Error}");
                }
            }
            catch (Exception exception)
            {
                observer?.Failed(client.Name, exception.Message);
                ctx.Log.Error($"[mail:{client.Name}] background send failed: {exception.Message}");
            }
        });
    }

    /// <summary>Template overload: sends the rendered template after the response.</summary>
    public void SendInBackground(
        IReadOnlyList<string> to,
        IMailTemplate template,
        string? from = null,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        SendInBackground(new MailMessage(
            to,
            template.Subject,
            from,
            template.Html,
            template.Text,
            IdempotencyKey: idempotencyKey));
    }

    private static double TicksToMs(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Mail transport factories and shared message helpers.
/// HTTP transports live in <c>HttpTransports.cs</c>; the Cloudflare binding
/// bridge in <c>Reptar.Wasm</c>; the console/dev transport (native only) in
/// <c>ConsoleTransport.cs</c>.
/// </summary>
public static partial class Mail
{
    /// <summary>In-memory sink: records every send. The automatic transport for <c>app.CreateTestClient()</c>.</summary>
    public static IMailClient InMemory() => new InMemoryMailTransport();

    /// <summary>Explicit no-op transport that fails every send with a clear error.</summary>
    public static IMailClient None() => new NoopMailClient();
}

/// <summary>Shared transport helpers: from-resolution and pre-flight validation.</summary>
internal static class MailMessages
{
    /// <summary>
    /// Resolves the effective From (message, else transport default) and
    /// validates the message. False plus a <see cref="MailResult"/> failure
    /// when the message cannot be sent; true plus a non-null
    /// <paramref name="from"/> when it can.
    /// </summary>
    public static bool TryPrepare(
        MailMessage message,
        string? defaultFrom,
        out string from,
        out MailResult failure)
    {
        if (!string.IsNullOrWhiteSpace(message.From))
        {
            from = message.From.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(defaultFrom))
        {
            from = defaultFrom;
        }
        else
        {
            from = "";
            failure = MailResult.Failed("MailMessage.From is empty and no defaultFrom was configured.");
            return false;
        }

        if (message.To is not { Count: > 0 })
        {
            failure = MailResult.Failed("MailMessage.To is empty.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            failure = MailResult.Failed("MailMessage.Subject is empty.");
            return false;
        }

        failure = default;
        return true;
    }

    public static string Truncate(string? text, int maxLength = 300) =>
        string.IsNullOrEmpty(text)
            ? ""
            : text.Length <= maxLength
                ? text
                : text[..maxLength] + "…";

    /// <summary>
    /// Neutral camelCase payload (also the Cloudflare EmailMessageBuilder
    /// shape) shared by the binding bridge, the Cloudflare REST transport,
    /// and the generic HTTP transport.
    /// </summary>
    public static CloudflareSendRequest ToCloudflarePayload(MailMessage message, string from)
    {
        var attachments = message.Attachments is { Count: > 0 }
            ? message.Attachments.Select(a => new CloudflareEmailAttachment(
                a.Filename,
                Convert.ToBase64String(a.Content),
                string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType,
                a.ContentId is { Length: > 0 } ? "inline" : "attachment",
                a.ContentId)).ToArray()
            : null;

        Dictionary<string, string>? headers = null;

        if (message.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(message.Headers.Count, StringComparer.Ordinal);
            foreach (var (name, value) in message.Headers)
                headers[name] = value;
        }

        return new CloudflareSendRequest(
            from,
            [.. message.To],
            message.Subject,
            message.Html,
            message.Text,
            message.Cc is { Count: > 0 } ? [.. message.Cc] : null,
            message.Bcc is { Count: > 0 } ? [.. message.Bcc] : null,
            string.IsNullOrWhiteSpace(message.ReplyTo) ? null : message.ReplyTo,
            attachments,
            headers);
    }
}
