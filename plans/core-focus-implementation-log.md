# Core Focus — Implementation Log

**Date:** 2026-10-26  
**Scope:** first implementation pass on the priorities in `plans/core-focus-roadmap.md` and `plans/product-scope-and-module-boundaries.md`.

---

## Landed

### 1. `Rule<T>` hot path

- Replaced `PropertyInfo.GetValue` / `Expression.Compile()` in the non-Wasm path with interpreted expression trees (`Compile(preferInterpretation: true)`) and a reflection fallback only when the runtime rejects the interpreter.
- `Validator<T>.Prop` now accepts `Expression<Func<T, TProp>>`, avoiding the `object?` conversion in the expression.
- Validation no longer allocates a `List<ValidationIssue>` per property or on the valid path; `Validate` returns `Array.Empty<ValidationIssue>()` when clean.
- Added `DynamicallyAccessedMembers(PublicProperties)` on `Validator<T>` so the reflection fallback remains trim-safe.
- Existing tests pass; validation semantics unchanged.
- Verified with a real Native AOT publish (`PublishAot=true`, osx-arm64): `Rule<T>` validates correctly (`ok=0 bad=2`) and the `IL2091` trim warning was fixed by annotating `ReptarApi.Rule<T>`.

- Added a generic `PropertyRules.Check<TProp>` fast path: strings and common numeric types no longer box through `object?`; uncommon types fall back to the object path.
- `Rule<T>` property getters now use compiled IL on JIT and `MethodInfo.CreateDelegate<Func<T,TProp>>` on Native AOT. The old interpreted expression tree allocated ~632 B and took ~417 ns per valid validation; the AOT delegate path is ~66 ns and 0 B. Reflection `GetValue` remains the fallback for value-type receivers or runtimes that reject `CreateDelegate`.

**Benchmark (`bench/validation`, Apple arm64):**

| Case | JIT median | AOT median | JIT/AOT alloc |
| :--- | ---: | ---: | ---: |
| declarative/valid | ~50 ns | ~66 ns | 0 B |
| manual/valid | ~15 ns | ~6 ns | 0 B |
| declarative/invalid | ~207 ns | ~281 ns | 656 B |

**Still open:** the invalid path allocates issue objects; that is acceptable for an error path, but pooling or a struct issue list is a possible follow-up.

### 1b. Build-time JSON context generation

- `packages/cli/src/json-context.ts` (re-exported from `scripts/json-context.ts`) scans `[ReptarEntity]`, `Rule<T>`, and `Produces<T>` and writes `json-context.g.cs`.
- `bun reptar json:gen` runs it; `make:model` runs it automatically and wires the include.
- `ReptarJson.RegisterContext` + `SourceGenJsonCodec`/`GeneratedFirstResolver` discover the context without a manual `AppJsonContext` list.
- Verified: generated context compiles with the STJ source generator and is visible to the app.

### 2. Observability v1

- `RequestTelemetry` structured record and JSON line output.
- `DbMetrics` + `InstrumentedDbClient`: per-request query count, total DB time, slow-query count/slowest SQL.
- `IAppLogger.Request` structured sink; `ConsoleAppLogger` writes JSON.
- Telemetry middleware now emits `db_ms`, `db_queries`, route template, status, cache, user, `cf_ray`, colo, error code, slow flag.
- W3C `traceparent` parsing and `trace_id` / `span_id` / `parent_span_id` (integrated with the concurrent trace-context work).
- Automatic `Server-Timing: db;..., total;...`.
- Sampling (`SampleRate`), slow-request threshold, slow-query threshold.
- `RequestId` + `Telemetry` remain opt-in middleware.

**Tests:** `TelemetryTests` cover structured fields, DB timing, traceparent, `Server-Timing`, and sample-rate zero.

### 3. Reliability

