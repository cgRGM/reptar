namespace Reptar.Tests;

public sealed class SwrCacheStoreTests
{
    [Fact]
    public async Task File_store_persists_entries_and_invalidation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"reptar-swr-{Guid.NewGuid():n}");

        try
        {
            var store = new FileSwrCacheStore(directory, maxEntries: 10);
            var app = ReptarApp.Create(new ReptarConfig());
            app.UseSWR(
                ttlSeconds: 60,
                swrSeconds: 60,
                options => options.Store = store);
            app.Get("/cached", ctx => ctx.JsonRaw("""{"ok":true}"""));

            var client = app.CreateTestClient();
            var first = await client.GetAsync("/cached");
            var second = await client.GetAsync("/cached");

            Assert.Equal("MISS", first.Headers["X-Cache"]);
            Assert.Equal("HIT", second.Headers["X-Cache"]);
            Assert.True(store.Count > 0);

            var reopened = new FileSwrCacheStore(directory, maxEntries: 10);
            Assert.Contains("GET:/cached", reopened.Keys);
            Assert.True(reopened.TryGet("GET:/cached", out var entry));
            Assert.Equal(200, entry.Status);

            ResponseCache.Invalidate("/cached");
            Assert.False(store.TryGet("GET:/cached", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Memory_store_evicts_oldest_entries()
    {
        var store = new MemorySwrCacheStore(maxEntries: 2);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        for (var i = 0; i < 3; i++)
        {
            store.Set($"GET:/item/{i}", new SwrCacheEntry
            {
                Body = [],
                Status = 200,
                ContentType = "application/json",
                Headers = [],
                CreatedTimestampTicks = now + i,
                TtlTicks = long.MaxValue / 4,
                SwrTicks = long.MaxValue / 4,
            });
        }

        Assert.Equal(2, store.Count);
        Assert.False(store.TryGet("GET:/item/0", out _));
        Assert.True(store.TryGet("GET:/item/2", out _));
    }
}
