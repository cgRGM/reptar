using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Reptar;

public static class Blog
{
    public const int MaxUploadBytes = 2_000_000;
    public const int MaxStreamUploadBytes = 20_000_000;

    public sealed record UploadResult(string Key, string Url);

    public static void Configure(ReptarApp app)
    {
        app.RegisterSchema(Schema.All);

        app.Get("/health", ctx => Task.FromResult(ctx.Json(
            new Dictionary<string, string> { ["status"] = "ok" })));

        app.Route("/auth", Auth.Routes());
        app.Route("/posts", Posts.Routes());

        app.Post("/upload", Auth.RequireUser, async ctx =>
        {
            var form = ctx.Req.Form;
            var file = form?.File("file") ??
                       form?.File("image") ??
                       (form is { Files.Count: > 0 } ? form.Files[0] : null);

            var check = Files.Check(
                file?.Data ?? ctx.Req.Body,
                MaxUploadBytes,
                "image/*");

            if (!check.Ok)
                return check.ToError(ctx);

            var userId = SanitizeUserSegment(ctx.User!.Id);
            var key = $"{userId}_{Ids.New("img")}.{check.Extension}";

            await ctx.Storage.PutAsync(
                key,
                check.Data,
                check.ContentType,
                new StoragePutOptions
                {
                    CacheControl = "public, max-age=31536000, immutable",
                });

            return ctx.Json(new UploadResult(key, $"/images/{key}"), 201);
        })
            .Produces<UploadResult>(201)
            .Produces<ErrorBody>(400)
            .Produces<ErrorBody>(401)
            .Produces<ErrorBody>(413)
            .Produces<ErrorBody>(415);

        app.Put("/upload/stream", Auth.RequireUser, async ctx =>
        {
            var check = await Files.CheckStreamAsync(ctx.Req.BodyStream, "image/*");
            if (!check.Ok)
                return check.ToError(ctx);

            var userId = SanitizeUserSegment(ctx.User!.Id);
            var key = $"{userId}_{Ids.New("img")}.{check.Extension}";

            await ctx.Storage.PutAsync(
                key,
                check.Body!,
                check.ContentType,
                new StoragePutOptions
                {
                    CacheControl = "public, max-age=31536000, immutable",
                });

            return ctx.Json(new UploadResult(key, $"/images/{key}"), 201);
        })
            .StreamBody(MaxStreamUploadBytes)
            .Produces<UploadResult>(201)
            .Produces<ErrorBody>(400)
            .Produces<ErrorBody>(401)
            .Produces<ErrorBody>(413)
            .Produces<ErrorBody>(415);

        app.Get("/images/:key", ctx =>
                ctx.File(
                    ctx.Req.Param("key")!,
                    new StorageFileOptions
                    {
                        Immutable = true,
                        Headers = new Dictionary<string, string>
                        {
                            ["Content-Security-Policy"] = "default-src 'none'; sandbox",
                        },
                    }))
            .Produces(200)
            .Produces(206)
            .Produces(304)
            .Produces<ErrorBody>(400)
            .Produces<ErrorBody>(404);

        app.Get("/me", Auth.RequireUser, async ctx =>
            ctx.Json(new SessionResponse(ctx.User)))
            .Produces<SessionResponse>(200)
            .Produces<ErrorBody>(401);
    }

    private static string SanitizeUserSegment(string id)
    {
        Span<char> buffer = stackalloc char[Math.Min(id.Length, 32)];
        var count = 0;
        for (var i = 0; i < id.Length && count < buffer.Length; i++)
        {
            var ch = id[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                buffer[count++] = ch;
            }
        }

        return count > 0 ? new string(buffer[..count]) : "usr";
    }
}
