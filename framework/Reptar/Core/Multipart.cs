using System.Text;

namespace Reptar;

public sealed record FormFile(
    string Name,
    string? FileName,
    string? ContentType,
    byte[] Data)
{
    public int Length => Data.Length;

    public string? SniffedContentType => Files.Sniff(Data)?.ContentType;
}

public sealed class MultipartOptions
{
    public int MaxTotalBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxFiles { get; set; } = 10;
    public int MaxFields { get; set; } = 100;
}

public sealed class MultipartForm
{
    private static readonly MultipartOptions DefaultOptions = new();
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
        byte[]? body,
        MultipartOptions? options = null) =>
        body is null ? null : TryParse(contentType, body.AsSpan(), options);

    public static MultipartForm? TryParse(
        string? contentType,
        ReadOnlySpan<byte> body,
        MultipartOptions? options = null)
    {
        if (body.IsEmpty || string.IsNullOrWhiteSpace(contentType))
            return null;

        var opts = options ?? DefaultOptions;
        if (body.Length > opts.MaxTotalBytes)
            return null;

        var boundary = Boundary(contentType);
        if (boundary is null || boundary.Length > 70)
            return null;

        var delimiterBytes = Encoding.ASCII.GetBytes($"--{boundary}");
        var delimiter = delimiterBytes.AsSpan();

        var firstPos = body.IndexOf(delimiter);
        if (firstPos < 0)
            return null;

        var cursor = body[(firstPos + delimiter.Length)..];
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FormFile>();
        var sawTerminator = false;

        while (!cursor.IsEmpty)
        {
            // Check for closing "--" right after "--boundary"
            if (cursor.Length >= 2 && cursor[0] == (byte)'-' && cursor[1] == (byte)'-')
            {
                sawTerminator = true;
                break;
            }

            // Advance past CRLF or LF after boundary line
            if (cursor.Length >= 2 && cursor[0] == (byte)'\r' && cursor[1] == (byte)'\n')
                cursor = cursor[2..];
            else if (cursor[0] == (byte)'\n')
                cursor = cursor[1..];
            else
                return null;

            var nextDelimiter = cursor.IndexOf(delimiter);
            if (nextDelimiter < 0)
                return null;

            var part = cursor[..nextDelimiter];
            cursor = cursor[(nextDelimiter + delimiter.Length)..];

            // Strip trailing CRLF or LF preceding the next "--boundary"
            if (part.Length >= 2 && part[^2] == (byte)'\r' && part[^1] == (byte)'\n')
                part = part[..^2];
            else if (part.Length >= 1 && part[^1] == (byte)'\n')
                part = part[..^1];

            if (part.IsEmpty)
                continue;

            var headerEnd = part.IndexOf("\r\n\r\n"u8);
            var separatorLength = 4;
            if (headerEnd < 0)
            {
                headerEnd = part.IndexOf("\n\n"u8);
                separatorLength = 2;
            }

            if (headerEnd < 0)
                return null;

            var headerText = Encoding.UTF8.GetString(part[..headerEnd]);
            var payload = part[(headerEnd + separatorLength)..];

            var disposition = Header(headerText, "content-disposition");
            if (disposition is null)
                continue;

            var name = Parameter(disposition, "name");
            if (string.IsNullOrEmpty(name))
                continue;

            var fileName = Parameter(disposition, "filename");
            var partType = Header(headerText, "content-type");

            if (fileName is null)
            {
                if (fields.Count >= opts.MaxFields)
                    return null;

                fields[name] = Encoding.UTF8.GetString(payload);
            }
            else
            {
                if (files.Count >= opts.MaxFiles || payload.Length > opts.MaxFileBytes)
                    return null;

                files.Add(new FormFile(
                    name,
                    SanitizeFileName(fileName),
                    partType,
                    payload.ToArray()));
            }
        }

        return sawTerminator ? new MultipartForm(fields, files) : null;
    }

    private static string SanitizeFileName(string rawFileName)
    {
        var lastSlash = Math.Max(rawFileName.LastIndexOf('/'), rawFileName.LastIndexOf('\\'));
        return lastSlash >= 0 ? rawFileName[(lastSlash + 1)..] : rawFileName;
    }

    private static string? Boundary(string contentType)
    {
        if (!contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
            return null;

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
        foreach (var rawLine in headers.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
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
