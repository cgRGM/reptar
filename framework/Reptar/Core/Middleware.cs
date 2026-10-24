using System.Diagnostics;

namespace Reptar;

public delegate Task<ReptarResponse> ReptarMiddleware(
    Ctx ctx,
    Func<Task<ReptarResponse>> next);

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

    public static ReptarMiddleware Cors() => async (ctx, next) =>
    {
        var origin = ctx.Req.Header("Origin");

        if (origin is not null)
        {
            ctx.SetHeader("Access-Control-Allow-Origin", origin);
            ctx.SetHeader("Access-Control-Allow-Credentials", "true");
            ctx.SetHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            ctx.SetHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (ctx.Req.Method == "OPTIONS")
                return ctx.Status(204);
        }

        return await next();
    };

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

        var allowed = await ctx.RateLimit.LimitAsync(
            category,
            key,
            limit,
            TimeSpan.FromSeconds(windowSeconds));

        if (!allowed)
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
}
