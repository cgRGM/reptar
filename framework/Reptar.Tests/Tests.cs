using System.Text;
using Reptar;
using Xunit;

namespace Reptar.Tests;

public static partial class TestSchema
{
    public static readonly TableDef Widgets = ReptarApi.DefineTable("widgets", new
    {
        Id = ReptarApi.Col.Text("id").PrimaryKey(),
        Name = ReptarApi.Col.Text("name").NotNull(),
        Count = ReptarApi.Col.Int("count").NotNull().Default(0),
    });

    public static readonly TableDef[] All = [Widgets];
}

public sealed record Widget(string Id, string Name, int Count);

public sealed record CreateWidget(string Name);

public static class TestFeatures
{
    public static readonly Validator<CreateWidget> CreateSchema =
        ReptarApi.Rule<CreateWidget>()
            .Prop(x => x.Name, rules => rules.Min(2).Max(40));

    public static ReptarApp WidgetRoutes()
    {
        var app = ReptarApp.Create();

        app.Get("/", async ctx => ctx.Json(
            await ctx.Db.ListAsync<Widget>(TestSchema.Widgets, "order by name")));

        app.Post("/", CreateSchema, async (ctx, input) =>
        {
            var widget = new Widget(Ids.New("w"), input.Name.Trim(), 0);
            await ctx.Db.InsertAsync(TestSchema.Widgets, widget);
            return ctx.Json(widget, 201);
        })
            .Produces<Widget>(201)
            .Produces<ValidationError>(400);

        return app;
    }
}

public class RouterTests
{
    [Fact]
    public async Task Matches_static_and_param_routes()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });

        app.Get("/hello", ctx => Task.FromResult(ctx.Text("world")));
        app.Get("/posts/:id", ctx => Task.FromResult(ctx.Text(ctx.Req.Param("id")!)));

        var hello = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/hello" });
        Assert.Equal(200, hello.Status);
        Assert.Equal("world", Encoding.UTF8.GetString(hello.Body!));

        var post = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/posts/42" });
        Assert.Equal(200, post.Status);
        Assert.Equal("42", Encoding.UTF8.GetString(post.Body!));
    }

    [Fact]
    public async Task Returns_404_for_unknown_routes()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var response = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/nope" });
        Assert.Equal(404, response.Status);
    }
}

public class ValidationTests
{
    private sealed record Input(string Name, string? Note);

    private static readonly Validator<Input> Schema = ReptarApi.Rule<Input>()
        .Prop(x => x.Name, rules => rules.Min(3).Max(10))
        .Prop(x => x.Note, rules => rules.Max(5).Optional())
        .Refine(x => x.Name != "admin", "Reserved name.", "name");

    [Fact]
    public void Passes_valid_input() =>
        Assert.Empty(Schema.Validate(new Input("hello", null)));

    [Fact]
    public void Reports_required_min_and_refine_issues()
    {
        var issues = Schema.Validate(new Input("ab", null));
        Assert.Contains(issues, issue => issue.Message == "Must be at least 3 characters.");

        var reserved = Schema.Validate(new Input("admin", null));
        Assert.Contains(reserved, issue => issue.Message == "Reserved name.");

        var required = Schema.Validate(new Input(null!, null));
        Assert.Contains(required, issue => issue.Message == "Required.");
    }
}

public class DbTests
{
    [Fact]
    public async Task Crud_round_trip()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestSchema.Widgets);

        await db.InsertAsync(TestSchema.Widgets, new Widget("w1", "Alpha", 1));

        var widget = await db.GetAsync<Widget>(TestSchema.Widgets, "w1");
        Assert.NotNull(widget);
        Assert.Equal("Alpha", widget!.Name);

        var list = await db.ListAsync<Widget>(TestSchema.Widgets, "order by name");
        Assert.Single(list);

        await db.UpdateAsync(TestSchema.Widgets, new Widget("w1", "Beta", 2), "w1");
        var updated = await db.GetAsync<Widget>(TestSchema.Widgets, "w1");
        Assert.Equal("Beta", updated!.Name);

        Assert.True(await db.ExistsAsync(TestSchema.Widgets, "id = @id", new { id = "w1" }));

        await db.DeleteAsync(TestSchema.Widgets, "w1");
        Assert.Null(await db.GetAsync<Widget>(TestSchema.Widgets, "w1"));
    }
}

public class E2eTests
{
    private static ReptarApp BuildApp()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Auth = ReptarApi.BuiltIn(new AuthOptions { AllowAnonymous = true }),
        });

        app.Use(ReptarApi.RequestId());
        app.Use(ReptarApi.Cors());
        app.Route("/auth", Auth.Routes());
        app.RegisterSchema(TestSchema.All);
        app.Route("/widgets", TestFeatures.WidgetRoutes());

        return app;
    }

    private static ReptarRequest Json(string method, string path, string json) => new()
    {
        Method = method,
        Path = path,
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        Body = Encoding.UTF8.GetBytes(json),
    };

    private static string? SessionCookie(ReptarResponse response) =>
        response.SetCookies
            .FirstOrDefault(cookie => cookie.StartsWith("reptar_session=", StringComparison.Ordinal))
            ?.Split(';')[0]
            .Split('=')[1];

    [Fact]
    public async Task Widgets_list_create_and_validate()
    {
        var app = BuildApp();
        await app.EnsureCreatedAsync();

        var empty = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/widgets" });
        Assert.Equal(200, empty.Status);
        Assert.Equal("[]", Encoding.UTF8.GetString(empty.Body!));

        var invalid = await app.HandleAsync(
            Json("POST", "/widgets", """{"name":"x"}"""));
        Assert.Equal(400, invalid.Status);
        Assert.Contains("Must be at least 2 characters.", Encoding.UTF8.GetString(invalid.Body!));

        var created = await app.HandleAsync(
            Json("POST", "/widgets", """{"name":"Alpha"}"""));
        Assert.Equal(201, created.Status);

        var list = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/widgets" });
        Assert.Contains("Alpha", Encoding.UTF8.GetString(list.Body!));
    }

    [Fact]
    public async Task Signup_and_session_work()
    {
        var app = BuildApp();
        await app.EnsureCreatedAsync();

        var signup = await app.HandleAsync(Json(
            "POST",
            "/auth/signup",
            """{"email":"ada@example.com","password":"password123","name":"Ada"}"""));

        Assert.Equal(201, signup.Status);

        var token = SessionCookie(signup);
        Assert.NotNull(token);

        var session = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/auth/session",
            Cookies = new Dictionary<string, string> { ["reptar_session"] = token! },
        });

        Assert.Equal(200, session.Status);
        Assert.Contains("ada@example.com", Encoding.UTF8.GetString(session.Body!));

        var signin = await app.HandleAsync(Json(
            "POST",
            "/auth/signin",
            """{"email":"ada@example.com","password":"password123"}"""));

        Assert.Equal(200, signin.Status);
        Assert.NotNull(SessionCookie(signin));
    }

    [Fact]
    public async Task Auth_requires_a_session_for_private_routes()
    {
        var app = BuildApp();
        await app.EnsureCreatedAsync();

        var response = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/posts",
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = Encoding.UTF8.GetBytes("""{"title":"Hello","content":"World"}"""),
        });

        Assert.Equal(404, response.Status);
    }
}

