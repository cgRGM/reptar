# Core Focus: Performance, Reliability, and Observability Roadmap

**Status:** proposal — no framework code changed  
**Date:** 2026-10-26  
**Scope:** framework core, hosts, data layer, telemetry, CLI/templates, benchmarks, docs.  
**Related:** `design.md`, `llms.txt`, `plans/master-audit-and-roadmap.md`, `plans/adonisjs-core-audit.md`, `plans/product-scope-and-module-boundaries.md`, `bench/README.md`, `apps/docs/src/content/docs/observability/telemetry.md`.

> **Product decision:** Reptar is a web framework for CRUD, SaaS, AI apps, and AI agents. Stripe and realtime stay in core; AI becomes an optional `Reptar.AI` SDK; the terminal agent is an example app; the AI gateway becomes an optional SDK. See [`plans/product-scope-and-module-boundaries.md`](./product-scope-and-module-boundaries.md).
>
> **Progress:** the first implementation pass landed `Rule<T>` hot-path fixes, telemetry v1, timeout/module lifecycle, preload v1, `make:model`/`make:feature`, JSON context build-time generation, the `Reptar.AI`/terminal/gateway split, and architecture/bench/size guards. See [`plans/core-focus-implementation-log.md`](./core-focus-implementation-log.md) for what changed, what was verified, and what is still open.

---

## 0. TL;DR

Reptar already has enough features. The next gains do **not** come from adding more framework surface. They come from making the existing surface:

1. **Measurable** — performance gates in CI, AOT/Wasm publish smoke, size/cold-start budgets.
2. **Honest on the hot path** — remove reflection and avoidable allocations from validation, JSON, logging, and the Wasm boundary.
3. **Observable** — structured logs, request/trace IDs, DB timing, metrics/OTLP, health/readiness, slow query/route detection. The docs already promise most of this; the code does not.
4. **Reliable** — timeouts, cancellation, graceful shutdown, fail-closed config, migration safety, cache correctness.
5. **Fast where it matters** — fewer DB round trips (preload/batching), cache/ETag/compression, connection reuse.
6. **Lean** — core stays small: Stripe and realtime stay in core; AI moves to an optional `Reptar.AI` SDK; terminal and BaaS Studio stay examples; the AI gateway becomes an optional SDK.

The framework's current performance story is genuinely good: on the calibrated VPS benchmark Reptar is at or above raw .NET Minimal API on HTTP/JSON/routing/validation, wins 10 of 12 tiers, and ships a ~10.4 MB AOT binary. The problem is that those numbers are not gated, and the observability/reliability story is ahead of the implementation. Fix that before adding preload, `make:*`, or lifecycle hooks.

**The one-sentence strategy:** _Make the fast path measurable, make the failure path observable, keep the core small, and add only features that remove a measured bottleneck._

---

## 1. Current-state scorecard