- `Timeout(TimeSpan)` middleware: returns `504 request_timeout`, cancels `ctx.RequestAborted`, observes late handler faults.
- `ctx.RequestAborted` wired from Kestrel (`http.RequestAborted`) and Lambda (runtime cancellation token).
- `IReptarModule` with `Configure` / `StartAsync` / `StopAsync`; `ReptarApp.AddModule`, `StartModulesAsync`, `StopModulesAsync`, `DisposeAsync`.
- Kestrel starts modules before serving and stops/disposes on shutdown; Lambda does the same around its runtime loop.
- `InstrumentedDbClient` and `PostgresDbClient` dispose their inner/pool resources.
- `ResponseCache` no longer uses `Task.Run` background revalidation under NetWasm; it falls through to synchronous revalidation because Workers freeze between events. Native cache misses are single-flight (SemaphoreSlim) to prevent stampedes; the Wasm path skips SemaphoreSlim because NetWasm does not expose it.
- Kestrel response compression is enabled for JSON/text/HTML/SVG with SSE excluded.
- `DefaultHttpClient` has a configurable timeout (`REPTAR_HTTP_TIMEOUT_SECONDS`, default 100s).
- `PostgresDbClient` has `CommandTimeoutSeconds` (default 30s), including transaction-context commands.
- `UseETag()` / `ETag()` middleware adds strong ETags to successful JSON/text GET/HEAD responses and returns `304` on `If-None-Match`; it skips `no-store`, cookies, non-GET, and oversized bodies.
- `UseMetrics()` / `MapMetrics()` exposes dependency-free Prometheus counters and latency histograms at `GET /metrics`, with route-template labels.
- `UseOtlp()` batches request records to an OTLP/HTTP logs endpoint and flushes on host shutdown; export failures never fail requests.
- `MapReptarDashboard()` exposes a local-only `GET /_reptar` page with routes, tables, cache count, and metrics.
- `UseSWR()` reports cache outcomes (`HIT`/`MISS`/`STALE`/`BYPASS`) to the same metrics registry.
- `SwrOptions.Store` accepts an `ISwrCacheStore`; the default is a bounded in-process map, and `FileSwrCacheStore` shares entries between native processes.
- SQL migrations store a SHA-256 checksum in `__reptar_migrations`; re-running an edited applied migration throws instead of silently diverging. Existing databases get the nullable column automatically.
- Migration runners use a lease lock table (`__reptar_migrations_lock`, 30-minute expiry) so concurrent deploys cannot apply the same migration. `db check` and `db dry-run` (or `db migrate --check/--dry-run`) report pending SQL without applying it.
- `IDbClient.TransactionAsync` is no longer a silent no-op: writes are buffered and committed atomically through `BatchAsync` (transactional on SQLite/Postgres/MySQL, atomic on D1). Reads and table-shaped writes inside a buffered transaction fail with a clear message.
- **SQLite now has real read/write transactions:** `SqliteDbClient.TransactionAsync` holds the write gate, begins a transaction on the single writer connection, and routes reads/writes through the ambient transaction. Tests verify uncommitted reads and rollback.
- **MySQL now has real read/write transactions:** `MySqlDbClient.TransactionAsync` holds one connection/transaction and routes reads/writes through an ambient scope.
- **PostgreSQL now has real read/write transactions through `IDbClient.TransactionAsync`:** `PostgresDbClient` opens one connection/transaction and routes commands through an ambient scope, so reads see uncommitted writes. The `PostgresTransactionContext` overload remains. D1 remains write-only buffered because the platform has no interactive transaction API.
- `DbTransactionExtensions` exposes the transaction contract on concrete clients.

**Tests:** `ReliabilityTests` cover timeout 504 and module order; `TransactionTests` cover commit, rollback, uncommitted reads, and buffered-transaction behavior; `ETagTests` cover 304 revalidation and skip rules; `MetricsTests` cover Prometheus output; `MigrationChecksumTests` cover edited-migration rejection.

### Provider contract matrix

- `ProviderContractTests` runs the same CRUD, transaction commit/rollback, nested-transaction, batch atomicity, concurrent insert, migration apply/check/checksum, and lock-release scenarios against SQLite, PostgreSQL, and MySQL.
- Postgres initially fell through to the buffered write-only `IDbClient.TransactionAsync`; it now uses a real ambient transaction on one connection.
- Migration timestamps now bind as `DateTimeOffset` on Postgres (the column is `TIMESTAMPTZ`) instead of a string, which previously failed with `42804`.
- `db check` / `db dry-run` now report checksum mismatches (exit 2) instead of only pending IDs.
- D1 contract tests run through Miniflare against `@reptar/host`'s `d1Query` / `d1Execute` / `d1Batch`, including batch atomicity and concurrent single-statement writes.
- `bun run test:providers` runs the matrix; `--require-all` makes missing Postgres/MySQL a CI failure. CI starts Postgres 17 and MySQL 8.4 service containers.

### 4. AI / terminal boundary

