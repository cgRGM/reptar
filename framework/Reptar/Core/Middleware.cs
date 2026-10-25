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

    public static ReptarMiddleware Telemetry() => async (ctx, next) =>
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await next();
        stopwatch.Stop();

        ctx.Log.Info(
            $"{ctx.RequestId} {ctx.Req.Method} {ctx.Req.Path} -> {response.Status} ({stopwatch.Elapsed.TotalMilliseconds:0.0}ms)");

        return response;
    };

    public static ReptarMiddleware RateLimit(
        int limit = 60,
        int windowSeconds = 60,
        string category = "api",
        Func<Ctx, string>? keySelector = null) => async (ctx, next) =>
    {
        var key = keySelector?.Invoke(ctx)
            ?? ctx.User?.Id
            ?? ctx.Req.Header("CF-Connecting-IP")
            ?? ctx.Req.Header("X-Forwarded-For")
            ?? "anonymous";

        var result = await ctx.RateLimit.CheckAsync(
            category,
            key,
            limit,
            TimeSpan.FromSeconds(windowSeconds));

        ctx.SetHeader("X-RateLimit-Limit", result.Limit.ToString());
        ctx.SetHeader("X-RateLimit-Remaining", result.Remaining.ToString());
        ctx.SetHeader("X-RateLimit-Reset", result.ResetSeconds.ToString());

        if (!result.Allowed)
        {
            ctx.SetHeader("Retry-After", windowSeconds.ToString());
            return ctx.Error(429, "rate_limited", "Rate limit exceeded. Try again later.");
        }

        return await next();
    };

    public static ReptarMiddleware SWR(
        int ttlSeconds = 15,
        int swrSeconds = 30,
        Action<SwrOptions>? configure = null) =>
        CacheControl.SWR(ttlSeconds, swrSeconds, configure);

    public static ReptarMiddleware SecurityHeaders(
        SecurityHeadersOptions? options = null) =>
        ProductionExtensions.SecurityHeadersMiddleware(options);

    public static ReptarMiddleware StaticFiles(
        string rootDirectory = "wwwroot",
        StaticFileOptions? options = null) =>
        ProductionExtensions.StaticFilesMiddleware(rootDirectory, options);
}

public static class RateLimit
{
    public static ReptarMiddleware SlidingWindow(
        int limit = 60,
        int windowSeconds = 60,
        string category = "api",
        Func<Ctx, string>? keySelector = null) =>
        Middlewares.RateLimit(limit, windowSeconds, category, keySelector);
}
