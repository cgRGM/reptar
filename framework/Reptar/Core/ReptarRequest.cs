using System.Text;

namespace Reptar;

public sealed class ReptarRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string Query { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Cookies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; init; }
    public Dictionary<string, string> RouteParams { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    private MultipartForm? _form;

    public MultipartForm? Form =>
        _form ??= MultipartForm.TryParse(Header("content-type"), Body);

    public string? Param(string name) =>
        RouteParams.TryGetValue(name, out var value) ? value : null;

    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

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

    public string? BodyText => Body is null ? null : Encoding.UTF8.GetString(Body);
}