| Area | What is strong | What is missing / risky | Priority |
| :-- | :-- | :-- | :-- |
| **HTTP / routing** | Static-route dictionaries, param-route matching, 41.9k–42.2k req/s on VPS T0/T1, at/above Minimal API | No performance regression gate; no route-count stress test | **P0 (gate)** |
| **JSON** | `SourceGenJsonCodec` is fast and AOT-safe; `JsonRaw` exists | Default codec is reflection; anonymous/unregistered records still fail AOT/Wasm; manual `JsonSerializerContext` maintenance | **P0 (fix)** |
| **Validation** | `Rule<T>` is expressive; generated Standard Schema/OpenAPI | `Rule<T>` uses `PropertyInfo.GetValue` on Native AOT and `Expression.Compile()` on Wasm; boxes values; allocates a `List<ValidationIssue>` per property per request; bench uses manual validation because `Rule<T>` is not AOT-safe | **P0 (fix)** |
| **Data** | Dapper-like explicit SQL, `ColumnRef<T>`, `QueryMappedAsync`, transactions, SQLite WAL, Postgres pooling | No preload/batching; `IDbClient` has no cancellation/timeout; D1 round trips; no slow-query instrumentation | **P1** |
| **Caching** | SWR middleware, `CacheControl.Invalidate`, ETag for static files | In-process only; `Task.Run` revalidation is not Worker-safe; no single-flight on misses; no JSON ETag/conditional GET; no cache metrics | **P1** |
| **Compression / streaming** | SSE, WebSockets, streaming responses | No gzip/brotli negotiation; large JSON lists pay full bandwidth; Wasm boundary base64-encodes bodies | **P1** |
| **Observability** | `Server-Timing` (total), SQLite `WriteTimingObserver`, health probes | Telemetry middleware is a plain string log; no structured JSON fields, trace IDs, DB timing, metrics, OTLP, `/_reptar`, or slow-query logs. Docs claim all of these. | **P0** |
| **Reliability** | 215+ tests, CI (tests, netwasm lint, doctor, realtime smoke, scaffold, contracts), error envelope, body limits, rate limits | No request/DB/HTTP timeouts; no cancellation propagation; no graceful app shutdown hooks; doctor can pass with a missing DB; migrations lack checksums/dry-run; cache correctness gaps | **P0/P1** |
| **Performance measurement** | Excellent calibrated `bench/` harness (T0–T11, 3 stacks, CPU pinning, fairness notes) | Not in CI; results mix JIT/AOT labels and cached/uncached figures; no binary/Wasm/cold-start budgets | **P0** |
| **Core size / bloat** | Wasm target already excludes AI and `AiTools`; AOT trims; Stripe/realtime are thin | Native AOT core project contains AI (30 files, ~500 KB source) and terminal UI; AI/terminal should move out; Stripe/realtime stay in core by decision; no size budget | **P1/P2** |
| **DX** | `create-reptar`, templates, `inspect`, `doctor`, MCP, `test:scaffold`, generated contracts | No `make:*`; templates still have some AOT-JSON traps; docs drift; two project shapes (file-based vs csproj) | **P2** |

---

## 2. What "most performant" actually means here

### 2.1 Measure before optimizing

The benchmark data already says where time goes:

- HTTP/JSON/routing/validation are at or above raw .NET Minimal API (T0 ~41.9k, T1 ~42.2k, T2 ~41.4k, T3 ~36.3k req/s on the VPS).
- DB-heavy tiers are much lower: ~13.8k single-task, ~5.9k list, ~10.7k comments, ~6.2k creates, ~4.9k updates.
- SWR-cached reads are 3.4×–5.6× faster than uncached.
- Mixed workload is ~6.1k req/s, roughly -2% vs Minimal API and below Hono on Bun (~7.3k).

So the remaining performance work is **not** "make the router 5% faster." It is:

1. **Reduce DB round trips and SQLite write-gate contention.**
2. **Cache correctly and cheaply.**
3. **Stop paying reflection/boxing/allocations on validation and JSON.**
4. **Reduce Wasm boundary overhead.**
5. **Keep binary/Wasm size and cold start under budget.**

### 2.2 The performance gates that must exist

Without gates, every performance claim is a one-off measurement that rots.

Add to CI:

- **Fast gate on every PR:** T0/T1/T3/T4/T10 (or a smaller subset) with generous thresholds; fail on >10–15% regression in RPS or p95. Use a fixed runner or a self-hosted box; GitHub-hosted runners are noisy.
- **Nightly full gate:** T0–T11, 3 reps, CPU pinning, cached and uncached variants; publish artifacts and a trend report.
- **AOT smoke:** `PublishAot=true`, run the binary, hit a route with a registered record and a DB query; assert a native executable, not a JIT folder.
- **Wasm smoke:** build the Worker, run under `workerd`, hit the same route; assert module size and cold-start budget.
- **Size budgets:** core AOT binary, Wasm module, and optional modules. Fail on unexpected growth.
- **Allocation budgets:** BenchmarkDotNet microbenchmarks for routing, JSON, validation, middleware, and DB mapping; fail on regressions.
- **Benchmark honesty:** label JIT vs AOT, cached vs uncached, and local vs VPS. Do not compare numbers from different setups.

