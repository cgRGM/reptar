namespace Reptar;

public sealed record PresignedUpload(
    string Url,
    string Key,
    string Method = "PUT",
    IReadOnlyDictionary<string, string>? Headers = null,
    int ExpiresInSeconds = 900);

public readonly record struct SniffedMediaType(string ContentType, string Extension);

public sealed record FileCheckResult(
    bool Ok,
    byte[] Data,
    string ContentType,
    string Extension,
    int ErrorStatus = 200,
    string ErrorCode = "",
    string ErrorMessage = "")
{
    public ReptarResponse ToError(Ctx ctx) =>
        ctx.Error(ErrorStatus, ErrorCode, ErrorMessage);
}

/// <summary>
/// Streaming variant of <see cref="FileCheckResult"/>. The body includes the
/// magic-byte prefix that was consumed during validation.
/// </summary>
public sealed record FileStreamCheckResult(
    bool Ok,
    Stream? Body,
    string ContentType,
    string Extension,
    int ErrorStatus = 200,
    string ErrorCode = "",
    string ErrorMessage = "")
{
    public ReptarResponse ToError(Ctx ctx) =>
        ctx.Error(ErrorStatus, ErrorCode, ErrorMessage);
}

public static class Files
{
    public static SniffedMediaType? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return new SniffedMediaType("image/png", "png");
        }

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return new SniffedMediaType("image/jpeg", "jpg");
        }

        // GIF87a / GIF89a
        if (data.Length >= 6 &&
            data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
            data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') &&
            data[5] == (byte)'a')
        {
            return new SniffedMediaType("image/gif", "gif");
        }

        // WebP: RIFF....WEBP
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return new SniffedMediaType("image/webp", "webp");
        }

        // ISO BMFF: ....ftyp (MP4, MOV, HEIC, AVIF)
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = data.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return new SniffedMediaType("image/avif", "avif");

            if (brand.SequenceEqual("heic"u8) ||
                brand.SequenceEqual("heix"u8) ||
                brand.SequenceEqual("hevc"u8) ||
                brand.SequenceEqual("hevx"u8) ||
                brand.SequenceEqual("mif1"u8) ||
                brand.SequenceEqual("msf1"u8))
            {
                return new SniffedMediaType("image/heic", "heic");
            }

            if (brand.SequenceEqual("qt  "u8))
                return new SniffedMediaType("video/quicktime", "mov");

            return new SniffedMediaType("video/mp4", "mp4");
        }

        // WebM / Matroska: 1A 45 DF A3
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return new SniffedMediaType("video/webm", "webm");

        // WAV: RIFF....WAVE
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
        {
            return new SniffedMediaType("audio/wav", "wav");
        }

        // OGG: OggS
        if (data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S')
            return new SniffedMediaType("audio/ogg", "ogg");

        // MP3 with ID3 tag
        if (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
            return new SniffedMediaType("audio/mpeg", "mp3");

        // PDF: %PDF-
        if (data.Length >= 5 &&
            data[0] == (byte)'%' && data[1] == (byte)'P' && data[2] == (byte)'D' &&
            data[3] == (byte)'F' && data[4] == (byte)'-')
        {
            return new SniffedMediaType("application/pdf", "pdf");
        }

        // WebAssembly: 00 61 73 6D
        if (data[0] == 0x00 && data[1] == 0x61 && data[2] == 0x73 && data[3] == 0x6D)
        {
            return new SniffedMediaType("application/wasm", "wasm");
        }

        // ZIP: 50 4B 03 04
        if (data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04)
        {
            return new SniffedMediaType("application/zip", "zip");
        }

        return null;
    }

    /// <summary>
    /// Best-effort content type from a file name or object key. Shared by every
    /// target (including NetWasm), unlike the static-file MIME resolver.
    /// </summary>
    public static string ContentTypeFor(string pathOrKey)
    {
        var slash = pathOrKey.LastIndexOf('/');
        var dot = pathOrKey.LastIndexOf('.');
        if (dot <= slash || dot == pathOrKey.Length - 1)
            return "application/octet-stream";

        var extension = pathOrKey[dot..].ToLowerInvariant();
        return extension switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".map" => "application/json; charset=utf-8",
            ".webmanifest" => "application/manifest+json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".heic" => "image/heic",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".wasm" => "application/wasm",
            ".txt" => "text/plain; charset=utf-8",
            ".md" => "text/markdown; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            _ => "application/octet-stream",
        };
    }

    public static FileCheckResult Check(
        FormFile? file,
        long maxBytes = 2_000_000,
        params string[]? types) =>
        file is null
            ? new FileCheckResult(false, [], "", "", 400, "bad_request", "File is required.")
            : Check(file.Data, maxBytes, types);

    public static FileCheckResult Check(
        byte[]? data,
        long maxBytes = 2_000_000,
        params string[]? types)
    {
        if (data is null || data.Length == 0)
            return new FileCheckResult(false, [], "", "", 400, "bad_request", "File payload is required.");

        if (data.Length > maxBytes)
            return new FileCheckResult(
                false,
                [],
                "",
                "",
                413,
                "payload_too_large",
                $"File exceeds maximum size of {maxBytes} bytes.");

        var sniffed = Sniff(data);
        if (sniffed is null)
        {
            return new FileCheckResult(
                false,
                [],
                "",
                "",
                415,
                "unsupported_media_type",
                "Unrecognized or unsupported binary file signature.");
        }

        var (contentType, extension) = sniffed.Value;

        if (types is { Length: > 0 } && !MatchesAnyType(contentType, types))
        {
            return new FileCheckResult(
                false,
                [],
                "",
                "",
                415,
                "unsupported_media_type",
                $"File type '{contentType}' is not permitted.");
        }

        return new FileCheckResult(true, data, contentType, extension);
    }

    /// <summary>
    /// Validates a streaming upload by peeking at its magic bytes, then returns
    /// a stream that replays those bytes before the rest of the body. The route
    /// should use <c>StreamBody(maxBytes)</c> to enforce the total size.
    /// </summary>
    public static async Task<FileStreamCheckResult> CheckStreamAsync(
        Stream? stream,
        params string[]? types)
    {
        if (stream is null)
        {
            return new FileStreamCheckResult(
                false,
                null,
                "",
                "",
                400,
                "bad_request",
                "File is required.");
        }

        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var current = await stream.ReadAsync(header.AsMemory(read));
            if (current == 0)
                break;
            read += current;
        }

        var check = Check(header.AsSpan(0, read).ToArray(), long.MaxValue, types);
        if (!check.Ok)
        {
            return new FileStreamCheckResult(
                false,
                null,
                "",
                "",
                check.ErrorStatus,
                check.ErrorCode,
                check.ErrorMessage);
        }

        return new FileStreamCheckResult(
            true,
            new PrefixedStream(header.AsSpan(0, read).ToArray(), stream),
            check.ContentType,
            check.Extension);
    }

    private static bool MatchesAnyType(string contentType, string[] allowedTypes)
    {
        foreach (var raw in allowedTypes)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0)
                continue;

            if (pattern == "*/*" || string.Equals(pattern, contentType, StringComparison.OrdinalIgnoreCase))
                return true;

            if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^1]; // e.g. "image/"
                if (contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Replays a validated magic-byte prefix before continuing with the original
/// request stream. Does not dispose the inner stream; the host owns it.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private int _offset;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_offset < _prefix.Length)
        {
            var copied = Math.Min(count, _prefix.Length - _offset);
            Array.Copy(_prefix, _offset, buffer, offset, copied);
            _offset += copied;
            return copied;
        }

        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var copied = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsSpan(_offset, copied).CopyTo(buffer.Span);
            _offset += copied;
            return copied;
        }

        return await _inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The host owns the request stream.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class StorageStreams
{
    /// <summary>Reads until the buffer is full or the stream reaches EOF.</summary>
    public static async Task<int> ReadFullAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}

public static class Storage
{
    public static IStorage R2(string? publicBaseUrl = null) =>
        ReptarApi.R2(publicBaseUrl);

    public static IStorage R2(R2StorageOptions options) =>
        ReptarApi.R2(options);

#if !NETWASM
    /// <summary>Local disk storage. Alias of <see cref="Local"/>.</summary>
    public static LocalStorage Disk(string rootDirectory = "uploads") =>
        new(rootDirectory);

    public static LocalStorage Local(string rootDirectory = "uploads") =>
        new(rootDirectory);

    public static S3Storage S3(S3StorageOptions options) =>
        new(options);
#endif
}
