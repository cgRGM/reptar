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

    private sealed class DictionaryEnv(IReadOnlyDictionary<string, string> values) : IEnvironment
    {
        public string? Get(string name) =>
            values.TryGetValue(name, out var value) ? value : null;
    }
}
