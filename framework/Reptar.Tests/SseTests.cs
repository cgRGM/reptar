using System.Text;
using Reptar;

namespace Reptar.Tests;

public class SseTests
{
    [Fact]
    public async Task Sse_streams_formatted_events_and_data_chunks()
    {
        var app = ReptarApp.Create();

        app.Get("/api/ai/stream", ctx =>
        {
            return Task.FromResult(ctx.Sse(async stream =>
            {
                await stream.SendAsync("first token");
                await stream.SendJsonAsync(new { token = "second", index = 2 });
                await stream.SendAsync("final block", @event: "done", id: "tok_3");
                await stream.PingAsync("keepalive");
            }));
        });

        var res = await app.HandleAsync(new ReptarRequest
        {
            Method = "GET",
            Path = "/api/ai/stream"
        });

        Assert.Equal(200, res.Status);
        Assert.Equal("text/event-stream; charset=utf-8", res.ContentType);
        Assert.Contains("no-cache", res.Headers["Cache-Control"]);
        Assert.Equal("no", res.Headers["X-Accel-Buffering"]);
        Assert.NotNull(res.BodyStream);

        var chunks = new List<string>();
        await foreach (var bytes in res.BodyStream(CancellationToken.None))
        {
            chunks.Add(Encoding.UTF8.GetString(bytes));
        }

        var fullOutput = string.Join("", chunks);

        // Chunk 1: simple data
        Assert.Contains("data: first token\n\n", fullOutput);

        // Chunk 2: json data
        Assert.Contains("data: {\"token\":\"second\",\"index\":2}\n\n", fullOutput);

        // Chunk 3: event and id
        Assert.Contains("id: tok_3\n", fullOutput);
        Assert.Contains("event: done\n", fullOutput);
        Assert.Contains("data: final block\n\n", fullOutput);

        // Chunk 4: ping comment
        Assert.Contains(": keepalive\n\n", fullOutput);
    }
}
