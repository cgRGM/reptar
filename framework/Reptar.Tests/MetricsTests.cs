namespace Reptar.Tests;

public sealed class MetricsTests
{
    [Fact]
    public async Task Metrics_endpoint_renders_counters_and_histograms()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.UseMetrics();
        app.Get("/hello", ctx => ctx.JsonRaw("""{"ok":true}"""));
        app.MapMetrics();

        var client = app.CreateTestClient();
        await client.GetAsync("/hello");
        await client.GetAsync("/hello");

        var metrics = await client.GetAsync("/metrics");

        Assert.Equal(200, metrics.Status);
        Assert.Contains(
            "reptar_http_requests_total{method=\"GET\",route=\"/hello\",status=\"200\"} 2",
            metrics.BodyText);
        Assert.Contains("reptar_http_request_duration_ms_bucket", metrics.BodyText);
        Assert.Contains("le=\"+Inf\"", metrics.BodyText);
        Assert.Contains("reptar_http_request_duration_ms_count", metrics.BodyText);
    }

    [Fact]
    public async Task Swr_cache_outcomes_are_counted()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.UseMetrics();
        app.UseSWR(ttlSeconds: 60, swrSeconds: 60);
        app.Get("/cached", ctx => ctx.JsonRaw("""{"ok":true}"""));
        app.MapMetrics();

        var client = app.CreateTestClient();
        await client.GetAsync("/cached");
        await client.GetAsync("/cached");
        await client.GetAsync(
            "/cached",
            new Dictionary<string, string> { ["Authorization"] = "Bearer test" });

        var metrics = await client.GetAsync("/metrics");

        Assert.Contains(
            "reptar_cache_requests_total{status=\"MISS\"} 1",
            metrics.BodyText);
        Assert.Contains(
            "reptar_cache_requests_total{status=\"HIT\"} 1",
            metrics.BodyText);
        Assert.Contains(
            "reptar_cache_requests_total{status=\"BYPASS\"} 1",
            metrics.BodyText);
    }
}
