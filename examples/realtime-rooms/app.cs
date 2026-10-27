#!/usr/bin/env dotnet run
#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema.cs
#:include hub.cs

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
//
// The NetWasm/Workers build (worker/) shares the exact hub and routes via
// RoomsRoutes.Configure; this file only wires the local SQLite host.
// ---------------------------------------------------------------------------

var database = Db.Sqlite(Environment.GetEnvironmentVariable("REPTAR_ROOMS_DB") ?? "rooms.db");

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
    Json = new SourceGenJsonCodec(RoomsJsonContext.Default),
});

app.RegisterSchema(Schema.All);
await app.EnsureCreatedAsync();

// Write-behind persistence: broadcast fanout never awaits disk I/O. The
// persister keeps multiple SQLite writers in flight, which is exactly the
// contended-writer case where Reptar's SqliteDbClient coalesces up to 64
// pending statements into one group-commit transaction (MaxBatchSize = 64).
var persister = new RoomMessagePersister(database, workerCount: 4, queueCapacity: 50_000);
app.AddModule(persister); // Drain accepted write-behind work before disposing SQLite.
var hub = new RoomHub(persister);
RoomsRoutes.Configure(app, hub);

await app.RunAsync(args);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<RoomMessageRecord>))]
internal partial class RoomsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
