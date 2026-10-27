#if !NETWASM
using System.Globalization;
using System.Text;

namespace Reptar;

/// <summary>
/// Local-disk storage with the same metadata/range/listing semantics as the
/// remote providers. Content type and cache headers live in a hidden sidecar
/// directory so the served object keeps its exact metadata across restarts.
/// </summary>
public class LocalStorage : IStorage, IR2Client
{
    private const string MetaDirectoryName = ".reptar-meta";
    private const string MetaFormatVersion = "v2";
    private const string LegacyMetaFormatVersion = "v1";

    private readonly string _fullRoot;
    private readonly string _fullRootWithSeparator;
    private readonly string _metaRoot;
    private readonly string _metaRootWithSeparator;

    public LocalStorage(string rootDirectory = "uploads")
    {
        Directory.CreateDirectory(rootDirectory);
        _fullRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _fullRootWithSeparator = _fullRoot + Path.DirectorySeparatorChar;

        _metaRoot = Path.Combine(_fullRoot, MetaDirectoryName);
        _metaRootWithSeparator = _metaRoot + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_metaRoot);
    }

    public StorageCapabilities Capabilities => new(
        StorageFeatures.Metadata |
        StorageFeatures.RangeReads |
        StorageFeatures.Listing |
        StorageFeatures.ConditionalPut |
        StorageFeatures.StreamingPut);

    public async Task<string> PutAsync(string key, byte[] body, string contentType)
    {
        using var stream = new MemoryStream(body, writable: false);
        return await PutAsync(key, stream, contentType, new StoragePutOptions());
    }

    public async Task<string> PutAsync(
        string key,
        byte[] body,
        string contentType,
        StoragePutOptions options)
    {
        using var stream = new MemoryStream(body, writable: false);
        return await PutAsync(key, stream, contentType, options);
    }

    public async Task<string> PutAsync(
        string key,
        Stream body,
        string contentType,
        StoragePutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        options ??= new StoragePutOptions();

        var fullPath = ResolvePath(key);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            await using var stream = new FileStream(
                fullPath,
                options.Overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            await body.CopyToAsync(stream);
        }
        catch (IOException exception) when (!options.Overwrite && File.Exists(fullPath))
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{key}' already exists.",
                key,
                exception);
        }
        catch
        {
            // Do not leave a partially written object behind.
            try
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }

        await WriteMetadataAsync(key, contentType, options);
        return key;
    }

    public async Task<StorageObject?> CopyAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        options ??= new StorageCopyOptions();

        var sourcePath = ResolvePath(sourceKey);
        var destinationPath = ResolvePath(destinationKey);

        if (!File.Exists(sourcePath))
        {
            throw new StorageException(
                StorageErrorCode.NotFound,
                $"Object '{sourceKey}' does not exist.",
                sourceKey);
        }

        if (!options.Overwrite && File.Exists(destinationPath))
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{destinationKey}' already exists.",
                destinationKey);
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        try
        {
            File.Copy(sourcePath, destinationPath, options.Overwrite);
        }
        catch
        {
            try
            {
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }

        var sourceMetadata = await ReadMetadataAsync(sourceKey);
        var contentType = options.ContentType
            ?? sourceMetadata?.ContentType
            ?? Files.ContentTypeFor(sourceKey);
        var cacheControl = options.CacheControl
            ?? (options.PreserveMetadata ? sourceMetadata?.CacheControl : null);
        var contentDisposition = options.ContentDisposition
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentDisposition : null);
        var contentEncoding = options.ContentEncoding
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentEncoding : null);
        var contentLanguage = options.ContentLanguage
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentLanguage : null);
        var metadata = options.Metadata
            ?? (options.PreserveMetadata ? sourceMetadata?.Metadata : null);

        await WriteMetadataAsync(
            destinationKey,
            contentType,
            new StoragePutOptions
            {
                Overwrite = true,
                CacheControl = cacheControl,
                ContentDisposition = contentDisposition,
                ContentEncoding = contentEncoding,
                ContentLanguage = contentLanguage,
                Metadata = metadata,
            });

        return await HeadAsync(destinationKey);
    }

    public async Task<StorageObject?> MoveAsync(
        string sourceKey,
        string destinationKey,
        StorageCopyOptions? options = null)
    {
        options ??= new StorageCopyOptions();

        var sourcePath = ResolvePath(sourceKey);
        var destinationPath = ResolvePath(destinationKey);

        if (!File.Exists(sourcePath))
        {
            throw new StorageException(
                StorageErrorCode.NotFound,
                $"Object '{sourceKey}' does not exist.",
                sourceKey);
        }

        if (!options.Overwrite && File.Exists(destinationPath))
        {
            throw new StorageException(
                StorageErrorCode.PreconditionFailed,
                $"Object '{destinationKey}' already exists.",
                destinationKey);
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var sourceMetadata = await ReadMetadataAsync(sourceKey);
        File.Move(sourcePath, destinationPath, options.Overwrite);

        var sourceMetaPath = MetaPath(sourceKey);
        if (File.Exists(sourceMetaPath))
            File.Delete(sourceMetaPath);

        var contentType = options.ContentType
            ?? sourceMetadata?.ContentType
            ?? Files.ContentTypeFor(sourceKey);
        var cacheControl = options.CacheControl
            ?? (options.PreserveMetadata ? sourceMetadata?.CacheControl : null);
        var contentDisposition = options.ContentDisposition
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentDisposition : null);
        var contentEncoding = options.ContentEncoding
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentEncoding : null);
        var contentLanguage = options.ContentLanguage
            ?? (options.PreserveMetadata ? sourceMetadata?.ContentLanguage : null);
        var metadata = options.Metadata
            ?? (options.PreserveMetadata ? sourceMetadata?.Metadata : null);

        await WriteMetadataAsync(
            destinationKey,
            contentType,
            new StoragePutOptions
            {
                Overwrite = true,
                CacheControl = cacheControl,
                ContentDisposition = contentDisposition,
                ContentEncoding = contentEncoding,
                ContentLanguage = contentLanguage,
                Metadata = metadata,
            });

        return await HeadAsync(destinationKey);
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

        var metaPath = MetaPath(key);
        if (File.Exists(metaPath))
            File.Delete(metaPath);

        return Task.CompletedTask;
    }

    public async Task DeleteManyAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            await DeleteAsync(key);
    }

    public async Task<StorageObject?> HeadAsync(string key)
    {
        var fullPath = ResolvePath(key);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return null;

        var meta = await ReadMetadataAsync(key);
        return BuildObject(key, info, meta);
    }

    public async Task<StorageRange?> GetRangeAsync(string key, long offset, long length)
    {
        var fullPath = ResolvePath(key);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return null;

        if (offset < 0)
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                "Range offset must not be negative.",
                key);

        var total = info.Length;
        var start = Math.Min(offset, total);
        var available = total - start;
        var count = length <= 0 ? available : Math.Min(length, available);

        if (count > int.MaxValue)
            throw new StorageException(
                StorageErrorCode.TooLarge,
                "Requested range is too large to buffer.",
                key);

        var buffer = new byte[(int)count];
        if (count > 0)
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);

            stream.Seek(start, SeekOrigin.Begin);
            var read = 0;
            while (read < buffer.Length)
            {
                var current = await stream.ReadAsync(buffer.AsMemory(read));
                if (current == 0)
                    break;
                read += current;
            }

            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
        }

        var meta = await ReadMetadataAsync(key);
        var contentType = ResolveContentType(key, meta);
        return new StorageRange(
            buffer,
            start,
            total,
            contentType,
            BuildEtag(info),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public async Task<StoragePage> ListAsync(StorageListOptions? options = null)
    {
        options ??= new StorageListOptions();
        var prefix = options.NormalizedPrefix;
        var limit = options.EffectiveLimit;
        var cursor = options.Cursor;

        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     _fullRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (file.StartsWith(_metaRootWithSeparator, StringComparison.Ordinal))
                continue;

            var key = Path.GetRelativePath(_fullRoot, file).Replace('\\', '/');
            if (prefix.Length > 0 &&
                !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            keys.Add(key);
        }

        keys.Sort(StringComparer.Ordinal);

        var start = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            start = keys.Count;
            for (var i = 0; i < keys.Count; i++)
            {
                if (string.CompareOrdinal(keys[i], cursor) > 0)
                {
                    start = i;
                    break;
                }
            }
        }

        var count = Math.Min(limit, keys.Count - start);
        var items = new List<StorageObject>(count);
        for (var i = 0; i < count; i++)
        {
            var key = keys[start + i];
            var info = new FileInfo(Path.Combine(_fullRoot, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists)
                continue;

            var meta = await ReadMetadataAsync(key);
            items.Add(BuildObject(key, info, meta));
        }

        var next = start + count < keys.Count && count > 0
            ? keys[start + count - 1]
            : null;

        return new StoragePage(items, next);
    }

    protected string ResolvePath(string key)
    {
        var normalized = StorageKey.Normalize(key);
        if (IsReservedKey(normalized))
        {
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                "Storage key uses a reserved namespace.",
                key);
        }

        var segments = normalized.Split('/');
        var fullPath = Path.GetFullPath(Path.Combine(_fullRoot, Path.Combine(segments)));
        if (!fullPath.StartsWith(_fullRootWithSeparator, StringComparison.Ordinal))
        {
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                "Path traversal in storage key is not permitted.",
                key);
        }

        return fullPath;
    }

    private string MetaPath(string key)
    {
        var normalized = StorageKey.Normalize(key);
        var segments = normalized.Split('/');
        var fileName = segments[^1] + ".meta";
        var directory = segments.Length > 1
            ? Path.Combine(_metaRoot, Path.Combine(segments[..^1]))
            : _metaRoot;

        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!fullPath.StartsWith(_metaRootWithSeparator, StringComparison.Ordinal))
        {
            throw new StorageException(
                StorageErrorCode.InvalidKey,
                "Path traversal in storage key is not permitted.",
                key);
        }

        return fullPath;
    }

    private static bool IsReservedKey(string normalized)
    {
        if (normalized.Equals(MetaDirectoryName, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalized.StartsWith(
            MetaDirectoryName + "/",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEtag(FileInfo info) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");

    private static StorageObject BuildObject(
        string key,
        FileInfo info,
        LocalMetadata? meta)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (meta is not null)
        {
            foreach (var (name, value) in meta.Metadata)
                metadata[name] = value;

            if (!string.IsNullOrWhiteSpace(meta.CacheControl))
                metadata["cache-control"] = meta.CacheControl;
            if (!string.IsNullOrWhiteSpace(meta.ContentDisposition))
                metadata["content-disposition"] = meta.ContentDisposition;
            if (!string.IsNullOrWhiteSpace(meta.ContentEncoding))
                metadata["content-encoding"] = meta.ContentEncoding;
            if (!string.IsNullOrWhiteSpace(meta.ContentLanguage))
                metadata["content-language"] = meta.ContentLanguage;
        }

        return new StorageObject(
            key,
            info.Length,
            ResolveContentType(key, meta),
            BuildEtag(info),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            metadata.Count == 0 ? null : metadata);
    }

    private static string ResolveContentType(string key, LocalMetadata? meta)
    {
        if (meta is not null && !string.IsNullOrWhiteSpace(meta.ContentType))
            return meta.ContentType;

        return Files.ContentTypeFor(key);
    }

    private async Task WriteMetadataAsync(
        string key,
        string contentType,
        StoragePutOptions options)
    {
        var hasMetadata = options.Metadata is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(contentType) &&
            string.IsNullOrWhiteSpace(options.CacheControl) &&
            string.IsNullOrWhiteSpace(options.ContentDisposition) &&
            string.IsNullOrWhiteSpace(options.ContentEncoding) &&
            string.IsNullOrWhiteSpace(options.ContentLanguage) &&
            !hasMetadata)
        {
            // Clear metadata from a previous write so an overwrite cannot
            // inherit a stale content type or cache policy.
            var stalePath = MetaPath(key);
            if (File.Exists(stalePath))
                File.Delete(stalePath);
            return;
        }

        var metaPath = MetaPath(key);
        var directory = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string>
        {
            MetaFormatVersion,
            Encode(contentType ?? ""),
            Encode(options.CacheControl ?? ""),
            Encode(options.ContentDisposition ?? ""),
            Encode(options.ContentEncoding ?? ""),
            Encode(options.ContentLanguage ?? ""),
        };

        if (hasMetadata)
        {
            foreach (var (name, value) in options.Metadata!)
                lines.Add($"{Encode(name)}:{Encode(value)}");
        }

        await File.WriteAllLinesAsync(metaPath, lines);
    }

    private async Task<LocalMetadata?> ReadMetadataAsync(string key)
    {
        var metaPath = MetaPath(key);
        if (!File.Exists(metaPath))
            return null;

        var lines = await File.ReadAllLinesAsync(metaPath);
        if (lines.Length < 3)
            return null;

        string contentType;
        string cacheControl;
        string contentDisposition;
        string contentEncoding;
        string contentLanguage;
        int metadataStart;

        if (lines[0] == LegacyMetaFormatVersion)
        {
            contentType = Decode(lines[1]);
            cacheControl = Decode(lines[2]);
            contentDisposition = "";
            contentEncoding = "";
            contentLanguage = "";
            metadataStart = 3;
        }
        else if (lines[0] == MetaFormatVersion && lines.Length >= 6)
        {
            contentType = Decode(lines[1]);
            cacheControl = Decode(lines[2]);
            contentDisposition = Decode(lines[3]);
            contentEncoding = Decode(lines[4]);
            contentLanguage = Decode(lines[5]);
            metadataStart = 6;
        }
        else
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = metadataStart; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
                continue;

            metadata[Decode(lines[i][..separator])] = Decode(lines[i][(separator + 1)..]);
        }

        return new LocalMetadata(
            contentType,
            cacheControl,
            contentDisposition,
            contentEncoding,
            contentLanguage,
            metadata);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        if (value.Length == 0)
            return "";

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            // A corrupt sidecar should degrade to extension-based metadata
            // rather than failing every read of the object.
            return "";
        }
    }

    private sealed record LocalMetadata(
        string ContentType,
        string CacheControl,
        string ContentDisposition,
        string ContentEncoding,
        string ContentLanguage,
        Dictionary<string, string> Metadata);
}
#endif
