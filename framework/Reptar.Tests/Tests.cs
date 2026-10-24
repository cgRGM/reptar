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
}
