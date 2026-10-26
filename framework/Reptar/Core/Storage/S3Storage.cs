#if !NETWASM
using System.Globalization;
using System.Text;

namespace Reptar;

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

    /// <summary>Optional CDN/custom-domain base URL used by <see cref="S3Storage.PublicUrl"/>.</summary>
    public string? PublicBaseUrl { get; init; }
}

/// <summary>
/// Zero-dependency S3 / Cloudflare R2 object storage client using AWS Signature V4.
/// Supports direct object operations, metadata/HEAD, byte ranges, paginated listing,
/// presigned uploads, and presigned downloads.
/// </summary>
public sealed partial class S3Storage(S3StorageOptions options) : IStorage, IR2Client
{
    private readonly IHttpClient _http = options.HttpClient ?? new DefaultHttpClient();
    private readonly Func<DateTimeOffset> _clock = options.Clock ?? (() => DateTimeOffset.UtcNow);

    public StorageCapabilities Capabilities
    {
        get
        {
            var features =
                StorageFeatures.Metadata |
                StorageFeatures.RangeReads |
                StorageFeatures.Listing |
                StorageFeatures.PresignUpload |
                StorageFeatures.PresignDownload |
                StorageFeatures.ConditionalPut;

            if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
                features |= StorageFeatures.PublicUrls;

            return new StorageCapabilities(features);
        }
    }

    public Task<string> PutAsync(string key, byte[] body, string contentType) =>
        PutAsync(key, body, contentType, new StoragePutOptions());

