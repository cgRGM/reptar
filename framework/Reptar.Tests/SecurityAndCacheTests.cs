using Xunit;

namespace Reptar.Tests;

public sealed class SecurityAndCacheTests
{
    [Fact]
    public async Task Cors_DefaultsToSameOriginOnly_AndHonorsAllowlistAndEnv()
    {
        // 1. Default Cors() with no allowlist configured omits CORS headers and rejects OPTIONS preflight
        var defaultApp = ReptarApp.Create(new ReptarConfig());
        defaultApp.Use(ReptarApi.Cors());
        defaultApp.Get("/items", ctx => ctx.Json(new { ok = true }));

        var unconfiguredGet = await defaultApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items",
            Headers = new Dictionary<string, string> { ["Origin"] = "https://evil.example" },
        });
        Assert.Equal(200, unconfiguredGet.Status);
        Assert.False(unconfiguredGet.Headers.ContainsKey("Access-Control-Allow-Origin"));

        var unconfiguredPreflight = await defaultApp.HandleAsync(new ReptarRequest
        {
            Method = "OPTIONS",
            Path = "/items",
            Headers = new Dictionary<string, string> { ["Origin"] = "https://evil.example" },
        });
        Assert.Equal(403, unconfiguredPreflight.Status);

        // 2. Explicit allowlist reflects allowed origin with Vary: Origin and rejects unlisted origins
        var allowlistApp = ReptarApp.Create(new ReptarConfig
        {
            Environment = new DictionaryEnv(new Dictionary<string, string>
            {
                ["CORS_ORIGINS"] = "https://env.example, https://staging.example",
            }),
        });
        allowlistApp.Use(ReptarApi.Cors("https://app.example"));
        allowlistApp.Get("/items", ctx => ctx.Json(new { ok = true }));

        var allowedGet = await allowlistApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items",
            Headers = new Dictionary<string, string> { ["Origin"] = "https://app.example" },
        });
        Assert.Equal(200, allowedGet.Status);
        Assert.Equal("https://app.example", allowedGet.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal("Origin", allowedGet.Headers["Vary"]);
        Assert.Equal("true", allowedGet.Headers["Access-Control-Allow-Credentials"]);

        var envAllowedPreflight = await allowlistApp.HandleAsync(new ReptarRequest
        {
            Method = "OPTIONS",
            Path = "/items",
            Headers = new Dictionary<string, string> { ["Origin"] = "https://staging.example" },
        });
        Assert.Equal(204, envAllowedPreflight.Status);
        Assert.Equal("https://staging.example", envAllowedPreflight.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal("Origin", envAllowedPreflight.Headers["Vary"]);

        var deniedPreflight = await allowlistApp.HandleAsync(new ReptarRequest
        {
            Method = "OPTIONS",
            Path = "/items",
            Headers = new Dictionary<string, string> { ["Origin"] = "https://attacker.example" },
        });
        Assert.Equal(403, deniedPreflight.Status);
        Assert.False(deniedPreflight.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task RateLimit_IgnoresSpoofedForwardedHeadersByDefault_AndUsesRemoteIp()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            RateLimit = new MemoryRateLimiter(defaultLimit: 1, TimeSpan.FromSeconds(60)),
        });
        app.Use(ReptarApi.RateLimit(limit: 1, windowSeconds: 60));
        app.Get("/ping", ctx => ctx.Text("pong"));

        // Request 1 from 203.0.113.10 with spoofed X-Forwarded-For succeeds
        var first = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
            RemoteIp = "203.0.113.10",
            Headers = new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.1" },
        });
        Assert.Equal(200, first.Status);

        // Request 2 from same RemoteIp rotating X-Forwarded-For is still rate-limited (429)!
        var second = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
            RemoteIp = "203.0.113.10",
            Headers = new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.2" },
        });
        Assert.Equal(429, second.Status);

        // Request from a different RemoteIp has its own independent bucket
        var otherCaller = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
            RemoteIp = "203.0.113.11",
        });
        Assert.Equal(200, otherCaller.Status);
    }

    [Fact]
    public async Task RateLimit_WhenTrustProxyHeadersEnabled_TakesFirstForwardedIpAndKvLimiterWorks()
    {
        var kv = new MemoryKvClient();
        var app = ReptarApp.Create(new ReptarConfig
        {
            RateLimit = new KvRateLimiter(kv, defaultLimit: 1, TimeSpan.FromSeconds(60)),
        });
        app.Use(ReptarApi.RateLimit(new RateLimitOptions
        {
            Limit = 1,
            WindowSeconds = 60,
            TrustProxyHeaders = true,
        }));
        app.Get("/ping", ctx => ctx.Text("pong"));

        var r1 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
            RemoteIp = "10.0.0.1",
            Headers = new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.50, 10.0.0.1" },
        });
        Assert.Equal(200, r1.Status);

        // Same first IP in X-Forwarded-For hits the KV rate limit
        var r2 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
            RemoteIp = "10.0.0.2",
            Headers = new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.50, 10.0.0.2" },
        });
        Assert.Equal(429, r2.Status);
    }

    [Fact]
    public async Task SwrCache_IsolatesAppInstances_EnforcesMaxEntriesMaxBodyBytes_AndHonorsVaryHeaders()
    {
        CacheControl.PurgeAll();

        // 1. Instance isolation: two apps with same path do not share cached responses
        var appA = ReptarApp.Create();
        appA.Use(CacheControl.SWR(ttlSeconds: 30, swrSeconds: 60));
        appA.Get("/api/info", ctx => ctx.Text("app-a"));

        var appB = ReptarApp.Create();
        appB.Use(CacheControl.SWR(ttlSeconds: 30, swrSeconds: 60));
        appB.Get("/api/info", ctx => ctx.Text("app-b"));

        var resA = await appA.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/info" });
        var resB = await appB.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/info" });

        Assert.Equal("MISS", resA.Headers["X-Cache"]);
        Assert.Equal("MISS", resB.Headers["X-Cache"]);
        Assert.Equal("app-a", System.Text.Encoding.UTF8.GetString(resA.Body!));
        Assert.Equal("app-b", System.Text.Encoding.UTF8.GetString(resB.Body!));

        // 2. MaxEntries eviction, MaxBodyBytes bypass, and VaryHeaders partitioning
        var boundedApp = ReptarApp.Create();
        boundedApp.Use(CacheControl.SWR(ttlSeconds: 30, swrSeconds: 60, opts =>
        {
            opts.MaxEntries = 2;
            opts.MaxBodyBytes = 64;
            opts.VaryHeaders = ["Accept-Language"];
        }));

        boundedApp.Get("/items/:id", ctx =>
        {
            var id = ctx.Req.Param("id")!;
            if (id == "huge")
                return ctx.Text(new string('x', 128));
            return ctx.Text($"item:{id}:{ctx.Req.Header("Accept-Language") ?? "en"}");
        });

        // Response exceeding MaxBodyBytes is bypassed and not stored
        var hugeRes = await boundedApp.HandleAsync(new ReptarRequest { Method = "GET", Path = "/items/huge" });
        Assert.Equal("BYPASS", hugeRes.Headers["X-Cache"]);

        // VaryHeaders caches en and fr separately; MaxEntries = 2 evicts oldest when 3rd is added
        var enRes = await boundedApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items/1",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "en" },
        });
        Assert.Equal("MISS", enRes.Headers["X-Cache"]);

        var frRes = await boundedApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items/1",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "fr" },
        });
        Assert.Equal("MISS", frRes.Headers["X-Cache"]);

        // Adding a 3rd entry evicts the oldest entry (en) because MaxEntries = 2
        var thirdRes = await boundedApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items/2",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "en" },
        });
        Assert.Equal("MISS", thirdRes.Headers["X-Cache"]);

        // fr is still a HIT, while en was evicted (MISS)
        var frHit = await boundedApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items/1",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "fr" },
        });
        Assert.Equal("HIT", frHit.Headers["X-Cache"]);

        var enEvicted = await boundedApp.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/items/1",
            Headers = new Dictionary<string, string> { ["Accept-Language"] = "en" },
        });
        Assert.Equal("MISS", enEvicted.Headers["X-Cache"]);
    }

    private sealed class MemoryKvClient : IKvClient
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_store.TryGetValue(key, out var val) ? val : null);

        public Task PutAsync(string key, string value, int? ttlSeconds = null)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class DictionaryEnv(IReadOnlyDictionary<string, string> values) : IEnvironment
    {
        public string? Get(string name) =>
            values.TryGetValue(name, out var value) ? value : null;
    }
}
