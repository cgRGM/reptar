---
title: RealWorld & Benchmarks
description: Spec-compliant Conduit API and head-to-head benchmarks against .NET 10 Minimal API and Hono on Bun.
---

Reptar includes both a 12-tier comparative benchmark suite (`bench/`) and a complete RealWorld (Conduit) backend (`examples/realworld`).

## 1. Reptar Bench: Reptar vs .NET 10 Minimal API vs Hono

Measured on an isolated 2-vCPU Linux VPS (Ubuntu 24.04, 3.7 GB RAM, server pinned to CPU 0, `oha` load generator pinned to CPU 1). All three frameworks run the exact same schema, seed data, and SQL queries:

- **Reptar (`net10.0` Native AOT, `-p:ReptarSqliteOnly=true`):** **10.4 MB** binary, **124 ms** startup
- **.NET 10 Minimal API (`net10.0` Native AOT, raw ADO.NET):** **10.4 MB** binary, **127 ms** startup
- **Hono on Bun (JIT compiled bundle):** **104.3 MB** binary, **240 ms** startup

| Tier | Scenario | Reptar (req/s) | .NET 10 Minimal API | Hono (Bun) | Reptar + SWR Cache | Reptar vs Minimal |
| :-- | :-- | --: | --: | --: | --: | --: |
| **T0** | `plaintext` | **41,877** | 41,852 | 47,298 | — | **+0%** |
| **T1** | `json` | **42,239** | 41,675 | 39,289 | — | **+1%** |
| **T2** | `router-params` | **41,355** | 40,954 | 34,401 | — | **+1%** |
| **T3** | `validate` (`Rule<T>`) | **36,262** | 34,427 | 18,256 | — | **+5%** |
| **T4** | `task-detail` | **13,765** | 12,973 | 17,909 | **39,214** (2.6 ms p50) | **+6%** (3.0x w/ SWR) |
| **T5** | `task-list` (20 rows) | **5,945** | 5,884 | 6,753 | **29,874** (3.3 ms p50) | **+1%** (5.0x w/ SWR) |
| **T6** | `task-comments` | **10,707** | 10,126 | 9,806 | **35,485** (2.8 ms p50) | **+6%** (3.5x w/ SWR) |
| **T7** | `create-task` | **6,224** | 3,628 | 4,235 | — | **+72%** |
| **T8** | `/me` (JWT + lookup) | **15,683** | 14,765 | 12,052 | — | **+6%** |
| **T9** | `login` (PBKDF2, 10c) | **16** | 16 | 22 | — | **0%** |
| **T10** | `update-task` | **4,925** | 2,150 | 4,949 | — | **+129%** |
| **T11** | `mixed` (Read/Write) | **4,548** | 4,673 | 6,628 | — | -3% |

### Key Takeaways from the Benchmark Suite

1. **Reptar Wins 10 of 12 Tiers vs .NET 10 Minimal API:** Reptar's zero-allocation radix router, Kestrel `PipeReader`/`PipeWriter` integration, `SourceGenJsonCodec`, and compile-time `[ReptarEntity]` positional mappers beat raw .NET 10 Minimal API across `T0`–`T8` and `T10`, while running declarative `Rule<T>()` validation at **1.99x** the speed of Hono (`36,262` vs `18,256` req/s).
2. **Lock-Free Group-Commit Write Batcher (`T7` +72%, `T10` +129%):** Under 100-connection concurrent write load, `SqliteDbClient` coalesces queued writes into a single `BEGIN ... COMMIT` transaction—beating raw .NET 10 Minimal API by **+72%** on `T7 create-task` (`6,224` vs `3,628` req/s) and **+129%** on `T10 update-task` (`4,925` vs `2,150` req/s).
3. **SWR Perimeter Caching (`CacheControl.SWR`):** Enabling `CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30)` accelerates database read endpoints (`T4`–`T6`) to **29.8k–39.2k req/s** at **2.6–3.3 ms p50**.

## 2. RealWorld (Conduit) Specification Suite

Reptar includes a complete RealWorld (Conduit) implementation under `examples/realworld`:

- **Official Hurl suite:** **13/13 files pass (154/154 requests)** across SQLite, PostgreSQL, the local Wasm Worker, and the deployed Cloudflare Edge Worker.
- **Targets:** Portable SQLite/Kestrel, PostgreSQL (`Npgsql`), Linux Native AOT, and Cloudflare Workers + D1.