### 2.3 Hot-path work that actually pays

1. **`Rule<T>` source generation.** Replace `PropertyInfo.GetValue` / `Expression.Compile` with generated direct property getters. Avoid `object?` boxing. Return `ValidationIssue[]?` (null when valid) or use a pooled builder. This is both a performance and an AOT-correctness fix.
2. **JSON context generation.** Auto-emit `[JsonSerializable]` for every `Produces<T>` and input type. Remove manual `AppJsonContext` maintenance. Add an analyzer for anonymous/unregistered `ctx.Json`.
3. **Middleware/logging allocations.** The current `Telemetry` middleware allocates a string per request and calls `Console.WriteLine` synchronously. Use a structured log record, sampling, and a buffered/async sink. Null-check fast path when telemetry is off.
4. **Wasm boundary.** The host base64-encodes request/response bodies for binary safety. Add a text fast path for `application/json`/`text/*` so JSON APIs do not pay 33% base64 overhead and extra copies. Measure T1/T4 on workerd before/after.
5. **DB batching.** D1: use `batch()` for multi-statement writes; preload: `WHERE IN` batching; Postgres: `NpgsqlBatch` where appropriate. Reduce round trips, not just CPU.
6. **Cache/ETag/compression.** Add JSON ETag + `If-None-Match` 304s, gzip/brotli for large responses (opt-in, size threshold), and cache hit/miss metrics. This is often a bigger win than micro-optimizing the router.
7. **Connection reuse.** Ensure Postgres `NpgsqlDataSource` is long-lived and disposed on shutdown; SQLite read pool is bounded; D1 calls are batched. No per-request connection creation.

### 2.4 What not to optimize yet

- Route matching: it is already fast and not the bottleneck for DB-backed APIs. Add a route-count stress test; only switch to a trie if the data says so.
- `ValueTask` conversion of the whole pipeline: potentially useful, but a large public API change. Measure allocation profiles first.
- Custom JSON writer: `System.Text.Json` source-gen is already good; only hand-roll if a profile shows it matters.
- LINQ removal everywhere: remove it from hot paths that show up in profiles, not everywhere.

---

## 3. What "most reliable" actually means here

Reliability is mostly about **failure behavior**, not happy-path features.

### 3.1 Timeouts and cancellation

Current state: `IDbClient` and `IHttpClient` do not accept a `CancellationToken`; DB commands have no command timeout; outbound HTTP has a static `HttpClient` default timeout; request handlers have no framework-level timeout.

Add:

- `RequestTimeout(TimeSpan)` middleware that cancels the handler and returns `504`/`503` with a structured error.
- `CancellationToken` on `IDbClient` query/execute methods (breaking but necessary) or a `CommandTimeout` property on each client. Propagate `ctx.RequestAborted` from the host.
- `CancellationToken` + timeout on `IHttpClient.FetchAsync`/`FetchBytesAsync`.
- D1 host-side timeout; Workers CPU limits are not a substitute for a clean timeout.
- A `Timeout` option on `ReptarConfig` with sensible defaults (e.g. 30s request, 10s DB, 15s outbound HTTP), overridable per route.

### 3.2 Graceful shutdown

Current state: Kestrel runs `web.RunAsync()`; `ReptarApp` has no start/stop hooks; `SqliteDbClient` is `IDisposable` but not disposed by the host; `NpgsqlDataSource` is not disposed.

Add a small explicit lifecycle (see `plans/adonisjs-core-audit.md` §5):

```csharp
public interface IReptarModule
{
    void Configure(ReptarApp app);
    Task StartAsync(ReptarApp app, CancellationToken ct) => Task.CompletedTask;
    Task StopAsync(ReptarApp app, CancellationToken ct) => Task.CompletedTask;
}
```

- Kestrel: start before serving, stop on `IHostApplicationLifetime.ApplicationStopping`.
- Lambda: start during init; stop best-effort.
- Workers: configure/start only; no shutdown promise.
- Dispose DB pools, flush telemetry, stop background workers.

