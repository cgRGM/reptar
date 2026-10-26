using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Reptar;

public sealed class SwrOptions
{
    public int TtlSeconds { get; set; } = 15;
    public int SwrSeconds { get; set; } = 30;
    public int MaxEntries { get; set; } = 1024;
    public int MaxBodyBytes { get; set; } = 1_048_576;
    public bool BypassOnAuth { get; set; } = true;
    public bool InvalidateOnWrites { get; set; } = true;
    public string[] VaryHeaders { get; set; } = [];
    public Func<Ctx, string>? KeyBuilder { get; set; }
    public Func<Ctx, bool>? ShouldCache { get; set; }
    public Action<string, string>? OnInvalidate { get; set; }
    public MetricsRegistry? Metrics { get; set; }
}

public static class CacheControl
{
    private static readonly object StoresLock = new();
    private static readonly List<WeakReference<ConcurrentDictionary<string, CachedEntry>>> Stores = [];

    public static ReptarMiddleware SWR(int ttlSeconds = 15, int swrSeconds = 30, Action<SwrOptions>? configure = null)
    {
        var options = new SwrOptions
        {
            TtlSeconds = ttlSeconds,
            SwrSeconds = swrSeconds,
        };
        configure?.Invoke(options);

        var ttlTicks = (long)(options.TtlSeconds * (double)Stopwatch.Frequency);
        var swrTicks = (long)(options.SwrSeconds * (double)Stopwatch.Frequency);
        var cache = new ConcurrentDictionary<string, CachedEntry>(StringComparer.Ordinal);
#if !NETWASM
        var missGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
#endif
        RegisterStore(cache);

        return async (ctx, next) =>
        {
            // Invalidate on write methods if enabled
            if (options.InvalidateOnWrites && IsWriteMethod(ctx.Req.Method))
            {
                var writeRes = await next();
                if (writeRes.Status is >= 200 and < 400)
                {
                    InvalidateStore(cache, ctx.Req.Path);
                    options.OnInvalidate?.Invoke(ctx.Req.Method, ctx.Req.Path);
                }
                writeRes.Headers["X-Cache"] = "BYPASS";
                options.Metrics?.RecordCache("BYPASS");
                return writeRes;
            }

            // Only cache GET and HEAD requests
            if (ctx.Req.Method != "GET" && ctx.Req.Method != "HEAD")
            {
                var bypassRes = await next();
                bypassRes.Headers["X-Cache"] = "BYPASS";
                options.Metrics?.RecordCache("BYPASS");
                return bypassRes;
            }

            // Bypass authentication / cookie requests if configured
            if (options.BypassOnAuth)
            {
                if (ctx.Req.Header("Authorization") is not null ||
                    ctx.Req.Header("Cookie") is not null)
                {
                    var authRes = await next();
                    authRes.Headers["X-Cache"] = "BYPASS";
                    options.Metrics?.RecordCache("BYPASS");
                    return authRes;
                }
            }

            // Custom predicate
            if (options.ShouldCache is not null && !options.ShouldCache(ctx))
            {
                var skippedRes = await next();
                skippedRes.Headers["X-Cache"] = "BYPASS";
                options.Metrics?.RecordCache("BYPASS");
                return skippedRes;
            }

            var cacheKey = BuildCacheKey(ctx, options);
            var nowTicks = Stopwatch.GetTimestamp();

            if (cache.TryGetValue(cacheKey, out var entry))
            {
                var ageTicks = nowTicks - entry.CreatedTimestampTicks;
                var ageSeconds = (long)(ageTicks / (double)Stopwatch.Frequency);

                // 1. Fresh cache hit
                if (ageTicks < entry.TtlTicks)
                {
                    return CreateResponse(entry, "HIT", ageSeconds, options);
                }

#if !NETWASM
                // 2. Stale-while-revalidate hit. Workers freeze between
                // events, so a background Task.Run revalidation is not
                // guaranteed to run; the Wasm path falls through to a
                // synchronous revalidation instead.
                if (ageTicks < (entry.TtlTicks + entry.SwrTicks))
                {
                    // Kick off background revalidation if not already in progress
                    if (Interlocked.CompareExchange(ref entry.IsRevalidating, 1, 0) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fresh = await next();
                                if (fresh.Status == 200 &&
                                    fresh.Body is not null &&
                                    fresh.Body.Length <= options.MaxBodyBytes)
                                {
                                    Store(cache, cacheKey, fresh, ttlTicks, swrTicks, options.MaxEntries);
                                }
                            }
                            catch
                            {
                                // Background revalidation failed; retain existing cached response
                            }
                            finally
                            {
                                Interlocked.Exchange(ref entry.IsRevalidating, 0);
                            }
                        });
                    }

                    return CreateResponse(entry, "STALE", ageSeconds, options);
                }
#endif
            }

            // 3. Cache miss or expired.
