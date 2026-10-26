using System.Diagnostics;

namespace Reptar;

public delegate Task<ReptarResponse> ReptarMiddleware(
    Ctx ctx,
    Func<Task<ReptarResponse>> next);

public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];

    public string[] AllowOrigins
    {
        get => AllowedOrigins;
        set => AllowedOrigins = value;
    }

    public string[] AllowedMethods { get; set; } = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    public string[] AllowMethods
    {
        get => AllowedMethods;
        set => AllowedMethods = value;
    }

    public string[] AllowedHeaders { get; set; } = ["Content-Type", "Authorization"];

    public string[] AllowHeaders
    {
        get => AllowedHeaders;
        set => AllowedHeaders = value;
    }

    public bool AllowCredentials { get; set; } = true;
    public int MaxAgeSeconds { get; set; } = 86_400;
}

public static class Middlewares
{
    public static ReptarMiddleware RequestId() => async (ctx, next) =>
    {
        ctx.RequestId = Ids.New("req");
        ctx.SetHeader("X-Request-Id", ctx.RequestId);

        var stopwatch = Stopwatch.StartNew();
        var response = await next();
        stopwatch.Stop();

        response.Headers["Server-Timing"] =
            $"reptar;dur={stopwatch.Elapsed.TotalMilliseconds:0.0}";

        return response;
    };

    public static ReptarMiddleware Cors() => Cors((CorsOptions?)null);

    public static ReptarMiddleware Cors(params string[] allowedOrigins) =>
        Cors(new CorsOptions { AllowedOrigins = allowedOrigins });

