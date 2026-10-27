# Reptar Rooms — High-Concurrency Multi-Room WebSocket Hub

A real-time WebSocket room broadcast and presence engine built as a single file-based Reptar app (`app.cs`), inspired by **PartyKit**, **y-websocket**, **Liveblocks**, and **uWebSockets**.

## Highlights

- **Parameterized WebSocket Routes**: `app.OnWebSocket("/rooms/{roomId}", handlers)` (or `/rooms/:roomId`).
- **Ephemerality + Presence (Awareness)**: Live peer rosters, join/leave deltas, and ephemeral cursor/typing presence.
- **Native Write-Behind SQLite Group-Commit**: Fanout precedes queued persistence. Queue admission and a sender echo are not durable-write acknowledgements; saturation and storage failures are observable.
- **Bounded Connection Channels**: Slow consumers can lose older outbound frames under `DropOldest`; actual evictions are counted by the native transport.
- **REST Introspection Surface**:
  - `GET /health` — Health and active connection counts
  - `GET /stats` — Relayed, persisted, failed and dropped persistence counts; pending/active writes; presence counts and server RSS
  - `GET /rooms` — Active room list with peer counts
  - `GET /rooms/:roomId/peers` — Active peers in a room
  - `GET /rooms/:roomId/messages` — Paginated history of persisted room messages
  - `POST /rooms/:roomId/broadcast` — Server-side message broadcast into a room

## Durability boundary

Native persistence has a bounded in-memory queue. `messagesPersisted` counts completed database inserts; `messagesPersistFailed` and `lastPersistError` report storage failures. `messagesPersistDropped` counts rejected admissions when full or stopped. `persisterQueueDepth` excludes `persisterActiveWrites`. These counters describe one process and reset on restart.

The native app registers the persister as an application module so accepted work drains before SQLite is disposed on normal shutdown. The queue is **not** a durable inbox: SIGKILL/crash can lose queued work, and failed writes are not retried. Arbitrary module shutdown hooks are not covered by a global deadline. A relayed frame or REST `202` is not a commit acknowledgement. Use history to observe completed snapshots; new snapshots retain their original relayed IDs. Existing historical rows are not rewritten.

The Worker/D1 port awaits persistence inline after fanout, including REST fanout. Fanout can still precede a failed write; the same failure counters apply. This is not exactly-once delivery or an aggregate cross-isolate metrics service.

Published native fault probes exercise a real SQLite write gate, queue overflow, storage-trigger failure and subsequent successful write, stable IDs, module drain, database reopening and integrity checks. An owned-PID SIGKILL checkpoint demonstrates loss of uncommitted admission without database corruption; it is not power-loss testing. The protocol smoke checks both WebSocket and REST snapshots against native and local Worker hosts. These short checks do not establish sustained deployment maturity.

## Quick Start

```bash
# Start server on http://localhost:8787
bun rooms:dev

# Run protocol smoke test
bun rooms:smoke

# Run load benchmark (e.g. 5,000 sockets across 100 rooms)
bun rooms:bench --clients 5000 --rooms 100 --peers 50
```
