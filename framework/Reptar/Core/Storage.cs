using System.Globalization;
#if !NETWASM
using System.Security.Cryptography;
#endif
using System.Text;

namespace Reptar;

public sealed record PresignedUpload(
    string Url,
    string Key,
    string Method = "PUT",
    IReadOnlyDictionary<string, string>? Headers = null,
    int ExpiresInSeconds = 900);

public interface IStorage
{
    Task<string> PutAsync(string key, byte[] body, string contentType);
    Task<byte[]?> GetAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task DeleteAsync(string key);

    Task<PresignedUpload> PresignPutAsync(
        string key,
        string contentType,
        TimeSpan expiresIn,
        long? maxBytes = null) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support presigned PUT URLs. Configure S3Storage (or R2 S3 credentials) to generate presigned URLs.");
}

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

public static class Storage
{
    public static IStorage R2() => ReptarApi.R2();

#if !NETWASM
    public static LocalStorage Local(string rootDirectory = "uploads") =>
        new(rootDirectory);

    public static S3Storage S3(S3StorageOptions options) =>
        new(options);
#endif
}

#if !NETWASM
public class LocalStorage : IStorage, IR2Client
{
    private readonly string _fullRoot;
    private readonly string _fullRootWithSeparator;

    public LocalStorage(string rootDirectory = "uploads")
    {
        Directory.CreateDirectory(rootDirectory);
        _fullRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _fullRootWithSeparator = _fullRoot + Path.DirectorySeparatorChar;
    }

    public async Task<string> PutAsync(string key, byte[] body, string contentType)
    {
        var fullPath = ResolvePath(key);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(fullPath, body);
        return key;
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        var fullPath = ResolvePath(key);
        return File.Exists(fullPath)
            ? await File.ReadAllBytesAsync(fullPath)
            : null;
    }

    public Task<bool> ExistsAsync(string key) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    public Task DeleteAsync(string key)
    {
        var fullPath = ResolvePath(key);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    protected string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOf('\0') >= 0)
            throw new ArgumentException("Invalid storage key.", nameof(key));

        var normalized = key
            .Replace('\\', '/')
            .TrimStart('/');

        if (normalized.Length == 0)
            throw new ArgumentException("Invalid storage key.", nameof(key));

        var segments = normalized.Split('/');
        foreach (var seg in segments)
        {
            if (seg is "" or "." or "..")
                throw new ArgumentException("Path traversal in storage key is not permitted.", nameof(key));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_fullRoot, Path.Combine(segments)));
        if (!fullPath.StartsWith(_fullRootWithSeparator, StringComparison.Ordinal))
            throw new ArgumentException("Path traversal in storage key is not permitted.", nameof(key));

        return fullPath;
    }
}

public sealed class S3StorageOptions
{
    public required string Endpoint { get; init; }
    public required string Bucket { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public string Region { get; init; } = "auto";
    public bool ForcePathStyle { get; init; } = true;
    public IHttpClient? HttpClient { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }
}

/// <summary>
/// Zero-dependency S3 / Cloudflare R2 object storage client using AWS Signature V4.
/// Supports presigned PUT uploads (<see cref="PresignPutAsync"/>) as well as direct object operations.
/// </summary>
public sealed class S3Storage(S3StorageOptions options) : IStorage, IR2Client
{
    private readonly IHttpClient _http = options.HttpClient ?? new DefaultHttpClient();
    private readonly Func<DateTimeOffset> _clock = options.Clock ?? (() => DateTimeOffset.UtcNow);

    public Task<PresignedUpload> PresignPutAsync(
        string key,
        string contentType,
        TimeSpan expiresIn,
        long? maxBytes = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage key is required.", nameof(key));

        var normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();

        var expiresSeconds = (int)Math.Clamp(expiresIn.TotalSeconds, 1, 604_800);
        var now = _clock().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var (hostHeader, baseUri, canonicalUri) = BuildObjectEndpoint(key);
        var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";
        var credential = $"{options.AccessKeyId}/{credentialScope}";

        var queryParts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = credential,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "content-type;host",
        };

        var canonicalQuery = string.Join(
            "&",
            queryParts.Select(kvp => $"{AwsUriEncode(kvp.Key)}={AwsUriEncode(kvp.Value)}"));

        var canonicalHeaders = $"content-type:{normalizedContentType}\nhost:{hostHeader}\n";
        var canonicalRequest = string.Join(
            "\n",
            "PUT",
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            "content-type;host",
            "UNSIGNED-PAYLOAD");