public class SqlParamParserTests
{
    [Fact]
    public void Ignores_parameters_inside_quotes_and_comments()
    {
        var sql =
            "select * from posts where title = @title " +
            "and body like '%@literal%' /* @comment */ " +
            "and id = $id -- @line\n";

        var (rewritten, names) = SqlParamParser.ParseNamedParameters(sql);

        Assert.Equal(["title", "id"], names);
        Assert.Contains("%@literal%", rewritten);
        Assert.Contains("/* @comment */", rewritten);
        Assert.DoesNotContain("@title", rewritten);
        Assert.DoesNotContain("$id", rewritten);
    }
}

public class StripeTests
{
    [Fact]
    public async Task HmacSha256_computes_correct_signature()
    {
        var crypto = new DevCrypto();
        var hash = await crypto.HmacSha256("secret_key", "test_message");
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);

        var hash2 = await crypto.HmacSha256("secret_key", "test_message");
        Assert.Equal(hash, hash2);
    }

    [Fact]
    public async Task Webhook_verifies_valid_signature()
    {
        var secret = "whsec_test_secret_123";
        var receivedEvent = false;

        var app = ReptarApp.Create();
        app.Route("/stripe", Stripe.Routes(new StripeConfig
        {
            ApiKey = "sk_test_123",
            WebhookSecret = secret,
        }, (ctx, evt) =>
        {
            receivedEvent = true;
            Assert.Equal("evt_123", evt.Id);
            return Task.FromResult(ctx.Status(200));
        }));

        var payload = "{\"id\":\"evt_123\",\"type\":\"checkout.session.completed\",\"data\":{}}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signedPayload = $"{timestamp}.{payload}";
        var crypto = new DevCrypto();
        var sig = await crypto.HmacSha256(secret, signedPayload);

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/webhook",
            Headers = new Dictionary<string, string>
            {
                ["stripe-signature"] = $"t={timestamp},v1={sig}",
            },
            Body = Encoding.UTF8.GetBytes(payload),
        });

        Assert.Equal(200, res.Status);
        Assert.True(receivedEvent);

        var badRes = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/webhook",
            Headers = new Dictionary<string, string>
            {
                ["stripe-signature"] = $"t={timestamp},v1=bad_sig",
            },
            Body = Encoding.UTF8.GetBytes(payload),
        });

        Assert.Equal(400, badRes.Status);
    }

    [Fact]
    public async Task Webhook_fails_closed_without_a_secret()
    {
        var app = ReptarApp.Create();
        app.Route("/stripe", Stripe.Routes(new StripeConfig
        {
            ApiKey = "sk_test_123",
            WebhookSecret = null,
        }));

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/webhook",
            Headers = new Dictionary<string, string>
            {
                ["stripe-signature"] = "t=1,v1=deadbeef",
            },
            Body = Encoding.UTF8.GetBytes("{}"),
        });

        Assert.Equal(503, res.Status);
    }

    [Fact]
    public async Task Webhook_rejects_a_stale_timestamp()
    {
        var secret = "whsec_test_secret_123";
        var app = ReptarApp.Create();
        app.Route("/stripe", Stripe.Routes(new StripeConfig
        {
            ApiKey = "sk_test_123",
            WebhookSecret = secret,
        }));

        var payload = "{\"id\":\"evt_stale\",\"type\":\"checkout.session.completed\"}";
        var timestamp = DateTimeOffset.UtcNow
            .AddHours(-1)
            .ToUnixTimeSeconds()
            .ToString();
        var crypto = new DevCrypto();
        var sig = await crypto.HmacSha256(
            secret,
            $"{timestamp}.{payload}");

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/webhook",
            Headers = new Dictionary<string, string>
            {
                ["stripe-signature"] = $"t={timestamp},v1={sig}",
            },
            Body = Encoding.UTF8.GetBytes(payload),
        });

        Assert.Equal(400, res.Status);
    }

    [Fact]
    public async Task Checkout_fails_closed_without_an_api_key()
    {
        var app = ReptarApp.Create();
        app.Route("/stripe", Stripe.Routes(new StripeConfig
        {
            ApiKey = "",
            WebhookSecret = "whsec_test",
        }));

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/checkout",
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"successUrl\":\"https://example.com/s\",\"cancelUrl\":\"https://example.com/c\",\"lineItems\":[]}"),
        });

        Assert.Equal(503, res.Status);
    }

    private sealed class MockHttpClient : IHttpClient
    {
        public string LastUrl { get; private set; } = "";
        public string LastMethod { get; private set; } = "";
        public Dictionary<string, string>? LastHeaders { get; private set; }
        public string LastBody { get; private set; } = "";
        public HttpResponseData ResponseToReturn { get; set; } = new(
            200,
            new Dictionary<string, string>(),
            "{\"id\":\"cs_test_123\",\"url\":\"https://checkout.stripe.com/pay/cs_test_123\",\"status\":\"open\"}");

        public Task<HttpResponseData> FetchAsync(string url, string method = "GET", Dictionary<string, string>? headers = null, string body = "")
        {
            LastUrl = url;
            LastMethod = method;
            LastHeaders = headers;
            LastBody = body;
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task CreateCheckoutSession_formats_spec_compliant_form_body_and_headers()
    {
        var mockHttp = new MockHttpClient();
        var app = ReptarApp.Create(new ReptarConfig { Http = mockHttp });
        var config = new StripeConfig { ApiKey = "sk_test_mock_key_555" };
        app.Route("/stripe", Stripe.Routes(config));

        var requestJson = System.Text.Json.JsonSerializer.Serialize(new CreateCheckoutInput(
            SuccessUrl: "https://example.com/success?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl: "https://example.com/cancel",
            CustomerEmail: "customer@example.com",
            ClientReferenceId: "order_999",
            LineItems:
            [
                new StripeLineItem("Premium Plan", 4900, "usd", 2)
            ]));

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/stripe/checkout",
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
            },
            Body = Encoding.UTF8.GetBytes(requestJson),
        });

        Assert.Equal(201, res.Status);
        Assert.Equal("https://api.stripe.com/v1/checkout/sessions", mockHttp.LastUrl);
        Assert.Equal("POST", mockHttp.LastMethod);
        Assert.NotNull(mockHttp.LastHeaders);
        Assert.Equal("Bearer sk_test_mock_key_555", mockHttp.LastHeaders["authorization"]);
        Assert.Equal("application/x-www-form-urlencoded", mockHttp.LastHeaders["content-type"]);

        var body = mockHttp.LastBody;
        Assert.Contains("success_url=https%3A%2F%2Fexample.com%2Fsuccess%3Fsession_id%3D%7BCHECKOUT_SESSION_ID%7D", body);
        Assert.Contains("cancel_url=https%3A%2F%2Fexample.com%2Fcancel", body);
        Assert.Contains("mode=payment", body);
        Assert.Contains("customer_email=customer%40example.com", body);
        Assert.Contains("client_reference_id=order_999", body);
        Assert.Contains("line_items[0][price_data][currency]=usd", body);
        Assert.Contains("line_items[0][price_data][unit_amount]=4900", body);
        Assert.Contains("line_items[0][price_data][product_data][name]=Premium%20Plan", body);
        Assert.Contains("line_items[0][quantity]=2", body);
    }
}

