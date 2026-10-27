using Reptar;

namespace Reptar.Tests;

public sealed class BodylessResponseTests
{
    [Theory]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public async Task Bodyless_status_discards_buffered_payload_without_changing_status_or_cookies(int status)
    {
        var app = ReptarApp.Create();
        app.Get("/empty", ctx =>
        {
            var response = ctx.Text("must-not-be-sent", status);
            response.Headers["Content-Length"] = "999";
            response.Headers["ETag"] = "probe";
            response.SetCookies.Add("probe=value; HttpOnly");
            return response;
        });
        var result = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/empty" });
        Assert.Equal(status, result.Status);
        Assert.Null(result.Body);
        Assert.Null(result.BodyStream);
        Assert.Equal("probe", result.Headers["ETag"]);
        Assert.Single(result.SetCookies);
        if (status == 204)
            Assert.False(result.Headers.ContainsKey("Content-Length"));
        if (status == 205)
            Assert.Equal("0", result.Headers["Content-Length"]);
        if (status == 304)
            Assert.Equal("999", result.Headers["Content-Length"]); // Representation length is permitted on 304.
    }

    [Theory]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public async Task Bodyless_status_never_enumerates_a_stream(int status)
    {
        var app = ReptarApp.Create();
        app.Get("/empty", ctx => ctx.Stream(_ => throw new InvalidOperationException("must not enumerate"), "text/plain", status));
        var result = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/empty" });
        Assert.Equal(status, result.Status);
        Assert.Null(result.BodyStream);
        Assert.Null(result.Body);
    }
}
