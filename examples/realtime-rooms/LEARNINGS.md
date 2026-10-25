# Reptar Rooms — Learnings & Onboarding Report

Deliverable: `examples/realtime-rooms/` — a high-concurrency multi-room WebSocket
presence & broadcast hub, plus a load benchmark, built by a brand-new agent
onboarding onto the Reptar codebase. This file records the onboarding
experience, DX friction (including two framework bugs found and fixed), and
benchmark results.

## How to run

```bash
# Local dev server (ws://127.0.0.1:8787/rooms/{roomId})
bun scripts/reptar.ts run examples/realtime-rooms/app.cs
#   (or: cd examples/realtime-rooms && ../../.reptar/sdk/dotnet run app.cs)

# macOS note: bind IPv4 to dodge a .NET/macOS accept bug (see §2.3)
HOST=0.0.0.0 PORT=8787 ../../.reptar/sdk/dotnet run app.cs

# Protocol smoke test (2 clients, full join/presence/broadcast/leave cycle)
BENCH_WS_URL=ws://127.0.0.1:8787 BENCH_HOST=http://127.0.0.1:8787 bun smoke.ts

# Concurrency benchmark (5,000 clients / 100 rooms by default)
BENCH_WS_URL=ws://127.0.0.1:8787 BENCH_HOST=http://127.0.0.1:8787 bun bench.ts --clients 5000 --rooms 100 --peers 50
```

## 1. Onboarding Experience

### What worked extremely well

- **`bun scripts/reptar.ts inspect --json`** — instantly surfaced the active
  SQLite dialect, all 12 live tables with column/index metadata, registered
  routes, migration status, and RPC contract health. Checking "does this table
  exist / what shape is it" took one command instead of grepping schema files.
- **`bun scripts/reptar.ts doctor --json`** — confirmed .NET SDK ≥ 10.0.401,
  Bun runtime, DB connectivity (12 tables), 4 applied migrations, and
  up-to-date RPC contracts in a single machine-readable report. Zero guesswork
  about toolchain health.
- **`llms.txt`** — the high-density overview is genuinely good onboarding: the
  `TableDef` vs DTO separation, ColumnRef tokens, `OnWebSocket`'s existence,
  and the strict "no runtime reflection" rule were all load-bearing for this
  task. The query-layer examples (`From<T>().Where().OrderBy().Limit()`)
  compiled as documented on first try.
- **File-based apps (`app.cs` + `#:project`/`#:include`)** — no `.csproj` to
  author; the framework "just works" as a library via project references.
- **The Kestrel WebSocket transport** — per-socket bounded send channels with
  a dedicated send pump (corking analog) is the right architecture for fanout;
  the hub could stay allocation-lean (serialize-once, enqueue N times).

### Friction points

- **The documented entrypoint doesn't exist.** `llms.txt`, `AGENTS.md`, and the
  mission brief all say `bun reptar inspect --json`, but there is no `reptar`
  script in `package.json` — the real CLI is `bun scripts/reptar.ts` (with
  subcommands, not the documented `reptar <noun>` shape). The `reptar` alias
  exists as `bun scripts/reptar.ts` in some docs but not as a runnable script.
  Fixing `package.json` to `"reptar": "bun scripts/reptar.ts"` would make the
  documented commands true.
- **Two .NET SDKs, one supported path.** The system `dotnet` (10.0.103) rejects
  `#:include` ("Unrecognized directive") and produces a confusing cascade of
  build errors; only the pinned `.reptar/sdk` (10.0.401) handles the
  file-based directives. `doctor` checks the version — but agents habitually
  call bare `dotnet`. Running `dotnet run app.cs` without `DOTNET_ROOT`
  pointing at `.reptar/sdk` fails mysteriously. Suggestion: have `doctor`
  explicitly warn when PATH `dotnet` differs from `.reptar/sdk/dotnet`.
- **The repo was mid-flight.** An untracked, truncated WIP file
  (`framework/Reptar/AI/LlmOpenAi.cs`, cut off mid-method) broke every fresh
  compile of the framework — including `bun scripts/reptar.ts test` after a
  `bin/obj` clean, and any `dotnet run app.cs` (which compiles the framework
  from source). I completed the missing LLM client implementations
  (Anthropic, Gemini; OpenAI + Ollama + the streaming agent harness were being
  authored concurrently by the maintainer and I reconciled with their versions
  rather than duplicating) to unblock the tree. Lesson for the repo: a
  truncation-broken file in the framework source is invisible to CI until a
  clean build happens — consider a `git status --porcelain` check for
  uncommitted framework sources in `doctor`.