### 3.3 Fail-closed configuration and doctor

- `doctor` must fail when the configured database is unreachable, not just when schema classes exist. The master audit already flags this.
- Validate config at startup: missing secrets, invalid URLs, missing `PublishAot`/source-gen JSON, incompatible options.
- `build` must assert AOT actually produced a native executable; the Dockerfile currently masks failures with `mv ... || true`.
- `check:netwasm --strict` should be part of CI (it already is) and should catch unregistered-record JSON, not just anonymous/dictionary.

### 3.4 Migrations and data safety

- Add checksums to `__reptar_migrations` to detect edited/applied migrations.
- Add a lock/advisory lock for concurrent migration runs (Postgres, SQLite, D1 where possible).
- Add `db plan`/`db generate` dry-run output and a `--check` mode for CI.
- Add transaction helpers with retry semantics for transient failures; document isolation levels.
- Add idempotency keys for write endpoints (optional middleware) — a real reliability feature for APIs.
- Add `RETURNING`/upsert helpers where the dialect supports them; avoid read-modify-write races.

### 3.5 Cache correctness

- SWR currently uses `Task.Run` for revalidation. On Workers, background tasks freeze between events; use `ctx.WaitUntil` or a host-provided cache. Gate/disable SWR on NetWasm if it cannot be made correct.
- Add single-flight on cache misses (stampede protection), not just revalidation.
- Add `Vary` correctness tests; don't cache authenticated responses by default.
- Add cache metrics (hit/miss/stale/bypass) and invalidation tests.
- Consider a shared cache adapter (KV/D1/Redis) for multi-instance Kestrel; in-process cache is not enough for horizontally scaled apps.

### 3.6 Tests that prove reliability

- AOT publish + route smoke on every PR.
- Wasm build + workerd smoke on every PR.
- Multi-DB integration: SQLite, Postgres, MySQL, D1 (where CI allows).
- Failure injection: DB down, timeout, malformed JSON, oversized body, rate limit, auth failure, migration failure.
- Load test with error rate and p99, not just RPS.
- Docs snippet check already exists; keep it strict.

---

## 4. What "most observable" actually means here

### 4.1 The gap

The docs promise:

- structured JSON logs with `request_id`, `trace_id`, `route`, `status`, `duration_ms`, `db_ms`, `db_queries`, `user_id`, `cf_ray`, `colo`;
- granular `Server-Timing`;
- `/_reptar` dev dashboard;
- OTLP/Axiom export with `AxiomOptions`.

The code currently has:

- `Telemetry()` middleware that logs `"{requestId} {method} {path} -> {status} ({ms}ms)"` via `ConsoleAppLogger`;
- `Server-Timing` with total request time only;
- `TelemetryOptions` with a single `Enabled` flag;
- `Axiom()` returning an empty `TelemetryOptions`;
- no `/_reptar`, no OTLP, no structured fields, no DB timing.

This is the biggest **credibility** gap in the framework. Either implement it or remove the claims. Implementing it is a differentiator; docs drift is a reliability bug.

### 4.2 Minimum viable observability (MVO)

1. **Structured request log.** One JSON line per request, with: `timestamp`, `level`, `request_id`, `trace_id`, `method`, `route` (template, not raw path), `path`, `status`, `duration_ms`, `db_ms`, `db_queries`, `cache`, `user_id`, `cf_ray`, `colo`, `error_code`.
2. **DB instrumentation.** Add an optional `IDbObserver`:
   ```csharp
   public interface IDbObserver
   {
       void OnQuery(in DbQueryEvent query);
   }
   public readonly record struct DbQueryEvent(
       string Sql, long ElapsedTicks, int Rows, string? Error);
   ```
   Implement in SQLite/Postgres/MySQL/D1. Null by default; only call `Stopwatch.GetTimestamp()` when an observer exists. Use it for `db_ms`, `db_queries`, slow-query logs, and traces.
