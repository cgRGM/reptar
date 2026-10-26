#if !NETWASM
using System.Globalization;
using System.Security.Cryptography;
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
    private const int MultipartPartSize = 8 * 1024 * 1024;
    private const int MaxDeleteBatch = 1000;

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
                StorageFeatures.ConditionalPut |
                StorageFeatures.ServerSideCopy |
                StorageFeatures.MultipartUpload |
                StorageFeatures.StreamingPut |
                StorageFeatures.BulkDelete;

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

    public async Task<string> PutAsync(
        string key,
        Stream body,
        string contentType,
        StoragePutOptions? putOptions = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        putOptions ??= new StoragePutOptions();

        var normalizedKey = StorageKey.Normalize(key);

        if (!putOptions.Overwrite && await HeadAsync(normalizedKey) is not null)
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{normalizedKey}' already exists.",
                normalizedKey);
        }

        var buffer = new byte[MultipartPartSize];
        var read = await ReadFullAsync(body, buffer);

        if (read == 0)
            return await PutAsync(normalizedKey, [], contentType, putOptions);

        if (read < buffer.Length)
        {
            var single = new byte[read];
            Array.Copy(buffer, single, read);
            return await PutAsync(normalizedKey, single, contentType, putOptions);
        }

        return await PutMultipartAsync(
            normalizedKey,
            buffer,
            read,
            body,
            contentType,
            putOptions);
    }

    public async Task<StorageObject?> CopyAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? copyOptions = null)
    {
        copyOptions ??= new StorageCopyOptions();

        var source = StorageKey.Normalize(sourceKey);
        var destination = StorageKey.Normalize(destinationKey);

        var info = await HeadAsync(source);
        if (info is null)
        {
            throw new StorageException(
                StorageErrorCode.NotFound,
                $"Object '{source}' does not exist.",
                source);
        }

        if (!copyOptions.Overwrite && await HeadAsync(destination) is not null)
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{destination}' already exists.",
                destination);
        }

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = BuildCopySource(source),
        };

        var replaceMetadata =
            !copyOptions.PreserveMetadata ||
            copyOptions.ContentType is not null ||
            copyOptions.CacheControl is not null ||
            copyOptions.Metadata is not null;

        if (replaceMetadata)
        {
            extra["x-amz-metadata-directive"] = "REPLACE";

            var contentType = copyOptions.ContentType ?? info.ContentType;
            if (!string.IsNullOrWhiteSpace(contentType))
                extra["content-type"] = contentType.Trim();

            var cacheControl = copyOptions.CacheControl
                ?? (copyOptions.PreserveMetadata
                    ? info.MetadataValue("cache-control")
                    : null);
            if (!string.IsNullOrWhiteSpace(cacheControl))
                extra["cache-control"] = cacheControl.Trim();

            var metadata = copyOptions.Metadata
                ?? (copyOptions.PreserveMetadata
                    ? info.MetadataExcept("cache-control")
                    : null);
            if (metadata is { Count: > 0 })
            {
                foreach (var (name, value) in metadata)
                    extra[$"x-amz-meta-{name}"] = value;
            }
        }
        else
        {
            extra["x-amz-metadata-directive"] = "COPY";
        }

        var signed = SignObjectRequest("PUT", destination, [], extra);
        var copyContentType = GetHeader(extra, "content-type");
        var response = await _http.FetchBytesAsync(
            signed.Url,
            "PUT",
            signed.Headers,
            [],
            copyContentType);
        ThrowForStatus(response.Status, destination, "COPY");

        var error = ParseErrorCode(response.Body);
        if (error is not null)
        {
            throw new StorageException(
                StorageErrorCode.Unknown,
                $"S3 COPY failed: {error}",
                destination);
        }

        return await HeadAsync(destination);
    }

    public async Task<StorageObject?> MoveAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? copyOptions = null)
    {
        var source = StorageKey.Normalize(sourceKey);
        var copied = await CopyAsync(source, destinationKey, copyOptions);
        await DeleteAsync(source);
        return copied;
    }

    public async Task DeleteManyAsync(IEnumerable<string> keys)
    {
        var normalized = new List<string>();
        foreach (var key in keys)
            normalized.Add(StorageKey.Normalize(key));

        for (var offset = 0; offset < normalized.Count; offset += MaxDeleteBatch)
        {
            var count = Math.Min(MaxDeleteBatch, normalized.Count - offset);
            var batch = normalized.GetRange(offset, count);
            var xml = BuildDeleteXml(batch);
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["delete"] = "",
            };

            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-md5"] = Convert.ToBase64String(MD5.HashData(xml)),
                ["content-type"] = "application/xml",
            };

            var (hostHeader, baseUri, canonicalUri) = BuildBucketEndpoint();
            var signed = SignRequest(
                "POST",
                hostHeader,
                baseUri,
                canonicalUri,
                xml,
                query,
                extra);

            var response = await _http.FetchBytesAsync(
                signed.Url,
                "POST",
                signed.Headers,
                xml,
                "application/xml");

            ThrowForStatus(response.Status, "", "DELETE objects");

            var error = ParseDeleteError(response.Body);
            if (error is not null)
            {
                throw new StorageException(
                    StorageErrorCode.Unknown,
                    $"S3 delete failed: {error}",
                    "");
            }
        }
    }

    private async Task<string> PutMultipartAsync(
        string key,
        byte[] firstPart,
        int firstLength,
        Stream body,
        string contentType,
        StoragePutOptions putOptions)
    {
        var uploadId = await CreateMultipartUploadAsync(key, contentType, putOptions);

        try
        {
            var parts = new List<(int Number, string ETag)>();
            var partNumber = 1;
            var buffer = firstPart;
            var length = firstLength;

            while (length > 0)
            {
                if (partNumber > 10_000)
                {
                    throw new StorageException(
                        StorageErrorCode.TooLarge,
                        "S3 multipart supports at most 10,000 parts; the object is too large for the current part size.",
                        key);
                }

                var etag = await UploadPartAsync(
                    key,
                    uploadId,
                    partNumber,
                    buffer,
                    length);
                parts.Add((partNumber, etag));
                partNumber++;

                buffer = new byte[MultipartPartSize];
                length = await ReadFullAsync(body, buffer);
            }

            await CompleteMultipartUploadAsync(key, uploadId, parts);
            return key;
        }
        catch
        {
            await AbortMultipartUploadAsync(key, uploadId);
            throw;
        }
    }

    private async Task<string> CreateMultipartUploadAsync(
        string key,
        string contentType,
        StoragePutOptions putOptions)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploads"] = "",
        };

        var extra = BuildPutHeaders(contentType, putOptions);
        var signed = SignObjectQueryRequest("POST", key, [], query, extra);
        var createContentType = GetHeader(extra, "content-type");
        var response = await _http.FetchBytesAsync(
            signed.Url,
            "POST",
            signed.Headers,
            [],
            createContentType);
        ThrowForStatus(response.Status, key, "multipart create");

        var uploadId = ParseUploadId(response.Body);
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new StorageException(
                StorageErrorCode.Unknown,
                "S3 multipart create returned no upload id.",
                key);
        }

        return uploadId;
    }

    private async Task<string> UploadPartAsync(
        string key,
        string uploadId,
        int partNumber,
        byte[] buffer,
        int length)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["partNumber"] = partNumber.ToString(CultureInfo.InvariantCulture),
            ["uploadId"] = uploadId,
        };

        var payload = length == buffer.Length
            ? buffer
            : buffer.AsSpan(0, length).ToArray();

        var signed = SignObjectQueryRequest("PUT", key, payload, query, null);
        var response = await _http.FetchBytesAsync(
            signed.Url,
            "PUT",
            signed.Headers,
            payload,
            "application/octet-stream");

        ThrowForStatus(response.Status, key, $"multipart part {partNumber}");

        var etag = GetHeader(response.Headers, "etag")?.Trim();
        if (string.IsNullOrWhiteSpace(etag))
        {
            throw new StorageException(
                StorageErrorCode.Unknown,
                $"S3 multipart part {partNumber} returned no ETag.",
                key);
        }

        return etag.StartsWith('"') ? etag : $"\"{etag}\"";
    }

    private async Task CompleteMultipartUploadAsync(
        string key,
        string uploadId,
        IReadOnlyList<(int Number, string ETag)> parts)
    {
        var xml = BuildCompleteXml(parts);
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploadId"] = uploadId,
        };

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/xml",
        };

        var signed = SignObjectQueryRequest("POST", key, xml, query, extra);
        var response = await _http.FetchBytesAsync(
            signed.Url,
            "POST",
            signed.Headers,
            xml,
            "application/xml");

        ThrowForStatus(response.Status, key, "multipart complete");

        var error = ParseErrorCode(response.Body);
        if (error is not null)
        {
            throw new StorageException(
                StorageErrorCode.Unknown,
                $"S3 multipart complete failed: {error}",
                key);
        }
    }

    private async Task AbortMultipartUploadAsync(string key, string uploadId)
    {
        try
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["uploadId"] = uploadId,
            };

            var signed = SignObjectQueryRequest("DELETE", key, [], query, null);
            await _http.FetchBytesAsync(signed.Url, "DELETE", signed.Headers);
        }
        catch
        {
            // Best-effort cleanup; the original exception is more useful.
        }
    }

    private static Dictionary<string, string> BuildPutHeaders(
        string contentType,
        StoragePutOptions putOptions)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(contentType))
            extra["content-type"] = contentType.Trim();
        if (!string.IsNullOrWhiteSpace(putOptions.CacheControl))
            extra["cache-control"] = putOptions.CacheControl.Trim();
        if (putOptions.Metadata is { Count: > 0 })
        {
            foreach (var (name, value) in putOptions.Metadata)
                extra[$"x-amz-meta-{name}"] = value;
        }

        return extra;
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer)
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