## 2. DX Friction & Discovered Issues (reported back to the repo)

### 2.1 WebSocket routes had no path parameters (fixed in framework)

`app.OnWebSocket(path, handlers)` matched paths by exact dictionary lookup —
no way to mount `ws://host/rooms/{roomId}`. Any real-time app wants per-room
paths (PartyKit and y-websocket both key rooms into the URL).

**Fixed** in `framework/Reptar/Core/ReptarApp.cs`: `OnWebSocket` now accepts
both the HTTP-route syntax (`/rooms/:roomId`) and the OpenAPI syntax
(`/rooms/{roomId}`). Matching mirrors the HTTP route matcher (static fast-path
dict first, then segment-matched param routes); extracted params land in
`request.RouteParams`, so `connection.Ctx.Req.Param("roomId")` works inside the
`Open` handler — the same API as HTTP handlers. Two tests added
(`Parameterized_websocket_routes_extract_path_params`,
`Parameterized_websocket_routes_support_openapi_syntax`).

### 2.2 `_webSocketSessions` was not thread-safe (fixed in framework)

`ReptarApp._webSocketSessions` was a plain `Dictionary<string, ...>` mutated
concurrently by the host (open = write, close = remove, message = read — all
on Kestrel request threads). Two sockets work; dozens of simultaneous opens
race the dictionary. **Fixed**: it is now a `ConcurrentDictionary` (call sites
use `TryRemove`). This is exactly the class of bug the benchmark was supposed
to flush out.

### 2.3 .NET 10 + macOS 26 accept() bug: Kestrel stalls/crashes on churn

