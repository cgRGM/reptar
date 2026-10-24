using System.Text;

namespace Reptar;

public sealed record FormFile(
    string Name,
    string? FileName,
    string? ContentType,
    byte[] Data);

public sealed class MultipartForm
{
    private readonly Dictionary<string, string> _fields;
    private readonly List<FormFile> _files;

    private MultipartForm(
        Dictionary<string, string> fields,
        List<FormFile> files)
    {
        _fields = fields;
        _files = files;
    }

    public IReadOnlyDictionary<string, string> Fields => _fields;
    public IReadOnlyList<FormFile> Files => _files;

    public string? Value(string name) =>
        _fields.TryGetValue(name, out var value) ? value : null;

    public FormFile? File(string name) =>
        _files.FirstOrDefault(file =>
            string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));

    public static MultipartForm? TryParse(
        string? contentType,
        byte[]? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(contentType))
            return null;

        var boundary = Boundary(contentType);

        if (boundary is null)
            return null;

        var text = Encoding.Latin1.GetString(body);
        var delimiter = $"--{boundary}";
        var segments = text.Split(delimiter);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FormFile>();

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment;

            if (segment.StartsWith("--", StringComparison.Ordinal))
                break;

            segment = segment.TrimStart('\r', '\n');

            if (segment.Length == 0)
                continue;

            var headerEnd = segment.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (headerEnd < 0)
                continue;

            var headers = segment[..headerEnd];
            var content = segment[(headerEnd + 4)..];

            if (content.EndsWith("\r\n", StringComparison.Ordinal))
                content = content[..^2];

            var disposition = Header(headers, "content-disposition");

            if (disposition is null)
                continue;

            var name = Parameter(disposition, "name");

            if (string.IsNullOrEmpty(name))
                continue;

            var fileName = Parameter(disposition, "filename");
            var partType = Header(headers, "content-type");
            var data = Encoding.Latin1.GetBytes(content);

            if (fileName is null)
            {
                fields[name] = Encoding.UTF8.GetString(data);
            }
            else
            {
                files.Add(new FormFile(name, fileName, partType, data));
            }
        }

        return new MultipartForm(fields, files);
    }

    private static string? Boundary(string contentType)
    {
        foreach (var part in contentType.Split(';'))
        {
            var trimmed = part.Trim();

            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed["boundary=".Length..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static string? Header(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var separator = line.IndexOf(':');

            if (separator <= 0)
                continue;

            if (line[..separator].Trim().Equals(
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private static string? Parameter(string header, string name)
    {
        foreach (var part in header.Split(';'))
        {
            var trimmed = part.Trim();
            var prefix = $"{name}=";

            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[prefix.Length..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            return value;
        }

        return null;
    }
}
