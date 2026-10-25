using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using static Reptar.ReptarApi;

namespace Reptar.Tests;

public sealed class ProductionAndSqliteTests
{
    [Fact]
    public async Task MapHealth_LiveAndReadyProbes_ReturnExpectedPayloads()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reptar_health_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SqliteDbClient(dbPath);
            var app = ReptarApp.Create(new ReptarConfig { Database = db });
            app.MapHealth();

            var liveRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/health/live",
            });

            Assert.Equal(200, liveRes.Status);
            Assert.Equal("{\"status\":\"healthy\"}", Encoding.UTF8.GetString(liveRes.Body!));

            var readyRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/health/ready",
            });

            Assert.Equal(200, readyRes.Status);
            Assert.Equal("{\"status\":\"ready\",\"database\":\"connected\"}", Encoding.UTF8.GetString(readyRes.Body!));
        }
        finally
        {
            TryDeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UseSecurityHeaders_InjectsDefaultAndCustomOwaspHeaders()
    {
        var app = ReptarApp.Create();
        app.UseSecurityHeaders(opts =>
        {
            opts.ContentSecurityPolicy = "default-src 'self'";
        });
        app.Get("/ping", ctx => Task.FromResult(ctx.Text("pong")));

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/ping",
        });

        Assert.Equal(200, res.Status);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", res.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", res.Headers["Referrer-Policy"]);
        Assert.Equal("max-age=31536000; includeSubDomains", res.Headers["Strict-Transport-Security"]);
        Assert.Equal("0", res.Headers["X-XSS-Protection"]);
        Assert.Equal("default-src 'self'", res.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task SignedCookies_RoundTripsValidSignature_AndRejectsTampering()
    {
        const string secret = "super-secret-hmac-key-2026";
        var app = ReptarApp.Create();

        app.Get("/login", ctx =>
        {
            ctx.Cookies.SetSigned("session_uid", "usr_42", secret, TimeSpan.FromHours(1));
            return Task.FromResult(ctx.Text("signed"));
        });

        app.Get("/profile", ctx =>
        {
            var uid = ctx.Cookies.GetSigned("session_uid", secret);
            return Task.FromResult(uid is not null ? ctx.Text(uid) : ctx.Unauthorized());
        });

        var loginRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/login",
        });

        Assert.Single(loginRes.SetCookies);
        var cookieHeader = loginRes.SetCookies[0];
        var cookiePair = cookieHeader.Split(';')[0];
        var signedValue = cookiePair.Split('=', 2)[1];

        // 1. Valid signed cookie round-trips
        var validRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/profile",
            Cookies = new Dictionary<string, string> { ["session_uid"] = signedValue },
        });
        Assert.Equal(200, validRes.Status);
        Assert.Equal("usr_42", Encoding.UTF8.GetString(validRes.Body!));

        // 2. Tampered value is rejected
        var tamperedValue = "usr_admin." + signedValue.Split('.')[1];
        var tamperedRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/profile",
            Cookies = new Dictionary<string, string> { ["session_uid"] = tamperedValue },
        });
        Assert.Equal(401, tamperedRes.Status);

        // 3. Tampered signature is rejected
        var tamperedSig = signedValue[..^2] + "aa";
        var tamperedSigRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/profile",
            Cookies = new Dictionary<string, string> { ["session_uid"] = tamperedSig },
        });
        Assert.Equal(401, tamperedSigRes.Status);
    }

    [Fact]
    public async Task UseStaticFiles_ServesAssets_HandlesEtag304_BlocksTraversal_AndSupportsSpaFallback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"reptar_static_{Guid.NewGuid():N}");
        var assetsDir = Path.Combine(tempRoot, "assets");
        Directory.CreateDirectory(assetsDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "index.html"), "<!doctype html><html><body>SPA Root</body></html>");
            await File.WriteAllTextAsync(Path.Combine(assetsDir, "app.js"), "console.log('reptar');");

            var app = ReptarApp.Create();
            app.UseStaticFiles(tempRoot);
            app.Get("/api/hello", ctx => Task.FromResult(ctx.Text("api-ok")));

            // 1. Explicit API route takes precedence
            var apiRes = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/hello" });
            Assert.Equal(200, apiRes.Status);
            Assert.Equal("api-ok", Encoding.UTF8.GetString(apiRes.Body!));

            // 2. Static JS asset with immutable cache header and ETag
            var jsRes = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/assets/app.js" });
            Assert.Equal(200, jsRes.Status);
            Assert.Equal("text/javascript; charset=utf-8", jsRes.ContentType);
            Assert.Equal("console.log('reptar');", Encoding.UTF8.GetString(jsRes.Body!));
            Assert.True(jsRes.Headers.TryGetValue("ETag", out var etag));
            Assert.Contains("immutable", jsRes.Headers["Cache-Control"]);

            // 3. Conditional If-None-Match returns 304 Not Modified
            var cachedRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/assets/app.js",
                Headers = new Dictionary<string, string> { ["If-None-Match"] = etag! },
            });
            Assert.Equal(304, cachedRes.Status);

            // 4. SPA fallback serves index.html on extensionless client routes
            var spaRes = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/dashboard/settings" });
            Assert.Equal(200, spaRes.Status);
            Assert.Equal("text/html; charset=utf-8", spaRes.ContentType);
            Assert.Contains("SPA Root", Encoding.UTF8.GetString(spaRes.Body!));

            // 5. Unmatched /api/* route does NOT trigger SPA fallback
            var missingApiRes = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/api/missing" });
            Assert.Equal(404, missingApiRes.Status);

            // 6. Path traversal attempt is blocked with 404
            var traversalRes = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/../secret.txt" });
            Assert.Equal(404, traversalRes.Status);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteGroupCommit_CoalescesConcurrentWrites_AndIsolatesConstraintFailures()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reptar_batch_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SqliteDbClient(dbPath);
            await db.ExecuteAsync("CREATE TABLE items (id TEXT PRIMARY KEY, val INTEGER NOT NULL)");

            // 1. 100 concurrent inserts coalesce cleanly via group-commit batcher
            var tasks = Enumerable.Range(0, 100)
                .Select(i => db.ExecuteAsync(
                    "INSERT INTO items (id, val) VALUES (@id, @val)",
                    ("id", $"item_{i}"),
                    ("val", i)))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.Equal(1, r));

            var rows = await db.QueryAsync<HealthProbeRow>("SELECT count(*) AS ok FROM items");
            Assert.Single(rows);
            Assert.Equal(100, rows[0].Ok);

            // 2. Error isolation: if one concurrent write in a batch violates a PRIMARY KEY constraint,
            // sibling writes in the same concurrent wave still succeed.
            var mixedTasks = new List<Task<int>>();
            for (var i = 100; i < 120; i++)
            {
                // i == 110 duplicates "item_0" -> PRIMARY KEY violation
                var id = i == 110 ? "item_0" : $"item_{i}";
                mixedTasks.Add(db.ExecuteAsync(
                    "INSERT INTO items (id, val) VALUES (@id, @val)",
                    ("id", id),
                    ("val", i)));
            }

            var outcomes = await Task.WhenAll(mixedTasks.Select(async t =>
            {
                try
                {
                    await t;
                    return true;
                }
                catch (SqliteException)
                {
                    return false;
                }
            }));

            Assert.Equal(19, outcomes.Count(succeeded => succeeded));
            Assert.Equal(1, outcomes.Count(succeeded => !succeeded));

            var finalCount = await db.QuerySingleOrDefaultAsync<HealthProbeRow>("SELECT count(*) AS ok FROM items");
            Assert.NotNull(finalCount);
            Assert.Equal(119, finalCount!.Ok);
        }
        finally
        {
            TryDeleteSqliteFiles(dbPath);
        }
    }

    private static void TryDeleteSqliteFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Ignore cleanup races
            }
        }
    }
}
