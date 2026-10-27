using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed partial class Ctx
{
    private readonly ServiceSet _services;
    private string? _requestId;
    private RequestTraceState? _traceState;

    internal Ctx(ReptarRequest request, ServiceSet services)
    {
        Req = request;
        _services = services;
        Cookies = new CookieJar(request);
    }

    public ReptarRequest Req { get; }

    /// <summary>
    /// Streaming request body for routes registered with
    /// <see cref="RouteBuilder.StreamBody"/>. Null when the request has no body.
    /// </summary>
    public Stream? BodyStream => Req.BodyStream;

    /// <summary>
    /// Streaming body when available, otherwise a memory stream over the
    /// buffered body.
    /// </summary>
    public Stream? OpenBody() => Req.OpenBody();

    /// <summary>
    /// Shortcut for <see cref="ReptarRequest.Param"/> route parameters,
    /// e.g. ctx.Param("id") on GET /api/posts/:id.
    /// </summary>
    public string? Param(string name) => Req.Param(name);

    public IDbClient Db => _services.Db;
    public IStorage Storage => _services.Storage;
    public IAiClient AI => _services.AI;
    public IRateLimiter RateLimit => _services.RateLimit;
    public IKvClient Cache => _services.Cache;
    public IQueueClient Queues => _services.Queues;
    public IDurableBridge Durable => _services.Durable;
    public MailSender Mail => new(_services.Mail, _services.MailObserver, this);
    public IHttpClient Http => _services.Http;
    public IClock Clock => _services.Clock;
    public ICrypto Crypto => _services.Crypto;
    public IEnvironment Environment => _services.Environment;
    public IAppLogger Log => _services.Log;
    public IWebSocketTransport WebSocket => _services.WebSocket;
    public IJsonCodec JsonCodec => _services.Json;
    public CookieJar Cookies { get; }
    public SessionUser? User { get; set; }
    public JwtPayload? Claims { get; set; }

    /// <summary>Per-request database timing, populated by InstrumentedDbClient.</summary>
    public DbMetrics DbMetrics { get; } = new();

    /// <summary>Cancellation token for the current request (host timeout/abort).</summary>
    public CancellationToken RequestAborted { get; internal set; } = CancellationToken.None;

    /// <summary>Matched route template (e.g. /posts/:id), or the raw path on 404.</summary>
    public string? RoutePattern { get; internal set; }

    /// <summary>W3C trace id parsed from traceparent, or generated on first use.</summary>
    public string? TraceId
    {
        get => _traceState?.TraceId;
        internal set => (_traceState ??= new RequestTraceState()).TraceId = value;
    }

    /// <summary>W3C span id, generated on first use within an HTTP request.</summary>
    public string? SpanId
    {
        get => _traceState?.SpanId;
        internal set => (_traceState ??= new RequestTraceState()).SpanId = value;
    }

    /// <summary>W3C parent span id parsed from the incoming traceparent.</summary>
    public string? ParentSpanId
    {
        get => _traceState?.ParentSpanId;
        internal set => (_traceState ??= new RequestTraceState()).ParentSpanId = value;
    }

    internal bool HasTraceIdentifiers => _traceState?.HasIdentifiers ?? false;
    internal RequestTraceState TraceState => _traceState
        ?? throw new InvalidOperationException("HTTP trace state has not been initialized.");

    internal void InitializeTrace(TraceParent? parent) => _traceState = new RequestTraceState(parent);

    public async Task<SessionUser?> UserAsync()
    {
        if (User is not null) return User;
        User = await Auth.CurrentUser(this);
        return User;
    }

    internal Dictionary<string, string> PendingHeaders { get; } = new();

    public string RequestId
    {
        // Prefer the id assigned by the host so telemetry correlation and
        // response headers use one value on every target.
        get => _requestId ??= Req.RequestId ?? Ids.New("req");
        set => _requestId = value;
    }

    public void SetHeader(string name, string value) => PendingHeaders[name] = value;

    private readonly List<Task> _backgroundTasks = new();
    public IReadOnlyList<Task> BackgroundTasks => _backgroundTasks;

    /// <summary>
    /// Enqueues background work that executes after or alongside the response.
    /// On Cloudflare Workers (NetWasm), keeps the execution isolate alive.
    /// On Kestrel, runs on the thread pool with top-level error capture.
    /// </summary>
    public void WaitUntil(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _backgroundTasks.Add(task);
    }

    /// <summary>
    /// Enqueues background work via a delegate.
    /// </summary>
    public void WaitUntil(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
#if NETWASM
        _backgroundTasks.Add(action());
#else
        _backgroundTasks.Add(Task.Run(action));
#endif
    }

    /// <summary>
    /// FastAPI / Hono alias for <see cref="WaitUntil(Func{Task})"/>.
    /// </summary>
    public void BackgroundTask(Func<Task> action) => WaitUntil(action);

    /// <summary>
    /// Emits a standard Server-Timing header (e.g. Server-Timing: db;dur=14.2;desc="query")
    /// visible in browser DevTools network waterfall.
    /// </summary>
    public void ServerTiming(string metric, double durationMs, string? description = null)
    {
        // Header values must never carry quotes, separators, or control
        // characters (response-header injection).
        var safeMetric = SanitizeServerTimingValue(metric);
        var durStr = durationMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var entry = description is { Length: > 0 }
            ? $"{safeMetric};dur={durStr};desc=\"{SanitizeServerTimingValue(description)}\""
            : $"{safeMetric};dur={durStr}";

        if (PendingHeaders.TryGetValue("Server-Timing", out var existing) && existing.Length > 0)
        {
            PendingHeaders["Server-Timing"] = $"{existing}, {entry}";
        }
        else
        {
            PendingHeaders["Server-Timing"] = entry;
        }
    }

    private static string SanitizeServerTimingValue(string value) =>
        value
            .Replace("\"", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(";", "")
            .Replace(",", "");

    /// <summary>
    /// Evaluates whether the current user is authorized to perform <paramref name="action"/> on <paramref name="resource"/>
    /// using policies registered via app.DefinePolicy&lt;T&gt;().
    /// </summary>
    public bool Can<TResource>(string action, TResource resource) =>
        _services.Policies.Can(User, action, resource);

    /// <summary>
    /// Authorizes the action or returns 403 Forbidden with a descriptive message.
    /// </summary>
    public bool Authorize<TResource>(
        string action,
        TResource resource,
        [NotNullWhen(false)] out ReptarResponse? forbiddenResponse)
    {
        if (Can(action, resource))
        {
            forbiddenResponse = null;
            return true;
        }
        forbiddenResponse = Forbidden($"You do not have permission to perform '{action}'.");
        return false;
    }

    public ReptarResponse Json<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value, int status = 200) =>
        ReptarResponse.Json(_services.Json.SerializeUtf8(value), status);

    public ReptarResponse JsonRaw(string json, int status = 200) =>
        Text(json, status, "application/json; charset=utf-8");

    public ReptarResponse Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8") => new()
    {
        Status = status,
        ContentType = contentType,
        Body = Encoding.UTF8.GetBytes(text),
    };

    public ReptarResponse Html(string html, int status = 200, string contentType = "text/html; charset=utf-8") =>
        Text(html, status, contentType);

    public ReptarResponse Status(int status) => new() { Status = status };

    public ReptarResponse Redirect(string url, int status = 302)
    {
        // Location must never carry control characters: CR/LF in a header value
        // is the response-splitting/header-injection primitive.
        if (url.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            throw new ArgumentException(
                "Redirect URL must not contain control characters.", nameof(url));

        var res = new ReptarResponse { Status = status };
        res.Headers["Location"] = url;
        return res;
    }

    public ReptarResponse Stream(byte[] body, string contentType, int status = 200) => new()
    {
        Status = status,
        ContentType = contentType,
        Body = body,
    };

    public ReptarResponse Stream(
        Func<CancellationToken, IAsyncEnumerable<byte[]>> body,
        string contentType,
        int status = 200) => new()
    {
        Status = status,
        ContentType = contentType,
        BodyStream = body,
    };

    public ReptarResponse Sse(
        IAsyncEnumerable<string> events,
        int status = 200) => Sse(_ => events, status);

    public ReptarResponse Sse(
        Func<CancellationToken, IAsyncEnumerable<string>> events,
        int status = 200) => new()
    {
        Status = status,
        ContentType = "text/event-stream",
        Headers =
        {
            ["cache-control"] = "no-cache",
            ["connection"] = "keep-alive",
        },
        BodyStream = cancellationToken => SseBody(events(cancellationToken)),
    };

    private static async IAsyncEnumerable<byte[]> SseBody(
        IAsyncEnumerable<string> events)
    {
        await foreach (var message in events)
        {
            // Multi-line payloads must stay a single data event: raw newlines
            // would otherwise inject new SSE fields into the stream.
            var framed = message
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace("\n", "\ndata: ");

            yield return Encoding.UTF8.GetBytes($"data: {framed}\n\n");
        }
    }

    public ReptarResponse Error(int status, string code, string message) =>
        _services.Errors.Error(this, status, code, message);

    public ReptarResponse NotFound(string message = "Not found.") =>
        Error(404, "not_found", message);

    public ReptarResponse Unauthorized(string message = "Unauthorized.") =>
        Error(401, "unauthorized", message);

    public ReptarResponse Forbidden(string message = "Forbidden.") =>
        Error(403, "forbidden", message);

    public ReptarResponse Conflict(string message = "Conflict.") =>
        Error(409, "conflict", message);

    public ReptarResponse BadRequest(string message = "Bad request.") =>
        Error(400, "bad_request", message);

    public ReptarResponse Invalid(IReadOnlyList<ValidationIssue> issues) =>
        _services.Errors.Validation(this, issues);
}

internal sealed record ServiceSet(
    IDbClient Db,
    IStorage Storage,
    IAiClient AI,
    IRateLimiter RateLimit,
    IKvClient Cache,
    IQueueClient Queues,
    IDurableBridge Durable,
    IMailClient Mail,
    IHttpClient Http,
    IClock Clock,
    ICrypto Crypto,
    IEnvironment Environment,
    IJsonCodec Json,
    IErrorMapper Errors,
    IAppLogger Log,
    IMailObserver? MailObserver,
    IWebSocketTransport WebSocket,
    IPolicyManager Policies);