- `framework/Reptar.AI` is now a separate optional project (22 files).
- `examples/agent-cli/terminal/` owns the terminal UI (8 files: terminal agent/editor/input/screen/UI, ReptarTerm, Ansi, TerminalBuffer).
- `framework/Reptar/Core/AiTools.cs` moved to `Reptar.AI`; core keeps only `RouteMeta` AI fields and `RouteBuilder.AsAiTool`.
- `examples/agent-cli` references `Reptar.AI` and includes the terminal files.
- `Reptar.Tests` references `Reptar.AI` and links the terminal example files.
- `framework/Reptar.AI.Gateway` is a separate optional project; `examples/ai-gateway/app.cs` is now a thin composition over `AiGateway` + `GatewaySchema`.
- Architecture guard (`scripts/check-architecture.ts`) reports core clean; CI now runs it with `--strict`.

**Still open:** move terminal tests into an example-local test project; package `Reptar.AI` / `Reptar.AI.Gateway` on NuGet; update README/llms paths.

### 5. Preload v1

- `Relation.BelongsTo<TParent, TRelated, TKey>` and `Relation.HasMany<...>` with explicit key selectors and immutable `attach` expressions.
- `TableQuery<T>.Preload(relation)` batches related rows with `WHERE ... IN (...)` in chunks of 500.
- Missing related rows attach `null`/empty lists.
- Reflection-free, AOT/Wasm-safe.
- `PreloadTests` cover belongs-to, has-many, and orphan parents.

**Still open:** attribute/source-generated relations, nested preloads, many-to-many, per-parent limits, D1 batch optimization.

### 6. CLI scaffolding

- `bun reptar make:model Post [-f] [-m] [--force] [--dry-run]`
- `bun reptar make:feature Post`
- Generates `schema/<plural>.cs` (TableDef + `[ReptarEntity]` DTO), optional `features/<plural>.cs`, updates `schema/index.cs`, inserts `#:include` lines and `Feature.Configure(app)`, and adds `[JsonSerializable(typeof(Dto))]` to an existing JSON context.
- `--migration` invokes `reptar db generate`.
- The scaffold runner (`templates/fullstack/scripts/reptar.ts`) imports `@reptar/cli-core` directly, so `make:model`, `make:feature`, and `json:gen` work in a scaffold without a framework clone. create-reptar copies the package into `packages/cli-core`, which the scaffold workspace links.
- The framework repo keeps `scripts/make.ts` / `scripts/json-context.ts` as re-export shims, so there is one implementation.
- Verified in a temp app: files, index, includes, feature call, and JSON context are updated.

**Still open:** pluralization edge cases and a standalone (NuGet-based) scaffold path.

### 6b. CI/codegen fixes

- `reptar codegen --check` now formats its temporary output before comparing; previously it compared unformatted generated files to oxfmt-formatted committed files and always reported stale. `bun rpc:check` passes again.
- `scripts/test-realtime.ts` accepts `REPTAR_REALTIME_WORKERS_PORT` / `REPTAR_REALTIME_KESTREL_PORT` so concurrent agents and CI jobs do not fight over 8790/8791.

### 7. Measurement guardrails

- `scripts/bench-gate.ts`: compares a `bench/load/run.ts` result to a compact baseline; RPS drop and p95 rise thresholds; JSON output.
- `bench/perf-baseline.json`: current VPS T0–T11 baseline.
- `.github/workflows/bench.yml`: manual self-hosted benchmark gate.
- `scripts/check-architecture.ts`: core/AI/terminal boundary guard; CI step added.
- `scripts/check-size.ts` + `bench/size-budgets.json`: AOT/Wasm size budgets.
- `package.json`: `check:architecture`, `check:size`, `bench:gate`.

---

## Findings / learnings

### Source generators cannot generate `JsonSerializerContext` metadata

The attempted `ReptarJsonContextGenerator` failed for a structural reason: Roslyn source generators do **not** see each other's generated output. The System.Text.Json source generator cannot see a `JsonSerializerContext` class emitted by another generator, so the generated class lacks the abstract `GetTypeInfo`/`GeneratedSerializerOptions` implementation.

**Do not retry this as a source generator.** The working alternative is a build-time file generator: `scripts/json-context.ts` scans `[ReptarEntity]`, `Rule<T>`, and `Produces<T>` types and writes `json-context.g.cs` as ordinary source **before** compilation. The STJ generator sees that file, emits the real context implementation, and the file's module initializer registers it through `ReptarJson.RegisterContext`.

- `bun reptar json:gen` runs it manually.
- `bun reptar make:model` runs it automatically and wires `#:include json-context.g.cs` into `app.cs`.
- Verified in a temp app: generated file compiles and the context type is available.

