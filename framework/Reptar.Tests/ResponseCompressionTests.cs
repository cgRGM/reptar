using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reptar.Hosting;

namespace Reptar.Tests;

public sealed class ResponseCompressionTests
{
    [Theory]
    [InlineData(0L, false)]
    [InlineData(13L, false)]
    [InlineData(1023L, false)]
    [InlineData(1024L, true)]
    [InlineData(4096L, true)]
    [InlineData(null, true)]
    public void CompressionRequiresUsefulKnownSizeOrAnUnknownLength(long? length, bool expected)
    {
        using var services = new ServiceCollection().AddLogging().AddResponseCompression().BuildServiceProvider();
        var provider = CreateProvider(services);
        var context = new DefaultHttpContext();
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = length;

        Assert.Equal(expected, provider.ShouldCompressResponse(context));
    }

    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("image/png")]
    public void NonCompressibleResponsesRetainStandardPolicy(string contentType)
    {
        using var services = new ServiceCollection().AddLogging().AddResponseCompression().BuildServiceProvider();
        var provider = CreateProvider(services);
        var context = new DefaultHttpContext();
        context.Response.ContentType = contentType;
        context.Response.ContentLength = 4096;

        Assert.False(provider.ShouldCompressResponse(context));
    }

    [Fact]
    public void AlreadyEncodedResponsesAreNotCompressedAgain()
    {
        using var services = new ServiceCollection().AddLogging().AddResponseCompression().BuildServiceProvider();
        var provider = CreateProvider(services);
        var context = new DefaultHttpContext();
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = 4096;
        context.Response.Headers.ContentEncoding = "gzip";

        Assert.False(provider.ShouldCompressResponse(context));
    }

    [Theory]
    [InlineData("br", 13, false)]
    [InlineData("gzip", 13, false)]
    [InlineData("br", 4096, true)]
    [InlineData("gzip", 4096, true)]
    public async Task NegotiatedCompressionRoundTripsWithoutCompressingTinyBodies(
        string encoding,
        int length,
        bool expectedCompression)
    {
        using var services = new ServiceCollection().AddLogging().AddResponseCompression().BuildServiceProvider();
        using var output = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptEncoding = encoding;
        context.Response.Body = output;
        var payload = Encoding.UTF8.GetBytes(new string('x', length));
        var middleware = new ResponseCompressionMiddleware(async http =>
        {
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = payload.Length;
            await http.Response.Body.WriteAsync(payload);
        }, CreateProvider(services));

        await middleware.Invoke(context);

        if (!expectedCompression)
        {
            Assert.True(string.IsNullOrEmpty(context.Response.Headers.ContentEncoding));
            Assert.Equal(payload, output.ToArray());
            return;
        }

        Assert.Equal(encoding, context.Response.Headers.ContentEncoding.ToString());
        Assert.True(output.Length < payload.Length);
        output.Position = 0;
        using Stream decoder = encoding == "br"
            ? new BrotliStream(output, CompressionMode.Decompress, leaveOpen: true)
            : new GZipStream(output, CompressionMode.Decompress, leaveOpen: true);
        using var decoded = new MemoryStream();
        await decoder.CopyToAsync(decoded);
        Assert.Equal(payload, decoded.ToArray());
    }

    private static ReptarResponseCompressionProvider CreateProvider(IServiceProvider services) =>
        new(services, Options.Create(new ResponseCompressionOptions
        {
            EnableForHttps = true,
            MimeTypes = ["application/json", "text/plain"],
        }));
}
