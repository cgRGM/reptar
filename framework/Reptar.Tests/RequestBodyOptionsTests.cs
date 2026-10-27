using Xunit;

namespace Reptar.Tests;

public sealed class RequestBodyOptionsTests
{
    [Fact]
    public void BufferedAndUnmatchedRoutesRetainGlobalLimit()
    {
        var app = ReptarApp.Create(new ReptarConfig { MaxBodyBytes = 4096 });
        app.Get("/plain", ctx => ctx.Text("ok"));
        app.Put("/tasks/:id", ctx => Task.FromResult(ctx.Text("ok")));

        Assert.Equal((4096L, false), app.GetRequestBodyOptions("GET", "/plain"));
        Assert.Equal((4096L, false), app.GetRequestBodyOptions("PUT", "/tasks/one"));
        Assert.Equal((4096L, false), app.GetRequestBodyOptions("POST", "/missing"));
    }

    [Fact]
    public void ParameterizedRouteRetainsItsBodyLimit()
    {
        var app = ReptarApp.Create(new ReptarConfig { MaxBodyBytes = 4096 });
        app.Put("/tasks/:id", ctx => Task.FromResult(ctx.Text("ok"))).WithMaxBody(256);

        Assert.Equal((256L, false), app.GetRequestBodyOptions("PUT", "/tasks/one"));
        Assert.Equal(256L, app.GetMaxBodyBytes("PUT", "/tasks/one"));
        Assert.False(app.IsStreamingBody("PUT", "/tasks/one"));
    }

    [Fact]
    public void CatchAllStreamingRouteRetainsItsLimitAndMethodIsolation()
    {
        var app = ReptarApp.Create(new ReptarConfig { MaxBodyBytes = 4096 });
        app.Post("/files/*path", ctx => ctx.Text("ok")).StreamBody(1024);

        Assert.Equal((1024L, true), app.GetRequestBodyOptions("POST", "/files/a/b"));
        Assert.True(app.IsStreamingBody("POST", "/files/a/b"));
        Assert.Equal((4096L, false), app.GetRequestBodyOptions("GET", "/files/a/b"));
    }

    [Fact]
    public void HeadFallbackRetainsUnlimitedStreamingPolicy()
    {
        var app = ReptarApp.Create(new ReptarConfig { MaxBodyBytes = 4096 });
        app.Get("/stream/:id", ctx => ctx.Text("ok")).StreamBody();

        Assert.Equal((0L, true), app.GetRequestBodyOptions("HEAD", "/stream/one"));
        Assert.Equal((0L, true), app.GetRequestBodyOptions("GET", "/stream/one"));
    }
}
