using System.Collections.Concurrent;

namespace Reptar.Realtime;

public sealed class ActionCableServer
{
    private sealed record Subscription(WebSocketConnection Connection, string Identifier);

    // streamName -> (connectionId -> Subscription)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Subscription>> _streams = new(StringComparer.OrdinalIgnoreCase);

    // connectionId -> set of streamNames
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionStreams = new(StringComparer.OrdinalIgnoreCase);

    // connectionId -> active WebSocketConnection
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    private readonly IJsonCodec _json;

    public ActionCableServer(IJsonCodec? json = null)
    {
        _json = json ?? new DefaultJsonCodec();
    }

    public Func<WebSocketConnection, ActionCableCommand, Task<bool>>? AuthorizeSubscription { get; set; }
    public Func<WebSocketConnection, ActionCableCommand, Task>? OnSubscribed { get; set; }
    public Func<WebSocketConnection, ActionCableCommand, Task>? OnUnsubscribed { get; set; }
    public Func<WebSocketConnection, ActionCableCommand, Task>? OnAction { get; set; }

    public int TotalConnections => _connections.Count;

    public int GetSubscriberCount(string streamName) =>
        _streams.TryGetValue(streamName, out var subs) ? subs.Count : 0;

    public async Task HandleOpenAsync(WebSocketConnection conn)
    {
        _connections[conn.Id] = conn;
        await conn.SendAsync(ActionCableProtocol.Welcome(conn.Id));
    }

    public async Task HandleMessageAsync(WebSocketConnection conn, string rawMessage)
    {
        var command = ActionCableProtocol.ParseClientMessage(rawMessage);
        if (command is null)
        {
            return;
        }

        switch (command.Command.ToLowerInvariant())
        {
            case ActionCableProtocol.CommandSubscribe:
                await HandleSubscribeAsync(conn, command);
                break;

            case ActionCableProtocol.CommandUnsubscribe:
                await HandleUnsubscribeAsync(conn, command);
                break;

            case ActionCableProtocol.CommandMessage:
                if (OnAction is not null)
                {
                    await OnAction(conn, command);
                }
                break;

            case "ping":
                await conn.SendAsync(ActionCableProtocol.Ping());
                break;
        }
    }

    public Task HandleCloseAsync(WebSocketConnection conn)
    {
        _connections.TryRemove(conn.Id, out _);

        if (_connectionStreams.TryRemove(conn.Id, out var streams))
        {
            foreach (var streamName in streams.Keys)
            {
                if (_streams.TryGetValue(streamName, out var subs))
                {
                    subs.TryRemove(conn.Id, out _);
                    if (subs.IsEmpty)
                    {
                        _streams.TryRemove(streamName, out _);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleSubscribeAsync(WebSocketConnection conn, ActionCableCommand cmd)
    {
        if (AuthorizeSubscription is not null)
        {
            var authorized = await AuthorizeSubscription(conn, cmd);
            if (!authorized)
            {
                await conn.SendAsync(ActionCableProtocol.RejectSubscription(cmd.Identifier));
                return;
            }
        }

        var streamSubs = _streams.GetOrAdd(cmd.StreamName, _ => new ConcurrentDictionary<string, Subscription>(StringComparer.OrdinalIgnoreCase));
        streamSubs[conn.Id] = new Subscription(conn, cmd.Identifier);

        var connStreams = _connectionStreams.GetOrAdd(conn.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        connStreams[cmd.StreamName] = 0;

        await conn.SendAsync(ActionCableProtocol.ConfirmSubscription(cmd.Identifier));

        if (OnSubscribed is not null)
        {
            await OnSubscribed(conn, cmd);
        }
    }

    private async Task HandleUnsubscribeAsync(WebSocketConnection conn, ActionCableCommand cmd)
    {
        if (_streams.TryGetValue(cmd.StreamName, out var streamSubs))
        {
            streamSubs.TryRemove(conn.Id, out _);
            if (streamSubs.IsEmpty)
            {
                _streams.TryRemove(cmd.StreamName, out _);
            }
        }

        if (_connectionStreams.TryGetValue(conn.Id, out var connStreams))
        {
            connStreams.TryRemove(cmd.StreamName, out _);
        }

        if (OnUnsubscribed is not null)
        {
            await OnUnsubscribed(conn, cmd);
        }
    }

    public async Task<int> BroadcastAsync(string streamName, object message)
    {
        if (!_streams.TryGetValue(streamName, out var subscribers) || subscribers.IsEmpty)
        {
            return 0;
        }

        var count = 0;
        string? commonIdentifier = null;
        string? cachedPayload = null;

        foreach (var sub in subscribers.Values)
        {
            try
            {
                if (commonIdentifier is null)
                {
                    commonIdentifier = sub.Identifier;
                    cachedPayload = ActionCableProtocol.Broadcast(sub.Identifier, message, _json);
                    await sub.Connection.SendAsync(cachedPayload);
                }
                else if (string.Equals(commonIdentifier, sub.Identifier, StringComparison.Ordinal))
                {
                    await sub.Connection.SendAsync(cachedPayload!);
                }
                else
                {
                    var individualPayload = ActionCableProtocol.Broadcast(sub.Identifier, message, _json);
                    await sub.Connection.SendAsync(individualPayload);
                }
                count++;
            }
            catch
            {
                // Disconnected connection will be cleaned up on close
            }
        }

        return count;
    }

    public async Task<int> BroadcastTurboStreamAsync(string streamName, string action, string target, string htmlContent)
    {
        var turboStreamHtml = action.ToLowerInvariant() switch
        {
            "append" => TurboStream.Append(target, htmlContent),
            "prepend" => TurboStream.Prepend(target, htmlContent),
            "replace" => TurboStream.Replace(target, htmlContent),
            "update" => TurboStream.Update(target, htmlContent),
            "remove" => TurboStream.Remove(target),
            "before" => TurboStream.Before(target, htmlContent),
            "after" => TurboStream.After(target, htmlContent),
            "morph" => TurboStream.Morph(target, htmlContent),
            _ => $"<turbo-stream action=\"{action}\" target=\"{target}\"><template>{htmlContent}</template></turbo-stream>"
        };

        return await BroadcastAsync(streamName, turboStreamHtml);
    }

    public WebSocketHandlers CreateHandlers() => new()
    {
        Open = HandleOpenAsync,
        Message = (conn, msg) => HandleMessageAsync(conn, msg),
        Close = HandleCloseAsync
    };
}
