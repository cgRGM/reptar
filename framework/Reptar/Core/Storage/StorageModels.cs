using System.Globalization;

namespace Reptar;

/// <summary>
/// Feature flags advertised by a storage provider. Portable app code can branch on
/// these instead of checking concrete provider types (and without reflection).
/// </summary>
[Flags]
public enum StorageFeatures
{
    None = 0,
    Metadata = 1 << 0,
    RangeReads = 1 << 1,
    Listing = 1 << 2,
    PresignUpload = 1 << 3,
    PresignDownload = 1 << 4,
    ConditionalPut = 1 << 5,
    PublicUrls = 1 << 6,
    BulkDelete = 1 << 7,
    ServerSideCopy = 1 << 8,
    MultipartUpload = 1 << 9,
    StreamingPut = 1 << 10,
}

public sealed record StorageCapabilities(StorageFeatures Features)
{
    public static readonly StorageCapabilities Basic = new(StorageFeatures.None);

    public bool Has(StorageFeatures feature) => (Features & feature) == feature;
}

/// <summary>
/// Typed storage failures. The HTTP layer maps these to meaningful status codes
/// instead of turning every provider error into a 500.
/// </summary>
public enum StorageErrorCode
{
    Unknown = 0,
    NotFound,
    Unauthorized,
    Forbidden,
    PreconditionFailed,
    Conflict,
    TooLarge,
    RangeNotSatisfiable,
    InvalidKey,
    Unsupported,
    Unavailable,
}

public sealed class StorageException : Exception
{
    public StorageException(
        StorageErrorCode code,
        string message,
        string? key = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Key = key;
    }

    public StorageErrorCode Code { get; }

    public string? Key { get; }
}

/// <summary>
/// Metadata for a stored object. Providers return what they can cheaply know;
/// <see cref="ContentType"/> and <see cref="ETag"/> may be empty when the provider
/// does not expose them (for example an S3 list response).
/// </summary>
public sealed record StorageObject(
    string Key,
    long Size,
    string ContentType = "",
    string ETag = "",
    DateTimeOffset? LastModified = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public string FileName
    {
        get
        {
            var slash = Key.LastIndexOf('/');
            return slash < 0 ? Key : Key[(slash + 1)..];
        }
    }

    /// <summary>ETag formatted for an HTTP response header, or empty.</summary>
    public string HttpETag => StorageKey.FormatETag(ETag);

    public string? MetadataValue(string name)
    {
        if (Metadata is null)
            return null;

        if (Metadata.TryGetValue(name, out var direct))
            return direct;

        foreach (var (key, value) in Metadata)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    public IReadOnlyDictionary<string, string>? MetadataExcept(string name)
    {
        if (Metadata is null)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Metadata)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                result[key] = value;
        }

        return result.Count == 0 ? null : result;
    }
}

/// <summary>A single byte range read from an object.</summary>
public sealed record StorageRange(
    byte[] Data,
    long Offset,
    long TotalSize,
    string ContentType = "",
    string ETag = "",
    DateTimeOffset? LastModified = null)
{
    public long Length => Data.Length;

    public long End => Offset + Data.Length - 1;

    public string HttpETag => StorageKey.FormatETag(ETag);
}

/// <summary>A page of listed objects. <see cref="Cursor"/> is opaque and provider-specific.</summary>
public sealed record StoragePage(
    IReadOnlyList<StorageObject> Items,
    string? Cursor = null)
{
    public bool HasMore => !string.IsNullOrEmpty(Cursor);

    public static readonly StoragePage Empty = new([], null);
}

public sealed record StorageListOptions
{
    /// <summary>Only return keys that start with this prefix.</summary>
    public string? Prefix { get; init; }

    /// <summary>Opaque continuation token from a previous <see cref="StoragePage"/>.</summary>
    public string? Cursor { get; init; }