    public async Task<string> PutAsync(
        string key,
        byte[] body,
        string contentType,
        StoragePutOptions putOptions)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(contentType))
            extra["content-type"] = contentType.Trim();
        if (!string.IsNullOrWhiteSpace(putOptions.CacheControl))
            extra["cache-control"] = putOptions.CacheControl.Trim();
        if (!putOptions.Overwrite)
            extra["if-none-match"] = "*";
        if (putOptions.Metadata is { Count: > 0 })
        {
            foreach (var (name, value) in putOptions.Metadata)
                extra[$"x-amz-meta-{name}"] = value;
        }

        var signed = SignObjectRequest("PUT", normalizedKey, body, extra);
        var response = await _http.FetchBytesAsync(
            signed.Url,
            "PUT",
            signed.Headers,
            body,
            contentType);

        ThrowForStatus(response.Status, normalizedKey, "PUT");
        return normalizedKey;
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var signed = SignObjectRequest("GET", normalizedKey, [], null);
        var response = await _http.FetchBytesAsync(signed.Url, "GET", signed.Headers);
        if (response.Status == 404)
            return null;

        ThrowForStatus(response.Status, normalizedKey, "GET");
        return response.Body;
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var signed = SignObjectRequest("HEAD", normalizedKey, [], null);
        var response = await _http.FetchBytesAsync(signed.Url, "HEAD", signed.Headers);
        if (response.Status == 404)
            return false;

        ThrowForStatus(response.Status, normalizedKey, "HEAD");
        return true;
    }

    public async Task DeleteAsync(string key)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var signed = SignObjectRequest("DELETE", normalizedKey, [], null);
        var response = await _http.FetchBytesAsync(signed.Url, "DELETE", signed.Headers);
        if (response.Status == 404)
            return;

        ThrowForStatus(response.Status, normalizedKey, "DELETE");
    }

    public async Task<StorageObject?> HeadAsync(string key)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var signed = SignObjectRequest("HEAD", normalizedKey, [], null);
        var response = await _http.FetchBytesAsync(signed.Url, "HEAD", signed.Headers);
        if (response.Status == 404)
            return null;

        ThrowForStatus(response.Status, normalizedKey, "HEAD");
        return ParseObject(normalizedKey, response.Headers);
    }

    public async Task<StorageRange?> GetRangeAsync(string key, long offset, long length)
    {
        if (offset < 0)
        {
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                "Range offset must not be negative.",
                key);
        }

        var normalizedKey = StorageKey.Normalize(key);
        var end = length > 0 ? offset + length - 1 : (long?)null;
        var rangeHeader = end is null
            ? $"bytes={offset.ToString(CultureInfo.InvariantCulture)}-"
            : $"bytes={offset.ToString(CultureInfo.InvariantCulture)}-{end.Value.ToString(CultureInfo.InvariantCulture)}";

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["range"] = rangeHeader,
        };

        var signed = SignObjectRequest("GET", normalizedKey, [], extra);
        var response = await _http.FetchBytesAsync(signed.Url, "GET", signed.Headers);
        if (response.Status == 404)
            return null;

        if (response.Status == 416)
        {
            throw new StorageException(
                StorageErrorCode.RangeNotSatisfiable,
                "Requested range is not satisfiable.",
                normalizedKey);
        }

        ThrowForStatus(response.Status, normalizedKey, "GET range");

        var data = response.Body;
        var resolvedOffset = offset;
        var totalSize = (long)data.Length;

        if (response.Status == 206)
        {
            var contentRange = GetHeader(response.Headers, "content-range");
            if (TryParseContentRange(contentRange, out var start, out var _, out var total))
            {
                resolvedOffset = start;
                totalSize = total;
            }
            else
            {
                // Some compatible providers omit Content-Range on 206.
                var info = await HeadAsync(normalizedKey);
                totalSize = info?.Size ?? data.Length;
            }
        }
        else
        {
            // Some S3-compatible servers ignore Range. Slice locally so the
            // caller still gets the requested window.
            var start = (int)Math.Min(offset, data.Length);
            var available = data.Length - start;
            var count = length <= 0
                ? available
                : (int)Math.Min(length, available);

            var slice = new byte[count];
            if (count > 0)
                Array.Copy(data, start, slice, 0, count);

            data = slice;
            resolvedOffset = start;
            totalSize = response.Body.Length;
        }

        return new StorageRange(
            data,
            resolvedOffset,
            totalSize,
            GetHeader(response.Headers, "content-type") ?? "",
            TrimEtag(GetHeader(response.Headers, "etag")),
            TryParseDate(GetHeader(response.Headers, "last-modified")));
    }

    public async Task<StoragePage> ListAsync(StorageListOptions? listOptions = null)
    {
        listOptions ??= new StorageListOptions();

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["list-type"] = "2",
            ["max-keys"] = listOptions.EffectiveLimit.ToString(CultureInfo.InvariantCulture),
        };

        var prefix = listOptions.NormalizedPrefix;
        if (prefix.Length > 0)
            query["prefix"] = prefix;
        if (!string.IsNullOrEmpty(listOptions.Cursor))
            query["continuation-token"] = listOptions.Cursor;

        var (hostHeader, baseUri, canonicalUri) = BuildBucketEndpoint();
        var signed = SignRequest(
            "GET",
            hostHeader,
            baseUri,
            canonicalUri,
            [],
            query,
            null);

        var response = await _http.FetchBytesAsync(signed.Url, "GET", signed.Headers);
        ThrowForStatus(response.Status, prefix, "LIST");

        return ParseList(response.Body);
    }

    public Task<PresignedUpload> PresignPutAsync(
        string key,
        string contentType,
        TimeSpan expiresIn,
        long? maxBytes = null)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();

        var expiresSeconds = ClampExpiry(expiresIn);
        var now = _clock().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var (hostHeader, baseUri, canonicalUri) = BuildObjectEndpoint(normalizedKey);
        var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";
        var credential = $"{options.AccessKeyId}/{credentialScope}";

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = credential,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "content-type;host",
        };

        var canonicalQuery = BuildCanonicalQuery(query);
        var canonicalHeaders =
            $"content-type:{CanonicalizeHeaderValue(normalizedContentType)}\nhost:{hostHeader}\n";
        var canonicalRequest = string.Join(
            "\n",
            "PUT",
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            "content-type;host",
            "UNSIGNED-PAYLOAD");

        var signature = SignString(
            canonicalRequest,
            amzDate,
            credentialScope,
            dateStamp);
        var url = $"{baseUri}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = normalizedContentType,
        };

        return Task.FromResult(new PresignedUpload(
            url,
            normalizedKey,
            "PUT",
            headers,
            expiresSeconds));
    }

    public Task<PresignedDownload> PresignGetAsync(
        string key,
        TimeSpan expiresIn,
        PresignDownloadOptions? downloadOptions = null)
    {
        var normalizedKey = StorageKey.Normalize(key);
        var expiresSeconds = ClampExpiry(expiresIn);
        var now = _clock().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var (hostHeader, baseUri, canonicalUri) = BuildObjectEndpoint(normalizedKey);
        var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";
        var credential = $"{options.AccessKeyId}/{credentialScope}";

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = credential,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };

        if (!string.IsNullOrWhiteSpace(downloadOptions?.ContentType))
            query["response-content-type"] = downloadOptions.ContentType.Trim();
        if (!string.IsNullOrWhiteSpace(downloadOptions?.CacheControl))
            query["response-cache-control"] = downloadOptions.CacheControl.Trim();
        if (!string.IsNullOrWhiteSpace(downloadOptions?.FileName))
        {
            query["response-content-disposition"] = StorageKey.ContentDisposition(
                downloadOptions.FileName,
                downloadOptions.Inline);
        }

        var canonicalQuery = BuildCanonicalQuery(query);
        var canonicalHeaders = $"host:{hostHeader}\n";
        var canonicalRequest = string.Join(
            "\n",
            "GET",
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            "host",
            "UNSIGNED-PAYLOAD");

        var signature = SignString(
            canonicalRequest,
            amzDate,
            credentialScope,
            dateStamp);
        var url = $"{baseUri}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";

        return Task.FromResult(new PresignedDownload(url, normalizedKey, expiresSeconds));
    }

    public string? PublicUrl(string key)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return null;

        var normalizedKey = StorageKey.Normalize(key);
        var encodedKey = string.Join(
            "/",
            normalizedKey.Split('/').Select(AwsUriEncode));

        return $"{options.PublicBaseUrl.TrimEnd('/')}/{encodedKey}";
    }
}
#endif