#if NETWASM
            // Workers are single-threaded per isolate and do not expose
            // SemaphoreSlim; a per-isolate stampede is bounded by the event
            // loop, so revalidate directly.
            var missRes = await next();
            if (missRes.Status == 200 &&
                missRes.Body is not null &&
                missRes.Body.Length <= options.MaxBodyBytes)
            {
                Store(cache, cacheKey, missRes, ttlTicks, swrTicks, options.MaxEntries);
                missRes.Headers["X-Cache"] = "MISS";
                missRes.Headers["Age"] = "0";
                missRes.Headers["Cache-Control"] = $"public, max-age={options.TtlSeconds}, stale-while-revalidate={options.SwrSeconds}";
            }
            else
            {
                missRes.Headers["X-Cache"] = "BYPASS";
            }

            options.Metrics?.RecordCache(missRes.Headers["X-Cache"]);
            return missRes;
#else
            // Single-flight the upstream call so a burst of concurrent misses
            // does not stampede the handler.
            var gate = missGates.GetOrAdd(
                cacheKey,
                static _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync();

            try
            {
                // Another request may have filled the cache while we waited.
                if (cache.TryGetValue(cacheKey, out var filled) &&
                    Stopwatch.GetTimestamp() - filled.CreatedTimestampTicks < filled.TtlTicks)
                {
                    var filledAge = (long)((Stopwatch.GetTimestamp() - filled.CreatedTimestampTicks) /
                        (double)Stopwatch.Frequency);
                    return CreateResponse(filled, "HIT", filledAge, options);
                }

                var missRes = await next();
                if (missRes.Status == 200 &&
                    missRes.Body is not null &&
                    missRes.Body.Length <= options.MaxBodyBytes)
                {
                    Store(cache, cacheKey, missRes, ttlTicks, swrTicks, options.MaxEntries);
                    missRes.Headers["X-Cache"] = "MISS";
                    missRes.Headers["Age"] = "0";
                    missRes.Headers["Cache-Control"] = $"public, max-age={options.TtlSeconds}, stale-while-revalidate={options.SwrSeconds}";
                }
                else
                {
                    missRes.Headers["X-Cache"] = "BYPASS";
                }

                options.Metrics?.RecordCache(missRes.Headers["X-Cache"]);
                return missRes;
            }
            finally
            {
                gate.Release();

                if (gate.CurrentCount == 1)
                    missGates.TryRemove(cacheKey, out _);
            }
#endif
        };
    }

    public static void Invalidate(string pathOrPrefix)
    {
        foreach (var store in GetActiveStores())
        {
            InvalidateStore(store, pathOrPrefix);
        }
    }

    private static void InvalidateStore(ConcurrentDictionary<string, CachedEntry> cache, string pathOrPrefix)
    {
        var target = pathOrPrefix.TrimEnd('/');
        foreach (var kvp in cache)
        {
            var key = kvp.Key;
            var pipeIdx = key.IndexOf('|');
            var keyWithoutVary = pipeIdx >= 0 ? key[..pipeIdx] : key;
            var colonIdx = keyWithoutVary.IndexOf(':');
            var pathPart = colonIdx >= 0 ? keyWithoutVary[(colonIdx + 1)..] : keyWithoutVary;
            var queryIdx = pathPart.IndexOf('?');
            var pathOnly = (queryIdx >= 0 ? pathPart[..queryIdx] : pathPart).TrimEnd('/');

            if (string.Equals(pathOnly, target, StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(pathOnly + "/", StringComparison.OrdinalIgnoreCase))
            {
                cache.TryRemove(key, out _);
            }
        }
    }

    public static void Invalidate(params string[] paths)
    {
        foreach (var p in paths)
        {
            Invalidate(p);
        }
    }

    public static void PurgeAll()
    {
        foreach (var store in GetActiveStores())
        {
            store.Clear();
        }
    }

    public static int Count => GetActiveStores().Sum(s => s.Count);

    private static void RegisterStore(ConcurrentDictionary<string, CachedEntry> store)
    {
        lock (StoresLock)
        {
            Stores.RemoveAll(wr => !wr.TryGetTarget(out _));
            Stores.Add(new WeakReference<ConcurrentDictionary<string, CachedEntry>>(store));
        }
    }

    private static List<ConcurrentDictionary<string, CachedEntry>> GetActiveStores()
    {
        var result = new List<ConcurrentDictionary<string, CachedEntry>>();
        lock (StoresLock)
        {
            for (var i = Stores.Count - 1; i >= 0; i--)
            {
                if (Stores[i].TryGetTarget(out var target))
                    result.Add(target);
                else
                    Stores.RemoveAt(i);
            }
        }
        return result;
    }

    private static bool IsWriteMethod(string method) =>
        method is "POST" or "PUT" or "DELETE" or "PATCH";

    private static string BuildCacheKey(Ctx ctx, SwrOptions options)
    {
        if (options.KeyBuilder is not null)
            return options.KeyBuilder(ctx);

        var path = ctx.Req.Path;
        var query = ctx.Req.Query;
        var baseKey = string.IsNullOrEmpty(query)
            ? $"{ctx.Req.Method}:{path}"
            : $"{ctx.Req.Method}:{path}?{query}";

        if (options.VaryHeaders.Length == 0)
            return baseKey;

        var sb = new StringBuilder(baseKey);
        foreach (var header in options.VaryHeaders)
        {
            sb.Append('|').Append(header).Append('=').Append(ctx.Req.Header(header) ?? "");
        }
        return sb.ToString();
    }

    private static void Store(
        ConcurrentDictionary<string, CachedEntry> cache,
        string key,
        ReptarResponse response,
        long ttlTicks,
        long swrTicks,
        int maxEntries)
    {
        var cap = Math.Max(1, maxEntries);
        var nowTicks = Stopwatch.GetTimestamp();

        if (!cache.ContainsKey(key) && cache.Count >= cap)
        {
            EvictToCapacity(cache, cap - 1, nowTicks);
        }

        var headers = response.Headers
            .Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("X-Cache", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Age", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var entry = new CachedEntry
        {
            Body = response.Body ?? [],
            Status = response.Status,
            ContentType = response.ContentType,
            Headers = headers,
            CreatedTimestampTicks = nowTicks,
            TtlTicks = ttlTicks,
            SwrTicks = swrTicks,
            IsRevalidating = 0,
        };

        cache[key] = entry;
    }

    private static void EvictToCapacity(
        ConcurrentDictionary<string, CachedEntry> cache,
        int targetCount,
        long nowTicks)
    {
        // Pass 1: Evict expired entries
        foreach (var kvp in cache)
        {
            if (nowTicks - kvp.Value.CreatedTimestampTicks >= kvp.Value.TtlTicks + kvp.Value.SwrTicks)
            {
                cache.TryRemove(kvp.Key, out _);
            }
        }

        // Pass 2: Evict oldest entries until count <= targetCount
        while (cache.Count > targetCount)
        {
            string? oldestKey = null;
            var oldestTicks = long.MaxValue;

            foreach (var kvp in cache)
            {
                if (kvp.Value.CreatedTimestampTicks < oldestTicks)
                {
                    oldestTicks = kvp.Value.CreatedTimestampTicks;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey is null || !cache.TryRemove(oldestKey, out _))
                break;
        }
    }

    private static ReptarResponse CreateResponse(CachedEntry entry, string cacheStatus, long ageSeconds, SwrOptions options)
    {
        var res = new ReptarResponse
        {
            Status = entry.Status,
            ContentType = entry.ContentType,
            Body = entry.Body
        };

        foreach (var h in entry.Headers)
        {
            res.Headers[h.Key] = h.Value;
        }

        res.Headers["X-Cache"] = cacheStatus;
        res.Headers["Age"] = ageSeconds.ToString();
        res.Headers["Cache-Control"] = $"public, max-age={options.TtlSeconds}, stale-while-revalidate={options.SwrSeconds}";
        options.Metrics?.RecordCache(cacheStatus);

        return res;
    }

    private sealed class CachedEntry
    {
        public required byte[] Body { get; init; }
        public required int Status { get; init; }
        public required string? ContentType { get; init; }
        public required KeyValuePair<string, string>[] Headers { get; init; }
        public required long CreatedTimestampTicks { get; init; }
        public required long TtlTicks { get; init; }
        public required long SwrTicks { get; init; }
        public int IsRevalidating;
    }
}

public static class ResponseCache
{
    public static ReptarMiddleware SWR(int ttlSeconds = 15, int swrSeconds = 30, Action<SwrOptions>? configure = null) =>
        CacheControl.SWR(ttlSeconds, swrSeconds, configure);

    public static void Invalidate(string pathOrPrefix) => CacheControl.Invalidate(pathOrPrefix);
    public static void Invalidate(params string[] paths) => CacheControl.Invalidate(paths);
    public static void PurgeAll() => CacheControl.PurgeAll();
    public static int Count => CacheControl.Count;
}
