namespace Reptar.Tests;

public sealed class TelemetryTests
{
    private sealed class RecordingLogger : IAppLogger
    {
        public List<RequestTelemetry> Requests { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message) { }
        public void Request(in RequestTelemetry telemetry) => Requests.Add(telemetry);
    }

    [Fact]
    public async Task Telemetry_logs_structured_request_with_db_timing_and_server_timing()
    {
        var logger = new RecordingLogger();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Log = logger,
        });

        app.Use(ReptarApi.RequestId());
        app.Use(ReptarApi.Telemetry(new TelemetryOptions
        {
            SampleRate = 1,
            SlowRequestMs = 0,
        }));

        app.Get("/hello", async ctx =>
        {
            await ctx.Db.ExecuteAsync("select 1");
            return ctx.JsonRaw("""{"ok":true}""");
        });

        var client = app.CreateTestClient();
        var response = await client.GetAsync(
            "/hello",
            new Dictionary<string, string>
            {
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            });

        Assert.Equal(200, response.Status);
        Assert.True(logger.Requests.Count == 1);

        var telemetry = logger.Requests[0];
        Assert.Equal("GET", telemetry.Method);
        Assert.Equal("/hello", telemetry.Route);
        Assert.Equal(200, telemetry.Status);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", telemetry.TraceId);
        Assert.Equal("00f067aa0ba902b7", telemetry.ParentSpanId);
        Assert.Equal(16, telemetry.SpanId?.Length);
        Assert.True(telemetry.DbQueries >= 1);
        Assert.True(telemetry.DbMs >= 0);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.RequestId));

        Assert.True(response.Headers.TryGetValue("Server-Timing", out var timing));
        Assert.Contains("db;", timing);
        Assert.Contains("total;", timing);
        // RequestId and Telemetry must compose into one header instead of
        // the PendingHeaders merge dropping the outer middleware's entry.
        Assert.Contains("reptar;", timing);
    }

    [Fact]
    public async Task RequestId_uses_the_host_supplied_id()
    {
        var app = ReptarApp.Create(new ReptarConfig());
        app.Use(ReptarApi.RequestId());
        app.Get("/id", ctx => ctx.Text(ctx.RequestId));

        var response = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/id",
            RequestId = "req_host",
        });

        Assert.Equal("req_host", response.BodyText);
        Assert.Equal("req_host", response.Headers["X-Request-Id"]);
    }

    [Fact]
    public async Task Telemetry_sample_rate_zero_skips_successful_requests()
    {
        var logger = new RecordingLogger();
        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = Db.Sqlite(":memory:"),
            Log = logger,
        });

        app.Use(ReptarApi.Telemetry(new TelemetryOptions
        {
            SampleRate = 0,
            SlowRequestMs = 0,
        }));

        app.Get("/ok", ctx => ctx.JsonRaw("""{"ok":true}"""));

        var client = app.CreateTestClient();
        var response = await client.GetAsync("/ok");

        Assert.Equal(200, response.Status);
        Assert.Empty(logger.Requests);
    }
}
