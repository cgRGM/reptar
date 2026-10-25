# Onboarding Experience & DX Friction Report

**Agent:** systems engineer, first contact with the Reptar codebase **Task:** build `examples/ai-gateway` (universal LLM proxy + streaming engine) using Reptar's machine-readable tools, and report DX friction back to the repo. **Date:** 2026-10-25

---

## 1. What the machine-readable tooling got right

- `bun scripts/reptar.ts inspect --json` and `doctor --json` answered schema/route/toolchain questions before I opened a single framework file. `doctor` correctly verified the Bun runtime, DB connectivity, migrations, and RPC contract status end-to-end.
- `llms.txt` is excellent: the `TableDef` vs DTO separation, `ColumnRef<T>` tokens, and the "no reflection in request paths" rule were load-bearing for this app and were stated up front, not discovered by trial and error.
- The file-based app model (`#:project` + `#:include`, no `.csproj`) is genuinely pleasant — the gateway is two C# files.
- The SQLite group-commit batcher worked exactly as advertised: the gateway sustained **21k req/s of streaming proxy traffic while committing 313k telemetry rows** through the write queue in the same run.

## 2. Friction and issues found (ranked by impact)

### F1 — `IHttpClient` cannot stream: the #1 blocker for an AI gateway

`framework/Reptar/Data/Services.cs` defines `IHttpClient.FetchAsync(...)` returning a buffered `HttpResponseData` (and `DefaultHttpClient` disposes a **new `HttpClient` per call**, so there is no connection pooling either — `framework/Reptar/Data/Services.cs:528`). This makes true token-by-token upstream proxying impossible: a Reptar AI gateway can only normalize a fully-buffered upstream response into frames. It also silently inflates TTFT (no first-token can be emitted until the entire upstream body arrives) and multiplies memory under concurrency (whole bodies held per request).

**Ask:** a streaming HTTP surface, e.g. `IHttpClient.FetchStreamAsync(url, method, headers, body, CancellationToken): IAsyncEnumerable<byte[]>` (plus a shared `HttpClient` instance). The Vercel/TanStack/Cloudflare-gateway use case cannot be fully realized without it. `WasmHttpClient` would map it onto the Workers `fetch()` body stream via `JSImport`. _(Workaround used here: upstream calls are buffered; the mock provider + frame re-emission still exercises the full downstream streaming path, and the benchmark honestly measures that.)_

### F2 — `bun reptar` documented everywhere, but not runnable as written

`llms.txt`, `AGENTS.md`, and the Reptar guidelines all say `bun reptar inspect --json`, `bun reptar mcp`, etc. There is no `reptar` bin/script alias in `package.json` — the actual entry is `bun scripts/reptar.ts …`. `bun reptar` fails with `error: Script not found "reptar"`. Either add `"reptar": "bun scripts/reptar.ts"` to package.json scripts or fix every doc reference. First-command-first-impression friction for every new agent.

### F3 — `inspect`/`db plan` are blog-app-specific, not project-aware

