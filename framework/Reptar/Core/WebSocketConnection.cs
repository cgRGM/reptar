namespace Reptar;

public sealed class WebSocketConnection
{
    internal WebSocketConnection(string id, Ctx ctx)
    {
        Id = id;
        Ctx = ctx;
    }

    public string Id { get; }
    public Ctx Ctx { get; }

    public Task SendAsync(string message)
    {
        Ctx.WebSocket.Send(Id, message);
        return Task.CompletedTask;
    }

    public Task CloseAsync(int code = 1000, string reason = "")
    {
        Ctx.WebSocket.Close(Id, code, reason);
        return Task.CompletedTask;
    }
}

public sealed class WebSocketHandlers
{
    public Func<WebSocketConnection, Task>? Open { get; init; }
    public Func<WebSocketConnection, string, Task>? Message { get; init; }
    public Func<WebSocketConnection, Task>? Close { get; init; }
}
