namespace Reptar.Tests;

public sealed class ETagTests
{
    [Fact]
    public async Task ETag_returns_304_for_matching_if_none_match()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Use(ReptarApi.ETag());
        app.Get("/data", ctx => ctx.JsonRaw("""{"ok":true}"""));

        var client = app.CreateTestClient();
        var first = await client.GetAsync("/data");

        Assert.Equal(200, first.Status);
        Assert.True(first.Headers.TryGetValue("ETag", out var etag));
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var second = await client.GetAsync(
            "/data",
            new Dictionary<string, string> { ["If-None-Match"] = etag });

        Assert.Equal(304, second.Status);
        Assert.Empty(second.Body ?? []);
        Assert.Equal(etag, second.Headers["ETag"]);
    }

    [Fact]
    public async Task ETag_skips_no_store_and_non_get()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Use(ReptarApi.ETag());
        app.Get("/no-store", ctx =>
        {
            var response = ctx.JsonRaw("""{"ok":true}""");
            response.Headers["Cache-Control"] = "no-store";
            return response;
        });
        app.Post("/write", ctx => ctx.JsonRaw("""{"ok":true}"""));

        var client = app.CreateTestClient();

        var noStore = await client.GetAsync("/no-store");
        Assert.False(noStore.Headers.ContainsKey("ETag"));

        var write = await client.PostJsonAsync("/write", "{}");
        Assert.False(write.Headers.ContainsKey("ETag"));
    }
}
