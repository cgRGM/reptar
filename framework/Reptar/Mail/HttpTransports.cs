using System.Text.Json;

namespace Reptar;

/// <summary>Target-default outbound HTTP client and environment access for mail transports.</summary>
internal static class MailHttp
{
    internal static readonly IHttpClient Client =
#if NETWASM
        new WasmHttpClient();
#else
        new DefaultHttpClient();
#endif

    internal static readonly IEnvironment Env =
#if NETWASM
        new WasmEnvironment();
#else
        new SystemEnvironment();
#endif
}

public static partial class Mail
{
    /// <summary>
    /// Resend HTTP API (https://api.resend.com/emails). Supports native
    /// Idempotency-Key (24h dedup window, max 256 chars). The API key is read
    /// lazily from <paramref name="apiKeyEnv"/> so constructing the transport
    /// never requires the secret to exist.
    /// </summary>
    public static IMailClient Resend(
        string apiKeyEnv = "RESEND_API_KEY",
        string? defaultFrom = null,
        string? endpoint = null) =>
        new ResendTransport(apiKeyEnv, defaultFrom, endpoint);

    /// <summary>
    /// Postmark HTTP API (https://api.postmarkapp.com/email). Postmark has no
    /// native idempotency; the key is forwarded as an advisory
    /// <c>Idempotency-Key</c> request header and Postmark's Tag/Metadata
    /// remain the documented dedup mechanisms.
    /// </summary>
    public static IMailClient Postmark(
        string tokenEnv = "POSTMARK_SERVER_TOKEN",
        string? defaultFrom = null,
        string messageStream = "outbound") =>
        new PostmarkTransport(tokenEnv, defaultFrom, messageStream);

    /// <summary>
    /// Cloudflare Email Service REST API — for native runtimes or sending
    /// into a different Cloudflare account. On Workers prefer
    /// <see cref="Cloudflare"/> (the binding). The API can answer 200 with
    /// <c>success: false</c> in the body; the transport checks the flag.
    /// </summary>
    public static IMailClient CloudflareRest(
        string accountIdEnv = "CLOUDFLARE_ACCOUNT_ID",
        string tokenEnv = "CLOUDFLARE_API_TOKEN",
        string? defaultFrom = null) =>
        new CloudflareRestTransport(accountIdEnv, tokenEnv, defaultFrom);

    /// <summary>
    /// MailPit HTTP API (http://localhost:8025/api/v1/send) — zero-config
    /// local dev when a MailPit container is already running. For dev without
    /// Docker, prefer <see cref="Console"/>.
    /// </summary>
    public static IMailClient MailPit(string baseUrl = "http://localhost:8025", string? defaultFrom = null) =>
        new MailPitTransport(baseUrl, defaultFrom);

    /// <summary>
    /// Generic HTTP JSON transport: POSTs the neutral camelCase payload to
    /// any endpoint (webhook relays, self-hosted forwarders, test spies).
    /// </summary>
    public static IMailClient Http(MailHttpOptions options) => new HttpMailTransport(options);
}

/// <summary>Options for the generic <see cref="HttpMailTransport"/>.</summary>
public sealed record MailHttpOptions(
    string Endpoint,
    string? ApiKeyEnv = null,
    string AuthHeader = "Authorization",
    string AuthPrefix = "Bearer ",
    string? DefaultFrom = null);

/// <summary>Base for HTTP JSON transports: lazy env-var credential resolution.</summary>
public abstract class HttpMailTransportBase : IMailClient
{
    private readonly string _apiKeyEnv;
    private string? _apiKey;
    private bool _apiKeyResolved;

    protected HttpMailTransportBase(string apiKeyEnv)
    {
        _apiKeyEnv = apiKeyEnv;
    }

    public abstract string Name { get; }

    /// <summary>The environment variable the credential is read from, for honest error messages.</summary>
    protected string ApiKeyEnv => _apiKeyEnv;

    protected IHttpClient Http => MailHttp.Client;