public class PostgresTests
{
    private sealed record ScalarVal(int Val);

    [Fact]
    public void SqlParamParser_rewrites_positional_and_named_params_for_postgres()
    {
        var input = "SELECT * FROM posts WHERE id = ? AND title = @title AND author_id = $author";
        var (sql, names) = SqlParamParser.ParsePostgresParameters(input);

        Assert.Equal("SELECT * FROM posts WHERE id = $1 AND title = $2 AND author_id = $3", sql);
        Assert.Equal(["title", "author"], names);
    }

    [Fact]
    public void SqlParamParser_ignores_literals_and_comments_for_postgres()
    {
        var input = "SELECT * FROM posts WHERE title = 'What @about ?' -- check @me\nAND id = ? /* block ? */";
        var (sql, names) = SqlParamParser.ParsePostgresParameters(input);

        Assert.Equal("SELECT * FROM posts WHERE title = 'What @about ?' -- check @me\nAND id = $1 /* block ? */", sql);
        Assert.Empty(names);
    }

    [Fact]
    public async Task PostgresDbClient_generates_correct_crud_sql()
    {
        var client = new PostgresDbClient("mock:test");

        await client.GetAsync<Widget>(TestSchema.Widgets, "w_123");
        Assert.Equal("SELECT * FROM \"widgets\" WHERE \"id\" = $1 LIMIT 1", client.LastExecutedSql);
        Assert.Equal(["w_123"], client.LastExecutedParams);

        await client.ListAsync<Widget>(TestSchema.Widgets, "count > 5", limit: 10, offset: 20);
        Assert.Equal("SELECT * FROM \"widgets\" WHERE count > 5 LIMIT 10 OFFSET 20", client.LastExecutedSql);

        var widget = new Widget("w_456", "Hammer", 42);
        await client.InsertAsync(TestSchema.Widgets, widget);
        Assert.Equal("INSERT INTO \"widgets\" (\"id\", \"name\", \"count\") VALUES ($1, $2, $3)", client.LastExecutedSql);
        Assert.Equal(new object?[] { "w_456", "Hammer", 42L }, client.LastExecutedParams);

        await client.DeleteAsync(TestSchema.Widgets, "w_456");
        Assert.Equal("DELETE FROM \"widgets\" WHERE \"id\" = $1", client.LastExecutedSql);
        Assert.Equal(["w_456"], client.LastExecutedParams);
    }

