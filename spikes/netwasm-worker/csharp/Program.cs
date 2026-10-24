using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

public sealed record HostRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body);

public sealed record HostResponse(
    int Status,
    Dictionary<string, string> Headers,
    string Body,
    string[] SetCookies);

public sealed record Post(
    string Id,
    string Title,
    string Content,
    string? ImageKey,
    string CreatedAt);

public sealed record CreatePost(string Title, string Content, string? ImageKey);

public sealed record UploadResult(string Key, string Url);

[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(Post))]
[JsonSerializable(typeof(List<Post>))]
[JsonSerializable(typeof(CreatePost))]
[JsonSerializable(typeof(UploadResult))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SessionUser))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(Auth.UserRow))]
[JsonSerializable(typeof(List<Auth.UserRow>))]
[JsonSerializable(typeof(Auth.CountRow))]
[JsonSerializable(typeof(List<Auth.CountRow>))]
[JsonSerializable(typeof(List<SessionUser>))]
[JsonSerializable(typeof(Auth.SignUpInput))]
[JsonSerializable(typeof(Auth.SignInInput))]
[JsonSerializable(typeof(CheckoutSession))]
[JsonSerializable(typeof(CreateCheckoutInput))]
[JsonSerializable(typeof(StripeLineItem))]
[JsonSerializable(typeof(List<StripeLineItem>))]
internal partial class BlogJsonContext : JsonSerializerContext
{
}

public static partial class PageExports
{
    private static readonly Validator<CreatePost> CreateSchema = Rule<CreatePost>()
        .Prop(x => x.Title, rules => rules.Min(3).Max(120))
        .Prop(x => x.Content, rules => rules.Min(1));

    public static readonly TableDef PostsTable = DefineTable("posts",
        Col.Text("id").PrimaryKey(),
        Col.Text("title").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("image_key"),
        Col.Text("created_at")
    );

    private static readonly ReptarApp App;

    static PageExports()
    {
        ReptarJson.Options.TypeInfoResolver = BlogJsonContext.Default;
        App = BuildApp();
    }

    [JSImport("respond", "app.host")]
    private static extern void Respond(string json);

    [JSExport("handle")]
    public static void Handle(string requestJson)
    {
        _ = HandleAsync(requestJson);
    }

    private static async Task HandleAsync(string requestJson)
    {
        var request = JsonSerializer.Deserialize<HostRequest>(
            requestJson,
            ReptarJson.Options);

        if (request is null)
        {
            Respond(JsonSerializer.Serialize(
                new HostResponse(400, [], "{\"error\":\"bad_request\"}", []),
                ReptarJson.Options));
            return;
        }

        var response = await App.HandleAsync(new ReptarRequest
        {
            Method = request.Method,
            Path = request.Path,
            Query = request.Query.TrimStart('?'),
            Headers = request.Headers,
            Cookies = ParseCookies(request.Headers),
            Body = Encoding.UTF8.GetBytes(request.Body),
        });

        var body = response.Body is null
            ? ""
            : Encoding.UTF8.GetString(response.Body);

        var headers = new Dictionary<string, string>(response.Headers);

        if (response.ContentType is not null)
        {
            headers["content-type"] = response.ContentType;
        }

        Respond(JsonSerializer.Serialize(
            new HostResponse(
                response.Status,
                headers,
                body,
                response.SetCookies.ToArray()),
            ReptarJson.Options));
    }

    private static Dictionary<string, string> ParseCookies(
        Dictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue("cookie", out var header))
        {
            return cookies;
        }

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (name.Length > 0)
            {
                cookies[name] = value;
            }
        }

        return cookies;
    }

    private static ReptarApp BuildApp()
    {
        var json = new DefaultJsonCodec();

        var app = ReptarApp.Create(new ReptarConfig
        {
            Debug = true,
            Auth = BuiltIn(),
            Database = new D1DbClient(json),
            Storage = new R2Client(),
            Json = json,
        });

        app.Use(ReptarApi.RequestId());
        app.Route("/auth", Auth.Routes());

        app.Get("/me", Auth.RequireUser, async ctx =>
            ctx.Json(new SessionResponse(ctx.User)));

        // Real CRUD helpers backed by TableDef and D1DbClient
        app.Get("/posts", async ctx => ctx.Json(
            await ctx.Db.ListAsync<Post>(PostsTable, "order by created_at desc")));

        app.Get("/posts/:id", async ctx =>
        {
            var post = await ctx.Db.GetAsync<Post>(PostsTable, ctx.Req.Param("id")!);
            return post is null ? ctx.NotFound("Post not found.") : ctx.Json(post);
        });

        app.Post("/posts", CreateSchema, async (ctx, input) =>
        {
            var post = new Post(
                Id: Ids.New("post"),
                Title: input.Title.Trim(),
                Content: input.Content,
                ImageKey: input.ImageKey,
                CreatedAt: ctx.Clock.IsoNow());

            await ctx.Db.InsertAsync(PostsTable, post);
            return ctx.Json(post, 201);
        });

        app.Put("/posts/:id", CreateSchema, async (ctx, input) =>
        {
            var post = await ctx.Db.GetAsync<Post>(PostsTable, ctx.Req.Param("id")!);
            if (post is null)
            {
                return ctx.NotFound("Post not found.");
            }

            var updated = post with
            {
                Title = input.Title.Trim(),
                Content = input.Content,
                ImageKey = input.ImageKey ?? post.ImageKey,
            };

            await ctx.Db.UpdateAsync(PostsTable, updated, updated.Id);
            return ctx.Json(updated);
        });

        app.Delete("/posts/:id", async ctx =>
        {
            var post = await ctx.Db.GetAsync<Post>(PostsTable, ctx.Req.Param("id")!);
            if (post is null)
            {
                return ctx.NotFound("Post not found.");
            }

            await ctx.Db.DeleteAsync(PostsTable, post.Id);
            return ctx.Status(204);
        });

        app.Post("/upload", async ctx =>
        {
            var key = ctx.Req.QueryValue("key") ?? Guid.NewGuid().ToString("N");
            var contentType = ctx.Req.Header("content-type") ?? "application/octet-stream";
            var body = ctx.Req.Body ?? [];

            var url = await ctx.Storage.PutAsync(key, body, contentType);
            return ctx.Json(new UploadResult(key, url), 201);
        });

        // Built-in Stripe module
        app.Route("/stripe", Stripe.Routes(new StripeConfig
        {
            ApiKey = "sk_test_placeholder",
            WebhookSecret = "whsec_placeholder",
        }));

        return app;
    }
}