3. **Server-Timing.** Add `db`, `cache`, `auth`, and `total` entries automatically. Keep `ctx.ServerTiming` for custom metrics.
4. **Health/readiness.** `MapHealth` exists; extend it with DB/cache/queue/migration checks and structured JSON. Add `?verbose` in dev.
5. **Metrics endpoint or OTLP.** Start with a Prometheus-text `GET /metrics` (opt-in) with low-cardinality route/status counters and duration histograms. Add an OTLP/HTTP exporter later, or bridge through the host on Workers. Do not pull the full OpenTelemetry SDK into the Wasm core.
6. **Slow query/route logs.** Thresholds configurable; log SQL fingerprint (not full SQL with PII), duration, route, request_id. Always log errors and slow requests; sample successful fast requests.
7. **Error correlation.** Include `error_id`/`request_id` in error envelopes; log exception type/message/stack in dev, sanitized message in prod.
8. **Dev dashboard (`/_reptar`).** Dev-only in-memory ring buffer: last N requests, p50/p95/p99, slow routes, validation failures, cache stats. Tree-shake in Release or make it an optional package. Do not ship it in the hot path.

### 4.3 Overhead rules

- Everything is **opt-in** or **sampled**; no telemetry means only a null check.
- No per-request dictionaries if avoidable; use a struct/record and `Utf8JsonWriter`.
- Log asynchronously/buffered on Kestrel; on Workers, `console.log` is the sink.
- Metrics labels must be low-cardinality: route template, method, status class; never raw IDs or query strings.
- Sampling: 100% errors/slow requests, 1–10% successful fast requests (configurable).
- Document the cost; add a benchmark tier with telemetry on (`BENCH_MIDDLEWARE=1` exists) and a budget.

---

## 5. What "best for building APIs and apps" means

### 5.1 API-first, app-capable, batteries optional

- **Core:** router, `Ctx`, validation, data, auth, JSON, contracts/codegen, telemetry, caching, storage, queues, **Stripe**, **realtime** (SSE/WebSockets/streaming + thin protocol adapters), host abstractions, and the `.AsAiTool` route bridge.
- **Optional packages:** `Reptar.AI` (LLM clients, agent harness, sessions, RAG/embeddings, evals, usage/cost) and `Reptar.AI.Gateway` (provider routing/failover, OpenAI-compatible proxy, prompt cache, usage sink).
- **Examples:** `agent-cli` (terminal UI), `ai-gateway` (auth/keys/dashboard), `baas`/Studio, `realtime-rooms`, `blog`, `realworld`.
- **Templates:** `api` (minimal production API), `ai-app` (streaming chat + `Reptar.AI`), `ai-gateway` (gateway SDK + auth/dashboard), `fullstack` (frontend wiring), `agent` (example app). The framework itself stays backend-only.

### 5.2 The golden path

```bash
bun reptar new my-api --template api --db postgres
bun reptar make:model Post -m -f
bun reptar dev            # Kestrel + SQLite + /_reptar
bun reptar codegen        # TS client + OpenAPI + Standard Schema
bun reptar deploy         # AOT container or Workers
```

A generated API should ship with, by default:

- health/readiness endpoints;
- structured request logs and request IDs;
- request/DB/HTTP timeouts;
- graceful shutdown;
- migrations with checksums;
- auth (built-in) and rate limits;
- generated contracts;
- a test project and a scaffold smoke test;
- no anonymous JSON, no reflection in the hot path.

That is what makes Reptar better for building APIs than a bag of features: the default path is production-shaped.

### 5.3 What "apps" should not mean

Reptar is not a full-stack framework. "Apps" means the backend for an app: auth, data, files, realtime, background jobs, and payments (Stripe lives in core, mounted only when the app needs it). It does **not** mean:

- a UI kit or frontend router;
- an admin panel/CMS;
- a GraphQL server;
- a plugin marketplace;
- a multi-tenant SaaS platform out of the box.

Those can be templates/examples/optional packages, not core.

---

## 6. Keep / Improve / Add / Avoid