    protected string? ResolveKey()
    {
        if (!_apiKeyResolved)
        {
            _apiKey = MailHttp.Env.Get(_apiKeyEnv);
            _apiKeyResolved = true;
        }

        return _apiKey;
    }

    public abstract Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Resend — https://api.resend.com/emails (Bearer auth, Idempotency-Key).</summary>
public sealed class ResendTransport : HttpMailTransportBase
{
    private readonly string? _defaultFrom;
    private readonly string _endpoint;
    private readonly IHttpClient _http;

    public override string Name => "resend";

    public ResendTransport(
        string apiKeyEnv = "RESEND_API_KEY",
        string? defaultFrom = null,
        string? endpoint = null,
        IHttpClient? httpClient = null) : base(apiKeyEnv)
    {
        _defaultFrom = defaultFrom;
        _endpoint = endpoint ?? "https://api.resend.com/emails";
        _http = httpClient ?? MailHttp.Client;
    }

    public override async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var key = ResolveKey();

        if (string.IsNullOrEmpty(key))
            return MailResult.Failed($"{ApiKeyEnv} is not set; cannot send email through Resend.");

        if (!MailMessages.TryPrepare(message, _defaultFrom, out var from, out var failure))
            return failure;

        var payload = new ResendSendRequest(
            from,
            [.. message.To],
            message.Subject,
            message.Cc is { Count: > 0 } ? [.. message.Cc] : null,
            message.Bcc is { Count: > 0 } ? [.. message.Bcc] : null,
            string.IsNullOrWhiteSpace(message.ReplyTo) ? null : [message.ReplyTo],
            message.Html,
            message.Text,
            message.Attachments is { Count: > 0 }
                ? message.Attachments.Select(a => new ResendAttachment(
                    a.Filename,
                    Convert.ToBase64String(a.Content),
                    a.ContentType,
                    a.ContentId)).ToArray()
                : null,
            message.Headers is { Count: > 0 } ? new Dictionary<string, string>(message.Headers) : null);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {key}",
            ["Content-Type"] = "application/json",
        };

        // Resend-native: unique per request, 24h TTL, max 256 chars.
        if (message.IdempotencyKey is { Length: > 0 })
            headers["Idempotency-Key"] = message.IdempotencyKey;

        var response = await _http.FetchAsync(
            _endpoint,
            "POST",
            headers,
            JsonSerializer.Serialize(payload, MailJsonContext.Default.ResendSendRequest));

        if (response.Status is >= 200 and < 300)
        {
            var sent = JsonSerializer.Deserialize(
                response.Body,
                MailJsonContext.Default.ResendSendResponse);
            return MailResult.Sent(sent?.Id);
        }

        return MailResult.Failed($"Resend HTTP {response.Status}: {MailMessages.Truncate(response.Body)}");
    }
}

/// <summary>Postmark — https://api.postmarkapp.com/email (X-Postmark-Server-Token).</summary>
public sealed class PostmarkTransport : HttpMailTransportBase
{
    private const string Endpoint = "https://api.postmarkapp.com/email";
    private readonly string? _defaultFrom;
    private readonly string _messageStream;
    private readonly IHttpClient _http;

    public override string Name => "postmark";

    public PostmarkTransport(
        string tokenEnv = "POSTMARK_SERVER_TOKEN",
        string? defaultFrom = null,
        string messageStream = "outbound",
        IHttpClient? httpClient = null) : base(tokenEnv)
    {
        _defaultFrom = defaultFrom;
        _messageStream = messageStream;
        _http = httpClient ?? MailHttp.Client;
    }

    public override async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var token = ResolveKey();

        if (string.IsNullOrEmpty(token))
            return MailResult.Failed($"{ApiKeyEnv} is not set; cannot send email through Postmark.");

        if (!MailMessages.TryPrepare(message, _defaultFrom, out var from, out var failure))
            return failure;

