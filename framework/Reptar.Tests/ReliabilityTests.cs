namespace Reptar.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task Timeout_middleware_returns_504_for_slow_handler()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Use(ReptarApi.Timeout(TimeSpan.FromMilliseconds(20)));
        app.Get("/slow", async ctx =>
        {
            await Task.Delay(250, ctx.RequestAborted);
            return ctx.Text("done");
        });

        var response = await app.CreateTestClient().GetAsync("/slow");

        Assert.Equal(504, response.Status);
        Assert.Contains("request_timeout", response.BodyText);
    }

    private sealed class RecordingModule : IReptarModule
    {
        public List<string> Events { get; } = [];

        public void Configure(ReptarApp app) => Events.Add("configure");

        public Task StartAsync(ReptarApp app, CancellationToken cancellationToken)
        {
            Events.Add("start");
            return Task.CompletedTask;
        }

        public Task StopAsync(ReptarApp app, CancellationToken cancellationToken)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleWithBackgroundAsync_returns_response_and_background_task()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        var backgroundRan = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        app.Get("/background", ctx =>
        {
            ctx.WaitUntil(async () =>
            {
                await Task.Yield();
                backgroundRan.TrySetResult();
            });

            return Task.FromResult(ctx.Text("ok"));
        });

        var result = await app.HandleWithBackgroundAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/background",
        });

        Assert.Equal(200, result.Response.Status);
        Assert.False(backgroundRan.Task.IsCompleted);

        await result.Background;
        await backgroundRan.Task;
    }

    [Fact]
    public async Task Modules_configure_start_and_stop_in_order()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        var module = new RecordingModule();

        app.AddModule(module);
        Assert.Equal(new[] { "configure" }, module.Events);

        await app.StartModulesAsync();
        await app.StopModulesAsync();

        Assert.Equal(new[] { "configure", "start", "stop" }, module.Events);
    }
}
