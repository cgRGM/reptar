using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Reptar;

public sealed class R2Client : IR2Client
{
    private const int MinimumMultipartPartSize = 5 * 1024 * 1024;
    private const int MaxDeleteBatch = 1000;
    private const int MaxMultipartParts = 10_000;

    private readonly string? _publicBaseUrl;
    private readonly int _multipartPartSize;

    public R2Client(
        string? publicBaseUrl = null,
        int multipartPartSizeBytes = 8 * 1024 * 1024)
    {
        _publicBaseUrl = publicBaseUrl;
        _multipartPartSize = Math.Max(
            MinimumMultipartPartSize,
            multipartPartSizeBytes);
    }

    public R2Client(R2StorageOptions options)
        : this(options.PublicBaseUrl, options.MultipartPartSizeBytes)
    {
    }

    [JSImport("r2put", "app.host")]
    private static extern Task<string> Put(
        string key,
        byte[] body,
        string contentType);

    [JSImport("r2putoptions", "app.host")]
    private static extern Task<string> PutWithOptions(
        string key,
        byte[] body,
        string contentType,
        string cacheControl,
        string contentDisposition,
        string contentEncoding,
        string contentLanguage,
        string metadataJson,
        bool onlyIfAbsent);

    [JSImport("r2get", "app.host")]
    private static extern Task<byte[]?> Get(string key);

    [JSImport("r2getrange", "app.host")]
    private static extern Task<byte[]?> GetRange(
        string key,
        int offset,
        int length);

    [JSImport("r2head", "app.host")]
    private static extern Task<string?> Head(string key);

    [JSImport("r2list", "app.host")]
    private static extern Task<string> List(
        string prefix,
        string cursor,
        int limit);

    [JSImport("r2delete", "app.host")]
    private static extern Task Delete(string key);

    [JSImport("r2deleteMany", "app.host")]
    private static extern Task DeleteMany(string keys);

    [JSImport("r2multipartcreate", "app.host")]
    private static extern Task<string> MultipartCreate(
        string key,
        string contentType,
        string cacheControl,
        string contentDisposition,
        string contentEncoding,
        string contentLanguage,
        string metadataJson);

    [JSImport("r2multipartpart", "app.host")]
    private static extern Task<string> MultipartPart(
        string key,
        string uploadId,
        int partNumber,
        byte[] body);

    [JSImport("r2multipartcomplete", "app.host")]
    private static extern Task MultipartComplete(
        string key,
        string uploadId,
        string parts);

    [JSImport("r2multipartabort", "app.host")]
    private static extern Task MultipartAbort(string key, string uploadId);

    [JSImport("r2exists", "app.host")]
    private static extern Task<bool> Exists(string key);

    public StorageCapabilities Capabilities
    {
        get
        {
            var features =
                StorageFeatures.Metadata |
                StorageFeatures.RangeReads |
                StorageFeatures.Listing |
                StorageFeatures.ConditionalPut |
                StorageFeatures.BulkDelete |
                StorageFeatures.MultipartUpload |
                StorageFeatures.StreamingPut;

            if (!string.IsNullOrWhiteSpace(_publicBaseUrl))
                features |= StorageFeatures.PublicUrls;

            return new StorageCapabilities(features);
        }
    }

    public Task<string> PutAsync(string key, byte[] body, string contentType) =>
        Put(key, body, contentType);

    public Task<string> PutAsync(
        string key,
        byte[] body,
        string contentType,
        StoragePutOptions options)
    {
        var metadataJson = options.Metadata is { Count: > 0 }
            ? JsonSerializer.Serialize(
                new Dictionary<string, string>(options.Metadata),
                ReptarInternalJsonContext.Default.DictionaryStringString)
            : "{}";

        return PutWithOptions(
            key,
            body,
            contentType ?? "",
            options.CacheControl ?? "",
            options.ContentDisposition ?? "",
            options.ContentEncoding ?? "",
            options.ContentLanguage ?? "",
            metadataJson,
            !options.Overwrite);
    }