    public static ReptarMiddleware Cors(CorsOptions? options)
    {
        var opts = options ?? new CorsOptions();
        var methodsHeader = string.Join(", ", opts.AllowedMethods);
        var headersHeader = string.Join(", ", opts.AllowedHeaders);
        var maxAgeHeader = opts.MaxAgeSeconds > 0 ? opts.MaxAgeSeconds.ToString() : null;

        return async (ctx, next) =>
        {
            var origin = ctx.Req.Header("Origin")?.Trim();
            if (string.IsNullOrEmpty(origin))
                return await next();

            var isAllowed = IsOriginAllowed(origin, opts.AllowedOrigins, ctx.Environment.Get("CORS_ORIGINS"));
            var isPreflight = string.Equals(ctx.Req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

            if (!isAllowed)
            {
                if (isPreflight)
                    return ctx.Status(403);

                return await next();
            }

            var allowWildcard = !opts.AllowCredentials && HasWildcardOrigin(opts.AllowedOrigins, ctx.Environment.Get("CORS_ORIGINS"));
            var allowOriginValue = allowWildcard ? "*" : origin;

            ctx.SetHeader("Access-Control-Allow-Origin", allowOriginValue);
            if (!allowWildcard)
                ctx.SetHeader("Vary", "Origin");

            if (opts.AllowCredentials && !allowWildcard)
                ctx.SetHeader("Access-Control-Allow-Credentials", "true");

            if (isPreflight)
            {
                ctx.SetHeader("Access-Control-Allow-Methods", methodsHeader);
                ctx.SetHeader("Access-Control-Allow-Headers", headersHeader);
                if (maxAgeHeader is not null)
                    ctx.SetHeader("Access-Control-Max-Age", maxAgeHeader);

                return ctx.Status(204);
            }

            return await next();
        };
    }

    private static bool IsOriginAllowed(string origin, string[] configuredOrigins, string? envOrigins)
    {
        var normalizedOrigin = origin.TrimEnd('/');

        for (var i = 0; i < configuredOrigins.Length; i++)
        {
            var candidate = configuredOrigins[i].Trim().TrimEnd('/');
            if (candidate.Length == 0)
                continue;
            if (candidate == "*" || string.Equals(candidate, normalizedOrigin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(envOrigins))
        {
            foreach (var part in envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = part.TrimEnd('/');
                if (candidate == "*" || string.Equals(candidate, normalizedOrigin, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool HasWildcardOrigin(string[] configuredOrigins, string? envOrigins)
    {
        for (var i = 0; i < configuredOrigins.Length; i++)
        {
            if (configuredOrigins[i].Trim() == "*")
                return true;
        }

        if (!string.IsNullOrWhiteSpace(envOrigins))
        {
            foreach (var part in envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part == "*")
                    return true;
            }
        }

        return false;
    }

    public static ReptarMiddleware Telemetry(TelemetryOptions? options = null)
    {
        var opts = options ?? new TelemetryOptions();
        DbMetrics.SlowQueryThresholdMs = opts.SlowQueryMs;

        return async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var response = await next();
            var elapsed = Stopwatch.GetTimestamp() - started;
            var durationMs = elapsed * 1000.0 / Stopwatch.Frequency;

            var db = ctx.DbMetrics;
            var slow = opts.SlowRequestMs > 0 && durationMs >= opts.SlowRequestMs;
            var errorCode = response.Status >= 500
                ? "internal_error"
                : response.Status == 404
                    ? "not_found"
                    : null;
            var cache = response.Headers.TryGetValue("X-Cache", out var cacheValue)
                ? cacheValue
                : null;
            var cfRay = ctx.Req.Header("CF-Ray");
            var telemetry = new RequestTelemetry(
                ctx.RequestId,
                ctx.TraceId,
                ctx.SpanId,
                ctx.ParentSpanId,
                ctx.Req.Method,
                ctx.RoutePattern ?? ctx.Req.Path,
                ctx.Req.Path,
                response.Status,
                durationMs,
                db.TotalMs,
                db.QueryCount,
                cache,
                ctx.User?.Id,
                cfRay,
                ColoFrom(cfRay),
                errorCode,
                slow);

            var shouldLog = opts.Enabled &&
                (slow || response.Status >= 400 || Sample(opts.SampleRate));

            if (shouldLog)
                ctx.Log.Request(in telemetry);

            if (db.QueryCount > 0)
                ctx.ServerTiming("db", db.TotalMs, $"{db.QueryCount} queries");

            ctx.ServerTiming("total", durationMs);

            if (db.SlowQueryCount > 0 && db.SlowestSql is not null)
                ctx.Log.Warn($"slow_query {db.SlowestQueryMs:0.0}ms {db.SlowestSql}");

            return response;
        };
    }

    /// <summary>
    /// Cancels the request pipeline after <paramref name="timeout"/> and returns
    /// 504 when the handler has not completed. Handlers should observe
    /// <see cref="Ctx.RequestAborted"/> to stop database/HTTP work promptly.
    /// </summary>
    public static ReptarMiddleware Timeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        return async (ctx, next) =>
        {
            var previous = ctx.RequestAborted;
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(previous);
            requestCts.CancelAfter(timeout);
            ctx.RequestAborted = requestCts.Token;

            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout, delayCts.Token);
            var handler = next();

            try
            {
                var completed = await Task.WhenAny(handler, delay);
                delayCts.Cancel();

                if (completed != handler)
                {
                    requestCts.Cancel();
                    _ = handler.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);

                    return ctx.Error(
                        504,
                        "request_timeout",
                        $"Request exceeded the {timeout.TotalMilliseconds:0}ms timeout.");
                }

                return await handler;
            }
            catch (OperationCanceledException) when (
                requestCts.IsCancellationRequested && !previous.IsCancellationRequested)
            {
                return ctx.Error(
                    504,
                    "request_timeout",
                    $"Request exceeded the {timeout.TotalMilliseconds:0}ms timeout.");
            }
            finally
            {
                ctx.RequestAborted = previous;
            }
        };
    }

    /// <summary>
    /// Adds a strong ETag to successful GET/HEAD JSON/text responses and
    /// returns 304 when If-None-Match matches. Opt-in: hashing every response
    /// costs CPU, so mount it only on cacheable read routes.
    /// </summary>
    public static ReptarMiddleware ETag(ETagOptions? options = null)
    {
        var opts = options ?? new ETagOptions();

        return async (ctx, next) =>
        {
            var response = await next();

            if (ctx.Req.Method is not ("GET" or "HEAD") ||
                response.Status != 200 ||
                response.Body is not { Length: > 0 } body ||
                body.Length > opts.MaxBodyBytes ||
                response.Headers.ContainsKey("ETag") ||
                response.SetCookies.Count > 0)
            {
                return response;
            }

            var contentType = response.ContentType ?? "";
            if (!opts.MimeTypePrefixes.Any(prefix =>
                contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return response;
            }

            if (response.Headers.TryGetValue("Cache-Control", out var cacheControl) &&
                cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }

            var hash = await ctx.Crypto.Sha256(Convert.ToBase64String(body));
            var etag = $"\"{hash[..16]}\"";
            response.Headers["ETag"] = etag;

            if (opts.MaxAgeSeconds > 0 &&
                !response.Headers.ContainsKey("Cache-Control"))
            {
                response.Headers["Cache-Control"] = $"public, max-age={opts.MaxAgeSeconds}";
            }

            var ifNoneMatch = ctx.Req.Header("If-None-Match");
            if (ifNoneMatch is null)
                return response;

            var normalized = ifNoneMatch.Trim();
            if (normalized.StartsWith("W/", StringComparison.Ordinal))
                normalized = normalized[2..].Trim();

            if (!string.Equals(normalized, etag, StringComparison.Ordinal))
                return response;

            var notModified = new ReptarResponse { Status = 304 };
            foreach (var (name, value) in response.Headers)
                notModified.Headers[name] = value;

            return notModified;
        };
    }

    public static ReptarMiddleware Metrics(MetricsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var response = await next();
            var durationMs = (Stopwatch.GetTimestamp() - started) * 1000.0 /
                Stopwatch.Frequency;

            registry.Record(
                ctx.Req.Method,
                ctx.RoutePattern ?? ctx.Req.Path,
                response.Status,
                durationMs);

            return response;
        };
    }

    private static bool Sample(double rate) =>
        rate >= 1 || (rate > 0 && Random.Shared.NextDouble() < rate);

    private static string? ColoFrom(string? cfRay)
    {
        if (string.IsNullOrEmpty(cfRay))
            return null;

        var dash = cfRay.LastIndexOf('-');
        return dash >= 0 && dash < cfRay.Length - 1
            ? cfRay[(dash + 1)..]
            : null;
    }

    public static ReptarMiddleware RateLimit(
        int limit = 60,
        int windowSeconds = 60,
        string category = "api",
        Func<Ctx, string>? keySelector = null,
        bool trustProxyHeaders = false) =>
        RateLimit(new RateLimitOptions
        {
            Limit = limit,
            WindowSeconds = windowSeconds,
            Category = category,
            KeySelector = keySelector,
            TrustProxyHeaders = trustProxyHeaders,
        });

    public static ReptarMiddleware RateLimit(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var window = TimeSpan.FromSeconds(Math.Max(1, options.WindowSeconds));

        return async (ctx, next) =>
        {
            var key = ResolveRateLimitKey(ctx, options);

            var result = await ctx.RateLimit.CheckAsync(
                options.Category,
                key,
                options.Limit,
                window);

            ctx.SetHeader("X-RateLimit-Limit", result.Limit.ToString());
            ctx.SetHeader("X-RateLimit-Remaining", result.Remaining.ToString());
            ctx.SetHeader("X-RateLimit-Reset", result.ResetSeconds.ToString());

            if (!result.Allowed)
            {
                ctx.SetHeader("Retry-After", options.WindowSeconds.ToString());
                return ctx.Error(429, "rate_limited", "Rate limit exceeded. Try again later.");
            }

            return await next();
        };
    }

    internal static string ResolveRateLimitKey(Ctx ctx, RateLimitOptions options)
    {
        var custom = NonEmpty(options.KeySelector?.Invoke(ctx));
        if (custom is not null)
            return custom;

        var userId = NonEmpty(ctx.User?.Id);
        if (userId is not null)
            return userId;

        if (options.TrustProxyHeaders)
        {
            var cfIp = NonEmpty(ctx.Req.Header("CF-Connecting-IP"));
            if (cfIp is not null)
                return cfIp;

            var forwardedIp = FirstForwardedIp(ctx.Req.Header("X-Forwarded-For"));
            if (forwardedIp is not null)
                return forwardedIp;
        }

        var remoteIp = NonEmpty(ctx.Req.RemoteIp);
        if (remoteIp is not null)
            return remoteIp;

        return "loopback";
    }

    private static string? FirstForwardedIp(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        var commaIndex = headerValue.IndexOf(',');
        var first = commaIndex >= 0 ? headerValue[..commaIndex] : headerValue;
        return NonEmpty(first);
    }

    private static string? NonEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    public static ReptarMiddleware SWR(
        int ttlSeconds = 15,
        int swrSeconds = 30,
        Action<SwrOptions>? configure = null) =>
        CacheControl.SWR(ttlSeconds, swrSeconds, configure);

    public static ReptarMiddleware SecurityHeaders(SecurityHeadersOptions? options = null)
    {
        var opts = options ?? new SecurityHeadersOptions();

        return async (ctx, next) =>
        {
            var response = await next();

            if (!string.IsNullOrEmpty(opts.ContentTypeOptions))
                response.Headers.TryAdd("X-Content-Type-Options", opts.ContentTypeOptions);

            if (!string.IsNullOrEmpty(opts.FrameOptions))
                response.Headers.TryAdd("X-Frame-Options", opts.FrameOptions);

            if (!string.IsNullOrEmpty(opts.ReferrerPolicy))
                response.Headers.TryAdd("Referrer-Policy", opts.ReferrerPolicy);

            if (!string.IsNullOrEmpty(opts.StrictTransportSecurity))
                response.Headers.TryAdd("Strict-Transport-Security", opts.StrictTransportSecurity);

            if (!string.IsNullOrEmpty(opts.XssProtection))
                response.Headers.TryAdd("X-XSS-Protection", opts.XssProtection);

            if (!string.IsNullOrEmpty(opts.PermissionsPolicy))
                response.Headers.TryAdd("Permissions-Policy", opts.PermissionsPolicy);

            if (!string.IsNullOrEmpty(opts.ContentSecurityPolicy))
                response.Headers.TryAdd("Content-Security-Policy", opts.ContentSecurityPolicy);

            return response;
        };
    }

    public static ReptarMiddleware BodyLimit(long maxBytes) => async (ctx, next) =>
    {
        if (ctx.Req.Body is not null && ctx.Req.Body.Length > maxBytes)
        {
            return ctx.Error(413, "payload_too_large", $"Payload exceeds maximum size of {maxBytes} bytes.");
        }
        return await next();
    };

#if !NETWASM
    public static ReptarMiddleware StaticFiles(
        string rootDirectory = "wwwroot",
        StaticFileOptions? options = null) =>
        ProductionExtensions.StaticFilesMiddleware(rootDirectory, options);
#endif
}

public sealed class SecurityHeadersOptions
{
    public string ContentTypeOptions { get; set; } = "nosniff";
    public string FrameOptions { get; set; } = "DENY";
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";
    public string? StrictTransportSecurity { get; set; } = "max-age=31536000; includeSubDomains";
    public string? XssProtection { get; set; } = "0";
    public string? PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=()";
    public string? ContentSecurityPolicy { get; set; }
}

public sealed class ETagOptions
{
    public long MaxBodyBytes { get; set; } = 1_048_576;
    public int MaxAgeSeconds { get; set; }
    public string[] MimeTypePrefixes { get; set; } = ["application/json", "text/"];
}

public sealed class RateLimitOptions
{
    public int Limit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
    public string Category { get; set; } = "api";
    public bool TrustProxyHeaders { get; set; }
    public Func<Ctx, string>? KeySelector { get; set; }
}

/// <summary>
/// Distributed fixed-window rate limiter backed by <see cref="IKvClient"/>.
/// Use when running across multiple worker isolates or replicas where
/// per-process <see cref="MemoryRateLimiter"/> state is not shared.
/// </summary>
public sealed class KvRateLimiter(
    IKvClient kv,
    int defaultLimit = 60,
    TimeSpan? defaultWindow = null) : IRateLimiter
{
    private readonly TimeSpan _defaultWindow = defaultWindow ?? TimeSpan.FromMinutes(1);

    public async Task<bool> LimitAsync(string category, string key) =>
        (await CheckAsync(category, key, defaultLimit, _defaultWindow)).Allowed;

    public async Task<bool> LimitAsync(string category, string key, int limit, TimeSpan window) =>
        (await CheckAsync(category, key, limit, window)).Allowed;

    public async Task<RateLimitResult> CheckAsync(
        string category,
        string key,
        int limit,
        TimeSpan window)
    {
        var max = Math.Max(1, limit);
        var windowSeconds = Math.Max(1, (int)Math.Ceiling(window.TotalSeconds));
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucketIndex = nowSeconds / windowSeconds;
        var resetSeconds = (int)Math.Max(1, ((bucketIndex + 1) * windowSeconds) - nowSeconds);
        var storageKey = $"rl:{category}:{key}:{bucketIndex}";

        var raw = await kv.GetAsync(storageKey);
        var current = int.TryParse(raw, out var parsed) ? parsed : 0;

        if (current >= max)
            return new RateLimitResult(false, max, 0, resetSeconds, resetSeconds);

        var nextCount = current + 1;
        await kv.PutAsync(storageKey, nextCount.ToString(), windowSeconds + 5);
        return new RateLimitResult(true, max, Math.Max(0, max - nextCount), resetSeconds, 0);
    }
}

public static class RateLimit
{
    public static ReptarMiddleware SlidingWindow(
        int limit = 60,
        int windowSeconds = 60,
        string category = "api",
        Func<Ctx, string>? keySelector = null,
        bool trustProxyHeaders = false) =>
        Middlewares.RateLimit(limit, windowSeconds, category, keySelector, trustProxyHeaders);

    public static ReptarMiddleware SlidingWindow(RateLimitOptions options) =>
        Middlewares.RateLimit(options);
}