Benchmarking exposed a **runtime-level** issue (not Reptar's): on macOS
26.5.2 + .NET 10 (runtime 10.0.12, apphost launch path — i.e. `dotnet run`),
`accept()` on an **AF_INET6** listener can return a zero-length sockaddr when
a peer resets mid-connect (dotnet/runtime#121848, fixed by PR #131869 in
newer servicing; related #130284 for the apphost path). Symptoms observed:
Kestrel silently stops accepting (connections pile up in backlog — looked
like a "slow trickle" of joins), or the whole process dies with
`System.ArgumentException: The supplied System.Net.SocketAddress is an
invalid size ... at IPEndPoint.Create ... AcceptCompletionCallback`.

**Mitigation added** to `framework/Reptar.Kestrel/KestrelHost.cs`: a `HOST`
env var to pin the listen address (`HOST=0.0.0.0` binds IPv4-only and avoids
the AF_INET6 accept path). On macOS dev machines, `HOST=0.0.0.0` is strongly
recommended for WebSocket-heavy local workloads. (Deployed Linux containers
and Workers are unaffected.)

### 2.4 `IWebSocketTransport` has no delivery guarantee or backpressure signal

`WebSocketConnection.SendAsync` always returns a completed task; the Kestrel
transport enqueues into a bounded per-socket channel and **silently drops**
(DropOldest) when a peer is saturated. uWebSockets exposes
`getBufferedAmount()`/send status so apps can apply backpressure; Liveblocks
throttles presence server-side. For a production hub, Reptar needs either a
`SendAsync` that reports backpressure or a `Connection.BufferedAmount`
surface — right now a slow client's frame loss is undetectable from C#.
(The hub compensates by counting fanout attempts only.)

### 2.5 Smaller paper cuts

- **No public JSON string-escape utility.** Hand-rolled envelopes need one;
  the only option is `ActionCableProtocol.EscapeString` — public but
  ActionCable-scoped. I rewrote a 20-line escaper in the example. A
  `Reptar.Text.JsonEscape` (or making the codec expose raw-string helpers)
  would remove that duplication.
- **No max-message-size config on the WS receive path.** KestrelHost buffers
  arbitrarily large frames (8 KiB chunks, no cap); the hub had to impose its
  own 64 KiB guard.
- **`EnsureCreatedAsync` + `TableDef`** worked flawlessly for DDL (nice DX),
  but note `DefineTable` DDL auto-runs at startup in every environment — fine
  for dev/examples, worth being explicit about in docs for prod.
- **`Process.PeakWorkingSet64` returns 0 on macOS** (working set is reported
  correctly) — the stats endpoint reports `peakWorkingSetMb: 0` on dev
  machines; misleading when tracking down memory issues.
- **Smoke-test harness is worth keeping as a pattern.** `smoke.ts` (2 clients,
  full protocol cycle, REST assertions) caught every regression during the
  macOS-bug investigation within seconds, where a full bench run costs
  minutes. Recommend the repo promote a per-example `smoke.ts` convention.

## 3. Benchmark Results

Machine: Apple Silicon, 8 cores, 16 GB RAM, macOS 26.5.2, .NET 10.0.12
(workstation GC), Bun 1.3.10 client. Server bound IPv4
(`HOST=0.0.0.0`), one process, no shard.

### 5,000 concurrent clients / 100 rooms (~50 peers/room)

| Metric                                  | Result                                    |
| --------------------------------------- | ----------------------------------------- |
| Connection ramp-up (connect+join)      | **1.53 s total (≈3,267 sockets/s)**       |
| Joined                                  | 5,000 / 5,000                             |
| Broadcast fanout, 1 sender → 50 peers   | **p50 0.50 ms · p95 2.41 ms · p99 29.4 ms**, max 39.2 ms, 5,000/5,000 receipts, 0 dropped |
| Presence storm (5 s, ~20 sends/s/client)| 140,000 frames sent → **6.86 M fanouts relayed server-side** |
| Persistence (write-behind + group commit)| 100/100 broadcasts persisted, queue drained to 0 |
| Server RSS delta under full load        | ≈978 MB → **≈0.20 MB/socket** (includes runtime + GC) |
| Clean teardown (leave + close)           | 5,000/5,000 in 1.3 s                     |

### 100 clients / 4 rooms (sanity run)

Ramp 0.10 s (988 sockets/s); fanout p50 0.5 ms; 232,800 presence fanouts from
9,700 sends; all 20 broadcasts relayed and persisted.

### 20,000-client attempt — deterministic CLR ceiling on macOS

Two attempts both reached **exactly 12,336 joined connections**, then the
process died with CLR `Out of memory.` (reproducible to the socket, with >10 GB
free system RAM). This is a per-process allocation ceiling of the .NET 10
runtime on this macOS/ARM machine (thread stacks + GC heap growth under
workstation GC), not a hub-data-structure limit — at 5k sockets the hub's own
state is a few MB. Production paths to 20k+: Linux container with server GC
(`DOTNET_gcServer=1`) and a tuned `GCHeapHardLimit`, sharding rooms across
processes, or running per-isolate on Workers (see the deployed dogfood build).

### Context vs. reference stacks

- **PartyKit/Cloudflare**: per-room isolate state matches this hub's design
  (room = unit); on Workers, the equivalent ceiling is per-isolate, which is
  exactly what the NetWasm deploy exercises.
- **Node `ws`-style servers** on comparable hardware typically sustain
  ~10-30k connections with ~30-100 KB/socket (heap + buffers). Reptar's
  ~0.2 MB/socket is heavier — expected for .NET/Kestrel with per-connection
  send channels; the fanout latency (sub-millisecond p50) is competitive.
- **uWebSockets** reports ~20 KB/socket and >100k connections per thread —
  the gold standard; Reptar would need a leaner transport buffer strategy to
  approach it.

## 4. Architecture notes (what was ported from where)

- **PartyKit** — room-scoped connection roster; `broadcast(frame, without)`
  exclusion semantics (peer_joined goes to everyone except the joiner).
- **y-websocket** — room keyed by URL path (`/rooms/{roomId}`); presence as
  ephemeral awareness state: never persisted, cleared on disconnect.
- **Liveblocks** — join returns a full roster snapshot so the joiner can
  render "others" immediately; `peer_left` deltas afterwards.
- **uWebSockets** — serialize-once fanout: build the frame string once, then
  enqueue per socket; Reptar's bounded per-socket channels provide the
  non-blocking publish.
- **SQLite group-commit** — the persister keeps 4 concurrent writers in
  flight; under contention `SqliteDbClient` coalesces up to 64 pending
  statements into one transaction (`MaxBatchSize`), so broadcast fanout never
  waits on fsync. On Workers the same writes flow through D1 batch bindings.