    public async Task<string> PutAsync(
        string key,
        Stream body,
        string contentType,
        StoragePutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        options ??= new StoragePutOptions();

        var normalizedKey = StorageKey.Normalize(key);

        if (!options.Overwrite && await HeadAsync(normalizedKey) is not null)
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{normalizedKey}' already exists.",
                normalizedKey);
        }

        var buffer = new byte[_multipartPartSize];
        var read = await StorageStreams.ReadFullAsync(body, buffer);

        if (read == 0)
            return await PutAsync(normalizedKey, [], contentType, options);

        if (read < buffer.Length)
        {
            var single = new byte[read];
            Array.Copy(buffer, single, read);
            return await PutAsync(normalizedKey, single, contentType, options);
        }

        return await PutMultipartAsync(
            normalizedKey,
            buffer,
            read,
            body,
            contentType,
            options);
    }

    private async Task<string> PutMultipartAsync(
        string key,
        byte[] firstPart,
        int firstLength,
        Stream body,
        string contentType,
        StoragePutOptions options)
    {
        var metadataJson = options.Metadata is { Count: > 0 }
            ? JsonSerializer.Serialize(
                new Dictionary<string, string>(options.Metadata),
                ReptarInternalJsonContext.Default.DictionaryStringString)
            : "{}";

        var uploadId = await MultipartCreate(
            key,
            contentType ?? "",
            options.CacheControl ?? "",
            options.ContentDisposition ?? "",
            options.ContentEncoding ?? "",
            options.ContentLanguage ?? "",
            metadataJson);

        if (string.IsNullOrWhiteSpace(uploadId))
        {
            throw new StorageException(
                StorageErrorCode.Unknown,
                "R2 multipart create returned no upload id.",
                key);
        }

        try
        {
            var parts = new List<(int Number, string ETag)>();
            var partNumber = 1;
            var buffer = firstPart;
            var length = firstLength;

            while (length > 0)
            {
                if (partNumber > MaxMultipartParts)
                {
                    throw new StorageException(
                        StorageErrorCode.TooLarge,
                        "R2 multipart supports at most 10,000 parts; the object is too large for the current part size.",
                        key);
                }

                var payload = length == buffer.Length
                    ? buffer
                    : buffer.AsSpan(0, length).ToArray();

                var etag = await MultipartPart(key, uploadId, partNumber, payload);
                if (string.IsNullOrWhiteSpace(etag))
                {
                    throw new StorageException(
                        StorageErrorCode.Unknown,
                        $"R2 multipart part {partNumber} returned no ETag.",
                        key);
                }

                parts.Add((partNumber, etag));
                partNumber++;

                buffer = new byte[_multipartPartSize];
                length = await StorageStreams.ReadFullAsync(body, buffer);
            }

            var encodedParts = string.Join(
                "\n",
                parts.Select(part => $"{part.Number}\t{part.ETag}"));

            await MultipartComplete(key, uploadId, encodedParts);
            return key;
        }
        catch
        {
            try
            {
                await MultipartAbort(key, uploadId);
            }
            catch
            {
                // Best-effort cleanup; the original exception is more useful.
            }

            throw;
        }
    }

    public Task<byte[]?> GetAsync(string key) => Get(key);

    public async Task<StorageObject?> CopyAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        options ??= new StorageCopyOptions();

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

        if (!options.Overwrite && await HeadAsync(destination) is not null)
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{destination}' already exists.",
                destination);
        }

        var bytes = await GetAsync(source);
        if (bytes is null)
        {
            throw new StorageException(
                StorageErrorCode.NotFound,
                $"Object '{source}' does not exist.",
                source);
        }

        var contentType = options.ContentType
            ?? (string.IsNullOrWhiteSpace(info.ContentType)
                ? Files.ContentTypeFor(source)
                : info.ContentType);

        var cacheControl = options.CacheControl
            ?? (options.PreserveMetadata
                ? info.MetadataValue("cache-control")
                : null);

        var contentDisposition = options.ContentDisposition
            ?? (options.PreserveMetadata
                ? info.MetadataValue("content-disposition")
                : null);
        var contentEncoding = options.ContentEncoding
            ?? (options.PreserveMetadata
                ? info.MetadataValue("content-encoding")
                : null);
        var contentLanguage = options.ContentLanguage
            ?? (options.PreserveMetadata
                ? info.MetadataValue("content-language")
                : null);

        var metadata = options.Metadata
            ?? (options.PreserveMetadata
                ? info.MetadataExcept(
                    "cache-control",
                    "content-disposition",
                    "content-encoding",
                    "content-language")
                : null);

        await PutAsync(
            destination,
            bytes,
            contentType,
            new StoragePutOptions
            {
                Overwrite = options.Overwrite,
                CacheControl = cacheControl,
                ContentDisposition = contentDisposition,
                ContentEncoding = contentEncoding,
                ContentLanguage = contentLanguage,
                Metadata = metadata,
            });

        return await HeadAsync(destination)
            ?? new StorageObject(
                destination,
                bytes.Length,
                contentType,
                "",
                null,
                metadata);
    }

    public async Task<StorageObject?> MoveAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        var source = StorageKey.Normalize(sourceKey);
        var copied = await CopyAsync(source, destinationKey, options);
        await DeleteAsync(source);
        return copied;
    }

    public Task<bool> ExistsAsync(string key) => Exists(key);

    public Task DeleteAsync(string key) => Delete(key);

    public async Task DeleteManyAsync(IEnumerable<string> keys)
    {
        var normalized = new List<string>();
        foreach (var key in keys)
            normalized.Add(StorageKey.Normalize(key));

        for (var offset = 0; offset < normalized.Count; offset += MaxDeleteBatch)
        {
            var count = Math.Min(MaxDeleteBatch, normalized.Count - offset);
            var batch = normalized.GetRange(offset, count);
            await DeleteMany(string.Join("\n", batch));
        }
    }

    public string? PublicUrl(string key)
    {
        if (string.IsNullOrWhiteSpace(_publicBaseUrl))
            return null;

        return $"{_publicBaseUrl.TrimEnd('/')}/{StorageKey.EncodePath(key)}";
    }

    public async Task<StorageObject?> HeadAsync(string key)
    {
        var json = await Head(key);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var metadata = ReadMetadata(root);

        return new StorageObject(
            key,
            root.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            root.TryGetProperty("contentType", out var contentType)
                ? contentType.GetString() ?? ""
                : "",
            root.TryGetProperty("etag", out var etag)
                ? etag.GetString() ?? ""
                : "",
            ReadUploaded(root),
            metadata.Count == 0 ? null : metadata);
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

        if (offset > int.MaxValue || length > int.MaxValue)
        {
            throw new StorageException(
                StorageErrorCode.TooLarge,
                "Requested range is too large to buffer.",
                key);
        }

        var info = await HeadAsync(key);
        if (info is null)
            return null;

        var bytes = await GetRange(
            key,
            (int)offset,
            length <= 0 ? 0 : (int)length);

        if (bytes is null)
            return null;

        return new StorageRange(
            bytes,
            offset,
            info.Size,
            info.ContentType,
            info.ETag,
            info.LastModified);
    }

    public async Task<StoragePage> ListAsync(StorageListOptions? options = null)
    {
        options ??= new StorageListOptions();
        var json = await List(
            options.NormalizedPrefix,
            options.Cursor ?? "",
            options.EffectiveLimit);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = new List<StorageObject>();

        if (root.TryGetProperty("items", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                var key = element.TryGetProperty("key", out var keyProperty)
                    ? keyProperty.GetString() ?? ""
                    : "";
                if (key.Length == 0)
                    continue;

                items.Add(new StorageObject(
                    key,
                    element.TryGetProperty("size", out var size)
                        ? size.GetInt64()
                        : 0,
                    element.TryGetProperty("contentType", out var contentType)
                        ? contentType.GetString() ?? ""
                        : "",
                    element.TryGetProperty("etag", out var etag)
                        ? etag.GetString() ?? ""
                        : "",
                    ReadUploaded(element)));
            }
        }

        var cursor = root.TryGetProperty("cursor", out var cursorProperty) &&
                     cursorProperty.ValueKind == JsonValueKind.String
            ? cursorProperty.GetString()
            : null;

        return new StoragePage(items, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static Dictionary<string, string> ReadMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("metadata", out var custom) &&
            custom.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in custom.EnumerateObject())
                metadata[property.Name] = property.Value.GetString() ?? "";
        }

        if (root.TryGetProperty("cacheControl", out var cacheControl))
        {
            var value = cacheControl.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                metadata["cache-control"] = value;
        }

        if (root.TryGetProperty("contentDisposition", out var contentDisposition))
        {
            var value = contentDisposition.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                metadata["content-disposition"] = value;
        }

        if (root.TryGetProperty("contentEncoding", out var contentEncoding))
        {
            var value = contentEncoding.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                metadata["content-encoding"] = value;
        }

        if (root.TryGetProperty("contentLanguage", out var contentLanguage))
        {
            var value = contentLanguage.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                metadata["content-language"] = value;
        }

        return metadata;
    }

    private static DateTimeOffset? ReadUploaded(JsonElement element)
    {
        if (!element.TryGetProperty("uploaded", out var uploaded) ||
            uploaded.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            uploaded.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
