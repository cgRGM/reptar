using System.Collections.Concurrent;

namespace Reptar;

/// <summary>
/// Shared per-process login failure throttling used by the built-in auth
/// routes (<see cref="Auth.Routes"/>) and the Better Auth protocol routes.
/// Keys are host-appropriate client identities — the socket peer address on
/// Kestrel/Lambda, or proxy headers only when the deployment opts into
/// <see cref="AuthOptions.TrustProxyHeaders"/> — and the tracked map is
/// bounded so it cannot be used for memory exhaustion.
///
/// The Workers (NetWasm) auth slice keeps its own per-isolate throttle in
/// <c>Reptar.Wasm/Auth/Auth.cs</c> because this file is not part of the Wasm
/// compilation.
/// </summary>
public static class LoginThrottle
{
    // Upper bound on tracked keys. Beyond this, expired windows are swept; if
    // the map is still full (a flood of distinct fresh keys), recording stops
    // so memory stays bounded. The throttle fails open rather than crashing.
    private const int MaxTrackedKeys = 10_000;

    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowStart)> Attempts = new();

    /// <summary>
    /// The client identity that throttling is keyed on. Defaults to the
    /// socket peer address (<see cref="ReptarRequest.RemoteIp"/>) because
    /// CF-Connecting-IP and X-Forwarded-For are client-controlled whenever no
    /// trusted proxy overwrites them.
    /// </summary>
    public static string ClientKey(Ctx ctx, AuthOptions options)
    {
        if (options.TrustProxyHeaders)
        {
            var forwarded = ctx.Req.Header("CF-Connecting-IP")
                ?? ctx.Req.Header("X-Forwarded-For")?.Split(',')[0].Trim();

            if (!string.IsNullOrEmpty(forwarded))
                return forwarded;
        }

        return ctx.Req.RemoteIp ?? "loopback";
    }

    public static bool IsLimited(string key, int maxAttempts, TimeSpan window)
    {
        if (maxAttempts <= 0)
            return false;

        if (Attempts.TryGetValue(key, out var attempt) &&
            DateTimeOffset.UtcNow - attempt.WindowStart < window)
        {
            return attempt.Failures >= maxAttempts;
        }

        return false;
    }

    public static void RecordFailure(string key, int maxAttempts, TimeSpan window)
    {
        if (maxAttempts <= 0)
            return;

        var now = DateTimeOffset.UtcNow;

        if (Attempts.Count > MaxTrackedKeys)
            SweepExpired(now, window);

        if (Attempts.Count > MaxTrackedKeys)
            return;

        Attempts.AddOrUpdate(
            key,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart > window
                ? (1, now)
                : (existing.Failures + 1, existing.WindowStart));
    }

    public static void Reset(string key) => Attempts.TryRemove(key, out _);

    private static void SweepExpired(DateTimeOffset now, TimeSpan window)
    {
        foreach (var pair in Attempts)
        {
            if (now - pair.Value.WindowStart > window)
                Attempts.TryRemove(pair.Key, out _);
        }
    }
}
