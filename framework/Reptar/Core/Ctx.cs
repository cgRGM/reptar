using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class Ctx
{
    private readonly ServiceSet _services;
    private string? _requestId;

    internal Ctx(ReptarRequest request, ServiceSet services)
    {
        Req = request;
        _services = services;
        Cookies = new CookieJar(request);
    }

    public ReptarRequest Req { get; }

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

    public async Task<SessionUser?> UserAsync()
    {
        if (User is not null) return User;
        User = await Auth.CurrentUser(this);
        return User;
    }

    internal Dictionary<string, string> PendingHeaders { get; } = new();

    public string RequestId
    {
        get => _requestId ??= Ids.New("req");
        set => _requestId = value;
    }

    public void SetHeader(string name, string value) => PendingHeaders[name] = value;

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
            yield return Encoding.UTF8.GetBytes($"data: {message}\n\n");
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
    IHttpClient Http,
    IClock Clock,
    ICrypto Crypto,
    IEnvironment Environment,
    IJsonCodec Json,
    IErrorMapper Errors,
    IAppLogger Log,
    IWebSocketTransport WebSocket);
