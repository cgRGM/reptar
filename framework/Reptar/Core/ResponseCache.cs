using System.Collections.Concurrent;
using System.Diagnostics;

namespace Reptar;

public sealed class SwrOptions
{
    public int TtlSeconds { get; set; } = 15;
    public int SwrSeconds { get; set; } = 30;
    public bool BypassOnAuth { get; set; } = true;
    public bool InvalidateOnWrites { get; set; } = true;
    public Func<Ctx, bool>? ShouldCache { get; set; }
    public Action<string, string>? OnInvalidate { get; set; }
}

public static class CacheControl
{
    private static readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);

    public static ReptarMiddleware SWR(int ttlSeconds = 15, int swrSeconds = 30, Action<SwrOptions>? configure = null)
    {
        var options = new SwrOptions
        {
            TtlSeconds = ttlSeconds,
            SwrSeconds = swrSeconds
        };
        configure?.Invoke(options);

        var ttlTicks = (long)(options.TtlSeconds * (double)Stopwatch.Frequency);
        var swrTicks = (long)(options.SwrSeconds * (double)Stopwatch.Frequency);

        return async (ctx, next) =>
        {
            // Invalidate on write methods if enabled
            if (options.InvalidateOnWrites && IsWriteMethod(ctx.Req.Method))
            {
                var writeRes = await next();
                if (writeRes.Status is >= 200 and < 400)
                {
                    Invalidate(ctx.Req.Path);
                    options.OnInvalidate?.Invoke(ctx.Req.Method, ctx.Req.Path);
                }
                writeRes.Headers["X-Cache"] = "BYPASS";
                return writeRes;
            }

            // Only cache GET and HEAD requests
            if (ctx.Req.Method != "GET" && ctx.Req.Method != "HEAD")
            {
                var bypassRes = await next();
                bypassRes.Headers["X-Cache"] = "BYPASS";
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
                    return authRes;
                }
            }

            // Custom predicate
            if (options.ShouldCache is not null && !options.ShouldCache(ctx))
            {
                var skippedRes = await next();
                skippedRes.Headers["X-Cache"] = "BYPASS";
                return skippedRes;
            }

            var cacheKey = BuildCacheKey(ctx);
            var nowTicks = Stopwatch.GetTimestamp();

            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                var ageTicks = nowTicks - entry.CreatedTimestampTicks;
                var ageSeconds = (long)(ageTicks / (double)Stopwatch.Frequency);

                // 1. Fresh cache hit
                if (ageTicks < entry.TtlTicks)
                {
                    return CreateResponse(entry, "HIT", ageSeconds, options);
                }

                // 2. Stale-while-revalidate hit
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
                                if (fresh.Status == 200 && fresh.Body is not null)
                                {
                                    Store(cacheKey, fresh, ttlTicks, swrTicks);
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
            }

            // 3. Cache miss or expired
            var missRes = await next();
            if (missRes.Status == 200 && missRes.Body is not null)
            {
                Store(cacheKey, missRes, ttlTicks, swrTicks);
                missRes.Headers["X-Cache"] = "MISS";
                missRes.Headers["Age"] = "0";
                missRes.Headers["Cache-Control"] = $"public, max-age={options.TtlSeconds}, stale-while-revalidate={options.SwrSeconds}";
            }
            else
            {
                missRes.Headers["X-Cache"] = "BYPASS";
            }

            return missRes;
        };
    }

    public static void Invalidate(string pathOrPrefix)
    {
        var target = pathOrPrefix.TrimEnd('/');
        foreach (var kvp in _cache)
        {
            var key = kvp.Key;
            var colonIdx = key.IndexOf(':');
            var pathPart = colonIdx >= 0 ? key.Substring(colonIdx + 1) : key;
            var queryIdx = pathPart.IndexOf('?');
            var pathOnly = (queryIdx >= 0 ? pathPart.Substring(0, queryIdx) : pathPart).TrimEnd('/');

            if (string.Equals(pathOnly, target, StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(pathOnly + "/", StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
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
        _cache.Clear();
    }

    public static int Count => _cache.Count;

    private static bool IsWriteMethod(string method) =>
        method is "POST" or "PUT" or "DELETE" or "PATCH";

    private static string BuildCacheKey(Ctx ctx)
    {
        var path = ctx.Req.Path;
        var query = ctx.Req.Query;
        return string.IsNullOrEmpty(query)
            ? $"{ctx.Req.Method}:{path}"
            : $"{ctx.Req.Method}:{path}?{query}";
    }

    private static void Store(string key, ReptarResponse response, long ttlTicks, long swrTicks)
    {
        var headers = response.Headers
            .Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("X-Cache", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Age", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var entry = new CachedEntry
        {
            Body = response.Body ?? Array.Empty<byte>(),
            Status = response.Status,
            ContentType = response.ContentType,
            Headers = headers,
            CreatedTimestampTicks = Stopwatch.GetTimestamp(),
            TtlTicks = ttlTicks,
            SwrTicks = swrTicks,
            IsRevalidating = 0
        };

        _cache[key] = entry;
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