    /// <summary>Requested page size. Clamped to 1..1000 by providers.</summary>
    public int Limit { get; init; } = 100;

    public int EffectiveLimit => Math.Clamp(Limit, 1, 1000);

    public string NormalizedPrefix =>
        string.IsNullOrWhiteSpace(Prefix)
            ? ""
            : StorageKey.NormalizePrefix(Prefix);
}

public sealed record StoragePutOptions
{
    /// <summary>When false, the provider fails with PreconditionFailed if the key exists.</summary>
    public bool Overwrite { get; init; } = true;

    public string? CacheControl { get; init; }

    /// <summary>User metadata. Providers that cannot store it ignore it.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public bool IsDefault =>
        Overwrite &&
        string.IsNullOrWhiteSpace(CacheControl) &&
        (Metadata is null || Metadata.Count == 0);
}

public sealed record StorageCopyOptions
{
    /// <summary>When false, the provider fails with PreconditionFailed if the destination exists.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Copy the source content type, cache control, and user metadata.</summary>
    public bool PreserveMetadata { get; init; } = true;

    /// <summary>Overrides the destination content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Overrides the destination cache control.</summary>
    public string? CacheControl { get; init; }

    /// <summary>Overrides destination user metadata. Replaces preserved metadata when set.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record PresignDownloadOptions
{
    /// <summary>Overrides the response Content-Type without changing the stored object.</summary>
    public string? ContentType { get; init; }

    /// <summary>When set, the response is served as an attachment with this filename.</summary>
    public string? FileName { get; init; }

    /// <summary>Use inline disposition instead of attachment when <see cref="FileName"/> is set.</summary>
    public bool Inline { get; init; }

    public string? CacheControl { get; init; }
}

public sealed record PresignedDownload(
    string Url,
    string Key,
    int ExpiresInSeconds,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Unified, provider-agnostic object storage. Only the four core operations are
/// required; richer operations have default implementations that throw
/// <see cref="StorageErrorCode.Unsupported"/> so existing providers keep compiling.
/// </summary>
public interface IStorage
{
    Task<string> PutAsync(string key, byte[] body, string contentType);

    Task<byte[]?> GetAsync(string key);

    Task<bool> ExistsAsync(string key);

    Task DeleteAsync(string key);

    StorageCapabilities Capabilities => StorageCapabilities.Basic;

    /// <summary>Returns object metadata, or null when the key does not exist.</summary>
    Task<StorageObject?> HeadAsync(string key) =>
        Task.FromException<StorageObject?>(Unsupported(nameof(HeadAsync)));

    /// <summary>Reads a byte range. <paramref name="length"/> &lt;= 0 reads to the end.</summary>
    Task<StorageRange?> GetRangeAsync(string key, long offset, long length) =>
        Task.FromException<StorageRange?>(Unsupported(nameof(GetRangeAsync)));

    /// <summary>Lists objects under an optional prefix, using an opaque cursor.</summary>
    Task<StoragePage> ListAsync(StorageListOptions? options = null) =>
        Task.FromException<StoragePage>(Unsupported(nameof(ListAsync)));

    Task<PresignedUpload> PresignPutAsync(
        string key,
        string contentType,
        TimeSpan expiresIn,
        long? maxBytes = null) =>
        Task.FromException<PresignedUpload>(Unsupported(nameof(PresignPutAsync)));

    Task<PresignedDownload> PresignGetAsync(
        string key,
        TimeSpan expiresIn,
        PresignDownloadOptions? options = null) =>
        Task.FromException<PresignedDownload>(Unsupported(nameof(PresignGetAsync)));

    /// <summary>Writes an object with metadata or conditional-overwrite semantics.</summary>
    Task<string> PutAsync(
        string key,
        byte[] body,
        string contentType,
        StoragePutOptions options) =>
        options.IsDefault
            ? PutAsync(key, body, contentType)
            : Task.FromException<string>(Unsupported("PutAsync with options"));

    /// <summary>
    /// Streams an object into storage. The default buffers the stream; providers
    /// with <see cref="StorageFeatures.StreamingPut"/> or
    /// <see cref="StorageFeatures.MultipartUpload"/> override this to avoid it.
    /// </summary>
    async Task<string> PutAsync(
        string key,
        Stream body,
        string contentType,
        StoragePutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        return options is null
            ? await PutAsync(key, bytes, contentType)
            : await PutAsync(key, bytes, contentType, options);
    }

    /// <summary>
    /// Copies an object. The default reads and rewrites the bytes; providers with
    /// <see cref="StorageFeatures.ServerSideCopy"/> override this.
    /// </summary>
    async Task<StorageObject?> CopyAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        options ??= new StorageCopyOptions();
        var source = StorageKey.Normalize(sourceKey);
        var destination = StorageKey.Normalize(destinationKey);

        StorageObject? info = null;
        try
        {
            info = await HeadAsync(source);
        }
        catch (StorageException exception) when (exception.Code == StorageErrorCode.Unsupported)
        {
            // Provider only implements the core four operations.
        }
        catch (NotSupportedException)
        {
            // Provider uses the framework's non-throwing convention.
        }

        if (info is null)
        {
            var exists = await ExistsAsync(source);
            if (!exists)
            {
                throw new StorageException(
                    StorageErrorCode.NotFound,
                    $"Object '{source}' does not exist.",
                    source);
            }
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
            ?? (string.IsNullOrWhiteSpace(info?.ContentType)
                ? Files.ContentTypeFor(source)
                : info!.ContentType);

        var cacheControl = options.CacheControl
            ?? (options.PreserveMetadata
                ? info?.MetadataValue("cache-control")
                : null);

        var metadata = options.Metadata
            ?? (options.PreserveMetadata ? info?.MetadataExcept("cache-control") : null);

        await PutAsync(
            destination,
            bytes,
            contentType,
            new StoragePutOptions
            {
                Overwrite = options.Overwrite,
                CacheControl = cacheControl,
                Metadata = metadata,
            });

        StorageObject? copied = null;
        try
        {
            copied = await HeadAsync(destination);
        }
        catch (StorageException exception) when (exception.Code == StorageErrorCode.Unsupported)
        {
            // Fall through to a synthesized result.
        }
        catch (NotSupportedException)
        {
            // Fall through to a synthesized result.
        }

        return copied ?? new StorageObject(
            destination,
            bytes.Length,
            contentType,
            "",
            null,
            metadata);
    }

    /// <summary>Moves an object by copying it and deleting the source.</summary>
    async Task<StorageObject?> MoveAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        var source = StorageKey.Normalize(sourceKey);
        var copied = await CopyAsync(source, destinationKey, options);
        await DeleteAsync(source);
        return copied;
    }

    async Task DeleteManyAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            await DeleteAsync(key);
    }