        var stringToSign = string.Join(
            "\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(options.SecretAccessKey, dateStamp, options.Region, "s3");
        var signature = HmacSha256Hex(signingKey, stringToSign);

        var presignedUrl = $"{baseUri}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = normalizedContentType,
        };

        return Task.FromResult(new PresignedUpload(
            presignedUrl,
            key,
            "PUT",
            headers,
            expiresSeconds));
    }

    public async Task<string> PutAsync(string key, byte[] body, string contentType)
    {
        var (url, headers) = SignHeaderRequest("PUT", key, body, contentType);
        var response = await _http.FetchAsync(url, "PUT", headers, Encoding.Latin1.GetString(body));
        if (response.Status is < 200 or >= 300)
            throw new InvalidOperationException($"S3 PUT failed with status {response.Status}.");

        return key;
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        var (url, headers) = SignHeaderRequest("GET", key, [], null);
        var response = await _http.FetchAsync(url, "GET", headers);
        if (response.Status == 404)
            return null;
        if (response.Status is < 200 or >= 300)
            throw new InvalidOperationException($"S3 GET failed with status {response.Status}.");

        return Encoding.UTF8.GetBytes(response.Body);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var (url, headers) = SignHeaderRequest("HEAD", key, [], null);
        var response = await _http.FetchAsync(url, "HEAD", headers);
        return response.Status is >= 200 and < 300;
    }

    public async Task DeleteAsync(string key)
    {
        var (url, headers) = SignHeaderRequest("DELETE", key, [], null);
        await _http.FetchAsync(url, "DELETE", headers);
    }

    private (string Url, Dictionary<string, string> Headers) SignHeaderRequest(
        string method,
        string key,
        byte[] payload,
        string? contentType)
    {
        var now = _clock().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Sha256Hex(payload);

        var (hostHeader, baseUri, canonicalUri) = BuildObjectEndpoint(key);
        var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";

        var signedHeaders = contentType is not null
            ? "content-type;host;x-amz-content-sha256;x-amz-date"
            : "host;x-amz-content-sha256;x-amz-date";

        var canonicalHeaders = contentType is not null
            ? $"content-type:{contentType.Trim()}\nhost:{hostHeader}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n"
            : $"host:{hostHeader}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        var canonicalRequest = string.Join(
            "\n",
            method,
            canonicalUri,
            "",
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join(
            "\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(options.SecretAccessKey, dateStamp, options.Region, "s3");
        var signature = HmacSha256Hex(signingKey, stringToSign);
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = hostHeader,
            ["x-amz-date"] = amzDate,
            ["x-amz-content-sha256"] = payloadHash,
            ["Authorization"] = authorization,
        };

        if (contentType is not null)
            headers["Content-Type"] = contentType.Trim();

        return ($"{baseUri}{canonicalUri}", headers);
    }

    private (string HostHeader, string BaseUri, string CanonicalUri) BuildObjectEndpoint(string key)
    {
        var endpointUri = new Uri(options.Endpoint.TrimEnd('/'));
        var encodedKey = string.Join(
            "/",
            key.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(AwsUriEncode));

        if (options.ForcePathStyle)
        {
            var hostHeader = endpointUri.IsDefaultPort
                ? endpointUri.Host
                : $"{endpointUri.Host}:{endpointUri.Port}";
            var baseUri = $"{endpointUri.Scheme}://{hostHeader}";
            var basePath = endpointUri.AbsolutePath.TrimEnd('/');
            var canonicalUri = $"{basePath}/{AwsUriEncode(options.Bucket)}/{encodedKey}";
            return (hostHeader, baseUri, canonicalUri);
        }
        else
        {
            var virtualHost = $"{options.Bucket}.{endpointUri.Host}";
            var hostHeader = endpointUri.IsDefaultPort
                ? virtualHost
                : $"{virtualHost}:{endpointUri.Port}";
            var baseUri = $"{endpointUri.Scheme}://{hostHeader}";
            var basePath = endpointUri.AbsolutePath.TrimEnd('/');
            var canonicalUri = $"{basePath}/{encodedKey}";
            return (hostHeader, baseUri, canonicalUri);
        }
    }

    private static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        var kDate = HMACSHA256.HashData(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, "aws4_request"u8);
    }

    private static string HmacSha256Hex(byte[] key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string AwsUriEncode(string value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if ((b is >= (byte)'A' and <= (byte)'Z') ||
                (b is >= (byte)'a' and <= (byte)'z') ||
                (b is >= (byte)'0' and <= (byte)'9') ||
                b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }
}
#endif