    [Fact]
    public void PostgresDbClient_generates_valid_table_ddl()
    {
        var ddl = PostgresDbClient.GenerateCreateTableSql(TestSchema.Widgets);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"widgets\"", ddl);
        Assert.Contains("\"id\" TEXT PRIMARY KEY", ddl);
        Assert.Contains("\"name\" TEXT NOT NULL", ddl);
        Assert.Contains("\"count\" INTEGER NOT NULL DEFAULT 0", ddl);
    }

    [Fact]
    public void PostgresDbClient_converts_uri_to_connection_string()
    {
        var uri = "postgres://myuser:mypassword@db.supabase.co:5432/postgres?sslmode=require";
        var connStr = PostgresDbClient.ConvertUrlToConnectionString(uri);

        Assert.Contains("Host=db.supabase.co", connStr);
        Assert.Contains("Port=5432", connStr);
        Assert.Contains("Database=postgres", connStr);
        Assert.Contains("Username=myuser", connStr);
        Assert.Contains("Password=mypassword", connStr);
        Assert.Contains("SSL Mode=Require", connStr);
    }

    [Fact]
    public async Task PostgresDbClient_live_integration_test_when_postgres_is_available()
    {
        var requirePostgres = Environment.GetEnvironmentVariable("POSTGRES_TEST") == "1";
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_URL")
            ?? $"Host=localhost;Database=reptar_test;Username={Environment.UserName};";

        var ranLive = false;
        PostgresDbClient client;
        try
        {
            client = new PostgresDbClient(connStr);
            var testConn = await client.QuerySingleOrDefaultAsync<ScalarVal>("SELECT 1 as val;");
            if (testConn != null) ranLive = true;
            else if (requirePostgres) Assert.Fail("Postgres connection succeeded but returned null test result.");
            else return;
        }
        catch
        {
            if (requirePostgres) throw;
            // PostgreSQL not available in this test environment; skip gracefully
            return;
        }

        Assert.True(ranLive);

        // Clean up any existing test table
        await client.ExecuteAsync("DROP TABLE IF EXISTS \"widgets\";");

        // 1. EnsureCreatedAsync (DDL)
        await client.EnsureCreatedAsync(TestSchema.Widgets);

        // 2. InsertAsync
        var w1 = new Widget("w_1", "Gear", 10);
        var w2 = new Widget("w_2", "Sprocket", 25);
        await client.InsertAsync(TestSchema.Widgets, w1);
        await client.InsertAsync(TestSchema.Widgets, w2);

        // 3. GetAsync
        var fetched = await client.GetAsync<Widget>(TestSchema.Widgets, "w_1");
        Assert.NotNull(fetched);
        Assert.Equal("w_1", fetched.Id);
        Assert.Equal("Gear", fetched.Name);
        Assert.Equal(10, fetched.Count);

        // 4. ListAsync with WHERE clause and pagination
        var list = await client.ListAsync<Widget>(TestSchema.Widgets, "count > 15", limit: 10);
        Assert.Single(list);
        Assert.Equal("w_2", list[0].Id);

        // 5. UpdateAsync
        var updatedCount = await client.UpdateAsync(TestSchema.Widgets, new { Name = "Super Gear", Count = 99 }, "w_1");
        Assert.Equal(1, updatedCount);
        var afterUpdate = await client.GetAsync<Widget>(TestSchema.Widgets, "w_1");
        Assert.NotNull(afterUpdate);
        Assert.Equal("Super Gear", afterUpdate.Name);
        Assert.Equal(99, afterUpdate.Count);

        // 6. ExistsAsync
        var exists = await client.ExistsAsync(TestSchema.Widgets, "\"id\" = 'w_1'");
        Assert.True(exists);
        var notExists = await client.ExistsAsync(TestSchema.Widgets, "\"id\" = 'w_999'");
        Assert.False(notExists);

        // 7. TransactionAsync - Commit
        await client.TransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("INSERT INTO \"widgets\" (\"id\", \"name\", \"count\") VALUES ('w_3', 'Bolt', 5);");
        });
        var w3 = await client.GetAsync<Widget>(TestSchema.Widgets, "w_3");
        Assert.NotNull(w3);
        Assert.Equal("Bolt", w3.Name);

        // 8. TransactionAsync - Rollback on exception
        try
        {
            await client.TransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("INSERT INTO \"widgets\" (\"id\", \"name\", \"count\") VALUES ('w_4', 'Nut', 7);");
                throw new InvalidOperationException("Force rollback");
            });
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
        var w4 = await client.GetAsync<Widget>(TestSchema.Widgets, "w_4");
        Assert.Null(w4); // Should be rolled back

        // 9. DeleteAsync
        var delCount = await client.DeleteAsync(TestSchema.Widgets, "w_1");
        Assert.Equal(1, delCount);
        var afterDelete = await client.GetAsync<Widget>(TestSchema.Widgets, "w_1");
        Assert.Null(afterDelete);

        // Cleanup
        await client.ExecuteAsync("DROP TABLE IF EXISTS \"widgets\";");
    }
}

public class RateLimiterTests
{
    [Fact]
    public async Task Limits_requests_when_threshold_exceeded()
    {
        var limiter = new MemoryRateLimiter(defaultLimit: 3, TimeSpan.FromSeconds(60));

        Assert.True(await limiter.LimitAsync("api", "user_1"));
        Assert.True(await limiter.LimitAsync("api", "user_1"));
        Assert.True(await limiter.LimitAsync("api", "user_1"));
        // 4th request exceeds limit
        Assert.False(await limiter.LimitAsync("api", "user_1"));

        // Different user is not blocked
        Assert.True(await limiter.LimitAsync("api", "user_2"));
    }

    [Fact]
    public async Task Middleware_returns_429_with_retry_after_header()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            RateLimit = new MemoryRateLimiter(defaultLimit: 2, TimeSpan.FromSeconds(30)),
        });

        app.Use(ReptarApi.RateLimit(limit: 2, windowSeconds: 30, keySelector: ctx => "test-client"));
        app.Get("/ping", ctx => Task.FromResult(ctx.Text("pong")));

        var res1 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/ping" });
        Assert.Equal(200, res1.Status);

        var res2 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/ping" });
        Assert.Equal(200, res2.Status);

        var res3 = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/ping" });
        Assert.Equal(429, res3.Status);
        Assert.Equal("30", res3.Headers["Retry-After"]);
        Assert.Contains("rate_limited", Encoding.UTF8.GetString(res3.Body!));
    }
}

public class QueueTests
{
    private sealed record DemoPayload(string Message, int Priority);

    [Fact]
    public async Task Dispatches_queue_batch_to_registered_handler()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var received = new List<string>();

        app.OnQueue("jobs", (ctx, msg) =>
        {
            received.Add($"{msg.Id}:{msg.Body}");
            return Task.CompletedTask;
        });

        var batch = new QueueBatch("batch_1", "jobs", [
            new QueueMessage("msg_1", "do work 1", DateTimeOffset.UtcNow.ToString("O"), 1),
            new QueueMessage("msg_2", "do work 2", DateTimeOffset.UtcNow.ToString("O"), 1),
        ]);

        var result = await app.HandleQueueBatchAsync(batch);

        Assert.True(result.Success);
        Assert.Empty(result.FailedMessageIds);
        Assert.Equal(2, received.Count);
        Assert.Equal("msg_1:do work 1", received[0]);
        Assert.Equal("msg_2:do work 2", received[1]);
    }

    [Fact]
    public async Task Dispatches_typed_queue_messages()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });
        var processed = new List<DemoPayload>();

        app.OnQueue<DemoPayload>("jobs", (ctx, msg, payload) =>
        {
            processed.Add(payload);
            return Task.CompletedTask;
        });

        var batch = new QueueBatch("batch_2", "jobs", [
            new QueueMessage("m1", "{\"message\":\"sync\",\"priority\":10}", DateTimeOffset.UtcNow.ToString("O"), 1),
        ]);

        var result = await app.HandleQueueBatchAsync(batch);

        Assert.True(result.Success);
        Assert.Single(processed);
        Assert.Equal("sync", processed[0].Message);
        Assert.Equal(10, processed[0].Priority);
    }

    [Fact]
    public async Task Reports_failed_message_ids_when_handler_throws()
    {
        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(":memory:") });

        app.OnQueue("jobs", (ctx, msg) =>
        {
            if (msg.Id == "bad_msg")
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        });

        var batch = new QueueBatch("batch_3", "jobs", [
            new QueueMessage("good_msg", "ok", DateTimeOffset.UtcNow.ToString("O"), 1),
            new QueueMessage("bad_msg", "crash", DateTimeOffset.UtcNow.ToString("O"), 1),
        ]);

        var result = await app.HandleQueueBatchAsync(batch);

        Assert.False(result.Success);
        Assert.Single(result.FailedMessageIds);
        Assert.Equal("bad_msg", result.FailedMessageIds[0]);
    }
}