    /// <summary>Public/CDN URL for a key, or null when the provider has no public base URL.</summary>
    string? PublicUrl(string key) => null;

    private static StorageException Unsupported(string operation) =>
        new(
            StorageErrorCode.Unsupported,
            $"{operation} is not supported by this storage provider.");
}

/// <summary>
/// Centralized object-key validation. Every provider funnels through this so a
/// key that is safe on R2 is also safe on local disk and S3.
/// </summary>
public static class StorageKey
{
    public const int MaxLength = 1024;
    public const int MaxSegmentLength = 255;

    public static string Normalize(string? key)
    {
        if (!TryNormalize(key, out var normalized, out var error))
            throw new StorageException(StorageErrorCode.InvalidKey, error, key);
        return normalized;
    }

    public static bool IsValid(string? key) =>
        TryNormalize(key, out _, out _);

    public static bool TryNormalize(string? key, out string normalized) =>
        TryNormalize(key, out normalized, out _);

    public static bool TryNormalize(
        string? key,
        out string normalized,
        out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(key))
        {
            error = "Storage key is required.";
            return false;
        }

        var candidate = key.Replace('\\', '/').TrimStart('/');
        if (candidate.Length == 0)
        {
            error = "Storage key is required.";
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            error = $"Storage key exceeds {MaxLength} characters.";
            return false;
        }

