using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Reptar;

public sealed class R2Client : IR2Client
{
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

    [JSImport("r2exists", "app.host")]
    private static extern Task<bool> Exists(string key);

    [JSImport("r2delete", "app.host")]
    private static extern Task Delete(string key);

    public StorageCapabilities Capabilities => new(
        StorageFeatures.Metadata |
        StorageFeatures.RangeReads |
        StorageFeatures.Listing |
        StorageFeatures.ConditionalPut);

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
            metadataJson,
            !options.Overwrite);
    }

    public Task<byte[]?> GetAsync(string key) => Get(key);

    public Task<bool> ExistsAsync(string key) => Exists(key);

    public Task DeleteAsync(string key) => Delete(key);

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
