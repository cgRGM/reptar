using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Reptar;

/// <summary>
/// One cached SWR response. Stores are responsible for persisting the entry;
/// <see cref="IsRevalidating"/> is only meaningful for in-process stores.
/// </summary>
public sealed class SwrCacheEntry
{
    public required byte[] Body { get; init; }
    public required int Status { get; init; }
    public required string? ContentType { get; init; }
    public required KeyValuePair<string, string>[] Headers { get; init; }
    public required long CreatedTimestampTicks { get; init; }
    public required long TtlTicks { get; init; }
    public required long SwrTicks { get; init; }

    internal int IsRevalidating;
}

/// <summary>
/// Pluggable SWR cache backend. The default is an in-process bounded map;
/// <see cref="FileSwrCacheStore"/> shares entries between processes on one
/// host, and applications can implement this interface over Redis, a KV
/// namespace, or another shared store.
/// </summary>
public interface ISwrCacheStore
{
    bool TryGet(string key, out SwrCacheEntry entry);
    void Set(string key, SwrCacheEntry entry);
    void Remove(string key);
    void Clear();
    int Count { get; }
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>Bounded in-process store (the default).</summary>
public sealed class MemorySwrCacheStore(int maxEntries = 1024) : ISwrCacheStore
{
    private readonly ConcurrentDictionary<string, SwrCacheEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly int _maxEntries = Math.Max(1, maxEntries);

    public int Count => _entries.Count;

    public IReadOnlyCollection<string> Keys => _entries.Keys.ToArray();

    public bool TryGet(string key, out SwrCacheEntry entry) =>
        _entries.TryGetValue(key, out entry!);

    public void Set(string key, SwrCacheEntry entry)
    {
        _entries[key] = entry;
        Trim();
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();

    private void Trim()
    {
        if (_entries.Count <= _maxEntries)
            return;

        var now = Stopwatch.GetTimestamp();

        // Pass 1: expired entries.
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.CreatedTimestampTicks >= entry.TtlTicks + entry.SwrTicks)
                _entries.TryRemove(key, out _);
        }

        // Pass 2: oldest entries.
        while (_entries.Count > _maxEntries)
        {
            string? oldestKey = null;
            var oldestTicks = long.MaxValue;

            foreach (var (key, entry) in _entries)
            {
                if (entry.CreatedTimestampTicks < oldestTicks)
                {
                    oldestTicks = entry.CreatedTimestampTicks;
                    oldestKey = key;
                }
            }

            if (oldestKey is null || !_entries.TryRemove(oldestKey, out _))
                break;
        }
    }
}

#if !NETWASM
/// <summary>
/// File-backed SWR store for multi-process Kestrel deployments. Entries are
/// written atomically and keyed by a SHA-256 of the cache key; the original
/// key is retained in the file header for invalidation.
/// </summary>
public sealed class FileSwrCacheStore : ISwrCacheStore
{
    private const int Magic = 0x52535731; // RSW1
    private const byte Version = 1;
    private const string Extension = ".swrcache";

    private readonly string _directory;
    private readonly int _maxEntries;

    public FileSwrCacheStore(string directory, int maxEntries = 1024)
    {
        _directory = Path.GetFullPath(directory);
        _maxEntries = Math.Max(1, maxEntries);
        Directory.CreateDirectory(_directory);
    }

    public int Count => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, $"*{Extension}").Count()
        : 0;

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            var keys = new List<string>();

            if (!Directory.Exists(_directory))
                return keys;

            foreach (var file in Directory.EnumerateFiles(_directory, $"*{Extension}"))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    using var reader = new BinaryReader(stream, Encoding.UTF8);
                    if (reader.ReadInt32() != Magic || reader.ReadByte() != Version)
                        continue;

                    keys.Add(reader.ReadString());
                }
                catch (IOException)
                {
                    // A concurrent writer or a partially written file.
                }
            }

            return keys;
        }
    }

    public bool TryGet(string key, out SwrCacheEntry entry)
    {
        entry = null!;

        if (!Directory.Exists(_directory))
            return false;

        var path = PathFor(key);

        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadInt32() != Magic || reader.ReadByte() != Version)
            {
                TryDelete(path);
                return false;
            }

            var storedKey = reader.ReadString();
            if (!string.Equals(storedKey, key, StringComparison.Ordinal))
            {
                TryDelete(path);
                return false;
            }

            var status = reader.ReadInt32();
            var hasContentType = reader.ReadBoolean();
            var contentType = hasContentType ? reader.ReadString() : null;
            var created = reader.ReadInt64();
            var ttl = reader.ReadInt64();
            var swr = reader.ReadInt64();
            var headerCount = reader.ReadInt32();
            var headers = new KeyValuePair<string, string>[headerCount];

            for (var i = 0; i < headerCount; i++)
                headers[i] = new KeyValuePair<string, string>(
                    reader.ReadString(),
                    reader.ReadString());

            var bodyLength = reader.ReadInt32();
            var body = reader.ReadBytes(bodyLength);

            if (body.Length != bodyLength)
            {
                TryDelete(path);
                return false;
            }

            entry = new SwrCacheEntry
            {
                Body = body,
                Status = status,
                ContentType = contentType,
                Headers = headers,
                CreatedTimestampTicks = created,
                TtlTicks = ttl,
                SwrTicks = swr,
            };

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            TryDelete(path);
            return false;
        }
    }

    public void Set(string key, SwrCacheEntry entry)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(key);
        var temp = $"{path}.{Guid.NewGuid():n}.tmp";

        try
        {
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(key);
                writer.Write(entry.Status);
                writer.Write(entry.ContentType is not null);

                if (entry.ContentType is not null)
                    writer.Write(entry.ContentType);

                writer.Write(entry.CreatedTimestampTicks);
                writer.Write(entry.TtlTicks);
                writer.Write(entry.SwrTicks);
                writer.Write(entry.Headers.Length);

                foreach (var (name, value) in entry.Headers)
                {
                    writer.Write(name);
                    writer.Write(value);
                }

                writer.Write(entry.Body.Length);
                writer.Write(entry.Body);
            }

            File.Move(temp, path, overwrite: true);
            Trim();
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public void Remove(string key)
    {
        if (Directory.Exists(_directory))
            TryDelete(PathFor(key));
    }

    public void Clear()
    {
        if (!Directory.Exists(_directory))
            return;

        foreach (var file in Directory.EnumerateFiles(_directory, $"*{Extension}"))
            TryDelete(file);
    }

    private void Trim()
    {
        if (!Directory.Exists(_directory))
            return;

        var files = Directory.EnumerateFiles(_directory, $"*{Extension}")
            .Select(file => new FileInfo(file))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        var excess = files.Count - _maxEntries;

        for (var i = 0; i < excess; i++)
            TryDelete(files[i].FullName);
    }

    private string PathFor(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(key));
        return Path.Combine(
            _directory,
            Convert.ToHexString(hash).ToLowerInvariant() + Extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Another process may hold the file; it will be retried next time.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave it for the operator to clean up.
        }
    }
}
#endif