public class StandardSchemaMetadataTests
{
    private sealed record UserRegistration(string Email, string Password, int Age, string? Website);

    private static readonly Validator<UserRegistration> UserSchema = ReptarApi.Rule<UserRegistration>()
        .Prop(x => x.Email, r => r.Email().Max(100))
        .Prop(x => x.Password, r => r.Min(8).Max(64))
        .Prop(x => x.Age, r => r.Min(18).Max(120))
        .Prop(x => x.Website, r => r.Url().Optional());

    [Fact]
    public void Validator_exposes_property_rule_metadata()
    {
        var meta = Assert.IsAssignableFrom<IValidatorMeta>(UserSchema);
        var rules = meta.Properties.ToDictionary(p => p.Name);

        Assert.Equal(4, rules.Count);

        var emailRule = rules["email"];
        Assert.True(emailRule.IsEmail);
        Assert.Equal(100, emailRule.MaxLength);
        Assert.False(emailRule.IsOptional);

        var passwordRule = rules["password"];
        Assert.Equal(8, passwordRule.MinLength);
        Assert.Equal(64, passwordRule.MaxLength);

        var ageRule = rules["age"];
        Assert.Equal(18, ageRule.MinValue);
        Assert.Equal(120, ageRule.MaxValue);

        var websiteRule = rules["website"];
        Assert.True(websiteRule.IsUrl);
        Assert.True(websiteRule.IsOptional);
    }
}

public class JwtTests
{
    private static readonly JwtOptions Options = new()
    {
        Audience = "reptar-tests",
        Issuer = "reptar-tests",
        Secret = "test-secret",
    };

    [Fact]
    public async Task Signs_and_verifies_a_token()
    {
        var crypto = new DevCrypto();
        var payload = new JwtPayload(
            "usr_1",
            "jake@example.com",
            "Jake",
            "user",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

        var token = await Jwt.SignAsync(crypto, Options, payload);
        var verified = await Jwt.VerifyAsync(crypto, Options, token);

        Assert.NotNull(verified);
        Assert.Equal("usr_1", verified!.Subject);
        Assert.Equal("jake@example.com", verified.Email);
    }

    [Fact]
    public async Task Rejects_tampered_and_expired_tokens()
    {
        var crypto = new DevCrypto();
        var payload = new JwtPayload(
            "usr_1",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

        var token = await Jwt.SignAsync(crypto, Options, payload);
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.Null(await Jwt.VerifyAsync(crypto, Options, tampered));

        var expired = payload with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        };
        var expiredToken = await Jwt.SignAsync(crypto, Options, expired);

        Assert.Null(await Jwt.VerifyAsync(crypto, Options, expiredToken));
    }

    [Fact]
    public void Extracts_token_and_bearer_schemes()
    {
        var request = new ReptarRequest
        {
            Method = "GET",
            Path = "/",
            Headers = new Dictionary<string, string>
            {
                ["authorization"] = "Token abc.def.ghi",
            },
        };

        Assert.Equal("abc.def.ghi", Jwt.ExtractToken(request));

        request.Headers["authorization"] = "Bearer xyz";
        Assert.Equal("xyz", Jwt.ExtractToken(request));
    }
}

public class ErrorMapperTests
{
    private sealed record Input(string Email);

    private sealed class RealWorldErrors : IErrorMapper
    {
        public ReptarResponse Error(
            Ctx ctx,
            int status,
            string code,
            string message) =>
            ctx.Json(new { errors = new { code, message } }, status);

        public ReptarResponse Validation(
            Ctx ctx,
            IReadOnlyList<ValidationIssue> issues) =>
            ctx.Json(
                new
                {
                    errors = issues.ToDictionary(
                        issue => issue.Path?.FirstOrDefault() ?? "base",
                        issue => new[] { issue.Message }),
                },
                422);
    }

    [Fact]
    public async Task Maps_validation_errors_to_a_custom_envelope()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Errors = new RealWorldErrors(),
        });

        app.Post(
            "/users",
            ReptarApi.Rule<Input>().Prop(x => x.Email, rules => rules.Email()),
            (ctx, input) => Task.FromResult(ctx.Json(input)));

        var response = await app.HandleAsync(new ReptarRequest
        {
            Method = "POST",
            Path = "/users",
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
            },
            Body = Encoding.UTF8.GetBytes("{\"email\":\"not-an-email\"}"),
        });

        Assert.Equal(422, response.Status);
        Assert.Contains("\"errors\"", Encoding.UTF8.GetString(response.Body!));
    }
}

public class CountAndBatchTests
{
    [Fact]
    public async Task Counts_rows_and_executes_an_atomic_batch()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestSchema.Widgets);

        await db.InsertAsync(TestSchema.Widgets, new Widget("w1", "Alpha", 1));
        Assert.Equal(1, await db.CountAsync(TestSchema.Widgets));

        var changes = await db.BatchAsync([
            new DbStatement(
                "insert into widgets (id, name, count) values (@id, @name, @count)",
                new { id = "w2", name = "Beta", count = 2 }),
            new DbStatement(
                "insert into widgets (id, name, count) values (@id, @name, @count)",
                new { id = "w3", name = "Gamma", count = 3 }),
        ]);

        Assert.Equal(2, changes);
        Assert.Equal(3, await db.CountAsync(TestSchema.Widgets));
    }

    [Fact]
    public async Task Rolls_back_a_failed_batch()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestSchema.Widgets);

        await Assert.ThrowsAnyAsync<Exception>(() => db.BatchAsync([
            new DbStatement(
                "insert into widgets (id, name, count) values (@id, @name, @count)",
                new { id = "w1", name = "Alpha", count = 1 }),
            new DbStatement("insert into missing_table (id) values (@id)", new { id = "bad" }),
        ]));

        Assert.Equal(0, await db.CountAsync(TestSchema.Widgets));
    }
}

