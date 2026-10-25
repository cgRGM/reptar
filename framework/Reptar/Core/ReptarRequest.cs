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

    public Func<Dictionary<string, string>>? HeadersFactory { get; init; }
    public Func<string, string?>? HeaderLookup { get; init; }

    public Dictionary<string, string> Headers
    {
        get => _headers ??= HeadersFactory?.Invoke() ?? new(StringComparer.OrdinalIgnoreCase);
        init => _headers = value;
    }

    public Dictionary<string, string> Cookies
    {
        get => _cookies ??= new(StringComparer.OrdinalIgnoreCase);
        init => _cookies = value;
    }

    public byte[]? Body { get; init; }
    public bool BodyTooLarge { get; init; }
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

        foreach (var pair in Query.Split('&'))
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
