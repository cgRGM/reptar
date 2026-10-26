namespace Reptar.Tests;

public sealed record Document(string Id, string AuthorId, string Title);

public class CoreFeaturesTests
{
    [Fact]
    public async Task TestClient_executes_in_memory_requests()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Get("/hello", ctx => ctx.Json(new { message = "world" }));
        app.Post("/echo", async ctx =>
        {
            var body = ctx.Req.BodyText;
            return ctx.Text("echo: " + body);
        });

        var client = app.CreateTestClient();

        var getRes = await client.GetAsync("/hello");
        Assert.Equal(200, getRes.Status);
        Assert.Contains("world", getRes.BodyText);

        var postRes = await client.PostJsonAsync("/echo", "{\"foo\":\"bar\"}");
        Assert.Equal(200, postRes.Status);
        Assert.Equal("echo: {\"foo\":\"bar\"}", postRes.BodyText);
    }

    [Fact]
    public async Task Policies_and_Authorize_gate_resources()
    {
        var app = ReptarApp.Create(new ReptarConfig());

        // Define policy for editing documents
        app.DefinePolicy<Document>("edit", (user, doc) =>
            user is not null && (user.Role == "admin" || doc.AuthorId == user.Id));

        app.Put("/docs/:id", ctx =>
        {
            var doc = new Document(ctx.Param("id")!, "user_alice", "Alice Doc");
            if (!ctx.Authorize("edit", doc, out var forbidden))
                return Task.FromResult(forbidden);

            return Task.FromResult(ctx.Json(new { ok = true }));
        });

        var client = app.CreateTestClient();

        // Anonymous request fails authorization
        var unauthedRes = await client.PutAsync("/docs/doc_1");
        Assert.Equal(403, unauthedRes.Status);
        Assert.Contains("permission", unauthedRes.BodyText);

        // App-level policy evaluation
        var alice = new SessionUser("user_alice", "alice@example.com", "Alice", "user");
        var bob = new SessionUser("user_bob", "bob@example.com", "Bob", "user");
        var admin = new SessionUser("user_admin", "admin@example.com", "Admin", "admin");

        var doc = new Document("doc_1", "user_alice", "Secret");

        Assert.True(app.Can(alice, "edit", doc));
        Assert.False(app.Can(bob, "edit", doc));
        Assert.True(app.Can(admin, "edit", doc));
    }

    [Fact]
    public async Task SecurityHeaders_adds_standard_security_headers()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.UseSecurityHeaders(opts =>
        {
            opts.FrameOptions = "SAMEORIGIN";
            opts.ContentSecurityPolicy = "default-src 'self'";
        });

        app.Get("/test", ctx => ctx.Text("ok"));

        var client = app.CreateTestClient();
        var res = await client.GetAsync("/test");

        Assert.Equal(200, res.Status);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.Equal("SAMEORIGIN", res.Headers["X-Frame-Options"]);
        Assert.Equal("default-src 'self'", res.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task BodyLimit_rejects_payloads_exceeding_max_bytes()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.UseBodyLimit(10); // 10 bytes max

        app.Post("/upload", ctx => ctx.Text("received"));

        var client = app.CreateTestClient();

        var allowedRes = await client.PostJsonAsync("/upload", "12345");
        Assert.Equal(200, allowedRes.Status);

        var tooLargeRes = await client.PostJsonAsync("/upload", "12345678901234567890");
        Assert.Equal(413, tooLargeRes.Status);
        Assert.Contains("payload_too_large", tooLargeRes.BodyText);
    }

    [Fact]
    public async Task ServerTiming_emits_header()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Get("/profiled", ctx =>
        {
            ctx.ServerTiming("db", 14.2, "user query");
            ctx.ServerTiming("cache", 0.5, "hit");
            return ctx.Text("done");
        });

        var client = app.CreateTestClient();
        var res = await client.GetAsync("/profiled");

        Assert.Equal(200, res.Status);
        Assert.True(res.Headers.TryGetValue("Server-Timing", out var timing));
        Assert.Contains("db;dur=14.2;desc=\"user query\"", timing);
        Assert.Contains("cache;dur=0.5;desc=\"hit\"", timing);
    }

    [Fact]
    public async Task WaitUntil_executes_background_work()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        var executed = false;

        app.Post("/trigger", ctx =>
        {
            ctx.WaitUntil(async () =>
            {
                await Task.Delay(10);
                executed = true;
            });
            return ctx.Json(new { status = "accepted" });
        });

        var client = app.CreateTestClient();
        var res = await client.PostAsync("/trigger");

        Assert.Equal(200, res.Status);
        Assert.Contains("accepted", res.BodyText);

        // Wait a few ms for the background task to complete
        await Task.Delay(50);
        Assert.True(executed);
    }
}