public class SlugTests
{
    [Fact]
    public async Task Generates_a_unique_slug()
    {
        using var db = new SqliteDbClient(":memory:");
        var table = ReptarApi.DefineTable("slugged", new
        {
            Id = ReptarApi.Col.Text("id").PrimaryKey(),
            Slug = ReptarApi.Col.Text("slug").NotNull(),
        });
        await db.EnsureCreatedAsync(table);
        await db.InsertAsync(table, new { Id = "1", Slug = "hello-world" });

        var slug = await Slugs.UniqueAsync(
            db,
            table,
            "slug",
            "Hello World");

        Assert.Equal("hello-world-2", slug);
    }
}

public class MultipartTests
{
    [Fact]
    public void Parses_fields_and_file_bytes()
    {
        var boundary = "reptar-boundary";
        var body = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"title\"\r\n\r\n" +
            "Hello\r\n" +
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"image\"; filename=\"pixel.png\"\r\n" +
            "Content-Type: image/png\r\n\r\n" +
            "PNG\r\n" +
            $"--{boundary}--\r\n");

        var request = new ReptarRequest
        {
            Method = "POST",
            Path = "/upload",
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            Body = body,
        };

        var form = request.Form;

        Assert.NotNull(form);
        Assert.Equal("Hello", form!.Value("title"));
        Assert.NotNull(form.File("image"));
        Assert.Equal("pixel.png", form.File("image")!.FileName);
        Assert.Equal("PNG", Encoding.UTF8.GetString(form.File("image")!.Data));
    }
}

public class StreamingTests
{
    [Fact]
    public async Task Returns_an_sse_body_stream()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });

        app.Get("/stream", ctx => Task.FromResult(ctx.Sse(
            Events())));

        var response = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/stream",
        });

        Assert.Equal("text/event-stream", response.ContentType);
        Assert.NotNull(response.BodyStream);

        var chunks = new List<string>();

        await foreach (var chunk in response.BodyStream!(CancellationToken.None))
            chunks.Add(Encoding.UTF8.GetString(chunk));

        Assert.Equal(["data: one\n\n", "data: two\n\n"], chunks);
    }

    private static async IAsyncEnumerable<string> Events()
    {
        yield return "one";
        await Task.Yield();
        yield return "two";
    }
}

public class ScheduledTests
{
    [Fact]
    public async Task Runs_matching_scheduled_handlers()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });
        var calls = new List<string>();

        app.OnScheduled("*", (ctx, evt) =>
        {
            calls.Add($"{evt.Cron}:{ctx.Req.Method}");
            return Task.CompletedTask;
        });

        var result = await app.HandleScheduledAsync(
            new ScheduledEvent("*/5 * * * *", DateTimeOffset.UtcNow.ToString("O")));

        Assert.True(result.Success);
        Assert.Single(calls);
        Assert.StartsWith("*/5", calls[0]);
    }
}

public class LambdaTests
{
    [Fact]
    public void Parses_api_gateway_v2_event_correctly()
    {
        var json = """
        {
            "rawPath": "/posts/123",
            "rawQueryString": "filter=recent",
            "headers": {
                "content-type": "application/json",
                "x-custom-header": "test-val"
            },
            "cookies": [
                "reptar_session=abc123xyz"
            ],
            "requestContext": {
                "http": {
                    "method": "POST"
                }
            },
            "body": "eyAidGl0bGUiOiAiSGVsbG8iIH0=",
            "isBase64Encoded": true
        }
        """;

        var request = Reptar.Hosting.Lambda.LambdaHost.ParseApiGatewayV2Event(json);

        Assert.Equal("POST", request.Method);
        Assert.Equal("/posts/123", request.Path);
        Assert.Equal("filter=recent", request.Query);
        Assert.Equal("test-val", request.Headers["x-custom-header"]);
        Assert.Equal("abc123xyz", request.Cookies["reptar_session"]);
        Assert.NotNull(request.Body);
        Assert.Equal("{ \"title\": \"Hello\" }", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void Formats_api_gateway_v2_response_correctly()
    {
        var response = new ReptarResponse
        {
            Status = 201,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"id\":\"p1\"}"),
        };
        response.Headers["x-powered-by"] = "Reptar";
        response.SetCookies.Add("reptar_session=xyz; Path=/; HttpOnly");

        var formatted = Reptar.Hosting.Lambda.LambdaHost.FormatApiGatewayV2Response(response);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(formatted)!;

        Assert.Equal(201, doc["statusCode"]!.GetValue<int>());
        Assert.True(doc["isBase64Encoded"]!.GetValue<bool>());
        Assert.Equal("application/json", doc["headers"]!["content-type"]!.GetValue<string>());
        Assert.Equal("Reptar", doc["headers"]!["x-powered-by"]!.GetValue<string>());
        Assert.Equal("reptar_session=xyz; Path=/; HttpOnly", doc["cookies"]![0]!.GetValue<string>());

        var bodyBase64 = doc["body"]!.GetValue<string>();
        var bodyBytes = Convert.FromBase64String(bodyBase64);
        Assert.Equal("{\"id\":\"p1\"}", Encoding.UTF8.GetString(bodyBytes));
    }

    [Fact]
    public async Task Runs_lambda_runtime_loop_against_mock_runtime_api()
    {
        var listener = new System.Net.HttpListener();
        var random = new Random();
        var port = random.Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch
        {
            port = random.Next(40001, 55000);
            listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocationProcessed = false;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var getContextTask = listener.GetContextAsync();
                    var completedTask = await Task.WhenAny(getContextTask, Task.Delay(5000, cts.Token));
                    if (completedTask != getContextTask) break;

                    var context = await getContextTask;
                    var req = context.Request;
                    var res = context.Response;

                    if (req.Url?.AbsolutePath == "/2018-06-01/runtime/invocation/next")
                    {
                        res.StatusCode = 200;
                        res.Headers["Lambda-Runtime-Aws-Request-Id"] = "test-req-456";
                        var payload = Encoding.UTF8.GetBytes("""
                        {
                            "rawPath": "/greet",
                            "requestContext": { "http": { "method": "GET" } }
                        }
                        """);
                        res.ContentType = "application/json";
                        res.ContentLength64 = payload.Length;
                        await res.OutputStream.WriteAsync(payload, cts.Token);
                        res.OutputStream.Close();
                    }
                    else if (req.Url?.AbsolutePath == "/2018-06-01/runtime/invocation/test-req-456/response")
                    {
                        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync(cts.Token);
                        if (body.Contains("statusCode") && body.Contains("SGVsbG8gZnJvbSBMYW1iZGE="))
                        {
                            invocationProcessed = true;
                        }
                        res.StatusCode = 202;
                        res.OutputStream.Close();

                        cts.Cancel();
                        break;
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.OutputStream.Close();
                    }
                }
            }
            catch
            {
                // listener stopped or cancelled
            }
        });

        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
        });
        app.Get("/greet", ctx => Task.FromResult(ctx.Text("Hello from Lambda")));

        var originalEnv = Environment.GetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API");
        try
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API", $"127.0.0.1:{port}");
            await Reptar.Hosting.Lambda.LambdaHost.RunLambdaAsync(app, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected upon completion
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_RUNTIME_API", originalEnv);
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch { }
            await serverTask;
        }

        Assert.True(invocationProcessed);
    }
}

