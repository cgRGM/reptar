using System.Text;

namespace Reptar;

public sealed class ReptarRequest
{
    private Dictionary<string, string>? _headers;
    private Dictionary<string, string>? _cookies;

    public required string Method { get; init; }
    public required string Path { get; init; }
    public string Query { get; init; } = "";
    public string? RemoteIp { get; init; }

    /// <summary>
    /// Host-assigned request id (the Worker envelope id). Native hosts leave
    /// this null and <see cref="Ctx.RequestId"/> generates one.
    /// </summary>
    public string? RequestId { get; init; }

    public Func<Dictionary<string, string>>? HeadersFactory { get; init; }
    public Func<string, string?>? HeaderLookup { get; init; }

    public Dictionary<string, string> Headers
    {
        get => _headers ??= HeadersFactory?.Invoke() ?? new(StringComparer.OrdinalIgnoreCase);
        init => _headers = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, string> Cookies
    {
        get => _cookies ??= new(StringComparer.OrdinalIgnoreCase);
        init => _cookies = value;
    }

    public byte[]? Body { get; init; }

    /// <summary>
    /// Raw request body stream for routes registered with
    /// <see cref="RouteBuilder.StreamBody"/>. Kestrel exposes the live request
    /// body; the Worker host exposes a memory stream over the decoded envelope.
    /// </summary>
    public Stream? BodyStream { get; init; }

    public bool BodyTooLarge { get; init; }

    /// <summary>
    /// Returns the streaming body when present, otherwise a memory stream over
    /// the buffered body. Null when the request has no body.
    /// </summary>
    public Stream? OpenBody() =>
        BodyStream ?? (Body is null ? null : new MemoryStream(Body, writable: false));

    /// <summary>
    /// Host cancellation signal for this request. Kestrel/Lambda populate it;
    /// Workers and the in-memory test client leave it at CancellationToken.None.
    /// </summary>
    public CancellationToken Aborted { get; set; } = CancellationToken.None;

    public Dictionary<string, string> RouteParams { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    private MultipartForm? _form;

    public MultipartForm? Form =>
        _form ??= MultipartForm.TryParse(Header("content-type"), Body);

    public string? Param(string name) =>
        RouteParams.TryGetValue(name, out var value) ? value : null;

    public string? Header(string name)
    {
        if (_headers is not null)
            return _headers.TryGetValue(name, out var value) ? value : null;

        if (HeaderLookup is not null)
            return HeaderLookup(name);

        return Headers.TryGetValue(name, out var fallback) ? fallback : null;
    }

    public string? QueryValue(string name)
    {
        if (Query.Length == 0)
            return null;

        var raw = Query.StartsWith('?') ? Query[1..] : Query;

        foreach (var pair in raw.Split('&'))
        {
            var index = pair.IndexOf('=');

            if (index < 0)
            {
                if (pair == name)
                    return "";

                continue;
            }

            if (pair[..index] == name)
                return Uri.UnescapeDataString(pair[(index + 1)..]);
        }

        return null;
    }

    public string QueryValue(string name, string defaultValue) =>
        QueryValue(name) ?? defaultValue;

    public string? BodyText => Body is null ? null : Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Thrown when a streaming request body crosses its route's byte limit. The
/// request pipeline maps it to a 413 instead of a generic 500.
/// </summary>
public sealed class PayloadTooLargeException(long limit)
    : Exception($"Request body exceeds the maximum allowed size of {limit} bytes.")
{
    public long Limit { get; } = limit;
}
