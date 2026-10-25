#if !NETWASM
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Reptar;

public readonly struct SseMessage
{
    public string Data { get; init; }
    public string? Event { get; init; }
    public string? Id { get; init; }
    public int? Retry { get; init; }

    public SseMessage(string data, string? @event = null, string? id = null, int? retry = null)
    {
        Data = data;
        Event = @event;
        Id = id;
        Retry = retry;
    }
}

public sealed class SseStream
{
    private readonly ChannelWriter<byte[]> _writer;
    private readonly IJsonCodec _json;

    internal SseStream(ChannelWriter<byte[]> writer, IJsonCodec json)
    {
        _writer = writer;
        _json = json;
    }

    public async ValueTask SendAsync(
        string data,
        string? @event = null,
        string? id = null,
        int? retry = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(id))
            sb.Append("id: ").Append(id).Append('\n');

        if (!string.IsNullOrEmpty(@event))
            sb.Append("event: ").Append(@event).Append('\n');

        if (retry.HasValue)
            sb.Append("retry: ").Append(retry.Value).Append('\n');

        // Multi-line data support per SSE spec
        foreach (var line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }

        sb.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await _writer.WriteAsync(bytes, cancellationToken);
    }

    public ValueTask SendJsonAsync<T>(
        T data,
        string? @event = null,
        string? id = null,
        int? retry = null,
        CancellationToken cancellationToken = default)
    {
        var json = _json.Serialize(data);
        return SendAsync(json, @event, id, retry, cancellationToken);
    }

    public ValueTask SendAsync(SseMessage message, CancellationToken cancellationToken = default) =>
        SendAsync(message.Data, message.Event, message.Id, message.Retry, cancellationToken);

    public async ValueTask PingAsync(string comment = "ping", CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes($": {comment}\n\n");
        await _writer.WriteAsync(bytes, cancellationToken);
    }
}

public static class SseExtensions
{
    public static ReptarResponse Sse(this Ctx ctx, Func<SseStream, Task> producer) =>
        ctx.Sse((stream, _) => producer(stream));

    public static ReptarResponse Sse(this Ctx ctx, Func<SseStream, CancellationToken, Task> producer)
    {
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var sseStream = new SseStream(channel.Writer, ctx.JsonCodec);

        var res = new ReptarResponse
        {
            Status = 200,
            ContentType = "text/event-stream; charset=utf-8",
            BodyStream = cancellationToken =>
            {
                // Run the producer in the background and pipe chunks into channel
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await producer(sseStream, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Client aborted connection
                    }
                    catch (Exception ex)
                    {
                        ctx.Log.Error($"SSE producer exception: {ex.Message}");
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                }, cancellationToken);

                return channel.Reader.ReadAllAsync(cancellationToken);
            }
        };

        res.Headers["Cache-Control"] = "no-cache, no-transform";
        res.Headers["Connection"] = "keep-alive";
        res.Headers["X-Accel-Buffering"] = "no";

        return res;
    }
}
#endif
