// ---------------------------------------------------------------------------
// Reptar Rooms hub — shared by the file-based local app (via #:include) and
// the NetWasm worker build (via <Compile Include>). Patterns ported from
// PartyKit (room roster + broadcast-except), y-websocket (ephemeral presence /
// awareness), Liveblocks (join snapshot + peer_joined/peer_left deltas), and
// uWebSockets (serialize-once fanout over per-socket bounded send channels).
// ---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
#if !NETWASM
using System.Threading.Channels;
#endif
using System.Threading.Tasks;
using Reptar;

public sealed class RoomHub
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Peer> _connections = new(StringComparer.Ordinal);
    private readonly RoomMessagePersister _persister;
    private readonly long _startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private long _messagesRelayed;
    private long _presenceUpdatesRelayed;
    private long _fanoutFailures;
    private long _joins;
    private long _leaves;

    public RoomHub(RoomMessagePersister persister)
    {
        _persister = persister;
    }

    public WebSocketHandlers CreateHandlers() => new()
    {
        Open = HandleOpenAsync,
        Message = HandleMessageAsync,
        Close = HandleCloseAsync,
    };

    // Snapshot payloads are hand-rolled strings (not anonymous objects) so the
    // same code is Native-AOT/NetWasm-safe without a JsonSerializerContext.

    public string HealthJson() =>
        $"{{\"ok\":true,\"rooms\":{_rooms.Count},\"activeConnections\":{_connections.Count}}}";

    public string StatsJson()
    {
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{{\"rooms\":{_rooms.Count},\"activeConnections\":{_connections.Count}" +
               $",\"joins\":{Interlocked.Read(ref _joins)},\"leaves\":{Interlocked.Read(ref _leaves)}" +
               $",\"messagesRelayed\":{Interlocked.Read(ref _messagesRelayed)}" +
               $",\"presenceUpdatesRelayed\":{Interlocked.Read(ref _presenceUpdatesRelayed)}" +
               $",\"messagesPersisted\":{_persister.PersistedCount},\"messagesPersistFailed\":{_persister.FailedCount},\"lastPersistError\":{_persister.LastErrorJson},\"persisterQueueDepth\":{_persister.QueueDepth}" +
               $",\"fanoutFailures\":{Interlocked.Read(ref _fanoutFailures)}" +
               $",\"fanoutAttempts\":{Interlocked.Read(ref _fanoutAttempts)}" +
               $",\"fanoutDelivered\":{Interlocked.Read(ref _fanoutDelivered)}" +
               $",\"lastFanoutError\":{RoomProtocol.Escape(Volatile.Read(ref _lastFanoutError) ?? "null")}" +
               $",\"uptimeSeconds\":{(nowUnixMs - _startedAtUnixMs) / 1000},\"process\":{ProcessInfo.Json()}" +
               "}";
    }

    public string RoomsJson()
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append('[');
        var first = true;
        foreach (var (roomId, room) in _rooms)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append("{\"roomId\":").Append(RoomProtocol.Escape(roomId))
              .Append(",\"peers\":").Append(room.Peers.Count).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    public string PeersJson(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return "[]";
        }

        var sb = new System.Text.StringBuilder(256);
        sb.Append('[');
        var first = true;
        foreach (var peer in room.Peers.Values)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            RoomProtocol.AppendPeer(sb, peer);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Fanout a message into a room without a live socket (REST trigger).</summary>
    public int BroadcastFromServer(string roomId, string senderId, string payloadJson)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return 0;
        }

        var frame = RoomProtocol.Message(
            messageId: Ids.New("msg"),
            roomId: roomId,
            connectionId: null,
            senderId: senderId,
            sentAt: DateTimeOffset.UtcNow.ToString("O"),
            payloadJson: payloadJson);

        var delivered = Fanout(room, frame, exceptConnectionId: null);
        Interlocked.Increment(ref _messagesRelayed);
        _ = _persister.PersistAsync(roomId, senderId, payloadJson);
        return delivered;
    }

    private async Task HandleOpenAsync(WebSocketConnection connection)
    {
        var roomId = connection.Ctx.Req.Param("roomId");
        if (string.IsNullOrWhiteSpace(roomId) || roomId.Length > RoomLimits.MaxRoomIdLength)
        {
            await connection.SendAsync(RoomProtocol.Error("invalid_room", "Missing or over-long room id."));
            await connection.CloseAsync(1008, "Invalid room id.");
            return;
        }

        _connections[connection.Id] = new Peer
        {
            Connection = connection,
            RoomId = roomId,
        };

        await connection.SendAsync(RoomProtocol.Connected(roomId, connection.Id));
    }

    private async Task HandleMessageAsync(WebSocketConnection connection, string raw)
    {
        if (raw.Length > RoomLimits.MaxInboundMessageBytes)
        {
            await connection.SendAsync(RoomProtocol.Error("message_too_large", "Inbound frame exceeds 64 KiB."));
            return;
        }

        if (!_connections.TryGetValue(connection.Id, out var peer))
        {
            return; // socket raced its own close; drop
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            await connection.SendAsync(RoomProtocol.Error("invalid_json", "Frame must be valid JSON."));
            return;
        }

        using (doc)
        {
            var root = doc!.RootElement;
            if (root.ValueKind is not JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind is not JsonValueKind.String)
            {
                await connection.SendAsync(RoomProtocol.Error("invalid_frame", "Frame needs a string \"type\"."));
                return;
            }

            switch (typeElement.GetString())
            {
                case "join":
                    await HandleJoinAsync(peer, root);
                    return;
                case "presence":
                    await HandlePresenceAsync(peer, root);
                    return;
                case "broadcast":
                    await HandleBroadcastAsync(peer, root);
                    return;
                case "leave":
                    await HandleLeaveAsync(peer);
                    return;
                case "ping":
                    await connection.SendAsync(RoomProtocol.Pong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    return;
                default:
                    await connection.SendAsync(RoomProtocol.Error("unknown_type", "Supported: join, presence, broadcast, leave, ping."));
                    return;
            }
        }
    }

    private async Task HandleJoinAsync(Peer peer, JsonElement root)
    {
        if (peer.Joined)
        {
            await peer.Connection.SendAsync(RoomProtocol.Error("already_joined", "This connection already joined the room."));
            return;
        }

        var room = GetOrAddRoom(peer.RoomId);
        string rosterFrame;
        string deltaFrame;

        lock (room.Gate)
        {
            peer.UserId = GetString(root, "userId") ?? $"anon_{peer.Connection.Id[..Math.Min(8, peer.Connection.Id.Length)]}";
            peer.Name = GetString(root, "name") ?? "Anonymous";
            peer.Avatar = GetString(root, "avatar");
            peer.Joined = true;
            Interlocked.Increment(ref _joins);

            room.Peers[peer.Connection.Id] = peer;

            // Liveblocks pattern: the joiner receives a full roster snapshot
            // (including itself) so it can render "others" immediately.
            rosterFrame = RoomProtocol.Joined(peer.RoomId, peer.Connection.Id, room);
            deltaFrame = RoomProtocol.PeerJoined(peer);
        }

        _ = Fanout(room, deltaFrame, exceptConnectionId: peer.Connection.Id);
        await peer.Connection.SendAsync(rosterFrame);
    }

    private async Task HandlePresenceAsync(Peer peer, JsonElement root)
    {
        if (!peer.Joined)
        {
            await peer.Connection.SendAsync(RoomProtocol.Error("not_joined", "Send \"join\" before presence updates."));
            return;
        }

        if (root.TryGetProperty("x", out var xElement) && xElement.ValueKind == JsonValueKind.Number)
        {
            peer.CursorX = xElement.GetDouble();
        }

        if (root.TryGetProperty("y", out var yElement) && yElement.ValueKind == JsonValueKind.Number)
        {
            peer.CursorY = yElement.GetDouble();
        }

        if (root.TryGetProperty("typing", out var typingElement) &&
            typingElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            peer.Typing = typingElement.GetBoolean();
        }

        if (!_rooms.TryGetValue(peer.RoomId, out var room))
        {
            return;
        }

        // Ephemeral by design (y-websocket awareness): presence is fanned out
        // to everyone else in the room and never persisted.
        var delivered = Fanout(room, RoomProtocol.Presence(peer), exceptConnectionId: peer.Connection.Id);
        Interlocked.Add(ref _presenceUpdatesRelayed, delivered);
    }

    private async Task HandleBroadcastAsync(Peer peer, JsonElement root)
    {
        if (!peer.Joined)
        {
            await peer.Connection.SendAsync(RoomProtocol.Error("not_joined", "Send \"join\" before broadcasting."));
            return;
        }

        var hasPayload = root.TryGetProperty("payload", out var payloadElement) &&
            payloadElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

        if (!hasPayload)
        {
            await peer.Connection.SendAsync(RoomProtocol.Error("invalid_payload", "\"payload\" must be a JSON object or array."));
            return;
        }

        if (!_rooms.TryGetValue(peer.RoomId, out var room))
        {
            return;
        }

        var payloadJson = payloadElement.GetRawText(); // no re-serialization of arbitrary payloads
        var frame = RoomProtocol.Message(
            messageId: Ids.New("msg"),
            roomId: peer.RoomId,
            connectionId: peer.Connection.Id,
            senderId: peer.UserId,
            sentAt: DateTimeOffset.UtcNow.ToString("O"),
            payloadJson: payloadJson);

        // Fan out first (including the sender, so it gets a durable id/ack);
        // persistence is write-behind locally (SQLite group-commit) and
        // inline via the D1 binding under NetWasm, where the isolate freezes
        // between events.
        _ = Fanout(room, frame, exceptConnectionId: null);
        Interlocked.Increment(ref _messagesRelayed);

        await _persister.PersistAsync(peer.RoomId, peer.UserId, payloadJson);
    }

    private async Task HandleLeaveAsync(Peer peer)
    {
        RemovePeer(peer);
        Interlocked.Increment(ref _leaves);
        await peer.Connection.SendAsync(RoomProtocol.Bye(peer.Connection.Id));
        await peer.Connection.CloseAsync(1000, "leave");
    }

    private Task HandleCloseAsync(WebSocketConnection connection)
    {
        if (_connections.TryRemove(connection.Id, out var peer))
        {
            RemovePeer(peer);
        }

        return Task.CompletedTask;
    }

    private void RemovePeer(Peer peer)
    {
        if (!_rooms.TryGetValue(peer.RoomId, out var room))
        {
            return;
        }

        lock (room.Gate)
        {
            room.Peers.TryRemove(peer.Connection.Id, out _);

            if (room.Peers.IsEmpty)
            {
                // Only reap the room if it is still the registered instance
                // (a concurrent join re-adds its own Room on GetOrAdd miss).
                if (_rooms.TryGetValue(peer.RoomId, out var current) && ReferenceEquals(current, room))
                {
                    _rooms.TryRemove(peer.RoomId, out _);
                }
            }
            else if (peer.Joined)
            {
                // Notify the remaining members (Liveblocks peer_left delta).
                _ = Fanout(room, RoomProtocol.PeerLeft(peer.Connection.Id, peer.UserId), exceptConnectionId: null);
            }
        }
    }

    private Room GetOrAddRoom(string roomId) =>
        _rooms.GetOrAdd(roomId, _ => new Room());

    /// <summary>
    /// Serialize once, enqueue per socket (uWebSockets corking analog). The
    /// per-socket send channels are bounded; a saturated peer's frame is
    /// dropped by the transport rather than stalling the fanout loop.
    /// </summary>
    private long _fanoutAttempts;
    private long _fanoutDelivered;
    private string? _lastFanoutError;

    private int Fanout(Room room, string frame, string? exceptConnectionId)
    {
        var delivered = 0;
        foreach (var (connectionId, target) in room.Peers)
        {
            if (exceptConnectionId is not null && connectionId == exceptConnectionId)
            {
                continue;
            }

            Interlocked.Increment(ref _fanoutAttempts);
            try
            {
                target.Connection.SendAsync(frame);
                delivered++;
                Interlocked.Increment(ref _fanoutDelivered);
            }
            catch (System.Exception exception)
            {
                // Disconnected connection is cleaned up by its close handler.
                Interlocked.Increment(ref _fanoutFailures);
                Volatile.Write(ref _lastFanoutError, $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return delivered;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Hub-level guards. Top-level-statement locals are not visible to types
    /// declared later in a file-based program, so these live here.
    /// </summary>
    internal static class RoomLimits
    {
        public const int MaxInboundMessageBytes = 64 * 1024; // transport accepts any size; guard the hub

        public const int MaxRoomIdLength = 128;
    }
}

/// <summary>
/// Route + WebSocket wiring shared by the local file-based app (app.cs) and
/// the NetWasm worker Program.cs, so both hosts expose identical surfaces.
/// </summary>
public static class RoomsRoutes
{
    public static ReptarApp Configure(ReptarApp app, RoomHub hub)
    {
        app.OnWebSocket("/rooms/{roomId}", hub.CreateHandlers());

        app.Get("/health", ctx => ctx.JsonRaw(hub.HealthJson()));

        app.Get("/stats", ctx => ctx.JsonRaw(hub.StatsJson()));

        app.Get("/rooms", ctx => ctx.JsonRaw(hub.RoomsJson()));

        app.Get("/rooms/:roomId/peers", ctx =>
        {
            var roomId = ctx.Req.Param("roomId")!;
            return ctx.JsonRaw(hub.PeersJson(roomId));
        });

        app.Get("/rooms/:roomId/messages", async ctx =>
        {
            var roomId = ctx.Req.Param("roomId")!;
            var limit = int.TryParse(ctx.Req.QueryValue("limit"), out var parsed)
                ? Math.Clamp(parsed, 1, 200)
                : 50;

            var recent = await ctx.Db.From<RoomMessageRecord>(Schema.RoomMessages)
                .Where(Schema.RoomMessages.Col<string>("room_id").Eq(roomId))
                .OrderBy(Schema.RoomMessages.Col<string>("created_at").Desc())
                .Limit(limit)
                .ToListAsync();

            recent.Reverse(); // oldest-first for display
            return ctx.Json(recent);
        });

        // Server-side fanout trigger (bench + admin use): same wire protocol
        // as a peer broadcasting, minus a live socket.
        app.Post("/rooms/:roomId/broadcast", async ctx =>
        {
            var roomId = ctx.Req.Param("roomId")!;

            string senderId = "server";
            string payloadJson = "{}";

            if (string.IsNullOrWhiteSpace(ctx.Req.BodyText))
            {
                return ctx.BadRequest("Body must be valid JSON.");
            }

            try
            {
                using var doc = JsonDocument.Parse(ctx.Req.BodyText);
                var root = doc.RootElement;

                if (root.TryGetProperty("senderId", out var senderElement) &&
                    senderElement.ValueKind == JsonValueKind.String)
                {
                    senderId = senderElement.GetString() ?? "server";
                }

                if (root.TryGetProperty("payload", out var payloadElement) &&
                    payloadElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    payloadJson = payloadElement.GetRawText();
                }
            }
            catch
            {
                return ctx.BadRequest("Body must be valid JSON.");
            }

            var delivered = hub.BroadcastFromServer(roomId, senderId, payloadJson);
            return ctx.JsonRaw($"{{\"delivered\":{delivered}}}", status: 202);
        });

        return app;
    }
}

/// <summary>A room is just its roster; all mutable state lives on the peers.</summary>
public sealed class Room
{
    public ConcurrentDictionary<string, Peer> Peers { get; } = new(StringComparer.Ordinal);

    /// <summary>Serializes roster mutation (join/leave) per room.</summary>
    public object Gate { get; } = new();
}

/// <summary>Per-connection state. One socket means one peer in one room.</summary>
public sealed class Peer
{
    public required WebSocketConnection Connection { get; init; }
    public string RoomId { get; init; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "Anonymous";
    public string? Avatar { get; set; }
    public bool Joined { get; set; }
    public double CursorX { get; set; }
    public double CursorY { get; set; }
    public bool Typing { get; set; }
}

/// <summary>
/// Write-behind message persistence. Multiple concurrent drain workers call
/// InsertAsync simultaneously; under that contention SqliteDbClient hands the
/// writes to its group-commit queue and coalesces them into transactions of
/// up to 64 statements (MaxBatchSize), keeping high broadcast velocity off
/// the fsync path. Under NetWasm the same enqueue path batches into D1
/// statements over the binding.
/// </summary>
public sealed class RoomMessagePersister
{
    private readonly IDbClient _db;
    private long _persisted;
    private long _persistFailed;
    private string? _lastPersistError;
#if !NETWASM
    private long _dropped;

    private readonly Channel<RoomMessageRow> _queue;
    private readonly Task[] _workers;

    public RoomMessagePersister(IDbClient db, int workerCount, int queueCapacity)
    {
        _db = db;
        _queue = Channel.CreateBounded<RoomMessageRow>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
        });

        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Run(DrainAsync);
        }
    }

    public long QueueDepth => _queue.Reader.Count;
#else
    // Cloudflare Workers freeze the isolate between events, so background
    // drain workers would never run — persistence is awaited inline through
    // the D1 binding instead (concurrent events still batch at the binding).
    public RoomMessagePersister(IDbClient db, int workerCount, int queueCapacity)
    {
        _db = db;
    }

    public long QueueDepth => 0;
#endif

    public long PersistedCount => Interlocked.Read(ref _persisted);

    public long FailedCount => Interlocked.Read(ref _persistFailed);

    public string LastErrorJson => RoomProtocol.Escape(Volatile.Read(ref _lastPersistError) ?? "null");

    /// <summary>
    /// Persist a broadcast snapshot. Locally this enqueues into the
    /// write-behind queue (group-commit coalescing); under NetWasm it awaits
    /// the D1 binding inline so the write lands before the event ends.
    /// </summary>
    public ValueTask PersistAsync(string roomId, string senderId, string payloadJson)
    {
#if NETWASM
        return PersistInlineAsync(roomId, senderId, payloadJson);
#else
        TryEnqueue(roomId, senderId, payloadJson);
        return ValueTask.CompletedTask;
#endif
    }

#if !NETWASM
    public bool TryEnqueue(string roomId, string senderId, string payloadJson)
    {
        var row = new RoomMessageRow(
            Ids.New("msg"),
            roomId,
            senderId,
            payloadJson,
            DateTimeOffset.UtcNow.ToString("O"));

        if (!_queue.Writer.TryWrite(row))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        return true;
    }

    private async Task DrainAsync()
    {
        await foreach (var row in _queue.Reader.ReadAllAsync())
        {
            await InsertRowAsync(row);
        }
    }
#endif

    private sealed record RoomMessageRow(string Id, string RoomId, string SenderId, string PayloadJson, string CreatedAt);

    private async ValueTask PersistInlineAsync(string roomId, string senderId, string payloadJson)
    {
        try
        {
            await InsertRowAsync(new RoomMessageRow(
                Ids.New("msg"),
                roomId,
                senderId,
                payloadJson,
                DateTimeOffset.UtcNow.ToString("O")));
        }
        catch (System.Exception exception)
        {
            Interlocked.Increment(ref _persistFailed);
            // exception.Message can itself throw under NetWasm JS interop;
            // fall back to the type name when reading it fails.
            string message;
            try
            {
                message = exception.Message;
            }
            catch
            {
                message = exception.GetType().FullName ?? "unknown";
            }

            Volatile.Write(ref _lastPersistError, message);
        }
    }

    private async Task InsertRowAsync(RoomMessageRow row)
    {
        try
        {
            // InsertAsync(TableDef, IDictionary) maps dictionaries straight
            // onto columns on both hosts — no JSON serialization involved,
            // which matters under NetWasm where the source-generated
            // serializer has no metadata for Dictionary<string, object?>.
            await _db.InsertAsync(Schema.RoomMessages, new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["room_id"] = row.RoomId,
                ["sender_id"] = row.SenderId,
                ["payload"] = row.PayloadJson,
                ["created_at"] = row.CreatedAt,
            });
            Interlocked.Increment(ref _persisted);
        }
        catch (System.Exception exception)
        {
            Interlocked.Increment(ref _persistFailed);
            string message;
            try
            {
                message = exception.Message;
            }
            catch
            {
                message = exception.GetType().FullName ?? "unknown";
            }

            Volatile.Write(ref _lastPersistError, message);
        }
    }
}

/// <summary>Hand-rolled envelope builder: zero-reflection, serialize-once fanout.</summary>
public static class RoomProtocol
{
    public static string Connected(string roomId, string connectionId) =>
        $"{{\"type\":\"connected\",\"connectionId\":{Escape(connectionId)},\"room\":{Escape(roomId)}}}";

    public static string Joined(string roomId, string connectionId, Room room)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append("{\"type\":\"joined\",\"room\":")
            .Append(Escape(roomId))
            .Append(",\"connectionId\":")
            .Append(Escape(connectionId))
            .Append(",\"peers\":[");

        var first = true;
        foreach (var peer in room.Peers.Values)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            AppendPeer(sb, peer);
        }

        sb.Append("]}");
        return sb.ToString();
    }

    public static string PeerJoined(Peer peer)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append("{\"type\":\"peer_joined\",\"peer\":");
        AppendPeer(sb, peer);
        sb.Append('}');
        return sb.ToString();
    }

    public static string PeerLeft(string connectionId, string userId) =>
        $"{{\"type\":\"peer_left\",\"connectionId\":{Escape(connectionId)},\"userId\":{Escape(userId)}}}";

    public static string Presence(Peer peer) =>
        $"{{\"type\":\"presence\",\"connectionId\":{Escape(peer.Connection.Id)},\"userId\":{Escape(peer.UserId)}," +
        $"\"x\":{Number(peer.CursorX)},\"y\":{Number(peer.CursorY)},\"typing\":{(peer.Typing ? "true" : "false")}}}";

    public static string Message(string messageId, string roomId, string? connectionId, string senderId, string sentAt, string payloadJson) =>
        $"{{\"type\":\"message\",\"id\":{Escape(messageId)},\"room\":{Escape(roomId)}" +
        (connectionId is null ? "" : $",\"connectionId\":{Escape(connectionId)}") +
        $",\"senderId\":{Escape(senderId)},\"sentAt\":{Escape(sentAt)},\"payload\":{payloadJson}}}";

    public static string Bye(string connectionId) =>
        $"{{\"type\":\"bye\",\"connectionId\":{Escape(connectionId)}}}";

    public static string Pong(long unixMs) =>
        $"{{\"type\":\"pong\",\"t\":{unixMs}}}";

    public static string Error(string error, string message) =>
        $"{{\"type\":\"error\",\"error\":{Escape(error)},\"message\":{Escape(message)}}}";

    public static void AppendPeer(System.Text.StringBuilder sb, Peer peer)
    {
        sb.Append("{\"connectionId\":")
            .Append(Escape(peer.Connection.Id))
            .Append(",\"userId\":")
            .Append(Escape(peer.UserId))
            .Append(",\"name\":")
            .Append(Escape(peer.Name))
            .Append(",\"avatar\":")
            .Append(peer.Avatar is null ? "null" : Escape(peer.Avatar))
            .Append(",\"x\":")
            .Append(Number(peer.CursorX))
            .Append(",\"y\":")
            .Append(Number(peer.CursorY))
            .Append(",\"typing\":")
            .Append(peer.Typing ? "true" : "false")
            .Append('}');
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    public static string Escape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// Read-side projection of a persisted room message. Storage schema lives in
/// TableDef (schema.cs); this record stays attribute-free.
/// </summary>
public sealed record RoomMessageRecord(string Id, string RoomId, string SenderId, string Payload, string? CreatedAt);

/// <summary>Server RSS for the stats endpoint. Full .NET only (NetWasm has no Process API).</summary>
public static class ProcessInfo
{
    public static string Json()
    {
#if NETWASM
        return "null";
#else
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var peak = (process.PeakWorkingSet64 / (1024.0 * 1024.0)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var working = (process.WorkingSet64 / (1024.0 * 1024.0)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return $"{{\"peakWorkingSetMb\":{peak},\"workingSetMb\":{working},\"threads\":{process.Threads.Count}}}";
#endif
    }
}
