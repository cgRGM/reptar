using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Reptar;

public static class Blog
{
    public const int MaxUploadBytes = 2_000_000;

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
            var body = ExtractUploadBytes(ctx);
            if (body is null || body.Length == 0)
            {
                return ctx.BadRequest("Image payload is required.");
            }

            if (body.Length > MaxUploadBytes)
            {
                return ctx.Error(
                    413,
                    "payload_too_large",
                    $"Image exceeds maximum size of {MaxUploadBytes} bytes.");
            }

            var detected = SniffImageFormat(body);
            if (detected is null)
            {
                return ctx.Error(
                    415,
                    "unsupported_media_type",
                    "Only PNG, JPEG, WebP, and GIF images are allowed.");
            }

            var (contentType, extension) = detected.Value;
            var userId = SanitizeUserSegment(ctx.User!.Id);
            var key = $"{userId}_{Ids.New("img")}.{extension}";

            var url = await ctx.Storage.PutAsync(key, body, contentType);
            return ctx.Json(new UploadResult(key, url), 201);
        })
            .Produces<UploadResult>(201)
            .Produces<ErrorBody>(400)
            .Produces<ErrorBody>(401)
            .Produces<ErrorBody>(413)
            .Produces<ErrorBody>(415);

        app.Get("/images/:key", async ctx =>
        {
            var key = ctx.Req.Param("key")!;
            if (string.IsNullOrWhiteSpace(key) ||
                key.Contains("..", StringComparison.Ordinal) ||
                key.Contains('/') ||
                key.Contains('\\'))
            {
                return ctx.BadRequest("Invalid image key.");
            }

            var bytes = await ctx.Storage.GetAsync(key);
            if (bytes is null)
            {
                return ctx.NotFound("Image not found.");
            }

            ctx.SetHeader("X-Content-Type-Options", "nosniff");
            ctx.SetHeader("Content-Security-Policy", "default-src 'none'; sandbox");

            var detected = SniffImageFormat(bytes);
            if (detected is null)
            {
                ctx.SetHeader("Content-Disposition", "attachment");
                return ctx.Stream(bytes, "application/octet-stream");
            }

            return ctx.Stream(bytes, detected.Value.ContentType);
        })
            .Produces(200)
            .Produces<ErrorBody>(400)
            .Produces<ErrorBody>(404);

        app.Get("/me", Auth.RequireUser, async ctx =>
            ctx.Json(new SessionResponse(ctx.User)))
            .Produces<SessionResponse>(200)
            .Produces<ErrorBody>(401);
    }

    private static byte[]? ExtractUploadBytes(Ctx ctx)
    {
        var contentType = ctx.Req.Header("content-type");
        if (contentType is not null &&
            contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var form = ctx.Req.Form;
            if (form is null)
                return null;

            var file = form.File("file") ?? form.File("image") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            return file?.Data;
        }

        return ctx.Req.Body;
    }

    public static (string ContentType, string Extension)? SniffImageFormat(ReadOnlySpan<byte> bytes)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47 &&
            bytes[4] == 0x0D &&
            bytes[5] == 0x0A &&
            bytes[6] == 0x1A &&
            bytes[7] == 0x0A)
        {
            return ("image/png", "png");
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return ("image/jpeg", "jpg");
        }

        // GIF87a / GIF89a: 47 49 46 38 (37|39) 61
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46 &&
            bytes[3] == 0x38 &&
            (bytes[4] == 0x37 || bytes[4] == 0x39) &&
            bytes[5] == 0x61)
        {
            return ("image/gif", "gif");
        }

        // WebP: RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46 &&
            bytes[3] == 0x46 &&
            bytes[8] == 0x57 &&
            bytes[9] == 0x45 &&
            bytes[10] == 0x42 &&
            bytes[11] == 0x50)
        {
            return ("image/webp", "webp");
        }

        return null;
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
