---
title: ReptarBase (BaaS Engine)
description: Single-container Backend-as-a-Service architecture, OpenShip Studio console, and high-concurrency benchmarks.
---

**ReptarBase** (`examples/baas/app.cs`) is a complete, single-file Backend-as-a-Service (BaaS) that compiles to a **10.4 MB Native AOT binary** running in **~15 MB RAM**. It serves as a modern, zero-dependency alternative to PocketBase, Supabase, and OpenShip for single-container and VPS deployments.

---

## Architecture & Capabilities

Traditional BaaS platforms like PocketBase or Supabase either carry heavy runtime baggage (Go CGO serialization, Docker multi-container compose stacks) or struggle under concurrent write pressure. ReptarBase runs in a single process with zero reflection:

```
┌────────────────────────────────────────────────────────────────────────┐
│                   ReptarBase Engine (10.4 MB AOT Binary)              │
├───────────────────┬───────────────────┬────────────────────────────────┤
│ Better Auth &     │ Typed Collections │ Magic-Byte Storage             │
│ Personal API Keys │ (ColumnRef<T>)    │ (Files.Check + ETag 304)       │
├───────────────────┴───────────────────┴────────────────────────────────┤
│ Real-Time SSE Change Feed (/api/realtime)                              │
├────────────────────────────────────────────────────────────────────────┤
│ Embedded Studio Console (GET /_ /dashboard)                            │
├────────────────────────────────────────────────────────────────────────┤
│ SQLite WAL Engine (Group-Commit Write Batcher + Pooled Readers)        │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. Better Auth & Personal API Keys

- PBKDF2 password hashing (WebCrypto-compatible) with secure salt generation.
- Session-based cookie auth for web browsers (`/api/auth/register`, `/api/auth/login`, `/api/auth/logout`, `/api/auth/session`).
- Personal API Key issuance (`/api/auth/keys`) with high-speed Bearer header validation for programmatic and agent access.

### 2. Typed Collections & `ColumnRef<T>`

- Fast document querying using compiled `ColumnRef<T>` tokens (`status`, `owner_id`) preventing SQL injection and typos at compile time.
- Atomic document counter increments (`UPDATE documents SET view_count = view_count + 1 WHERE id = @id`).
- Strict JSON validation with `Rule<T>`.

### 3. Magic-Byte Object Storage (`Files.Check`)

- Stack-allocated magic-byte sniffing (`Files.Check`) validates file content against actual binary headers (PNG, JPEG, GIF, WebP, PDF) rather than trusting user-supplied `Content-Type` headers.
- Automatic SHA-256 ETag computation and conditional `If-None-Match` fast-pathing returning `304 Not Modified`.

### 4. Real-Time Change Feed via SSE

- Native Server-Sent Events endpoint (`GET /api/realtime`) broadcasting document mutations (`document:created`, `document:updated`, `document:deleted`) to connected clients.
- Automatic 15-second heartbeat keepalive pings.

### 5. Embedded Studio Web Console

- OpenShip-inspired dark-mode management GUI embedded directly into the binary at `GET /_`, `GET /dashboard`, and `GET /`.
- Interactive collections explorer with pagination, status badges, and search.
- Drag-and-drop file uploader with instant image previews.
- API key manager with one-click token generation and copying.
- Live SSE event streaming terminal displaying mutations in real time.

---

## Running ReptarBase

ReptarBase is completely file-based and requires no `.csproj` project files:

```bash
# Start the BaaS server and Studio console on http://localhost:8787
bun baas:dev

# Run the automated 12-step verification suite
bun baas:test

# Run the 5-tier high-concurrency oha load benchmark suite
bun baas:bench
```

---

## Benchmark Results

Benchmarked with `oha` using 20 concurrent connections and SQLite in WAL mode:

| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | p99 | Success |
| :-- | :-- | :-- | :-- | :-: | :-: | :-: | :-: | :-: |
| **T1** | **Baseline Engine** | `GET` | `/health` | **132,907** | 0.13 ms | 0.26 ms | 0.58 ms | 100% |
| **T2** | **Filtered Query (`ColumnRef<T>`)** | `GET` | `/api/documents?status=published` | **34,215** | 0.43 ms | 1.38 ms | 2.19 ms | 100% |
| **T3** | **Atomic Read + Counter** | `GET` | `/api/documents/:id` | **55,689** | 0.30 ms | 0.96 ms | 1.50 ms | 100% |
| **T4** | **Authenticated Writes + SSE** | `POST` | `/api/documents` | **31,722** | 0.52 ms | 1.55 ms | 3.08 ms | 100% |
| **T5** | **Conditional ETag Caching** | `GET` | `/api/files/:key` | **68,052** | 0.27 ms | 0.46 ms | 1.01 ms | 100% |

### Key Architectural Findings

1. **31,722 Authenticated Writes/Sec:** Each write executes token verification, ULID generation (`Ids.New("doc")`), SQLite insertion through Reptar's lock-free group-commit batcher, and event broadcasting to active SSE subscribers.
2. **Comparison with PocketBase & Node:** Under equivalent concurrent write loads, PocketBase (Go/mattn SQLite) tops out around 2,500–3,500 writes/sec due to CGO overhead and write lock contention. Standard Node.js ORM stacks deliver 2,000–4,000 writes/sec. ReptarBase achieves an ~8x–10x throughput improvement.
3. **Zero Reflection:** Native AOT compilation ensures zero runtime reflection in request routing, SQL parameter binding, and JSON serialization.
