using System.Text;
using System.Text.Json;
using Xunit;

namespace Reptar.Tests;

public sealed class BlogUploadTests
{
    private static readonly byte[] ValidPngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Fact]
    public async Task Upload_RequiresAuth_RejectsSvgAndOversize_AndGeneratesServerKey()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), $"reptar_blog_r2_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"reptar_blog_db_{Guid.NewGuid():N}.db");

        try
        {
            using var db = new SqliteDbClient(dbPath);
            var app = ReptarApp.Create(new ReptarConfig
            {
                Auth = ReptarApi.BuiltIn(),
                Database = db,
                Storage = new LocalR2Client(tempStorage),
            });

            Blog.Configure(app);
            await app.EnsureCreatedAsync();

            // 1. Unauthenticated upload returns 401
            var unauthRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/upload",
                Headers = new Dictionary<string, string> { ["content-type"] = "image/png" },
                Body = ValidPngBytes,
            });
            Assert.Equal(401, unauthRes.Status);

            // Create an authenticated anonymous session
            var anonRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/auth/anonymous",
            });
            Assert.Equal(201, anonRes.Status);
            var sessionCookie = ExtractSessionToken(anonRes);

            // 2. SVG upload (even with image/png header!) is rejected by magic-byte sniffing (415)
            var svgRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/upload",
                Cookies = new Dictionary<string, string> { ["reptar_session"] = sessionCookie },
                Headers = new Dictionary<string, string> { ["content-type"] = "image/png" },
                Body = Encoding.UTF8.GetBytes("<svg onload=alert(1)></svg>"),
            });
            Assert.Equal(415, svgRes.Status);

            // 3. Oversize upload (> 2MB) is rejected with 413
            var oversizeBytes = new byte[Blog.MaxUploadBytes + 1];
            ValidPngBytes.CopyTo(oversizeBytes, 0);
            var oversizeRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/upload",
                Cookies = new Dictionary<string, string> { ["reptar_session"] = sessionCookie },
                Headers = new Dictionary<string, string> { ["content-type"] = "image/png" },
                Body = oversizeBytes,
            });
            Assert.Equal(413, oversizeRes.Status);

            // 4. Valid PNG upload ignores client-supplied ?key= and returns server-generated key
            var validRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "POST",
                Path = "/upload",
                Query = "key=victim_avatar.png",
                Cookies = new Dictionary<string, string> { ["reptar_session"] = sessionCookie },
                Headers = new Dictionary<string, string> { ["content-type"] = "application/octet-stream" },
                Body = ValidPngBytes,
            });
            Assert.Equal(201, validRes.Status);

            using var doc = JsonDocument.Parse(validRes.Body!);
            var generatedKey = doc.RootElement.GetProperty("key").GetString()!;
            Assert.NotEqual("victim_avatar.png", generatedKey);
            Assert.EndsWith(".png", generatedKey);

            // 5. Serving the image sets X-Content-Type-Options: nosniff and CSP sandbox
            var getRes = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = $"/images/{generatedKey}",
            });
            Assert.Equal(200, getRes.Status);
            Assert.Equal("image/png", getRes.ContentType);
            Assert.Equal("nosniff", getRes.Headers["X-Content-Type-Options"]);
            Assert.Contains("sandbox", getRes.Headers["Content-Security-Policy"]);
            Assert.Equal(ValidPngBytes, getRes.Body);
        }
        finally
        {
            if (Directory.Exists(tempStorage))
                Directory.Delete(tempStorage, recursive: true);

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(dbPath + suffix))
                        File.Delete(dbPath + suffix);
                }
                catch { }
            }
        }
    }

    [Fact]
    public async Task LocalR2Client_PreservesSubpaths_AndRejectsPathTraversal()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"reptar_r2_paths_{Guid.NewGuid():N}");
        try
        {
            var r2 = new LocalR2Client(tempRoot);

            await r2.PutAsync("a/x.png", [1, 2, 3], "image/png");
            await r2.PutAsync("b/x.png", [4, 5, 6], "image/png");

            // Subpaths do not collide
            Assert.Equal(new byte[] { 1, 2, 3 }, await r2.GetAsync("a/x.png"));
            Assert.Equal(new byte[] { 4, 5, 6 }, await r2.GetAsync("b/x.png"));

            // Traversal attempts throw ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(() =>
                r2.PutAsync("../escape.png", [1], "image/png"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                r2.GetAsync("a/../../escape.png"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string ExtractSessionToken(ReptarResponse response)
    {
        var cookie = response.SetCookies[0];
        var pair = cookie.Split(';')[0];
        return pair.Split('=', 2)[1];
    }
}