### Run Locally

```bash
bun run realworld:dev
```

To run the official 154-request Hurl specification suite:

```bash
export REALWORLD_SPEC_DIR=/path/to/realworld-apps/realworld
HOST=http://localhost:8787 bun run realworld:hurl
```

### RealWorld VPS SQLite Results

Measured on the Lightsail VPS with the Native AOT container and SQLite, 300 seeded articles, 100 connections:

| Scenario                          | Connections | Req/sec |    p50 |  p97.5 |
| :-------------------------------- | ----------: | ------: | -----: | -----: |
| `GET /api/tags`                   |         100 |   7,702 |  11 ms |  32 ms |
| `GET /api/articles/:slug`         |         100 |   7,164 |  12 ms |  29 ms |
| `GET /api/articles?limit=20`      |         100 |   6,468 |  13 ms |  34 ms |
| `GET /api/articles/feed?limit=20` |         100 |     721 | 134 ms | 244 ms |
| `GET /api/articles?tag=bench`     |         100 |   6,192 |  14 ms |  36 ms |
| `POST /api/articles`              |          10 |     455 |  19 ms |  47 ms |

### RealWorld PostgreSQL 14 Results

Running the exact same routes against PostgreSQL 14 (`REALWORLD_DATABASE_URL=postgresql://...`) with `CacheControl.SWR` and transactional `NpgsqlBatch` writes:

| Scenario                             |     Req/sec |      p50 |
| :----------------------------------- | ----------: | -------: |
| `GET /api/tags`                      | **101,536** | &lt;1 ms |
| `GET /api/articles?limit=20`         |  **94,112** | &lt;1 ms |
| `GET /api/articles/:slug`            | **102,816** | &lt;1 ms |
| `GET /api/articles/feed`             |   **3,047** |    30 ms |
| `GET /api/articles?tag=bench`        |  **72,221** |     1 ms |
| `POST /api/articles` (unique titles) |   **4,664** |     2 ms |

### Cloudflare Workers + D1 Edge Results

Measured against the deployed Cloudflare Worker (`reptar-realworld-bench`) with 50 concurrent clients:

| Scenario | Cold p50 | Cache-Warm Req/sec | Cache-Warm p50 |
| :-- | --: | --: | --: |
| `GET /api/tags` | 68 ms | **3,280** | **9 ms** |
| `GET /api/articles?limit=20` | 365 ms | **4,358** | **9 ms** |
| `GET /api/articles/:slug` | 145 ms | **4,940** | **8 ms** |
| `GET /api/articles/feed` | 635 ms | 80 | 494 ms |
| `POST /api/articles` | 559 ms | 29 | 289 ms |

## 3. ReptarBase (Single-Container BaaS) Workload Benchmarks

`examples/baas` demonstrates a high-concurrency, single-container Backend-as-a-Service (BaaS) equipped with Better Auth, typed document collections via `ColumnRef<T>`, magic-byte file uploads, real-time Server-Sent Events, and an OpenShip-style embedded Studio console.

Measured against the standalone Native AOT binary using `oha` with 20 concurrent connections and SQLite in WAL mode:

| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | p99 | Success |
| :-- | :-- | :-- | :-- | :-: | :-: | :-: | :-: | :-: |
| **T1** | **Baseline Engine** | `GET` | `/health` | **132,907** | 0.13 ms | 0.26 ms | 0.58 ms | 100% |
| **T2** | **Filtered Query (`ColumnRef<T>`)** | `GET` | `/api/documents?status=published` | **34,215** | 0.43 ms | 1.38 ms | 2.19 ms | 100% |
| **T3** | **Atomic Read + Counter** | `GET` | `/api/documents/:id` | **55,689** | 0.30 ms | 0.96 ms | 1.50 ms | 100% |
| **T4** | **Authenticated Writes + SSE** | `POST` | `/api/documents` | **31,722** | 0.52 ms | 1.55 ms | 3.08 ms | 100% |
| **T5** | **Conditional ETag Caching** | `GET` | `/api/files/:key` | **68,052** | 0.27 ms | 0.46 ms | 1.01 ms | 100% |

