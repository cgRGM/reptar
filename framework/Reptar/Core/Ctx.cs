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
    public IDbClient Db => _services.Db;
    public IR2Client Storage => _services.Storage;
    public IAiClient AI => _services.AI;
    public IRateLimiter RateLimit => _services.RateLimit;
    public IKvClient Cache => _services.Cache;
    public IQueueClient Queues => _services.Queues;
    public IDurableBridge Durable => _services.Durable;
    public IHttpClient Http => _services.Http;
    public IClock Clock => _services.Clock;
    public ICrypto Crypto => _services.Crypto;
    public IEnvironment Environment => _services.Environment;
    public IJsonCodec JsonCodec => _services.Json;
    public CookieJar Cookies { get; }
    public SessionUser? User { get; set; }

    internal Dictionary<string, string> PendingHeaders { get; } = new();

    public string RequestId
    {
        get => _requestId ??= Ids.New("req");
        set => _requestId = value;
    }

    public void SetHeader(string name, string value) => PendingHeaders[name] = value;

    public ReptarResponse Json<T>(T value, int status = 200) =>
        ReptarResponse.Json(_services.Json.Serialize(value), status);

    public ReptarResponse Text(string text, int status = 200) => new()
    {
        Status = status,
        ContentType = "text/plain; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(text),
    };

    public ReptarResponse Status(int status) => new() { Status = status };

    public ReptarResponse Stream(byte[] body, string contentType, int status = 200) => new()
    {
        Status = status,
        ContentType = contentType,
        Body = body,
    };

    public ReptarResponse Error(int status, string code, string message) =>
        Json(new ErrorBody(code, message, RequestId), status);

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
        Json(
            new ValidationError(
                "validation_error",
                "Validation failed.",
                RequestId,
                issues.ToArray()),
            400);
}

internal sealed record ServiceSet(
    IDbClient Db,
    IR2Client Storage,
    IAiClient AI,
    IRateLimiter RateLimit,
    IKvClient Cache,
    IQueueClient Queues,
    IDurableBridge Durable,
    IHttpClient Http,
    IClock Clock,
    ICrypto Crypto,
    IEnvironment Environment,
    IJsonCodec Json);
