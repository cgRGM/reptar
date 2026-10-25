using System.Text;
using Reptar;
using Xunit;

namespace Reptar.Tests;

[Collection("ResponseCache")]
public class ResponseCacheTests
{
    [Fact]
    public async Task SWR_caches_anonymous_read_and_returns_HIT_on_subsequent_read()
    {
        CacheControl.PurgeAll();
        var handlerExecutions = 0;

        var app = ReptarApp.Create();
        app.Use(CacheControl.SWR(ttlSeconds: 10, swrSeconds: 20));

        app.Get("/api/widgets", ctx =>
        {
            handlerExecutions++;
            return Task.FromResult(ctx.Json(new { count = handlerExecutions }));
        });

        // First request: MISS
        var res1 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/api/widgets"
        });

        Assert.Equal(200, res1.Status);
        Assert.Equal("MISS", res1.Headers["X-Cache"]);
        Assert.Contains("public, max-age=10, stale-while-revalidate=20", res1.Headers["Cache-Control"]);
        Assert.Equal(1, handlerExecutions);

        // Second request: HIT
        var res2 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/api/widgets"
        });

        Assert.Equal(200, res2.Status);
        Assert.Equal("HIT", res2.Headers["X-Cache"]);
        Assert.Equal(1, handlerExecutions);
        Assert.Equal(Encoding.UTF8.GetString(res1.Body!), Encoding.UTF8.GetString(res2.Body!));
    }

    [Fact]
    public async Task SWR_bypasses_cache_when_authorization_or_cookie_is_present()
    {
        CacheControl.PurgeAll();
        var handlerExecutions = 0;

        var app = ReptarApp.Create();
        app.Use(CacheControl.SWR(10, 20));

        app.Get("/api/profile", ctx =>
        {
            handlerExecutions++;
            return Task.FromResult(ctx.Json(new { id = "user_" + handlerExecutions }));
        });

        // Request with Authorization: BYPASS
        var res1 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/api/profile",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token123" }
        });

        Assert.Equal("BYPASS", res1.Headers["X-Cache"]);
        Assert.Equal(1, handlerExecutions);

        // Request with Cookie: BYPASS
        var res2 = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/api/profile",
            Headers = new Dictionary<string, string> { ["Cookie"] = "session=xyz" }
        });

        Assert.Equal("BYPASS", res2.Headers["X-Cache"]);
        Assert.Equal(2, handlerExecutions);
    }

    [Fact]
    public async Task SWR_invalidates_cache_on_write_methods()
    {
        CacheControl.PurgeAll();
        var articlesCreated = 0;

        var app = ReptarApp.Create();
        app.Use(CacheControl.SWR(15, 30));

        app.Get("/api/articles", ctx =>
            Task.FromResult(ctx.Json(new { total = articlesCreated })));

        app.Post("/api/articles", ctx =>
        {
            articlesCreated++;
            return Task.FromResult(ctx.Json(new { status = "created" }, 201));
        });

        // 1. Read: MISS
        var read1 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/articles" });
        Assert.Equal("MISS", read1.Headers["X-Cache"]);
        Assert.Contains("\"total\":0", Encoding.UTF8.GetString(read1.Body!));

        // 2. Read: HIT
        var read2 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/articles" });
        Assert.Equal("HIT", read2.Headers["X-Cache"]);

        // 3. Write: Triggers invalidation
        var writeRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/api/articles",
            Body = Encoding.UTF8.GetBytes("""{"title":"New Article"}""")
        });
        Assert.Equal(201, writeRes.Status);
        Assert.Equal("BYPASS", writeRes.Headers["X-Cache"]);

        // 4. Read again: MISS (new data visible)
        var read3 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/articles" });
        Assert.Equal("MISS", read3.Headers["X-Cache"]);
        Assert.Contains("\"total\":1", Encoding.UTF8.GetString(read3.Body!));
    }

    [Fact]
    public async Task Explicit_invalidation_and_purge_clears_cache()
    {
        CacheControl.PurgeAll();

        var app = ReptarApp.Create();
        app.Use(ResponseCache.SWR(15, 30));

        app.Get("/api/items", ctx => Task.FromResult(ctx.Text("items")));
        app.Get("/api/users", ctx => Task.FromResult(ctx.Text("users")));

        await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/items" });
        await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/users" });
        Assert.Equal(2, CacheControl.Count);

        // Invalidate specific path
        CacheControl.Invalidate("/api/items");
        Assert.Equal(1, CacheControl.Count);

        // Purge all
        CacheControl.PurgeAll();
        Assert.Equal(0, CacheControl.Count);
    }

    [Fact]
    public async Task SWR_serves_stale_data_and_revalidates_in_background()
    {
        CacheControl.PurgeAll();
        var counter = 0;

        var app = ReptarApp.Create();
        app.Use(CacheControl.SWR(ttlSeconds: 1, swrSeconds: 5));

        app.Get("/api/live", ctx =>
        {
            var current = Interlocked.Increment(ref counter);
            return Task.FromResult(ctx.Json(new { version = current }));
        });

        // 1. Initial read: MISS
        var res1 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/live" });
        Assert.Equal("MISS", res1.Headers["X-Cache"]);
        Assert.Contains("\"version\":1", Encoding.UTF8.GetString(res1.Body!));

        // Wait for TTL (1 second) to expire so entry becomes STALE
        await Task.Delay(1100);

        // 2. Read during SWR window: returns STALE immediately with version 1
        var res2 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/live" });
        Assert.Equal("STALE", res2.Headers["X-Cache"]);
        Assert.Contains("\"version\":1", Encoding.UTF8.GetString(res2.Body!));

        // Wait a brief moment for the background revalidation task to complete
        await Task.Delay(150);

        // 3. Subsequent read: fresh HIT with version 2
        var res3 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/live" });
        Assert.Equal("HIT", res3.Headers["X-Cache"]);
        Assert.Contains("\"version\":2", Encoding.UTF8.GetString(res3.Body!));
    }
}
