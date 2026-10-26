#if !NETWASM
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Reptar;

public sealed partial class S3Storage
{
    private SignedRequest SignObjectRequest(
        string method,
        string key,
        byte[] payload,
        Dictionary<string, string>? extraHeaders)
    {
        var (hostHeader, baseUri, canonicalUri) = BuildObjectEndpoint(key);
        return SignRequest(
            method,
            hostHeader,
            baseUri,
            canonicalUri,
            payload,
            null,
            extraHeaders);
    }

    private SignedRequest SignRequest(
        string method,
        string hostHeader,
        string baseUri,
        string canonicalUri,
        byte[] payload,
        SortedDictionary<string, string>? query,
        Dictionary<string, string>? extraHeaders)
    {
        var now = _clock().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Sha256Hex(payload);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = hostHeader,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
                headers[name.ToLowerInvariant()] = value;
        }

        var canonicalHeaders = new StringBuilder();
        var signedNames = new List<string>(headers.Count);
        foreach (var name in headers.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            signedNames.Add(name);
            canonicalHeaders
                .Append(name)
                .Append(':')
                .Append(CanonicalizeHeaderValue(headers[name]))
                .Append('\n');
        }

        var signedHeaders = string.Join(";", signedNames);
        var canonicalQuery = BuildCanonicalQuery(query);
        var canonicalRequest = string.Join(
            "\n",
            method,
            canonicalUri,
            canonicalQuery,
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";
        var signature = SignString(
            canonicalRequest,
            amzDate,
            credentialScope,
            dateStamp);

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        headers["authorization"] = authorization;

        var url = canonicalQuery.Length == 0
            ? $"{baseUri}{canonicalUri}"
            : $"{baseUri}{canonicalUri}?{canonicalQuery}";

        return new SignedRequest(url, headers);
    }

    private string SignString(
        string canonicalRequest,
        string amzDate,
        string credentialScope,
        string dateStamp)
    {
        var stringToSign = string.Join(
            "\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(
            options.SecretAccessKey,
            dateStamp,
            options.Region,
            "s3");

        return HmacSha256Hex(signingKey, stringToSign);
    }

    private (string HostHeader, string BaseUri, string CanonicalUri) BuildObjectEndpoint(string key)
    {
        var endpointUri = new Uri(options.Endpoint.TrimEnd('/'));
        var encodedKey = string.Join(
            "/",
            key.TrimStart('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(AwsUriEncode));

        if (options.ForcePathStyle)
        {
            var hostHeader = HostHeader(endpointUri);
            var baseUri = $"{endpointUri.Scheme}://{hostHeader}";
            var basePath = endpointUri.AbsolutePath.TrimEnd('/');
            var canonicalUri = $"{basePath}/{AwsUriEncode(options.Bucket)}/{encodedKey}";
            return (hostHeader, baseUri, canonicalUri);
        }

        var virtualHost = $"{options.Bucket}.{endpointUri.Host}";
        var virtualHostHeader = endpointUri.IsDefaultPort
            ? virtualHost
            : $"{virtualHost}:{endpointUri.Port}";
        var virtualBaseUri = $"{endpointUri.Scheme}://{virtualHostHeader}";
        var virtualBasePath = endpointUri.AbsolutePath.TrimEnd('/');
        var virtualCanonicalUri = $"{virtualBasePath}/{encodedKey}";
        return (virtualHostHeader, virtualBaseUri, virtualCanonicalUri);
    }

    private (string HostHeader, string BaseUri, string CanonicalUri) BuildBucketEndpoint()
    {
        var endpointUri = new Uri(options.Endpoint.TrimEnd('/'));

        if (options.ForcePathStyle)
        {
            var hostHeader = HostHeader(endpointUri);
            var baseUri = $"{endpointUri.Scheme}://{hostHeader}";
            var basePath = endpointUri.AbsolutePath.TrimEnd('/');
            var canonicalUri = $"{basePath}/{AwsUriEncode(options.Bucket)}";
            return (hostHeader, baseUri, canonicalUri);
        }

        var virtualHost = $"{options.Bucket}.{endpointUri.Host}";
        var virtualHostHeader = endpointUri.IsDefaultPort
            ? virtualHost
            : $"{virtualHost}:{endpointUri.Port}";
        var virtualBaseUri = $"{endpointUri.Scheme}://{virtualHostHeader}";
        var virtualBasePath = endpointUri.AbsolutePath.TrimEnd('/');
        var virtualCanonicalUri = virtualBasePath.Length == 0 ? "/" : virtualBasePath;
        return (virtualHostHeader, virtualBaseUri, virtualCanonicalUri);
    }

    private static string HostHeader(Uri endpointUri) =>
        endpointUri.IsDefaultPort
            ? endpointUri.Host
            : $"{endpointUri.Host}:{endpointUri.Port}";

    private static StorageObject ParseObject(
        string key,
        Dictionary<string, string> headers)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                metadata[name["x-amz-meta-".Length..]] = value;
        }

        var cacheControl = GetHeader(headers, "cache-control");
        if (!string.IsNullOrWhiteSpace(cacheControl))
            metadata["cache-control"] = cacheControl;

        return new StorageObject(
            key,
            TryParseLong(GetHeader(headers, "content-length")) ?? 0,
            GetHeader(headers, "content-type") ?? "",
            TrimEtag(GetHeader(headers, "etag")),
            TryParseDate(GetHeader(headers, "last-modified")),
            metadata.Count == 0 ? null : metadata);
    }

