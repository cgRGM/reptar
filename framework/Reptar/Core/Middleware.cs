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
        var response = await next();
        Console.WriteLine(
            $"{DateTimeOffset.UtcNow:O} {ctx.Req.Method} {ctx.Req.Path} -> {response.Status}");
        return response;
    };
}
