using System.Net;

namespace Reptar.Tests;

public sealed class OtlpExporterTests
{
    [Fact]
    public async Task Exporter_posts_otlp_log_payload()
    {
        var handler = new CapturingHandler();
        var exporter = new OtlpExporter(
            new OtlpExporterOptions
            {
                Endpoint = "http://collector/v1/logs",
                ServiceName = "reptar-test",
                BatchSize = 64,
                FlushInterval = TimeSpan.FromHours(1),
            },
            handler);

        exporter.Record(new RequestTelemetry(
            "req-1",
            "4bf92f3577b34da6a3ce929d0e0e4736",
            "00f067aa0ba902b7",
            null,
            "GET",
            "/api/items",
            "/api/items",
            200,
            12.5,
            1.5,
            2,
            "HIT",
            "user-1",
            null,
            null,
            null,
            false));

        await exporter.FlushAsync();

        Assert.NotNull(handler.Body);
        Assert.Contains("\"resourceLogs\"", handler.Body);
        Assert.Contains("\"service.name\"", handler.Body);
        Assert.Contains("reptar-test", handler.Body);
        Assert.Contains("\"http.route\"", handler.Body);
        Assert.Contains("/api/items", handler.Body);
        Assert.Contains("4bf92f3577b34da6a3ce929d0e0e4736", handler.Body);

        await exporter.DisposeAsync();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
