#!/usr/bin/env dotnet run
#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema.cs
#:include hub.cs

using System.Text.Json;
using Reptar;
using Reptar.Hosting;
using static Reptar.ReptarApi;

// ---------------------------------------------------------------------------
// Reptar Rooms — high-concurrency multi-room WebSocket presence & broadcast hub
//
// Patterns ported from:
//   - PartyKit:      room-scoped broadcast(msg, without[]) + connection roster
//   - y-websocket:   room multiplexing keyed by path, ephemeral presence
//                    (awareness) that is never persisted, removed on disconnect
//   - Liveblocks:    join -> full roster snapshot, peer_joined/peer_left deltas,
//                    per-peer presence (cursor x/y, typing) fanout to others
//   - uWebSockets:   serialize-once fanout over per-socket bounded send
//                    channels (Kestrel transport) — the "corked publish" analog
// ---------------------------------------------------------------------------

var database = Db.Sqlite("rooms.db");

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
});

app.RegisterSchema(Schema.All);
await app.EnsureCreatedAsync();

// Write-behind persistence: broadcast fanout never awaits disk I/O. The
// persister keeps multiple SQLite writers in flight, which is exactly the
// contended-writer case where Reptar's SqliteDbClient coalesces up to 64
// pending statements into one group-commit transaction (MaxBatchSize = 64).
var persister = new RoomMessagePersister(database, workerCount: 4, queueCapacity: 50_000);
var hub = new RoomHub(persister);

// ws://host/rooms/{roomId} — parameterized WebSocket route
// (also accepts the HTTP route syntax "/rooms/:roomId")
app.OnWebSocket("/rooms/{roomId}", hub.CreateHandlers());

app.Get("/health", ctx => ctx.Json(hub.HealthSnapshot()));

app.Get("/stats", ctx => ctx.Json(hub.StatsSnapshot()));

app.Get("/rooms", ctx => ctx.Json(hub.ListRooms()));

app.Get("/rooms/:roomId/peers", ctx =>
{
    var roomId = ctx.Req.Param("roomId")!;
    return ctx.Json(hub.GetPeers(roomId));
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

// Server-side fanout trigger (bench + admin use): same wire protocol as a
// peer broadcasting, minus a live socket.
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
    return ctx.Json(new { delivered }, status: 202);
});

await app.RunAsync(args);