        if (candidate[^1] == '/')
        {
            error = "Storage key must not end with '/'.";
            return false;
        }

        var segmentStart = 0;
        for (var i = 0; i <= candidate.Length; i++)
        {
            var atEnd = i == candidate.Length;
            if (!atEnd && candidate[i] != '/')
            {
                var c = candidate[i];
                if (c < 0x20 || c == 0x7F)
                {
                    error = "Storage key contains a control character.";
                    return false;
                }
                continue;
            }

            var length = i - segmentStart;
            if (length == 0)
            {
                error = "Storage key contains an empty path segment.";
                return false;
            }

            if (length > MaxSegmentLength)
            {
                error = $"Storage key segment exceeds {MaxSegmentLength} characters.";
                return false;
            }

            var segment = candidate.AsSpan(segmentStart, length);
            if (segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                error = "Path traversal in storage key is not permitted.";
                return false;
            }

            segmentStart = i + 1;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// Prefixes are allowed to end with '/' and are not required to be a full key.
    /// </summary>
    public static string NormalizePrefix(string prefix)
    {
        var candidate = prefix.Replace('\\', '/').TrimStart('/');
        if (candidate.Length == 0)
            return "";

        if (candidate.Length > MaxLength)
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                $"Storage prefix exceeds {MaxLength} characters.",
                prefix);

        var segments = candidate.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                // A trailing slash is a valid folder prefix; interior empty
                // segments are not.
                if (i == segments.Length - 1)
                    break;
                throw new StorageException(
                    StorageErrorCode.InvalidKey,
                    "Storage prefix contains an empty path segment.",
                    prefix);
            }

            if (segment is "." or "..")
                throw new StorageException(
                    StorageErrorCode.InvalidKey,
                    "Path traversal in storage prefix is not permitted.",
                    prefix);
        }

        return candidate;
    }

    public static string Combine(params string[] parts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmed = part.Replace('\\', '/').Trim('/');
            if (trimmed.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append('/');
            builder.Append(trimmed);
        }

        return Normalize(builder.ToString());
    }

    public static string FileName(string key)
    {
        var normalized = Normalize(key);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    /// <summary>Percent-encodes each path segment while preserving '/'.</summary>
    public static string EncodePath(string key)
    {
        var normalized = Normalize(key);
        return string.Join(
            "/",
            normalized.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// Builds an RFC 6266 Content-Disposition value with an ASCII fallback and a
    /// UTF-8 <c>filename*</c> parameter.
    /// </summary>
    public static string ContentDisposition(string fileName, bool inline = false)
    {
        var sanitized = SanitizeFileName(fileName);
        var ascii = new System.Text.StringBuilder(sanitized.Length);
        foreach (var character in sanitized)
        {
            ascii.Append(character is >= (char)0x20 and <= (char)0x7E ? character : '_');
        }

        var encoded = Uri.EscapeDataString(sanitized);
        return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }

    /// <summary>Normalizes a provider ETag into a quoted HTTP ETag value.</summary>
    public static string FormatETag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return "";

        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            value = value[2..].Trim();

        value = value.Trim('"');
        return value.Length == 0 ? "" : $"\"{value}\"";
    }

    public static string SanitizeFileName(string fileName)
    {
        var trimmed = (fileName ?? "").Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (character is '"' or '\r' or '\n' or '\\' or '/' or < (char)0x20)
                continue;

            builder.Append(character);
        }

        return builder.Length == 0 ? "download" : builder.ToString();
    }

    internal static bool TryParseHttpDate(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
}