    private static StoragePage ParseList(byte[] xml)
    {
        XDocument document;
        using (var stream = new MemoryStream(xml))
            document = XDocument.Load(stream);

        var root = document.Root;
        if (root is null)
            return StoragePage.Empty;

        var ns = root.Name.Namespace;
        var items = new List<StorageObject>();
        foreach (var content in root.Elements(ns + "Contents"))
        {
            var key = content.Element(ns + "Key")?.Value;
            if (string.IsNullOrEmpty(key))
                continue;

            items.Add(new StorageObject(
                key,
                TryParseLong(content.Element(ns + "Size")?.Value) ?? 0,
                "",
                TrimEtag(content.Element(ns + "ETag")?.Value),
                TryParseDate(content.Element(ns + "LastModified")?.Value)));
        }

        var truncated = string.Equals(
            root.Element(ns + "IsTruncated")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

        var cursor = truncated
            ? root.Element(ns + "NextContinuationToken")?.Value
            : null;

        return new StoragePage(items, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static void ThrowForStatus(int status, string key, string operation)
    {
        if (status is >= 200 and < 300)
            return;

        var code = status switch
        {
            401 => StorageErrorCode.Unauthorized,
            403 => StorageErrorCode.Forbidden,
            404 => StorageErrorCode.NotFound,
            409 => StorageErrorCode.Conflict,
            412 => StorageErrorCode.PreconditionFailed,
            413 => StorageErrorCode.TooLarge,
            416 => StorageErrorCode.RangeNotSatisfiable,
            >= 500 => StorageErrorCode.Unavailable,
            _ => StorageErrorCode.Unknown,
        };

        throw new StorageException(
            code,
            $"S3 {operation} failed with status {status}.",
            key);
    }

    private static string? GetHeader(
        Dictionary<string, string> headers,
        string name)
    {
        if (headers.TryGetValue(name, out var value))
            return value;

        foreach (var (key, candidate) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static string TrimEtag(string? etag) =>
        string.IsNullOrWhiteSpace(etag) ? "" : etag.Trim().Trim('"');

    private static long? TryParseLong(string? value) =>
        long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? TryParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static bool TryParseContentRange(
        string? value,
        out long start,
        out long end,
        out long total)
    {
        start = 0;
        end = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangeAndTotal = value["bytes ".Length..].Split('/');
        if (rangeAndTotal.Length != 2)
            return false;

        var range = rangeAndTotal[0].Split('-');
        if (range.Length != 2)
            return false;

        return long.TryParse(
                   range[0],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out start) &&
               long.TryParse(
                   range[1],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out end) &&
               long.TryParse(
                   rangeAndTotal[1],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out total);
    }

    private static int ClampExpiry(TimeSpan expiresIn) =>
        (int)Math.Clamp(expiresIn.TotalSeconds, 1, 604_800);

    private static string BuildCanonicalQuery(
        SortedDictionary<string, string>? query)
    {
        if (query is null || query.Count == 0)
            return "";

        return string.Join(
            "&",
            query.Select(kvp => $"{AwsUriEncode(kvp.Key)}={AwsUriEncode(kvp.Value)}"));
    }

    private static string CanonicalizeHeaderValue(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var character in trimmed)
        {
            if (character is ' ' or '\t')
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString();
    }

    private static byte[] DeriveSigningKey(
        string secretAccessKey,
        string dateStamp,
        string region,
        string service)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        var kDate = HMACSHA256.HashData(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, "aws4_request"u8);
    }

    private static string HmacSha256Hex(byte[] key, string data) =>
        Convert.ToHexString(
                HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data)))
            .ToLowerInvariant();

    private static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string AwsUriEncode(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if ((b is >= (byte)'A' and <= (byte)'Z') ||
                (b is >= (byte)'a' and <= (byte)'z') ||
                (b is >= (byte)'0' and <= (byte)'9') ||
                b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%')
                    .Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private sealed record SignedRequest(
        string Url,
        Dictionary<string, string> Headers);
}
#endif