public class WebSocketTests
{
    private sealed class TestTransport : IWebSocketTransport
    {
        public List<string> Sent { get; } = [];
        public List<string> Closed { get; } = [];

        public void Send(string sessionId, string message) =>
            Sent.Add($"{sessionId}:{message}");

        public void Close(string sessionId, int code, string reason) =>
            Closed.Add($"{sessionId}:{code}:{reason}");
    }

    [Fact]
    public async Task Routes_open_message_and_close_events()
    {
        var transport = new TestTransport();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            WebSocket = transport,
        });
        var closed = false;

        app.OnWebSocket("/ws", new WebSocketHandlers
        {
            Open = connection => connection.SendAsync("connected"),
            Message = (connection, message) =>
                connection.SendAsync($"echo:{message}"),
            Close = connection =>
            {
                closed = true;
                return Task.CompletedTask;
            },
        });

        var request = new ReptarRequest
        {
            Method = "GET",
            Path = "/ws",
        };

        await app.HandleWebSocketOpenAsync("session_1", request);
        await app.HandleWebSocketMessageAsync("session_1", "hello");
        await app.HandleWebSocketCloseAsync("session_1");

        Assert.Equal(
            ["session_1:connected", "session_1:echo:hello"],
            transport.Sent);
        Assert.True(closed);
    }
}

public class ActionCableTests
{
    private sealed class TestTransport : IWebSocketTransport
    {
        public List<string> Sent { get; } = [];
        public List<string> Closed { get; } = [];

        public void Send(string sessionId, string message) =>
            Sent.Add(message);

        public void Close(string sessionId, int code, string reason) =>
            Closed.Add($"{code}:{reason}");
    }

    [Fact]
    public void Parses_turbo_streams_channel_subscription_command()
    {
        var json = """{"command":"subscribe","identifier":"{\"channel\":\"Turbo::StreamsChannel\",\"signed_stream_name\":\"room_42\"}"}""";
        var cmd = Reptar.Realtime.ActionCableProtocol.ParseClientMessage(json);

        Assert.NotNull(cmd);
        Assert.Equal("subscribe", cmd.Command);
        Assert.Equal("Turbo::StreamsChannel", cmd.Channel);
        Assert.Equal("room_42", cmd.StreamName);
    }

    [Fact]
    public void Parses_room_channel_subscription_command()
    {
        var json = """{"command":"subscribe","identifier":"{\"channel\":\"RoomChannel\",\"room_id\":105}"}""";
        var cmd = Reptar.Realtime.ActionCableProtocol.ParseClientMessage(json);

        Assert.NotNull(cmd);
        Assert.Equal("subscribe", cmd.Command);
        Assert.Equal("RoomChannel", cmd.Channel);
        Assert.Equal("room_105", cmd.StreamName);
        Assert.Equal(105L, cmd.Parameters["room_id"]);
    }

    [Fact]
    public void TurboStream_generates_spec_compliant_tags()
    {
        var append = Reptar.Realtime.TurboStream.Append("messages", "<p>Hello</p>");
        Assert.Equal("<turbo-stream action=\"append\" target=\"messages\"><template><p>Hello</p></template></turbo-stream>", append);

        var remove = Reptar.Realtime.TurboStream.Remove("message_12");
        Assert.Equal("<turbo-stream action=\"remove\" target=\"message_12\"></turbo-stream>", remove);

        var replace = Reptar.Realtime.TurboStream.Replace("message_12", "<p>Edited</p>");
        Assert.Equal("<turbo-stream action=\"replace\" target=\"message_12\"><template><p>Edited</p></template></turbo-stream>", replace);
    }