| Keep | Improve | Add | Avoid |
| :-- | :-- | :-- | :-- |
| Hono-like router | `Rule<T>` (AOT-safe, allocation-free) | Performance gates | ORM / Active Record |
| `TableDef` + `ColumnRef<T>` | JSON contexts (generated) | Structured observability | DI container |
| Built-in auth | Timeouts/cancellation | Preload/batching | Admin UI / CMS |
| Generated TS/OpenAPI contracts | Cache correctness | `make:*` generators | GraphQL |
| AOT/NetWasm dual target | Graceful shutdown | Metrics/OTLP | Plugin marketplace |
| Cloudflare bindings | Wasm boundary overhead | Lean `api` template | Multi-tenancy/feature flags by default |
| `inspect`/MCP/doctor | Docs-code parity | `Reptar.AI` / gateway split | AI runtime in core by default |
| SSE/WebSockets/streaming | Compression/ETag | Idempotency/retries | WebSockets as the default realtime path |
| Stripe + realtime | Stripe/realtime hardening | AI SDK + gateway SDK | Terminal UI in core |

## 6.1 What not to add (anti-bloat list)

| Do not add | Why |
| :-- | :-- |
| **ORM / Active Record** | Preload + explicit SQL covers the real need. An ORM would add reflection, migrations complexity, and hidden queries — against the design. |
| **DI container / `IServiceCollection`** | Explicit composition + optional lifecycle modules is enough. A container adds runtime resolution, reflection/decorators, and startup cost. |
| **Admin UI / CMS / Studio in core** | The BaaS Studio is a great example/template, not framework surface. Keep the core API-only. |
| **GraphQL** | Generated REST/OpenAPI/TS contracts already serve the frontend. GraphQL is a separate product decision. |
| **Plugin marketplace** | Stabilize the core and optional package boundary first. |
| **Multi-tenancy / feature flags** | Add when a real app needs them; they complicate every query and config path. |
| **AI runtime in core** | Decision: AI is a first-class **optional** module. Move the LLM/agent runtime to `Reptar.AI` and the gateway core to `Reptar.AI.Gateway`; keep only the small `.AsAiTool` route bridge in core. Don't let AI/terminal code accrete in the web core. |
| **WebSockets everywhere** | SSE/HTTP streaming covers most realtime needs. WebSockets are already supported; don't make them the default. |
| **Custom JSON/HTTP stack** | `System.Text.Json` source-gen + Kestrel/Workers are good. Only hand-roll after a profile proves it. |

---

## 7. Roadmap: Now / Next / Later

### Now (0–4 weeks) — trust the numbers and the failure path

| Work | Acceptance criteria |
| :-- | :-- |
| **Performance gate in CI** | T0/T1/T3/T4/T10 run on a stable runner; fail on >10–15% RPS/p95 regression; artifacts published. |
| **AOT/Wasm smoke + size budgets** | `PublishAot` produces a native binary; workerd smoke passes; core binary/Wasm size and cold start budgets enforced. |
| **Architecture guard + size baseline** | CI fails if `Reptar` references `Reptar.AI`, terminal namespaces, `System.Diagnostics.Process`, or termios P/Invoke; record core AOT/Wasm size and dependency list before the split. |
| **Fix `Rule<T>` hot path** | Generated direct getters, no `PropertyInfo.GetValue`, no `Expression.Compile`, no boxing, no per-property `List` allocation; bench T3 uses `Rule<T>` and matches manual validation. |
| **Generated JSON contexts + analyzer** | Every `Produces<T>`/input type registered; analyzer flags anonymous/unregistered `ctx.Json`; templates/examples have zero anonymous JSON. |
| **Observability v1** | Structured request log with request_id/trace_id/route/status/duration/db_ms/db_queries; DB observer; Server-Timing db/cache/total; health/readiness; docs match code. |
| **Reliability v1** | Request timeout middleware; DB/HTTP timeouts; cancellation propagation; graceful shutdown hooks; doctor fails closed; migration checksums/dry-run. |

