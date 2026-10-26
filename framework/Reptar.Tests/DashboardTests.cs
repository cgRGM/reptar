using static Reptar.ReptarApi;

namespace Reptar.Tests;

public sealed class DashboardTests
{
    [Fact]
    public async Task Dashboard_renders_routes_tables_and_metrics()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.UseMetrics();
        app.RegisterSchema(TableDef.Define(
            "dash_items",
            Col.Text("id").PrimaryKey(),
            Col.Text("name").NotNull()));
        app.Get("/api/items", ctx => ctx.JsonRaw("[]"))
            .WithSummary("List items");
        app.MapReptarDashboard();

        var client = app.CreateTestClient();
        await client.GetAsync("/api/items");

        var response = await client.GetAsync("/_reptar");

        Assert.Equal(200, response.Status);
        Assert.Contains("/api/items", response.BodyText);
        Assert.Contains("List items", response.BodyText);
        Assert.Contains("dash_items", response.BodyText);
        Assert.Contains("reptar_http_requests_total", response.BodyText);
    }

    [Fact]
    public async Task Dashboard_rejects_remote_hosts_by_default()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.MapReptarDashboard();

        var client = app.CreateTestClient();
        var response = await client.GetAsync(
            "/_reptar",
            new Dictionary<string, string> { ["Host"] = "example.com" });

        Assert.Equal(404, response.Status);
    }
}