Other options remain: an MSBuild target, or an analyzer that flags unregistered `ctx.Json` types. The `ReptarJson.RegisterContext` registry and `SourceGenJsonCodec` lookup are the runtime bridge for build-time/manual contexts.

### Concurrent-agent integration

Another agent added W3C trace context (`TraceParent`, `TraceContext`, `Ctx.SpanId`) while this pass was in progress. The final telemetry middleware uses that trace context; the `RequestTelemetry` record now includes `span_id`/`parent_span_id`. Avoid reimplementing trace parsing in a separate path.

### Preload API shape

Explicit relation descriptors are the right v1: no reflection, no attributes, works with immutable records. The ergonomic next step is a generator/attributes that emit these descriptors, not runtime reflection.

### `TransactionAsync` v1 is write-only

SQLite, PostgreSQL, and MySQL have real ambient read/write transactions. D1 uses the write-only buffer because the platform has no interactive transaction API.

### Benchmark gate needs a stable runner

The baseline is a 2-vCPU VPS AOT run. GitHub-hosted runners are too noisy for a 15% gate; `.github/workflows/bench.yml` is self-hosted/manual by design.

---

## Verification

- `bun reptar test`: **367 tests** in the tree; the provider matrix (21 native cases) and D1 contract (3 cases) pass. Six unrelated failures from concurrent pagination/mail edits were present during verification.
- `bun run test:providers`: passes against SQLite, live PostgreSQL, and D1 via Miniflare; MySQL is skipped locally when no server is present and is required in CI.
- `bun run test:providers --require-all`: CI gate with Postgres 17 and MySQL 8.4 service containers.
- Lint cleanup: `scripts/reptar.ts`, `scripts/diff.ts`, `scripts/mcp.ts`, `scripts/inspect.ts`, `scripts/docs-gen.ts`, and `scripts/test-scaffold.ts` pass targeted Ultracite checks.
- `bun reptar self-check`: **all green** on a clean port set (doctor, tests, host contract, NetWasm lint, workers + Kestrel realtime smoke). A concurrent agent's `bun e2e` held 8790 during one run; the new `REPTAR_REALTIME_*_PORT` env vars avoid that collision.
- `bun scripts/check-architecture.ts --strict`: **core is clean**.
- `framework/Reptar.Wasm` builds (NetWasm 0.10.1) with telemetry/preload/queue changes; the Rooms Worker module is 13.13 MB against a 16 MB budget.
- `framework/Reptar.Kestrel` builds with response compression.
- `examples/agent-cli` and `examples/ai-gateway` file-based builds pass with the new project references.
- Native AOT publish of a `Rule<T>` app succeeds and validates correctly (`ok=0 bad=2`).
- `bun rpc:check` passes after fixing `codegen --check` formatting.
- `bun run docs:check:snippets` passes (127 C# snippets across 44 docs).
- `scripts/bench-gate.ts` passes against the existing VPS result and baseline.
- `scripts/test-scaffold.ts` passes end-to-end (scaffold → build → codegen → tsc → runtime smoke).
- `make:model Post --feature` verified in a temp app (schema, index, generated JSON context, includes, configure call).

---

## Next priorities

1. D1 interactive transactions are not available on the platform; D1 keeps the write-only buffered transaction. SQLite/PostgreSQL/MySQL have read/write ambient transactions.
2. JSON context generation via build-time codegen (not a source generator) — implemented in `packages/cli/src/json-context.ts`; Wasm host `Program.cs` examples now call `ReptarJson.RegisterContext(...)` + `UseRegisteredContexts()`.
3. DB/HTTP cancellation + command timeouts.
4. Migration locks/check/dry-run landed; next is automatic lock renewal for very long migrations.
5. Shared cache adapters landed (`ISwrCacheStore`, `FileSwrCacheStore`); distributed Redis/KV adapters are left to applications.
6. `Rule<T>` typed path benchmarked (~50 ns JIT / ~66 ns AOT valid, 0 B); invalid path still allocates issues.
7. `@reptar/cli-core` packaged and copied into scaffolds; `make:*` scaffold smoke step added.
8. Update README/llms/docs for `Reptar.AI`, terminal example, preload, and telemetry.
9. OTLP log export and the `/_reptar` dashboard landed; span export remains.
10. CLI script lint is clean; generated browser bundles and some host/rpc package files still have pre-existing Ultracite findings.

The post-matrix backlog (reference app, failure UX, API stability, residual adapters) lives in `plans/next-priorities.md`.