### Next (1–3 months) — make the fast path easy

| Work | Acceptance criteria |
| :-- | :-- |
| **Preload v1** | `BelongsTo` + `HasMany`, explicit relation metadata, chunked `WHERE IN` batching, generated setters, JSON context updates, SQLite/Postgres/D1 tests; RealWorld list endpoint uses it; query count drops. |
| **Cache/ETag/compression** | JSON ETag + 304; gzip/brotli opt-in with size threshold; single-flight; Worker-safe SWR; cache hit/miss metrics. |
| **D1 batch + Postgres batch** | Multi-statement writes use one round trip where possible; benchmarks show fewer round trips and lower p95. |
| **Wasm boundary fast path** | JSON/text bodies avoid base64; workerd benchmark shows lower CPU/latency; binary bodies still base64. |
| **`make:model`/`make:feature`** | Stub-based generators in the project-local CLI; generated code builds, codegens, passes `check:netwasm`; scaffold smoke covers it. |
| **Metrics/OTLP v1** | `/metrics` or OTLP exporter; low-cardinality labels; sampling; documented overhead; benchmark with telemetry on. |

### Later (3–6 months) — production confidence and a lean core

| Work | Acceptance criteria |
| :-- | :-- |
| **Core/optional split** | Move AI SDK to `Reptar.AI`, gateway core to `Reptar.AI.Gateway`, terminal UI to `examples/agent-cli`; keep Stripe/realtime in core; core AOT size and public surface drop (the Wasm target already excludes AI); apps opt in explicitly. |
| **Idempotency + retries** | Idempotency-key middleware for writes; retry/backoff helpers for transient DB/HTTP failures; docs and tests. |
| **Migration safety** | Checksums, locks, dry-run/check mode, rollback tests across SQLite/Postgres/D1. |
| **Golden-path templates** | `api` template ships with observability, timeouts, health, migrations, tests, and no AOT traps; `fullstack` and `agent` remain optional. |
| **Docs truth pass** | Every observability/performance claim maps to code or is removed; benchmark labels are accurate. |

---

## 8. First PRs (concrete, small, high leverage)

1. **PR 1 — `Rule<T>` source-gen getters + allocation-free validation.**
   - Add a source generator or generic `PropertyRule<T, TProp>` that avoids `object?` boxing.
   - Return `ValidationIssue[]?` (null when valid) from `Validate`.
   - Add a BenchmarkDotNet microbenchmark and a `Rule<T>` tier in `bench/`.
   - Acceptance: no reflection in the validation hot path; T3 with `Rule<T>` matches manual validation within noise; AOT smoke passes.

2. **PR 2 — JSON context generator + analyzer.**
   - Emit `[JsonSerializable]` from `Produces<T>`/input types; keep the app's `partial class AppJsonContext`.
   - Analyzer: warn/error on anonymous/dictionary/unregistered `ctx.Json`.
   - Replace remaining template/example anonymous JSON.
   - Acceptance: zero `check:netwasm` JSON warnings; AOT smoke hits a record route and an anonymous route (the latter should fail at build, not runtime).

3. **PR 3 — Observability v1.**
   - `RequestTelemetry` record + structured JSON log sink; `IDbObserver` in SQLite/Postgres/D1; `Server-Timing` db/cache/total; health/readiness; docs updated.
   - Acceptance: a request log contains all fields; `db_queries` is accurate; overhead with telemetry off is a null check; benchmark with telemetry on is documented.

4. **PR 4 — Timeouts + graceful shutdown.**
   - `RequestTimeout` middleware; `CancellationToken`/command timeout on DB/HTTP; `IReptarModule` start/stop; dispose pools on Kestrel shutdown.
   - Acceptance: a slow handler returns 504; a canceled request stops the DB query; Kestrel SIGTERM runs `StopAsync`; Workers build unaffected.

5. **PR 5 — Performance gate.**
   - CI job for T0/T1/T3/T4/T10 with thresholds; AOT/Wasm smoke; size/cold-start budgets; trend artifacts.
   - Acceptance: a deliberate 20% regression fails the job; a normal change passes; results are published.

