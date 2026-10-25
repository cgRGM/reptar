using System.Text;
using System.Text.Json;
using Xunit;

namespace Reptar.Tests;

public sealed class StorageAndUploadTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE, 0x00, 0x80];

    [Fact]
    public void MultipartForm_ParsesSpanDirectly_EnforcesLimits_AndRejectsMalformedBoundaries()
    {
        var boundary = "----ReptarBoundary7MA4YWxkTrZu0gW";
        var contentType = $"multipart/form-data; boundary=\"{boundary}\"";

        var preamble = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"title\"\r\n\r\n" +
            "Hello Multipart\r\n" +
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"avatar\"; filename=\"../../avatar.png\"\r\n" +
            "Content-Type: image/png\r\n\r\n");
        var epilogue = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

        var fullBody = new byte[preamble.Length + PngSignature.Length + epilogue.Length];
        preamble.CopyTo(fullBody, 0);
        PngSignature.CopyTo(fullBody, preamble.Length);
        epilogue.CopyTo(fullBody, preamble.Length + PngSignature.Length);

        var parsed = MultipartForm.TryParse(contentType, fullBody.AsSpan());
        Assert.NotNull(parsed);
        Assert.Equal("Hello Multipart", parsed!.Value("title"));

        var file = parsed.File("avatar");
        Assert.NotNull(file);
        Assert.Equal("avatar.png", file!.FileName); // directory traversal stripped
        Assert.Equal("image/png", file.SniffedContentType);
        Assert.Equal(PngSignature, file.Data);

        // Enforce MaxFileBytes limit
        var rejectedBySize = MultipartForm.TryParse(
            contentType,
            fullBody.AsSpan(),
            new MultipartOptions { MaxFileBytes = 4 });
        Assert.Null(rejectedBySize);

        // Reject malformed body missing closing "--boundary--"
        var truncatedBody = fullBody[..^epilogue.Length];
        Assert.Null(MultipartForm.TryParse(contentType, truncatedBody.AsSpan()));
    }

    [Fact]
    public void Files_Check_ValidatesMagicBytes_WildcardTypes_AndSize()
    {
        var pngFile = new FormFile("avatar", "avatar.png", "application/octet-stream", PngSignature);
        var ok = Files.Check(pngFile, maxBytes: 1024, "image/*");
        Assert.True(ok.Ok);
        Assert.Equal("image/png", ok.ContentType);
        Assert.Equal("png", ok.Extension);

        // Disallowed MIME family (e.g. only application/pdf permitted)
        var wrongType = Files.Check(pngFile, maxBytes: 1024, "application/pdf");
        Assert.False(wrongType.Ok);
        Assert.Equal(415, wrongType.ErrorStatus);

        // Oversize check
        var tooLarge = Files.Check(pngFile, maxBytes: 4, "image/*");
        Assert.False(tooLarge.Ok);
        Assert.Equal(413, tooLarge.ErrorStatus);

        // SVG masquerading as image/png
        var svgFile = new FormFile("avatar", "avatar.png", "image/png", Encoding.UTF8.GetBytes("<svg></svg>"));
        var spoofed = Files.Check(svgFile, maxBytes: 1024, "image/*");
        Assert.False(spoofed.Ok);
        Assert.Equal(415, spoofed.ErrorStatus);
    }

    [Fact]
    public async Task S3Storage_PresignPutAsync_GeneratesDeterministicSigV4Url()
    {
        var fixedTime = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var s3 = Storage.S3(new S3StorageOptions
        {
            Endpoint = "https://acc123.r2.cloudflarestorage.com",
            Bucket = "media",
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            Region = "auto",
            Clock = () => fixedTime,
        });

        var presigned = await s3.PresignPutAsync(
            "avatars/user_1.png",
            "image/png",
            TimeSpan.FromMinutes(15));

        Assert.Equal("PUT", presigned.Method);
        Assert.Equal("avatars/user_1.png", presigned.Key);
        Assert.Equal(900, presigned.ExpiresInSeconds);
        Assert.Equal("image/png", presigned.Headers!["Content-Type"]);
        Assert.StartsWith("https://acc123.r2.cloudflarestorage.com/media/avatars/user_1.png?", presigned.Url);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", presigned.Url);
        Assert.Contains("X-Amz-Date=20261025T120000Z", presigned.Url);
        Assert.Contains("X-Amz-Expires=900", presigned.Url);
        Assert.Contains("X-Amz-SignedHeaders=content-type%3Bhost", presigned.Url);
        Assert.Matches(@"X-Amz-Signature=[0-9a-f]{64}$", presigned.Url);
    }

    [Fact]
    public async Task AcceptsFile_EmitsMultipartFormDataBinarySchemaInOpenApi()
    {
        var app = ReptarApp.Create();
        app.Post("/upload", ctx => Task.FromResult(ctx.Status(201)))
            .AcceptsFile("avatar", "User profile image")
            .Produces(201);
        app.UseScalar();

        var res = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/openapi.json" });
        Assert.Equal(200, res.Status);

        using var doc = JsonDocument.Parse(res.Body!);
        var avatarProp = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/upload")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("avatar");

        Assert.Equal("string", avatarProp.GetProperty("type").GetString());
        Assert.Equal("binary", avatarProp.GetProperty("format").GetString());
    }
}