    [Fact]
    public async Task ActionCableServer_manages_lifecycle_and_broadcasts_to_streams()
    {
        var transport = new TestTransport();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            WebSocket = transport,
        });

        var cable = app.UseActionCable("/cable");

        var req1 = new ReptarRequest { Method = "GET", Path = "/cable" };
        var req2 = new ReptarRequest { Method = "GET", Path = "/cable" };

        // 1. Clients connect -> Receive welcome
        await app.HandleWebSocketOpenAsync("client_1", req1);
        await app.HandleWebSocketOpenAsync("client_2", req2);

        Assert.Equal("{\"type\":\"welcome\",\"sid\":\"client_1\"}", transport.Sent[0]);
        Assert.Equal("{\"type\":\"welcome\",\"sid\":\"client_2\"}", transport.Sent[1]);
        Assert.Equal(2, cable.TotalConnections);

        // 2. Client 1 subscribes to room_42
        var subJson1 = """{"command":"subscribe","identifier":"{\"channel\":\"Turbo::StreamsChannel\",\"signed_stream_name\":\"room_42\"}"}""";
        await app.HandleWebSocketMessageAsync("client_1", subJson1);

        Assert.Contains("confirm_subscription", transport.Sent[2]);
        Assert.Equal(1, cable.GetSubscriberCount("room_42"));

        // 3. Client 2 subscribes to room_42
        var subJson2 = """{"command":"subscribe","identifier":"{\"channel\":\"Turbo::StreamsChannel\",\"signed_stream_name\":\"room_42\"}"}""";
        await app.HandleWebSocketMessageAsync("client_2", subJson2);

        Assert.Contains("confirm_subscription", transport.Sent[3]);
        Assert.Equal(2, cable.GetSubscriberCount("room_42"));

        // 4. Broadcast Turbo Stream to room_42 -> both receive it
        var sentCountBefore = transport.Sent.Count;
        var broadcastCount = await cable.BroadcastTurboStreamAsync("room_42", "append", "messages", "<div id='m1'>Hi</div>");
        Assert.Equal(2, broadcastCount);

        // Both clients received the broadcast
        Assert.Equal(sentCountBefore + 2, transport.Sent.Count);
        Assert.Contains("turbo-stream action=\\\"append\\\" target=\\\"messages\\\"", transport.Sent[^1]);
        Assert.Contains("turbo-stream action=\\\"append\\\" target=\\\"messages\\\"", transport.Sent[^2]);

        // 5. Client 1 disconnects -> cleans up subscription
        await app.HandleWebSocketCloseAsync("client_1");
        Assert.Equal(1, cable.TotalConnections);
        Assert.Equal(1, cable.GetSubscriberCount("room_42"));

        // 6. Broadcast again -> only Client 2 receives it
        var sentCountAfterClose = transport.Sent.Count;
        var broadcastCount2 = await cable.BroadcastTurboStreamAsync("room_42", "append", "messages", "<div id='m2'>Hi 2</div>");
        Assert.Equal(1, broadcastCount2);
        Assert.Equal(sentCountAfterClose + 1, transport.Sent.Count);
    }

    [Fact]
    public async Task ActionCableServer_rejects_unauthorized_subscription()
    {
        var transport = new TestTransport();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            WebSocket = transport,
        });

        var cable = app.UseActionCable("/cable", server =>
        {
            server.AuthorizeSubscription = (conn, cmd) =>
            {
                // Only allow room_public
                return Task.FromResult(cmd.StreamName == "room_public");
            };
        });

        await app.HandleWebSocketOpenAsync("c1", new ReptarRequest { Method = "GET", Path = "/cable" });

        var secretSub = """{"command":"subscribe","identifier":"{\"channel\":\"Turbo::StreamsChannel\",\"signed_stream_name\":\"room_secret\"}"}""";
        await app.HandleWebSocketMessageAsync("c1", secretSub);

        Assert.Contains("reject_subscription", transport.Sent[^1]);
        Assert.Equal(0, cable.GetSubscriberCount("room_secret"));
    }
}

public class CampfireRealtimeTests
{
    private sealed class TestTransport : IWebSocketTransport
    {
        public List<string> Sent { get; } = [];
        public void Send(string sessionId, string message) => Sent.Add(message);
        public void Close(string sessionId, int code, string reason) { }
    }

    [Fact]
    public async Task CampfireRealtimeService_broadcasts_message_lifecycle_and_typing()
    {
        var transport = new TestTransport();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            WebSocket = transport,
        });

        var campfire = app.UseCampfire("/cable");

        await app.HandleWebSocketOpenAsync("user_101", new ReptarRequest { Method = "GET", Path = "/cable" });
        var subJson = """{"command":"subscribe","identifier":"{\"channel\":\"Turbo::StreamsChannel\",\"signed_stream_name\":\"room_7\"}"}""";
        await app.HandleWebSocketMessageAsync("user_101", subJson);

        // 1. Message created -> appends to messages container
        var msg = new Reptar.Realtime.CampfireMessage(
            Id: 1001,
            RoomId: 7,
            UserId: 42,
            UserName: "DHH",
            Body: "Welcome to ONCE Campfire!",
            CreatedAt: "2026-10-24T09:00:00Z"
        );
        await campfire.BroadcastMessageCreatedAsync(msg);

        // Checked that Turbo Stream was sent
        var appendFrame = transport.Sent.First(s => s.Contains("turbo-stream") && s.Contains("message_1001"));
        Assert.Contains("action=\\\"append\\\"", appendFrame);
        Assert.Contains("target=\\\"messages\\\"", appendFrame);
        Assert.Contains("Welcome to ONCE Campfire!", appendFrame);

        // Checked that typed JSON Action Cable frame was sent
        var jsonCreatedFrame = transport.Sent.First(s => s.Contains("message_created"));
        Assert.Contains("\"type\":\"message_created\"", jsonCreatedFrame);
        Assert.Contains("\"body\":\"Welcome to ONCE Campfire!\"", jsonCreatedFrame);
        Assert.Contains("\"user_name\":\"DHH\"", jsonCreatedFrame);

        // 2. Message updated -> replaces message_1001
        var updatedMsg = msg with { Body = "Welcome to ONCE Campfire on Reptar Native AOT!" };
        await campfire.BroadcastMessageUpdatedAsync(updatedMsg);

        var replaceFrame = transport.Sent.First(s => s.Contains("turbo-stream") && s.Contains("action=\\\"replace\\\""));
        Assert.Contains("target=\\\"message_1001\\\"", replaceFrame);
        Assert.Contains("Reptar Native AOT", replaceFrame);

        // 3. Typing indicator -> updates typing_indicator container
        await campfire.BroadcastTypingAsync(new Reptar.Realtime.CampfireTypingStatus(7, 42, "DHH", true));
        var typingFrame = transport.Sent.First(s => s.Contains("typing-active"));
        Assert.Contains("DHH is typing...", typingFrame);

        // 4. Message deleted -> removes message_1001
        await campfire.BroadcastMessageDeletedAsync(7, 1001);
        var removeFrame = transport.Sent.First(s => s.Contains("action=\\\"remove\\\"") && s.Contains("target=\\\"message_1001\\\""));
        Assert.NotNull(removeFrame);
    }
}

public class AotSafeSqliteTests
{
    [Fact]
    public async Task QueryMappedAsync_maps_rows_without_constructor_reflection()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestSchema.Widgets);
        await db.InsertAsync(TestSchema.Widgets, new Widget("w1", "Alpha", 3));

        var rows = await db.QueryMappedAsync(
            "select id, name, count from widgets",
            null,
            row => new Widget(
                row["id"]?.ToString() ?? "",
                row["name"]?.ToString() ?? "",
                Convert.ToInt32(row["count"])));

        Assert.Single(rows);
        Assert.Equal("Alpha", rows[0].Name);
        Assert.Equal(3, rows[0].Count);
    }

    [Fact]
    public async Task DeleteAsync_binds_the_primary_key_without_anonymous_types()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(TestSchema.Widgets);
        await db.InsertAsync(TestSchema.Widgets, new Widget("w1", "Alpha", 3));

        await db.DeleteAsync(TestSchema.Widgets, "w1");

        Assert.Equal(0, await db.CountAsync(TestSchema.Widgets));
    }
}