---

## 9. Success metrics

### Performance

- T0–T11 RPS, p50/p95/p99 on a fixed runner.
- AOT binary size, Wasm module size, cold start, RSS.
- Allocations/request for routing, JSON, validation, middleware.
- DB round trips/request for list/detail/write endpoints.
- Cache hit ratio and cache-correctness tests.

### Reliability

- AOT/Wasm smoke pass rate.
- Error rate, timeout rate, 5xx rate under load.
- Migration success/rollback rate.
- Flaky-test rate; test coverage of failure paths.
- Docs-code parity for claimed features.

### Observability

- % of requests with structured logs.
- % of DB queries with timing.
- Trace coverage (sampled) and metric coverage per route/status.
- Slow-query/route detection coverage.
- Time from error to root cause in a staging incident.

### DX / bloat

- Time to first endpoint (`new` → `make:model` → `dev`).
- Scaffold success rate and codegen accuracy.
- Core assembly size, dependency count, public API count.
- Number of optional modules vs core modules.

---

## 10. Bottom line

The framework does not need another 20 features. It needs **five disciplines**:

1. **Gate the numbers** so speed cannot silently regress.
2. **Remove reflection and allocations from the hot path** (`Rule<T>`, JSON, logging).
3. **Make observability real** (structured logs, DB timing, metrics/OTLP, health, slow query/route).
4. **Make failure behavior explicit** (timeouts, cancellation, shutdown, fail-closed config, migration/cache safety).
5. **Keep the core small** (optional AI/Stripe/realtime; no ORM, no DI container, no admin UI).

Then add the features that remove measured friction: preload/batching, `make:*` generators, cache/ETag/compression, and a lean `api` template. That is how Reptar becomes the most performant, reliable, observable framework for APIs and apps without becoming bloated.

---

## Sources and evidence

- `bench/README.md` — calibrated T0–T11 harness, fairness notes, AOT/validation caveats, latest results.
- `bench/results/FINDINGS.md` — performance findings, cached vs uncached, remaining bottlenecks.
- `framework/Reptar/Validation/Validator.cs` — `PropertyInfo.GetValue` / `Expression.Compile`, boxing, per-property list allocations.
- `framework/Reptar/Core/JsonCodec.cs` — `SourceGenJsonCodec` / `DefaultJsonCodec`.
- `framework/Reptar/Core/ReptarJson.cs` — reflection resolver on non-NetWasm.
- `framework/Reptar/Core/Middleware.cs` — current `Telemetry` implementation.
- `framework/Reptar/Core/ReptarApi.cs` — empty `TelemetryOptions`, `Axiom()`.
- `framework/Reptar/Core/ResponseCache.cs` — in-process SWR, `Task.Run` revalidation.
- `framework/Reptar/Data/Services.cs` — `IDbClient`, `IHttpClient` without cancellation/timeouts.
- `framework/Reptar/Data/SqliteDbClient.cs` — WAL, write gate, `WriteTimingObserver`.
- `framework/Reptar/Data/PostgresDbClient.cs` — `NpgsqlDataSource`, transactions, batch.
- `framework/Reptar.Wasm/Data/WasmHttpClient.cs` — host HTTP bridge without timeout/cancellation.
- `packages/host/src/host.ts` — JSON envelope, base64 request/response bodies, pending maps.
- `framework/Reptar.Kestrel/KestrelHost.cs` — `RunAsync`, no app lifecycle hooks.
- `framework/Reptar.Lambda/LambdaHost.cs` — custom runtime loop, cancellation.
- `.github/workflows/ci.yml` — current CI gates (no benchmark/AOT/Wasm publish).
- `apps/docs/src/content/docs/observability/telemetry.md` — claimed observability features vs implementation.
- `plans/master-audit-and-roadmap.md` — known scaffolding/AOT/doctor gaps.
- `plans/adonisjs-core-audit.md` — CLI/preload/lifecycle adaptation audit.