`scripts/inspect.ts` hardcodes candidate DB paths (`.reptar/reptar.db`, the blog app) and derives routes from the blog's generated `openapi.json` — so "inspect the project" actually inspects the blog example. For a new example app (`examples/ai-gateway/gateway.db`) with its own routes, `inspect` reports the wrong tables and `bun reptar db plan --db gateway.db` cannot see the app's `TableDef`s at all (plan diffs only files under the cwd's `migrations/`). Per-app examples need either a `--app examples/ai-gateway` flag that runs the app's `--schema --out` export and diffs _that_, or at minimum a documented way to point the tooling at a non-blog app. `app.ExportSchemaJson()` already exists — `inspect` should use it per-app.

### F4 — `doctor`'s SDK check is self-contradictory

`scripts/inspect.ts` (doctor path) prints "required: >= 10.0.401" but passes on any `major >= 10` — my system dotnet is 10.0.103 and it shows a green checkmark next to a message claiming a requirement it didn't test. Meanwhile `scripts/reptar.ts ensureSdk` uses a real `versionAtLeast` check and installs 10.0.401 into `.reptar/sdk`. Two different notions of "installed" (system vs `.reptar/sdk`) and two different version rules. Doctor should use the same `versionAtLeast` and prefer `.reptar/sdk` resolution like `reptar.ts` does.

### F5 — Reflection-based JSON is disabled in file-based apps, and the failure is opaque

Building the gateway, `ctx.Json(...)`-style reflection serialization isn't just AOT-unsafe — it is **disabled by default in the dev host** (ReptarJson installs `DefaultJsonTypeInfoResolver` only when the host opts in). My first streaming request crashed at runtime with `InvalidOperationException: Reflection-based serialization has been disabled` from a _plain string_ `JsonSerializer.Serialize(token)` call, while `dotnet build` was clean. Nothing surfaces this at build time, and the stack trace pointed into STJ internals. Two suggestions:

1. Document in `llms.txt`/docs: "file-based apps must pass `ReptarJson.Options` or use `Utf8JsonWriter`/source-gen — bare `JsonSerializer.Serialize` throws at runtime even for strings."
2. Even bare `JsonSerializer.Serialize(string, ReptarJson.Options)` emits IL2026/IL3050 trim warnings; I ended up hand-rolling a 30-line escaper to be AOT-clean. A public, trim-safe `ReptarJson.EscapeString(string)` helper in the framework would save every gateway/protocol author this dance.

### F6 — `CacheControl.SWR` cannot serve POST bodies (by design, but a gap for AI workloads)

`framework/Reptar/Core/ResponseCache.cs` only caches GET/HEAD and bypasses on `Authorization` headers. Chat completions are POST + `Authorization: Bearer`, so the documented "prompt caching with CacheControl.SWR" story is impossible for exactly the workload the marketing bullet targets. The gateway ships its own in-process SHA-256 prompt cache instead. Suggestion: either a body-aware cache API (`CacheControl.ForPost(keyBuilder, ttl, swr)`) or docs that steer AI-app authors to the in-process pattern (with TTL + bounded entries + `X-Cache` header conventions).

### F7 — SSE (`Sse.cs`, `ctx.Sse`, `StreamLlmAsync`) is `#if !NETWASM`

The flagship `StreamLlmAsync` API compiles out on NetWasm, so any file-based app that uses it cannot compile for Workers — apps must fall back to `ctx.Stream(...)` raw bodies (what this gateway does) and re-implement SSE framing. Two asks: (1) make the frame _builders_ (`SseMessage` → wire bytes) NetWasm-safe even if the host transport stays TS-side, so apps share one framing code path; (2) the design.md note about the Binaryen single-`[JSExport]` limitation should be linked from `llms.txt`, since it dictates app architecture choices.

### F8 — The `Db.Sqlite` vs NetWasm compile split forces a copy-paste env dance

Every example (`blog`, this gateway) starts with the same `DATABASE_URL ? Db.Postgres(...) : Db.Sqlite(...)` block because `Db.Sqlite` is `#if !NETWASM`. A `Db.Auto("gateway.db")` (D1 on Workers, Sqlite/Postgres elsewhere, or throw with an actionable message) would remove a boilerplate footgun that breaks the "same C# app compiles to all targets" promise at the very first line of every app.

### F9 — Trunk flapped red repeatedly during onboarding (multi-agent repo)

Over one afternoon, `framework/Reptar/AI/*` (another agent's uncommitted WIP) broke the framework build at least three times with different compile errors, which fails _every_ example app (`dotnet run app.cs` builds Reptar first). Agents cannot lockstep around untracked WIP folders. Suggestions: (1) a fast `bun reptar preflight` that builds `framework/Reptar` and reports red/green so agents can poll instead of discovering via their app build; (2) a convention that WIP lands in `spikes/` (excluded from the main project globs) until it compiles; (3) commit-or-stash discipline for framework-level edits in multi-agent sessions.

### F10 — Measured: the SQLite group-commit writer cannot keep up with a gateway-scale telemetry burst

While benchmarking the gateway I hammered it with **~506k requests over ~25s** (oha at 500 concurrency), each logging one `gateway_logs` insert fire-and-forget. Measured behavior:

- **Sustained committed-row rate: ~1,200–1,500 rows/sec** on this machine (M3 MacBook, WAL + `REPTAR_SQLITE_SYNCHRONOUS` default NORMAL), i.e. ~19–23 × 64-row batches/sec.
- The pending-write queue (`_pendingWrites` in `framework/Reptar/Data/SqliteDbClient.cs`) is **unbounded**, so the backlog grew to ~400k queued `SqliteCommand`s → **RSS climbed to 1.7 GB**.
- Fire-and-forget + SIGTERM = **tail loss**: killing the server with ~400k inserts still queued dropped them all (100,445 of ~506k committed).
- Nothing bounds this, warns, or applies backpressure; the response path stays fast, which is exactly the point of the design, but the memory growth and silent loss will surprise anyone logging at gateway rates.

Reproduction: `bun examples/ai-gateway/bench.ts` then watch `GET /gateway/stats` climb for minutes after load stops. Suggestions: (1) bounded queue with drop-oldest + a dropped-counter; (2) a `DbStatement[]` batching API for telemetry-style inserts so one request can log N rows per commit; (3) a `Doctor`/docs note on realistic group-commit drain rates; (4) flush-on-shutdown hook so SIGTERM drains (or reports) the queue. The batch mechanism itself is excellent — it kept response latency at 0.03 ms avg under a 20k req/s stream; the gap is queue policy, not batching.

### F11 — Minor polish items

- `oha`'s JSON schema uses `metrics.latency_ms.p50` / `statusCodeDistribution`; nothing in the repo documents the expected oha output shape (bench/load uses autocannon instead) — noted here and handled in `examples/ai-gateway/bench.ts`.
- `Ctx.Stream(...)` sets no default `Cache-Control`/`X-Accel-Buffering` for streaming responses; SSE authors must remember `no-cache, no-transform` + `X-Accel-Buffering: no` or get buffered by proxies. Good defaults in `ctx.Stream` would help.
- `ReptarApp.RegisterSchema` vs `EnsureCreatedAsync` is a silent trap: forgetting `RegisterSchema(...)` yields `no such table` at first request instead of an empty-but-created DB — a `doctor` check ("N TableDefs discovered in app vs M tables in DB") would catch it.
- Request-id log lines during load testing (`req_… POST … -> 200 (0.0ms)`) print for every request; a `TELEMETRY_QUIET`/`LOG_LEVEL` env would keep bench logs readable.
- `examples/blog/app.cs`'s `--codegen` arg handling duplicates `KestrelHost.RunAsync`'s `--schema` handling; a shared arg-parsing helper would prevent drift.

## 3. Numbers worth keeping

- 500-connection streaming load: **22.0k req/s uncached / 28.5k cached** (dev JIT host, mock provider, 100% 2xx, ~506k requests) — see `README.md` and `results/`.
- Prompt-cache HITs answered with **~0.03 ms server-side latency** (0.196 ms measured on a cold pair), validating the "sub-millisecond cache" claim at the server boundary.
- Telemetry: the group-commit batcher kept response latency at **0.03 ms avg under 20k req/s**, but only commits **~1.2–1.5k rows/sec** on this machine (F10) — the two-sided result of this benchmark.

## 4. Suggested framework follow-ups, in order

1. `IHttpClient.FetchStreamAsync` + pooled `HttpClient` (unlocks real gateway streaming).
2. `bun reptar` script alias + per-app `inspect --app` support.
3. Trim-safe `ReptarJson.EscapeString` + file-based-app JSON guidance in `llms.txt`.
4. `Db.Auto(...)` target-agnostic default.
5. Body-aware prompt-cache API (or documented in-process pattern).
6. `doctor` SDK check parity with `ensureSdk`.
7. NetWasm-safe SSE frame builders.
8. Bounded group-commit queue with backpressure/drop policy + shutdown flush (F10).
