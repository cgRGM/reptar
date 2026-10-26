using System.Text;
using Xunit;

namespace Reptar.Tests;

public sealed class StorageV2Tests
{
    private static readonly byte[] SampleBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0xFE, 0xFF];

    [Theory]
    [InlineData("avatars/user_1.png", "avatars/user_1.png")]
    [InlineData("/avatars/user_1.png", "avatars/user_1.png")]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("folder/file name.webp", "folder/file name.webp")]
    public void StorageKey_Normalize_AcceptsPortableKeys(string input, string expected)
    {
        Assert.Equal(expected, StorageKey.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("a/")]
    [InlineData("a\0b")]
    public void StorageKey_Normalize_RejectsUnsafeKeys(string? input)
    {
        Assert.False(StorageKey.IsValid(input));
        Assert.Throws<StorageException>(() => StorageKey.Normalize(input));
    }

    [Fact]
    public void StorageKey_Normalize_RejectsOverlongSegments()
    {
        var key = new string('a', StorageKey.MaxSegmentLength + 1);
        Assert.False(StorageKey.IsValid(key));
    }

    [Fact]
    public void Files_Check_RecognizesMediaSignatures()
    {
        var mp4 = new byte[]
        {
            0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
        };
        var mp4Check = Files.Check(mp4, maxBytes: 1024, "video/*");
        Assert.True(mp4Check.Ok);
        Assert.Equal("video/mp4", mp4Check.ContentType);
        Assert.Equal("mp4", mp4Check.Extension);

        var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02 };
        var webmCheck = Files.Check(webm, maxBytes: 1024, "video/*");
        Assert.True(webmCheck.Ok);
        Assert.Equal("video/webm", webmCheck.ContentType);

        var wav = new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x00, 0x00, 0x00, 0x00,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
        };
        var wavCheck = Files.Check(wav, maxBytes: 1024, "audio/*");
        Assert.True(wavCheck.Ok);
        Assert.Equal("audio/wav", wavCheck.ContentType);
    }

    [Fact]
    public async Task LocalStorage_SupportsMetadataRangesListingAndConditionalPut()
    {
        var root = CreateTempRoot();
        try
        {
            var storage = new LocalStorage(root);

            await storage.PutAsync(
                "avatars/a.png",
                SampleBytes,
                "image/png",
                new StoragePutOptions
                {
                    CacheControl = "public, max-age=60",
                    Metadata = new Dictionary<string, string> { ["owner"] = "user_1" },
                });

            var head = await storage.HeadAsync("avatars/a.png");
            Assert.NotNull(head);
            Assert.Equal(SampleBytes.Length, head!.Size);
            Assert.Equal("image/png", head.ContentType);
            Assert.False(string.IsNullOrEmpty(head.ETag));
            Assert.Equal("public, max-age=60", head.Metadata!["cache-control"]);
            Assert.Equal("user_1", head.Metadata["owner"]);

            var range = await storage.GetRangeAsync("avatars/a.png", 2, 4);
            Assert.NotNull(range);
            Assert.Equal(2, range!.Offset);
            Assert.Equal(SampleBytes.Length, range.TotalSize);
            Assert.Equal(SampleBytes[2..6], range.Data);

            var page = await storage.ListAsync(new StorageListOptions
            {
                Prefix = "avatars/",
            });
            Assert.Single(page.Items);
            Assert.Equal("avatars/a.png", page.Items[0].Key);
            Assert.False(page.HasMore);

            var precondition = await Assert.ThrowsAsync<StorageException>(() =>
                storage.PutAsync(
                    "avatars/a.png",
                    SampleBytes,
                    "image/png",
                    new StoragePutOptions { Overwrite = false }));
            Assert.Equal(StorageErrorCode.PreconditionFailed, precondition.Code);

            await storage.DeleteAsync("avatars/a.png");
            Assert.Null(await storage.HeadAsync("avatars/a.png"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CtxFile_ServesEtagConditionalRangeHeadAndAttachment()
    {
        var root = CreateTempRoot();
        try
        {
            var storage = new LocalStorage(root);
            await storage.PutAsync(
                "a.png",
                SampleBytes,
                "image/png",
                new StoragePutOptions
                {
                    CacheControl = "public, max-age=120",
                });

            var app = ReptarApp.Create(new ReptarConfig { Storage = storage });
            app.Get("/files/:key", ctx => ctx.File(ctx.Param("key")!));
            app.Get("/download/:key", ctx => ctx.Attachment(ctx.Param("key")!));

            var full = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
            });
            Assert.Equal(200, full.Status);
            Assert.Equal("image/png", full.ContentType);
            Assert.Equal(SampleBytes, full.Body);
            Assert.Equal("public, max-age=120", full.Headers["Cache-Control"]);
            Assert.Equal("bytes", full.Headers["Accept-Ranges"]);
            Assert.True(full.Headers.ContainsKey("ETag"));

            var etag = full.Headers["ETag"];
            var notModified = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string> { ["If-None-Match"] = etag },
            });
            Assert.Equal(304, notModified.Status);
            Assert.Null(notModified.Body);
            Assert.Equal(etag, notModified.Headers["ETag"]);

            var partial = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string> { ["Range"] = "bytes=2-5" },
            });
            Assert.Equal(206, partial.Status);
            Assert.Equal(SampleBytes[2..6], partial.Body);
            Assert.Equal($"bytes 2-5/{SampleBytes.Length}", partial.Headers["Content-Range"]);
            Assert.Equal("4", partial.Headers["Content-Length"]);

            var head = await app.HandleAsync(new ReptarRequest
            {
                Method = "HEAD",
                Path = "/files/a.png",
            });
            Assert.Equal(200, head.Status);
            Assert.Null(head.Body);
            Assert.Equal(SampleBytes.Length.ToString(), head.Headers["Content-Length"]);

            var headNotModified = await app.HandleAsync(new ReptarRequest
            {
                Method = "HEAD",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string> { ["If-None-Match"] = etag },
            });
            Assert.Equal(304, headNotModified.Status);
            Assert.Null(headNotModified.Body);

            var staleIfRange = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string>
                {
                    ["Range"] = "bytes=2-5",
                    ["If-Range"] = "\"stale\"",
                },
            });
            Assert.Equal(200, staleIfRange.Status);
            Assert.Equal(SampleBytes, staleIfRange.Body);

            var freshIfRange = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string>
                {
                    ["Range"] = "bytes=2-5",
                    ["If-Range"] = etag,
                },
            });
            Assert.Equal(206, freshIfRange.Status);

            var notModifiedSince = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string>
                {
                    ["If-Modified-Since"] = DateTimeOffset.UtcNow.AddHours(1).ToString("R"),
                },
            });
            Assert.Equal(304, notModifiedSince.Status);

            var unsatisfiable = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/a.png",
                Headers = new Dictionary<string, string> { ["Range"] = "bytes=999-1000" },
            });
            Assert.Equal(416, unsatisfiable.Status);
            Assert.Equal($"bytes */{SampleBytes.Length}", unsatisfiable.Headers["Content-Range"]);

            var missing = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/missing.png",
            });
            Assert.Equal(404, missing.Status);

            var invalid = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/files/..",
            });
            Assert.Equal(400, invalid.Status);

            var invalidAttachment = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/download/..",
            });
            Assert.Equal(400, invalidAttachment.Status);

            var attachment = await app.HandleAsync(new ReptarRequest
            {
                Method = "GET",
                Path = "/download/a.png",
            });
            Assert.Equal(200, attachment.Status);
            Assert.Contains(
                "attachment",
                attachment.Headers["Content-Disposition"],
                StringComparison.Ordinal);
            Assert.Contains(
                "a.png",
                attachment.Headers["Content-Disposition"],
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CtxFile_FallsBackToGetForProvidersWithoutHead()
    {
        var app = ReptarApp.Create(new ReptarConfig
        {
            Storage = new MinimalStorage("a.txt", Encoding.UTF8.GetBytes("hello")),
        });
        app.Get("/files/:key", ctx => ctx.File(ctx.Param("key")!));

        var response = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/files/a.txt",
        });

        Assert.Equal(200, response.Status);
        Assert.Equal("text/plain; charset=utf-8", response.ContentType);
        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task S3Storage_PresignGetAsync_GeneratesSigV4UrlWithResponseOverrides()
    {
        var fixedTime = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var s3 = CreateS3(fixedTime);

        var presigned = await s3.PresignGetAsync(
            "avatars/user_1.png",
            TimeSpan.FromMinutes(15),
            new PresignDownloadOptions
            {
                FileName = "profile.png",
                ContentType = "image/png",
                CacheControl = "public, max-age=60",
            });

        Assert.Equal("avatars/user_1.png", presigned.Key);
        Assert.Equal(900, presigned.ExpiresInSeconds);
        Assert.StartsWith(
            "https://acc123.r2.cloudflarestorage.com/media/avatars/user_1.png?",
            presigned.Url);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", presigned.Url);
        Assert.Contains("X-Amz-Date=20261025T120000Z", presigned.Url);
        Assert.Contains("X-Amz-Expires=900", presigned.Url);
        Assert.Contains("X-Amz-SignedHeaders=host", presigned.Url);
        Assert.Contains("response-content-type=image%2Fpng", presigned.Url);
        Assert.Contains("response-cache-control=public%2C%20max-age%3D60", presigned.Url);
        Assert.Contains("response-content-disposition=", presigned.Url);
        Assert.Contains("attachment", presigned.Url);
        Assert.Matches(@"X-Amz-Signature=[0-9a-f]{64}$", presigned.Url);
    }

    [Fact]
    public async Task S3Storage_HeadGetRangeAndList_UseBinaryTransportAndParseMetadata()
    {
        var http = new FakeHttpClient();
        var s3 = CreateS3(
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
            http);

        http.Handler = request =>
        {
            if (request.Method == "HEAD")
            {
                return new HttpBytesResponse(
                    200,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["content-length"] = SampleBytes.Length.ToString(),
                        ["content-type"] = "image/png",
                        ["etag"] = "\"abc123\"",
                        ["last-modified"] = "Sun, 25 Oct 2026 12:00:00 GMT",
                        ["cache-control"] = "public, max-age=60",
                        ["x-amz-meta-owner"] = "user_1",
                    },
                    []);
            }

            if (request.Headers.TryGetValue("range", out _))
            {
                return new HttpBytesResponse(
                    206,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["content-type"] = "image/png",
                        ["etag"] = "\"abc123\"",
                        ["content-range"] = $"bytes 2-5/{SampleBytes.Length}",
                    },
                    SampleBytes[2..6]);
            }

            if (request.Url.Contains("list-type=2", StringComparison.Ordinal))
            {
                const string xml = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <IsTruncated>true</IsTruncated>
                      <NextContinuationToken>next-page</NextContinuationToken>
                      <Contents>
                        <Key>avatars/a.png</Key>
                        <LastModified>2026-10-25T12:00:00.000Z</LastModified>
                        <ETag>"abc123"</ETag>
                        <Size>12</Size>
                      </Contents>
                    </ListBucketResult>
                    """;
                return new HttpBytesResponse(
                    200,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Encoding.UTF8.GetBytes(xml));
            }

            return new HttpBytesResponse(
                200,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["content-type"] = "application/octet-stream",
                },
                SampleBytes);
        };

        var head = await s3.HeadAsync("avatars/a.png");
        Assert.NotNull(head);
        Assert.Equal(SampleBytes.Length, head!.Size);
        Assert.Equal("image/png", head.ContentType);
        Assert.Equal("abc123", head.ETag);
        Assert.Equal("public, max-age=60", head.Metadata!["cache-control"]);
        Assert.Equal("user_1", head.Metadata["owner"]);

        var range = await s3.GetRangeAsync("avatars/a.png", 2, 4);
        Assert.NotNull(range);
        Assert.Equal(2, range!.Offset);
        Assert.Equal(SampleBytes.Length, range.TotalSize);
        Assert.Equal(SampleBytes[2..6], range.Data);

        var bytes = await s3.GetAsync("avatars/a.png");
        Assert.Equal(SampleBytes, bytes);

        var page = await s3.ListAsync(new StorageListOptions { Prefix = "avatars/" });
        Assert.Single(page.Items);
        Assert.Equal("avatars/a.png", page.Items[0].Key);
        Assert.Equal(SampleBytes.Length, page.Items[0].Size);
        Assert.Equal("abc123", page.Items[0].ETag);
        Assert.Equal("next-page", page.Cursor);
    }

    [Fact]
    public async Task S3Storage_PutAsync_SendsMetadataAndConditionalHeaders()
    {
        var http = new FakeHttpClient();
        var s3 = CreateS3(
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
            http);

        http.Handler = _ => new HttpBytesResponse(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            []);

        await s3.PutAsync(
            "avatars/a.png",
            SampleBytes,
            "image/png",
            new StoragePutOptions
            {
                Overwrite = false,
                CacheControl = "public, max-age=60",
                Metadata = new Dictionary<string, string> { ["owner"] = "user_1" },
            });

        var request = Assert.Single(http.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal(SampleBytes, request.Body);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal("public, max-age=60", request.Headers["cache-control"]);
        Assert.Equal("user_1", request.Headers["x-amz-meta-owner"]);
        Assert.Equal("*", request.Headers["if-none-match"]);
        Assert.True(request.Headers.ContainsKey("authorization"));
    }

    private static S3Storage CreateS3(
        DateTimeOffset now,
        IHttpClient? http = null) =>
        new(new S3StorageOptions
        {
            Endpoint = "https://acc123.r2.cloudflarestorage.com",
            Bucket = "media",
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            Region = "auto",
            Clock = () => now,
            HttpClient = http,
        });

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reptar-storage-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeHttpClient : IHttpClient
    {
        public Func<FakeRequest, HttpBytesResponse> Handler { get; set; } =
            _ => new HttpBytesResponse(
                200,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                []);

        public List<FakeRequest> Requests { get; } = [];

        public Task<HttpResponseData> FetchAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            string body = "") =>
            throw new NotSupportedException();

        public Task<HttpBytesResponse> FetchBytesAsync(
            string url,
            string method = "GET",
            Dictionary<string, string>? headers = null,
            byte[]? body = null,
            string? contentType = null)
        {
            var request = new FakeRequest(
                url,
                method,
                new Dictionary<string, string>(
                    headers ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase),
                body,
                contentType);
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    private sealed record FakeRequest(
        string Url,
        string Method,
        Dictionary<string, string> Headers,
        byte[]? Body,
        string? ContentType);

    private sealed class MinimalStorage(string key, byte[] body) : IStorage
    {
        public Task<string> PutAsync(string key, byte[] value, string contentType) =>
            Task.FromResult(key);

        public Task<byte[]?> GetAsync(string requestKey) =>
            Task.FromResult<byte[]?>(requestKey == key ? body : null);

        public Task<bool> ExistsAsync(string requestKey) =>
            Task.FromResult(requestKey == key);

        public Task DeleteAsync(string requestKey) => Task.CompletedTask;
    }
}