        var payload = new PostmarkSendRequest(
            from,
            string.Join(", ", message.To),
            message.Subject,
            message.Html,
            message.Text,
            message.Cc is { Count: > 0 } ? string.Join(", ", message.Cc) : null,
            message.Bcc is { Count: > 0 } ? string.Join(", ", message.Bcc) : null,
            string.IsNullOrWhiteSpace(message.ReplyTo) ? null : message.ReplyTo,
            message.Headers is { Count: > 0 }
                ? message.Headers.Select(pair => new PostmarkHeader(pair.Key, pair.Value)).ToArray()
                : null,
            null,
            message.Attachments is { Count: > 0 }
                ? message.Attachments.Select(a => new PostmarkAttachment(
                    a.Filename,
                    Convert.ToBase64String(a.Content),
                    string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType,
                    a.ContentId)).ToArray()
                : null,
            _messageStream);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json",
            ["X-Postmark-Server-Token"] = token,
        };

        // Advisory: Postmark has no native idempotency today; documented per-adapter.
        if (message.IdempotencyKey is { Length: > 0 })
            headers["Idempotency-Key"] = message.IdempotencyKey;

        var response = await _http.FetchAsync(
            Endpoint,
            "POST",
            headers,
            JsonSerializer.Serialize(payload, MailJsonContext.Default.PostmarkSendRequest));

        if (response.Status is not (>= 200 and < 300))
            return MailResult.Failed($"Postmark HTTP {response.Status}: {MailMessages.Truncate(response.Body)}");

        var sent = JsonSerializer.Deserialize(
            response.Body,
            MailJsonContext.Default.PostmarkSendResponse);

        if (sent is not null && sent.ErrorCode != 0)
            return MailResult.Failed(
                $"Postmark error {sent.ErrorCode}: {MailMessages.Truncate(sent.Message ?? response.Body)}");

        return MailResult.Sent(sent?.MessageID);
    }
}

/// <summary>Cloudflare Email Service REST API (any runtime, Bearer token).</summary>
public sealed class CloudflareRestTransport : HttpMailTransportBase
{
    private readonly string _accountIdEnv;
    private readonly string? _defaultFrom;
    private readonly IHttpClient _http;
    private string? _accountId;
    private bool _accountIdResolved;

    public override string Name => "cloudflare";

    public CloudflareRestTransport(
        string accountIdEnv = "CLOUDFLARE_ACCOUNT_ID",
        string tokenEnv = "CLOUDFLARE_API_TOKEN",
        string? defaultFrom = null,
        IHttpClient? httpClient = null) : base(tokenEnv)
    {
        _accountIdEnv = accountIdEnv;
        _defaultFrom = defaultFrom;
        _http = httpClient ?? MailHttp.Client;
    }

    public override async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var token = ResolveKey();

        if (string.IsNullOrEmpty(token))
            return MailResult.Failed($"{ApiKeyEnv} is not set; cannot send through the Cloudflare Email API.");

        if (!_accountIdResolved)
        {
            _accountId = MailHttp.Env.Get(_accountIdEnv);
            _accountIdResolved = true;
        }

        if (string.IsNullOrEmpty(_accountId))
            return MailResult.Failed($"{_accountIdEnv} is not set; cannot send through the Cloudflare Email API.");

        if (!MailMessages.TryPrepare(message, _defaultFrom, out var from, out var failure))
            return failure;

        var payload = MailMessages.ToCloudflarePayload(message, from);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {token}",
            ["Content-Type"] = "application/json",
        };

        var url = $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/email/sending/send";
        var response = await _http.FetchAsync(
            url,
            "POST",
            headers,
            JsonSerializer.Serialize(payload, MailJsonContext.Default.CloudflareSendRequest));

        if (response.Status is not (>= 200 and < 300))
            return MailResult.Failed($"Cloudflare HTTP {response.Status}: {MailMessages.Truncate(response.Body)}");

        // The Cloudflare API can answer 200 with success: false in the body.
        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var reason = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? MailMessages.Truncate(errors[0].TryGetProperty("message", out var m) ? m.GetString() : response.Body)
                : MailMessages.Truncate(response.Body);
            return MailResult.Failed($"Cloudflare email API reported failure: {reason}");
        }

        return MailResult.Sent();
    }
}

