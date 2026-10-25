# Reptar Rooms — High-Concurrency Multi-Room WebSocket Hub

A real-time WebSocket room broadcast and presence engine built as a single file-based Reptar app (`app.cs`), inspired by **PartyKit**, **y-websocket**, **Liveblocks**, and **uWebSockets**.

## Highlights

- **Parameterized WebSocket Routes**: `app.OnWebSocket("/rooms/{roomId}", handlers)` (or `/rooms/:roomId`).
- **Ephemerality + Presence (Awareness)**: Live peer rosters, join/leave deltas, and ephemeral cursor/typing presence.
- **Write-Behind SQLite Group-Commit**: Fanout never awaits disk I/O; SQLite background batcher persists chat messages with zero throughput bottleneck.
- **Backpressure & Bounded Channels**: Per-connection bounded channels with drop-oldest protection prevent slow clients from choking the event loop.
- **REST Introspection Surface**:
  - `GET /health` — Health and active connection counts
  - `GET /stats` — Relayed vs persisted message telemetry, presence counts, and server RSS
  - `GET /rooms` — Active room list with peer counts
  - `GET /rooms/:roomId/peers` — Active peers in a room
  - `GET /rooms/:roomId/messages` — Paginated history of persisted room messages
  - `POST /rooms/:roomId/broadcast` — Server-side message broadcast into a room

## Quick Start

```bash
# Start server on http://localhost:8787
bun rooms:dev

# Run protocol smoke test
bun rooms:smoke

# Run load benchmark (e.g. 5,000 sockets across 100 rooms)
bun rooms:bench --clients 5000 --rooms 100 --peers 50
```
