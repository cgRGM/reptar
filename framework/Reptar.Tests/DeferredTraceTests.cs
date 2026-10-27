namespace Reptar.Tests;

public sealed class DeferredTraceTests
{
    [Fact]
    public async Task AmbientStateDoesNotRetainTheRequestContext()
    {
        var (state, request) = await CaptureStateWithoutRequest();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(request.TryGetTarget(out _));
        Assert.False(state.HasIdentifiers);
        using var scope = TraceContext.Begin(state.Capture());
        GC.KeepAlive(state);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(RequestTraceState State, WeakReference<Ctx> Request)> CaptureStateWithoutRequest()
    {
        RequestTraceState? state = null;
        WeakReference<Ctx>? request = null;
        var app = ReptarApp.Create();
        app.Get("/state", ctx =>
        {
            state = ctx.TraceState;
            request = new WeakReference<Ctx>(ctx);
            return ctx.Text("ok");
        });
        await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/state" });
        return (state!, request!);
    }

    [Fact]
    public async Task UnobservedRequestDoesNotGenerateTraceIdentifiers()
    {
        Ctx? captured = null;
        var app = ReptarApp.Create();
        app.Get("/plain", ctx =>
        {
            captured = ctx;
            Assert.False(ctx.HasTraceIdentifiers);
            return ctx.Text("ok");
        });

        var response = await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/plain" });

        Assert.Equal("ok", response.BodyText);
        Assert.NotNull(captured);
        Assert.False(captured.HasTraceIdentifiers);
    }

    [Fact]
    public async Task HandlerAndAmbientConsumersShareStableIdentifiers()
    {
        var app = ReptarApp.Create();
        app.Get("/trace", ctx =>
        {
            var traceId = ctx.TraceId;
            var spanId = ctx.SpanId;
            var ambient = TraceContext.Current;
            Assert.NotNull(ambient);
            Assert.Matches("^[0-9a-f]{32}$", traceId!);
            Assert.Matches("^[0-9a-f]{16}$", spanId!);
            Assert.Equal(traceId, ambient.Value.TraceId);
            Assert.Equal(spanId, ambient.Value.SpanId);
            Assert.Equal(ambient, TraceContext.Current);
            Assert.True(ctx.HasTraceIdentifiers);
            return ctx.Text("ok");
        });

        await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/trace" });
    }

    [Fact]
    public async Task IncomingUnsampledParentIsPreservedWhenFirstObserved()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string parentId = "00f067aa0ba902b7";
        var app = ReptarApp.Create();
        app.Get("/trace", ctx =>
        {
            Assert.False(ctx.HasTraceIdentifiers);
            Assert.Equal(traceId, ctx.TraceId);
            Assert.False(ctx.HasTraceIdentifiers);
            var ambient = TraceContext.Current!.Value;
            Assert.Equal(traceId, ambient.TraceId);
            Assert.Equal(parentId, ambient.ParentSpanId);
            Assert.NotEqual(parentId, ambient.SpanId);
            Assert.False(ambient.Sampled);
            Assert.True(ctx.HasTraceIdentifiers);
            return ctx.Text("ok");
        });

        await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/trace",
            Headers = new Dictionary<string, string> { ["traceparent"] = $"00-{traceId}-{parentId}-00" },
        });
    }

    [Fact]
    public async Task ParallelConsumersObserveOneRequestIdentity()
    {
        var app = ReptarApp.Create();
        app.Get("/parallel", async ctx =>
        {
            var consumers = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => TraceContext.Current!.Value));
            var contexts = await Task.WhenAll(consumers);
            foreach (var context in contexts)
            {
                Assert.Equal(ctx.TraceId, context.TraceId);
                Assert.Equal(ctx.SpanId, context.SpanId);
            }
            return ctx.Text("ok");
        });

        await app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/parallel" });
    }

    [Fact]
    public async Task ConcurrentRequestsStayIsolatedAndRestoreOuterScope()
    {
        var outer = new TraceContext(new string('a', 32), new string('b', 16), null, true);
        using var scope = TraceContext.Begin(outer);
        var app = ReptarApp.Create();
        app.Get("/trace", async ctx =>
        {
            await Task.Yield();
            var ambient = TraceContext.Current!.Value;
            Assert.Equal(ctx.TraceId, ambient.TraceId);
            Assert.Equal(ctx.SpanId, ambient.SpanId);
            Assert.NotEqual(outer.TraceId, ambient.TraceId);
            return ctx.Text($"{ambient.TraceId}/{ambient.SpanId}");
        });

        var responses = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => app.HandleAsync(new ReptarRequest { Method = "GET", Path = "/trace" })));

        Assert.Equal(32, responses.Select(response => response.BodyText).Distinct().Count());
        Assert.Equal(outer, TraceContext.Current);
    }
}
