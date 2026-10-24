using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Reptar;

public static class Blog
{
    public sealed record UploadResult(string Key, string Url);

    public static void Configure(ReptarApp app)
    {
        app.RegisterSchema(Schema.All);

        app.Get("/health", ctx => Task.FromResult(ctx.Json(
            new Dictionary<string, string> { ["status"] = "ok" })));

        app.Route("/auth", Auth.Routes());
        app.Route("/posts", Posts.Routes());

        app.Post("/upload", async ctx =>
        {
            var key = ctx.Req.QueryValue("key") ?? Ids.New("img");
            var contentType =
                ctx.Req.Header("content-type") ?? "application/octet-stream";
            var body = ctx.Req.Body ?? [];

            var url = await ctx.Storage.PutAsync(key, body, contentType);
            return ctx.Json(new UploadResult(key, url), 201);
        })
            .Produces<UploadResult>(201);

        app.Get("/images/:key", async ctx =>
        {
            var key = ctx.Req.Param("key")!;
            var bytes = await ctx.Storage.GetAsync(key);

            return bytes is null
                ? ctx.NotFound("Image not found.")
                : ctx.Stream(bytes, ContentTypeFor(key));
        })
            .Produces(200)
            .Produces<ErrorBody>(404);

        app.Get("/me", Auth.RequireUser, async ctx =>
            ctx.Json(new SessionResponse(ctx.User)))
            .Produces<SessionResponse>(200)
            .Produces<ErrorBody>(401);
    }

    private static string ContentTypeFor(string key)
    {
        if (key.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return "image/svg+xml";

        if (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        if (key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";

        if (key.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        return "application/octet-stream";
    }
}