### Why ReptarBase Outperforms PocketBase & Node BaaS

- **31,722 authenticated writes/sec**: Reptar's SQLite write path utilizes lock-free group-commit batching and zero-copy `PipeReader` JSON parsing. Under similar concurrent write conditions, PocketBase (Go CGO) tops out at ~2.5k–3.5k writes/sec and Node.js solutions at ~2k–4k writes/sec.
- **34,215 filtered queries/sec**: Stack-allocated `ColumnRef<T>` filter evaluation with zero runtime reflection and an optimized SQLite read connection pool.
- **Footprint**: The complete server, database engine, storage pipeline, and embedded UI compile to a single 10.4 MB standalone binary with ~15 MB RAM usage.

## 4. AI Gateway & Universal Streaming Benchmarks

`examples/ai-gateway` implements a Cloudflare AI Gateway / Portkey-style universal LLM proxy with in-connection failover, SHA-256 prompt caching, and dual-protocol streaming (SSE and Vercel AI SDK Data Stream Protocol `0:/9:/d:`).

Measured against the standalone Native AOT binary using `oha` and native TTFT probes across 500 concurrent connections:

| Metric | Measured Value | Scenario Notes |
| :-- | :-: | :-- |
| **Uncached Streaming Throughput** | **22,044 req/s** | Full downstream streaming POST, `?no_cache=1`, 100% success (220k reqs) |
| **Prompt-Cache-Hit Throughput** | **28,499 req/s** | SHA-256 cache HIT, sub-millisecond token stream replay (285k reqs) |
| **Time to First Token (TTFT p50)** | **36 ms** (Cache HIT) / **59 ms** (Miss) | 500 concurrent connections under full burst load |
| **Time to First Token (TTFT p99)** | **45 ms** (Cache HIT) / **69 ms** (Miss) | Zero dropped connections across 506k total requests |
| **In-Connection Failover** | **0 ms client impact** | Dead upstream automatically falls back to secondary provider in same HTTP stream |
| **Group-Commit Telemetry Drain** | **100,445 rows** | Fire-and-forget `gateway_logs` writes decoupled from client response path |

### Key Highlights

- **Vercel AI SDK Compatibility**: React and Next.js applications using `useChat({ streamProtocol: 'data' })` connect directly without custom client adapters.
- **Failover Without Connection Drops**: 429 rate limits or upstream 5xx errors trigger transparent failover before headers are flushed, shielding users from model outages.

## 5. Realtime Rooms Multi-Peer WebSocket Fanout Benchmarks

`examples/realtime-rooms` implements a high-concurrency multi-room WebSocket broadcast and presence engine inspired by **PartyKit**, **y-websocket**, and **Liveblocks**.

Measured using `bun rooms:bench` with 500 concurrent clients across 20 rooms (25 peers/room):

| Metric | Measured Value | Target / Comparison |
| :-- | :-: | :-- |
| **Socket Connection Ramp-Up** | **4,926 sockets/sec** | 500 connections joined in 101.5 ms (100% success) |
| **Fanout Latency (p50)** | **0.32 ms** | 10x faster than PartyKit / Cloudflare DO (1.5–3.0 ms) |
| **Fanout Latency (p90)** | **0.48 ms** | Consistent sub-millisecond distribution |
| **Fanout Latency (p99)** | **0.71 ms** | Outperforms Elixir Phoenix (2.5–4.5 ms) and Socket.io (45–90 ms) |
| **Fanout Delivery Reliability** | **2,500 / 2,500 (100%)** | **0 dropped receipts** across 20 rooms |
| **Presence Relay Throughput** | **8,627 updates/sec** | 1,140,001 cursor/typing presence events relayed with zero backpressure drops |
| **SQLite Group-Commit Persistence** | **101 messages (100%)** | Zero backlog queue at teardown; writes fully decoupled from WebSocket fanout |

### Why Realtime Rooms Scales

- **Zero-Allocation Broadcast**: Payloads are serialized once per broadcast and enqueued into per-connection bounded channels (`BoundedChannelFullMode.DropOldest`), preventing slow or stalled clients from blocking healthy peers.
- **Write-Behind Group-Commit**: Chat messages broadcast instantly over WebSockets while background SQLite batchers coalesce disk writes into single `BEGIN ... COMMIT` transactions.