/// <summary>MailPit — POST {base}/api/v1/send (local development UI container).</summary>
public sealed class MailPitTransport : IMailClient
{
    private readonly string _endpoint;
    private readonly string? _defaultFrom;
    private readonly IHttpClient _http;

    public string Name => "mailpit";

    public MailPitTransport(
        string baseUrl = "http://localhost:8025",
        string? defaultFrom = null,
        IHttpClient? httpClient = null)
    {
        _endpoint = baseUrl.TrimEnd('/') + "/api/v1/send";
        _defaultFrom = defaultFrom;
        _http = httpClient ?? MailHttp.Client;
    }

    public async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        if (!MailMessages.TryPrepare(message, _defaultFrom, out var from, out var failure))
            return failure;

        MailPitAddress[]? replyTo = null;

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
            replyTo = [ToMailPitAddress(message.ReplyTo)];

        var payload = new MailPitSendRequest(
            ToMailPitAddress(from),
            message.To.Select(ToMailPitAddress).ToArray(),
            message.Cc is { Count: > 0 } ? message.Cc.Select(ToMailPitAddress).ToArray() : null,
            message.Bcc is { Count: > 0 } ? [.. message.Bcc] : null,
            replyTo,
            message.Subject,
            message.Text,
            message.Html,
            message.Attachments is { Count: > 0 }
                ? message.Attachments.Select(a => new MailPitAttachment(
                    Convert.ToBase64String(a.Content),
                    a.Filename)).ToArray()
                : null);

        var response = await _http.FetchAsync(
            _endpoint,
            "POST",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
            },
            JsonSerializer.Serialize(payload, MailJsonContext.Default.MailPitSendRequest));

        if (response.Status is >= 200 and < 300)
            return MailResult.Sent();

        return MailResult.Failed($"MailPit HTTP {response.Status}: {MailMessages.Truncate(response.Body)}");
    }

    private static MailPitAddress ToMailPitAddress(string text) =>
        EmailAddress.TryParse(text, out var address)
            ? new MailPitAddress(address.Address, address.Name)
            : new MailPitAddress(text, null);
}

/// <summary>Generic HTTP JSON transport over the neutral camelCase payload.</summary>
public sealed class HttpMailTransport(MailHttpOptions options, IHttpClient? httpClient = null) : IMailClient
{
    private readonly IHttpClient _http = httpClient ?? MailHttp.Client;

    public string Name => "http";

    public async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var apiKey = string.IsNullOrEmpty(options.ApiKeyEnv) ? null : MailHttp.Env.Get(options.ApiKeyEnv);

        if (options.ApiKeyEnv is { Length: > 0 } && string.IsNullOrEmpty(apiKey))
            return MailResult.Failed($"{options.ApiKeyEnv} is not set; cannot send email through {options.Endpoint}.");

        if (!MailMessages.TryPrepare(message, options.DefaultFrom, out var from, out var failure))
            return failure;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };

        if (!string.IsNullOrEmpty(apiKey))
            headers[options.AuthHeader] = options.AuthPrefix + apiKey;

        if (message.IdempotencyKey is { Length: > 0 })
            headers["Idempotency-Key"] = message.IdempotencyKey;

        var payload = MailMessages.ToCloudflarePayload(message, from);
        var response = await _http.FetchAsync(
            options.Endpoint,
            "POST",
            headers,
            JsonSerializer.Serialize(payload, MailJsonContext.Default.CloudflareSendRequest));

        if (response.Status is >= 200 and < 300)
            return MailResult.Sent();

        return MailResult.Failed($"HTTP {response.Status}: {MailMessages.Truncate(response.Body)}");
    }
}
