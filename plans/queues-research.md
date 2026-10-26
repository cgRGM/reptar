# Queues & Background Jobs Research → Reptar Queue Engine Plan

**Status:** research/audit — no framework code changed
**Date:** 2026-10-26
**Scope:** the queue/jobs proposal in the pasted transcript (System.Threading.Channels, TickerQ, MediatR, dual-target `#if NETWASM` queue engine), what `@adonisjs/queue` actually ships, what Go/Rust/Wasm ecosystems do (River, asynq, apalis, BullMQ, worker-rs), what the Cloudflare platform itself offers (Queues, Workflows, DO Alarms), and an audit of Reptar's current queue surface.
**Related:** `plans/adonisjs-core-audit.md` (the first AdonisJS pass — CLI, preload, lifecycles), `design.md` §7/§13/§16, `llms.txt` §5b/§6, `plans/master-audit-and-roadmap.md`.

---

## 0. TL;DR

The transcript's *conclusion* — build a dual-target queue engine (Channels-based worker under Native AOT, platform hooks under NetWasm) — is right and matches the repo's own constraints. But its *facts* are a mix of verified, outdated, and misattributed, and it under-credits what Reptar already has. The bigger finding is from auditing our own code: **Reptar's queue story is a Cloudflare-only half-bridge, and parts of the docs overstate what exists.**

| Question | Verdict | Short reason |
| :-- | :-- | :-- |
| Does `@adonisjs/queue` exist? | **Yes — verified** | Official AdonisJS package (experimental, created 2026-01-02, by Romain Lanz + core team), built on `@boringnode/queue`. Job classes, typed payloads, delay/priority/batch/dedup, backoff strategies, cron scheduling, workers with heartbeats, Redis/Database/Sync adapters, OTel, test fakes. |
| Is the "Section 6 Rule #3" quote real? | **Yes — verified** | Nearly verbatim from `llms.txt` §6 item 3. |
| Are Hangfire/Quartz "entirely built on reflection" and AOT-broken? | **Half-right, outdated** | Hangfire: still true (`Type.GetType` + expression-tree serialization, AOT issue #2478 open). **Quartz.NET 4.x now declares `IsAotCompatible`** with registered-type tables, System.Text.Json default, and native canary builds. Right conclusion (don't adopt), wrong basis. |
| Is TickerQ the AOT reference? | **Real, but license changed** | Source-generated, Native-AOT-verified (3.5k stars) — but **5.x moved to a commercial license** (Community/Evaluation/Commercial). Learn the design; don't plan on it as a dependency. |
| Is MediatR a useful reference? | **Pattern only** | MediatR went dual/commercial license in v13 (Jul 2025) and its AOT-support issue was closed stale. The "in-process dispatcher" idea is the reusable part — and Reptar's `OnQueue` dictionary already is one. |
| Should we build the dual-target queue engine? | **Yes — phased** | Native targets currently have *no* background-job story at all (producers no-op, crons never fire). The unified facade is required for the "one app source, every target" promise. |

**Bottom line:** steal the *options layer* from AdonisJS/`@boringnode/queue` (delay, priority, backoff, dedup, history, sync adapter), the *storage model* from River (`jobs` table in the app's existing DB), and the *edge mapping* already proven by worker-rs (typed batches → `ack()`/`retry(delaySeconds)`). Fix the honesty gaps first (silent no-op producers, waitUntil that doesn't actually wait, crons that never fire on Kestrel), then ship the native SQLite-backed queue, then the edge power features.

---

## 1. What Reptar actually has today (verified inventory)

Every claim below was checked in the current checkout.

### Producer side
- `IQueueClient` has a single method: `SendAsync(queueName, messageJson)` plus a typed helper that serializes with `ReptarJson.Options` (`framework/Reptar/Data/Services.cs:491-507`).
- Under NetWasm, `QueueClient` bridges via `[JSImport("queueSend")]` → host `__REPTAR_QUEUE_SEND` → Cloudflare Queues binding (`framework/Reptar.Wasm/Data/QueueClient.cs`, `packages/host/src/host.ts:384-398`).
- **On Kestrel, Lambda, and dev, the default is `NoopQueueClient` — `SendAsync` silently drops every message** (`framework/Reptar/Core/ReptarApp.cs:1212-1219`). No log, no exception.
- The host-side `QueueLike` is `send(message)` only — **no `sendBatch`, no `delaySeconds`, no content type**, even though Cloudflare Queues supports all three.

### Consumer side
- `app.OnQueue(name, handler)` / `OnQueue<T>(...)` register handlers in a plain dictionary — a compile-time, reflection-free dispatcher (the transcript's "statically map a job key to a code path" already exists).
- `HandleQueueBatchAsync` processes messages **sequentially**, catches per-message exceptions, and returns failed message IDs; the TS host maps those to `message.retry()` / `message.ack()` (`packages/host/src/host.ts:946-1007`). The wiring works and is tested (`framework/Reptar.Tests/Tests.cs:819-880`).
- Gaps: no retry-delay control (Cloudflare's `retry({delaySeconds })` is unreachable from C#), no backoff policy surface, no DLQ concept in C#, no per-queue concurrency, no batch acknowledgment strategy, no idempotency helpers, no job history.
- **The reference Wasm app doesn't even export `handleQueue`/`handleScheduled`** — the NetWasm/Binaryen optimizer limitation documented in `design.md` ("rejects or stalls on additional JS exports... cron, durable-object... queue-consumer demos"). The host supports them; the app never wires them.

### Cron side
- `app.OnScheduled(cron, handler)` + `HandleScheduledAsync` — but matching is **exact cron-string equality or `"*"`** (`framework/Reptar/Core/ReptarApp.cs:652-694`). `"*/5 * * * * *"` never fires unless the platform hands back the identical string.
- On Cloudflare this works only if the registered string matches the wrangler trigger exactly. **On Kestrel/Lambda nothing ever invokes `HandleScheduledAsync` — there is no scheduler loop at all.** Native apps have zero cron.

### Background tasks — a docs/runtime gap
- `ctx.WaitUntil(...)` collects tasks; `ReptarApp.HandleAsync` then does `_ = Task.Run(() => Task.WhenAll(...))` on every target (`framework/Reptar.Core/ReptarApp.cs:883-896`).
- The docs claim Workers semantics: *"Passes the asynchronous task to `ctx.executionCtx.waitUntil(promise)`. The Worker isolate remains warm"* (`apps/docs/src/content/docs/cookbook/background-tasks.mdx`, `llms.txt` matrix).
- **Verified false.** `createReptarHost.fetch(request, env)` never receives an `ExecutionContext`, and no `waitUntil` exists anywhere in `packages/host`. The only real `ctx.waitUntil` in the repo is hand-written TS in `spikes/realworld-worker/src/index.ts:183` for its own cache. Under NetWasm, the C# `Task.Run` continuation is dead the moment `respond()` resolves. `ctx.WaitUntil` on Workers today is fire-and-freeze.

### Other verified gaps
- `createReptarDurableObjectHost` has no `alarm()` handler (`packages/host/src/durable-object.ts`) — yet `llms.txt` §6's matrix says "⚡ Use DO Alarms" for delayed work. No bridge exists.
- The README's queue row claims a "Lambda Event Adapter" for queue consumers — there is no SQS/queue code in `Reptar.Lambda` or `packages/adapter-lambda`.
- Lambda background tasks are marked "⚠️ Buffered execution" in the matrix; the adapter only parses API Gateway events.

---

## 2. Claim-by-claim audit of the transcript

| # | Transcript claim | Accuracy | Evidence / correction |
| :-- | :-- | :-- | :-- |
| 1 | AdonisJS has an official `@adonisjs/queue` requiring explicit job classes, structured keys, serializable payloads | **Correct** | Created 2026-01-02, `adonisjs/queue` on GitHub (MIT, experimental), by Romain Lanz with Julien-R44/thetutlage/JanStevens. Powered by `@boringnode/queue` (Romain Lanz, MIT, framework-agnostic, BullMQ-comparable benchmarks). |
| 2 | Hangfire/Quartz are *entirely* reflection-built; expression trees break under AOT | **Half-right** | Hangfire: yes — `Type.GetType` on stored type names, expression-tree serialization, AOT issue #2478 open. Quartz.NET 4.x: now `IsAotCompatible` for core packages — reflective fallback behind a feature switch (off when trimmed), `JOB_CLASS_NAME` resolved against a registered-type table, System.Text.Json default, ILCompiler-published canaries per PR. |
| 3 | `System.Threading.Channels` is AOT-safe, near-zero-alloc, handles backpressure | **Correct** | Already proven in-repo: `KestrelWebSocketConnection` uses a bounded `Channel<byte[]>` pump with `DropOldest` (`framework/Reptar.Kestrel/KestrelHost.cs:182-251`); SQLite group-commit batcher uses the same idea. "Lock-free" is an overstatement (they're low-allocation, not strictly lock-free) — irrelevant for our purposes. |
| 4 | TickerQ is a fast, reflection-free AOT task scheduler | **Correct on facts, missing the license** | `Arcenox-co/TickerQ` — source generator wires `[TickerFunction]` handlers at compile time; 5.x is Native-AOT-verified for scheduler, dashboard, in-memory, and Redis providers (EF Core/MongoDB providers are managed-only). **5.x is distributed under the TickerQ Software License Agreement** (free Community tier, paid Commercial); earlier releases were MIT/Apache. |
| 5 | MediatR can be configured explicitly / write a mini reflection-free variant | **Pattern is fine; library isn't** | MediatR v13+ (Jul 2025) is dual-licensed RPL 1.5 + Commercial (free Community under $5M revenue). Its AOT-support issue (#892) was closed stale — never shipped. The "mini dispatcher" is exactly what `OnQueue`'s handler dictionary already is. |
| 6 | "Section 6 Rule #3": Channels/Task.Run loops don't exist under NetWasm; freeze between events; gate with `#if !NETWASM` | **Correct, quoted nearly verbatim** | `llms.txt` §6 item 3. |
| 7 | Under NetWasm, route to `ctx.Queue` or a Durable Object alarm instead of threads | **Correct direction** | Cloudflare's own docs frame it the same way. Gap: we have the Queues bridge but no alarm bridge (§1). |
| 8 | Sketch: "statically map a job key string to a code execution path" | **Already implemented** | `_queueHandlers: Dictionary<string, Func<Ctx, QueueMessage, Task>>` + `OnQueue<T>` typed wrapper do this today (`framework/Reptar/Core/ReptarApp.cs:108-111, 445-462`). |
| 9 | Dual-target wrapper interface with an explicit factory | **Right architecture** | Matches Reptar's `#if NETWASM` service-default pattern already used for DB/Storage/AI/Cache/Queues/Durable/Http/Crypto/Env (`ReptarApp.cs:1158-1255`). |

---

## 3. The reference frameworks — what each one teaches

### 3.1 `@adonisjs/queue` / `@boringnode/queue` (TypeScript) — the ergonomic target

The design worth mirroring, feature for feature:

- **Jobs are classes with typed payloads** and static options: `static options = { queue: 'default', maxRetries: 3 }`. `execute()` runs the work; `failed(error)` is the terminal-failure hook.
- **Dispatch options:** delay, priority, bulk/batch dispatch, **dispatch-time deduplication** (custom dedup IDs, stored per driver with schema helper migration).
- **Retry strategies:** exponential, linear, or fixed backoff — pluggable per job.
- **Scheduled jobs:** cron or interval, defined in `start/scheduler.ts`; schedules are **persisted** (`queue_schedules` table), keep run counts and next-run, survive restarts, run a missed schedule once on worker start, can be paused/resumed.
- **Workers:** separate process (`queue:work`), `--queue=`, `--concurrency=`, `--adapter` flags; **heartbeats renew job ownership** so long jobs aren't reaped as stalled; graceful shutdown drains running jobs then closes connections.
- **Adapters:** Redis (prod), Database (Postgres/MySQL/**SQLite** via Lucid/Kysely/Knex), and **Sync** — executes jobs inline, for dev/tests, no worker needed.
- **Job timeout** (fail or retry over-limit), **job history** retention, **job grouping** for monitoring, **auto-discovery** by glob location, dev hot-reload of job code, OpenTelemetry tracing, **fake adapter for tests**.

### 3.2 River (Go) — the "your DB is your queue" reference

- Jobs are enqueued **transactionally with your data changes** — guaranteed-enqueued-if-committed, invisible until commit. This is the philosophical fit for Reptar: SQLite/D1 are already there; no new infra.
- **Retry policy:** quartic backoff `attempts^4 + rand(±10%)` jitter, default 25 attempts; **custom per-worker `NextRetry`** (River's "snooze": a handler can reschedule itself).
- **Unique jobs** by args/period/queue/state; priorities; multiple queues; cancel/snooze from inside the handler.
- **Periodic/cron jobs:** leader-elected, in-memory schedule — with honest caveats: skipped runs possible around leader failover; mitigations are `RunOnStart` + uniqueness; durable periodic jobs are a Pro feature. This is the clearest warning for our native scheduler design: **persist schedules, don't trust an in-memory loop.**
- Insert-only clients ("queue-only mode") mirror TickerQ's `DisableBackgroundServices()` — producers don't have to run workers.
- Middleware (insert + work phases), subscriptions for telemetry, test helpers, web UI, **SQLite support**, cross-language clients (Go/TS/Ruby/Rust share the schema).

### 3.3 apalis (Rust) — the backend abstraction reference

- `Backend` trait (`poll_next` + `TaskSink`) decouples worker logic from storage; backends: Postgres, MySQL, SQLite, Redis, AMQP, NATS, pgmq, rsmq.
- **Worker heartbeats + task leases** (visibility timeouts / orphan recovery) — the stalled-worker defense `@boringnode/queue` also adopted.
- **`pipe_to`** — ephemeral streams (cron ticks, webhooks) are piped *into* a durable backend before processing. "A missed cron tick should be a retryable job, not a lost event." This is the right shape for Reptar cron → jobs-table.
- Tower middleware (retry, rate limit, timeout, load-shed, tracing) as composable layers; `apalis-workflow` for orchestration.

### 3.4 Cloudflare platform — what the edge natively gives us

- **Queues consumers:** `max_batch_size` (default 10, max 100), `max_batch_timeout` (default 5s, max 60s), `max_retries` (default 3, max 100), `dead_letter_queue` (auto-created; unconsumed DLQ messages persist 4 days), `max_concurrency` (autoscales to 250 when unset), `retry_delay`, `visibility_timeout_ms` (pull consumers, up to 12h).
- **Delays:** `delaySeconds` 0–86400 (**24h cap**) on send/sendBatch/retry; queue-level `delivery_delay` default; message-level overrides queue-level. Retention configurable 4→14 days; 5,000 msg/s per queue; 25GB backlog; consumer wall clock 15 min; one consumer per queue; at-least-once, best-effort ordering.
- **Workflows:** durable steps (`step.do` with per-step `retries {limit, delay, backoff: linear|exponential}` and `timeout`), `step.sleep()`/`sleepUntil()` for hours/days, `step.waitForEvent()` for webhooks/human approval, attempt number in step context, 25,000 steps/instance, instance lifecycle APIs (trigger/pause/resume/terminate), rollback handlers. TS-only runtime — a Reptar integration would be a host-package shim, same as DOs.
- **DO Alarms:** `setAlarm` is the "wake me at time T" primitive for one-off delayed work beyond 24h or without a Queue — **no Reptar bridge exists yet**.
- **worker-rs (Rust→Wasm):** the exact precedent for a statically-typed language bridging Queues: `#[event(queue)]` on `MessageBatch<T>` (serde-typed), `message.ack()`, `message.retry_with_options(delay)`, `MessageBuilder::new(...).delay_seconds(20)`, `send_batch`. If Rust can expose this surface from wasm32, NetWasm C# can too — nothing about our target blocks it except the export-count/Binaryen issue we already track.

### 3.5 TickerQ (.NET) — the chains reference

Even though we won't adopt it (commercial 5.x), three ideas are worth keeping: **job chains** with committed parent results (`GetParentResult<T>`, `ThenMany` groups), **queue-only producer mode**, and **bounded overdue-drain with capacity backpressure** ("overdue work stays eligible after downtime and drains in bounded batches"). Also its fail-fast AOT validation ("No JsonTypeInfo was available" at startup, not at first job) is the same discipline our `check:netwasm` + JsonContext approach already follows.

### 3.6 The convergence observation

Every mature system — BullMQ, `@boringnode/queue`, River, asynq, apalis, Oban, TickerQ, Cloudflare Queues itself — converges on the same core model:

1. **typed payload + string job key → registered handler** (no reflection)
2. **enqueue options:** delay, priority, batch, dedup
3. **at-least-once delivery + explicit ack/retry**, retries with backoff, dead-letter after max attempts
4. **per-queue concurrency** control
5. **lifecycle hooks + observability** on enqueue/execute/fail/complete
6. **a sync/inline adapter for dev and tests**
7. **persisted schedules** for cron, with missed-run recovery

Reptar has (1) and half of (3) (on the Workers host only). Everything else is the roadmap.

---

## 4. The architectural blueprint: a target-aware queue engine

Keep the transcript's dual-target conclusion, but implement it as **one facade with target-specific default clients** — the same pattern `ReptarApp.Services` already uses — so user code never contains `#if NETWASM`:

```
                  ctx.Queues / ctx.Jobs  (uniform C# surface)
                          │  SendAsync(name, payload, options)
                          │  (options: delay, priority, dedupId, attempts)
   ┌──────────────────────┼───────────────────────────────┐
   │ Native AOT (Kestrel, │ NetWasm (Workers)             │ Lambda
   │ Lambda, dev, tests)  │                               │
   ├──────────────────────┼───────────────────────────────┤
   │ SyncQueueClient      │ CloudflareQueueClient          │ (sync or SQS
   │ (default: inline     │ (existing QueueClient +        │  event adapter,
   │ execute, tests)      │  delay/batch options)          │  later)
   │ SqliteQueueClient    │                                │
   │ (opt-in: jobs table, │ delayed >24h / one-off wake:   │
   │  Channels drain      │ DO alarm bridge (to build)     │
   │  worker in KestrelHost│ multi-step durable: Workflows  │
   │  BackgroundService)  │  bridge (optional, later)      │
   └──────────────────────┴───────────────────────────────┘
```

Design rules (non-negotiable, from the repo's identity):

- **Explicit registration only.** `OnQueue`/`OnQueue<T>` dictionaries; no assembly scanning, no attribute discovery at runtime. Job payloads are registered records in the app's `JsonSerializerContext` (`OnQueue<T>`'s `Deserialize<T>` already carries the `[DynamicallyAccessedMembers]` contract).
- **The platform is the scheduler on Workers.** C# code only ever runs inside an event (fetch/queue/scheduled/alarm). "Background job" = serialize intent now, let the platform re-invoke you. Never a loop under `NETWASM`.
- **The framework is the scheduler on native.** A `PeriodicTimer`/Channels-based worker drains whatever provider is configured on Kestrel; no platform exists to do it for us.
- **No new infra.** SQLite (native) and Queues/D1/DO (edge) are already part of every Reptar deployment. Redis is a non-goal.
- **One jobs schema, generated by our own engine.** A `jobs` `TableDef` + `reptar db generate` migration, exactly like user tables — visible in `inspect`/`doctor`/MCP, not a private side schema.

### 4.1 The port surface (concrete)

```csharp
// Producer — one method today; becomes a vocabulary, still tiny.
public interface IQueueClient
{
    Task SendAsync(string queue, string payloadJson, QueueSendOptions? options = null);
    Task SendBatchAsync(string queue, IReadOnlyList<string> payloadsJson, QueueSendOptions? options = null);
}

// The three options every serious provider can express (validated in §4.2).
public sealed record QueueSendOptions(
    int? DelaySeconds,     // honor or clamp per provider; document per adapter
    string? DedupId,       // honored where the provider has dedup; ignored + logged where not
    int? AttemptsHint);    // advisory; real policy lives on the queue (QueuePolicy)

// Consumer-side control — the handler vocabulary.
public sealed class RetryIn(TimeSpan delay) : Exception;  // snooze/unify: CF retry delay,
                                                           // SQS ChangeMessageVisibility,
                                                           // Service Bus defer, Storage
                                                           // UpdateMessage visibility,
                                                           // Pub/Sub modifyAckDeadline,
                                                           // SQLite visible_at
// ack = return normally; retry = throw; RetryIn(ts) = throw with a delay hint.
// HandleQueueBatchAsync already reports failed IDs; it gains per-message retry delay.

// Queue declaration/policy — one line per queue, feeds inspect/codegen/provisioning.
public sealed record QueuePolicy(int MaxAttempts, string? DeadLetterQueue);

// Polling consumer port (native runtimes only) — the SQLite queue is just the
// local implementation of this; SQS/Service Bus/Storage/Pub/Sub implement the same.
public interface IQueueConsumer
{
    Task<QueueBatch?> ReceiveAsync(int maxBatch, CancellationToken ct);
    Task AckAsync(QueueBatch batch);
    Task RetryAsync(QueueBatch batch, IReadOnlyDictionary<string, int> retryDelaySeconds);
}
```

**Producer adapters are pure HTTP** — SQS/Service Bus/Pub/Sub/Storage all have REST APIs, so every runtime (including NetWasm, via the existing `WasmHttpClient` allowlist + `WasmCrypto` HMAC-SHA256 for SigV4) can produce to every provider. **Consumer adapters are where the runtime matters**, and there are exactly three integration patterns:

1. **Event-mapped** (platform invokes you): Cloudflare Worker `queue()` handler → existing `@reptar/host` bridge; AWS Lambda SQS event source (`ReportBatchItemFailures`) → thin parse → `HandleQueueBatchAsync`; Azure Functions/AWS push equivalents → same shape. The adapter is event-parsing + outcome mapping; the dispatcher already exists.
2. **Poller** (native runtimes): one generic bounded Channels worker per configured `IQueueConsumer`, registered as a Kestrel `BackgroundService`. SQLite is the zero-config default implementation; cloud adapters slot in behind the same interface.
3. **Sync** (dev/tests): execute inline, no infra.

### 4.2 Provider capability matrix (verified)

This is the validation that the §4.1 vocabulary is the right one — every field maps to a real primitive somewhere, and the two universally-supported concepts (ack/retry + max-attempts→DLQ) are in core:

| Capability | Cloudflare Queues | AWS SQS | Azure Service Bus | Azure Storage Queues | GCP Pub/Sub | Redis/BullMQ | SQLite (ours) |
| :-- | :-- | :-- | :-- | :-- | :-- | :-- | :-- |
| **Delay on send** | ✅ `delaySeconds` ≤24h | ✅ ≤15 min | ✅ `ScheduledEnqueueTimeUtc`, arbitrary | ✅ `visibilityTimeout` ≤7d | ❌ (needs Cloud Tasks workaround) | ✅ | ✅ `visible_at` |
| **Dedup** | ❌ | ✅ FIFO `MessageDeduplicationId` (5-min window) / content-based | ✅ `MessageId` + duplicate-detection window 20s–7d (queue config) | ❌ | ~ exactly-once delivery (pull, opt-in) | ✅ job ID | ✅ dedup key |
| **Ack / retry** | ✅ ack / retry per message | ✅ delete / visibility timeout (0–12h, per-message extend) | ✅ complete / abandon, peek-lock + lock renewal | ✅ delete / pop-receipt visibility update | ✅ ack / nack via `modifyAckDeadline` (10s–600s) | ✅ | ✅ |
| **Retry-delay control** | ✅ `retry({delaySeconds})` | ✅ `ChangeMessageVisibility` | ✅ deferral via `ScheduledEnqueueTimeUtc` | ✅ `UpdateMessage` visibility | ~ subscription-level `retryPolicy` backoff | ✅ moveToDelayed | ✅ |
| **Max attempts → DLQ** | ✅ `max_retries` + `dead_letter_queue` | ✅ redrive `maxReceiveCount` | ✅ `MaxDeliveryCount` (default 10) → DLQ | ~ client convention: `DequeueCount` → `{queue}-poison` | ✅ `maxDeliveryAttempts` 5–100 (best-effort) → dead-letter topic | ✅ | ✅ `dlq` state |
| **Ordering/FIFO** | best-effort | FIFO queues: `MessageGroupId` | sessions: `SessionId` | ❌ | ordering keys (opt-in) | per-queue | per-queue (single node) |
| **Batch** | ✅ ≤100 msgs / 256KB | ✅ ≤10 msgs | ✅ | ✅ ≤32 get | ✅ | ✅ | ✅ |

Deliberately **not** in the core vocabulary: ordering/group keys (each provider needs queue-side config to unlock it — FIFO queue, session-enabled entity, ordering-enabled subscription — so it can't be a clean per-message option; document as provider-specific, revisit if asked), and priority (only Redis honors it).

### 4.3 What this implies for scope

- The **only** provider concepts that are truly universal are ack/retry and attempts→DLQ. Those are already Reptar concepts — so core stays the two interface groups above and nothing more.
- `DedupId` and `DelaySeconds` are "honor or honestly ignore" — each adapter documents per-field behavior, which is the adapter-honesty success criterion.
- The SQS Lambda adapter is the highest-value external one: it makes an existing README claim true, and AWS users get queues with zero new services. Service Bus and Pub/Sub adapters are the same shape; build on demand.
- NetWasm producing to *any* provider via HTTP means a Cloudflare-hosted Reptar app can still enqueue into a customer's existing AWS/Azure queue — "interface with their stuff" works in both directions.

---

## 5. Recommended sequence

> **Revision 4 (2026-10-26, "do it all" round): the four follow-ups landed.**
>
> 1. **DO alarm bridge — shipped.** `app.OnAlarm(...)` dispatches behind the reserved `/_reptar/alarm` path (rides the normal fetch pipeline — no new JS exports). `createReptarDurableObjectHost` now captures Durable Object storage, intercepts `POST /_reptar/alarm/schedule` → `state.storage.setAlarm(runAtMs)`, and implements `alarm()` with runtime-retry propagation on 500s. Portable tests cover dispatch, failure isolation, background-task draining, and route-metadata isolation.
> 2. **Queue stats in inspect/MCP — shipped.** `bun reptar inspect --app .` reads `reptar_jobs` directly: `Queue: 3 pending, oldest 62s, 0 completed, 1 dead (reptar_jobs)` — same section in `inspect --json` and the `reptar_inspect` MCP tool (no MCP changes needed; it returns the full inspection).
> 3. **Wasm export verification — the documented Binaryen limitation is GONE.** `WorkerEntrypoint.HandleQueueAsync`/`HandleScheduledAsync` + `handleQueue`/`handleScheduled` exports built clean through the full NetWasm publish. Local workerd proof of the complete round trip: `POST /platform/queue` → Cloudflare Queues → the C# `handleQueue` export → `HandleQueueBatchAsync` → `OnQueue` handler (KV write) → **`1/1` acked**. Three real findings: (a) the spike's `wrangler.jsonc` never configured a **consumer** — the queue demo was silently dead in local dev; fixed. (b) workerd delivers the *physical* queue name where `OnQueue` registers *logical* names — unmapped, the dispatcher returns all-failed with zero logs (the exact "silent retry loop" symptom); the Worker's `queue()` handler now maps it, and the features doc calls the requirement out. (c) The *actual* optimizer limitation is not export count: `wasm-opt -Oz` fails outright on ~38 MiB modules — `build:worker` now falls back to `-O1` (which shrinks the core 38→24 MB). `design.md`'s note updated with the precise facts.
> 4. **Live-fire harness — shipped.** `bun examples/jobs/livefire.ts [sqs|azure|pubsub]` runs enqueue → retry (`REPTAR_JOBS_FAIL_FIRST=1`) → process against real infrastructure, reporting PASS/FAIL/MISSING with exact remediation. On this machine all three are credential-blocked (MISSING): AWS SSO token expired, no Azure CLI, gcloud needs `gcloud auth login`.
>
> Also this round: consumer config added to the spike's `wrangler.jsonc` (a producer without a consumer never delivered anything in local dev — the README's queue demo was silently dead), and the `applyBatchResult` host helper extracted to keep lint complexity gates green.

> **Revision 3 (2026-10-26): SHIPPED.** The queue port landed in the working tree this session. What is now real (all tested, 304+ tests green):
>
> - **Vocabulary:** `QueueSendOptions` (delay/dedup/attempts-hint), `RetryIn` + per-message retry delays through `QueueBatchResult.Retries`, `IQueueConsumer` poll port, `IQueueObserver` null-default hooks, `SendBatchAsync` on `IQueueClient`.
> - **Adapters in-repo:** `SyncQueueClient` (native default — replaces the silent Noop), `FakeQueueClient` (tests), `DbQueueClient` (durable over the app's own SQLite/Postgres/MySQL with backoff, delays, dedup, dead-letter state, retention sweep), `SqsQueueClient` (hand-rolled SigV4 over HTTP, golden-tested against the published AWS vector; producer + consumer), `AzureStorageQueueClient` (SharedKey HMAC), `PubSubQueueClient` (portable JSON + bearer token), Cloudflare Queues parity (sendBatch/delaySeconds/retry-delay through `@reptar/host`).
> - **Kestrel runtime:** module-lifecycle-aligned queue poller + cron scheduler (5/6-field regex-free parser, dom/dow OR rule) with `REPTAR_QUEUE_POLL_SECS`/`REPTAR_QUEUE_BATCH`/`REPTAR_CRON_TICK_SECS` tuning.
> - **Lambda:** SQS event source adapter → `HandleQueueBatchAsync` → `ReportBatchItemFailures` partial responses.
> - **Example + bench:** `examples/jobs` (one app, adapter via `REPTAR_QUEUE`) with measured numbers: **~22,000 enqueues/s, ~200 jobs/s durable drain** including handler writes (SQLite WAL, single-writer gate).
> - **Docs:** `apps/docs/.../features/queues.md`, bindings/deployment-matrix/README truth updates.
>
> Deliberately not built (recipes only, documented in the features page): Service Bus (AMQP), Redis/BullMQ (RESP replication of BullMQ structures), Kafka/RabbitMQ/NATS (protocol weight) — each is an `IQueueClient` + optional `IQueueConsumer` pair when a real workload asks.

> **Revision 2 (2026-10-26):** Re-scoped after the platform decision. Reptar should not build a queue *engine* (not a River/Oban clone — that's feature creep and maintenance surface). It should build the thinnest complete **port** — the same move the framework already made for DB/storage/cache — plus per-target adapters that connect to whatever the user's platform already runs. Core vocabulary stays small; adapters do the platform work; every adapter is opt-in except the dev/test one.

### Phase 0 — Correctness & honesty (stop the silent lies) — ~3–4 days *(unchanged — this is the "fix ours" part)*

1. **Replace `NoopQueueClient` with `SyncQueueClient`** as the non-NetWasm default: execute the registered `OnQueue` handler inline (no registered handler → log a warning once, still no throw). Tests and dev get real behavior; the Noop remains available explicitly. This single change turns every local enqueue from a silent drop into working code.
2. **Make `ctx.WaitUntil` real on Workers.** Extend the host contract the same way `supportsWsMessageDone` already does: the Wasm runtime awaits `ctx.BackgroundTasks` before calling a new `respondDone(requestId)` (or reuses the done-queue pattern), and the host holds the event open via `executionCtx.waitUntil`. Until this lands, fix `background-tasks.mdx`/`llms.txt`/`context.md` to say the truth.
3. **Pass retry delays through.** Extend `QueueBatchResult` with per-message retry `delaySeconds` (C# record change) and map it in the host to `message.retry({ delaySeconds })`. This gives consumers Cloudflare's backoff control from C#, and gives the native client the same vocabulary.
4. **Cron expressions on native + Kestrel scheduler.** Parse 5/6-field cron (small hand-rolled parser, no regex on Wasm — see the validator lesson in `design.md`), register `PeriodicTimer`-driven dispatch in `KestrelHost.RunAsync` that invokes `HandleScheduledAsync`. Workers keeps using Cron Triggers (exact-string match stays valid there).
5. **Docs/matrix truth pass:** the README "Lambda Event Adapter" queue row, the `llms.txt` DO-alarms row, and the background-tasks table.

### Phase 1 — The port vocabulary, not the engine (~1 week)

Everything here is interface + semantics; nothing here is infrastructure.

1. **`QueueSendOptions`** on the producer: `DelaySeconds`, `DedupId`, `Attempts` (hint). Three fields, cross-platform meaning: delay is native to Queues/SQS/Service Bus; dedup is native to Queues (via content/`Uniqness` semantics — actually not supported by CF Queues, so document per-adapter) — each adapter maps what the platform supports and documents what it ignores. No priority in v1 (Cloudflare/SQS don't have it; don't spec what most adapters can't honor).
2. **Consumer control:** handlers can throw a `RetryIn(TimeSpan)` (unifies River's snooze / Cloudflare's `retry({delaySeconds })` / SQS visibility-timeout), plus an `OnJobFailed` terminal hook (AdonisJS's `failed()`). `QueueBatchResult` gains per-message retry-delay passthrough (Phase 0 item 3 delivers the host half).
3. **Host bridge parity:** `sendBatch` + `delaySeconds` in `QueueLike`/`__REPTAR_QUEUE_SEND` — matching what the platform API already offers (worker-rs proves the surface).
4. **One observer hook, zero deps:** a null-by-default `IQueueObserver` (OnEnqueued/OnStarted/OnRetried/OnDeadLettered/OnCompleted) — the same zero-cost-when-null pattern as `SqliteDbClient.WriteTimingObserver`. OTel is a user-side adapter over the hook, not a framework dependency.
5. **`FakeQueueClient`** for the portable test suite (records enqueues, drains programmatically).

### Phase 2 — Adapters: connect to their stuff (~2–4 days each; build on demand)

The core ships two adapters; everything else is a documented port (§4.1).

1. **`SyncQueueClient`** (dev/tests — ships in Phase 0 as the non-NetWasm default): inline execute, zero infra.
2. **`SqliteQueueClient`** (opt-in, single-node VPS/containers — the "no infra" durable default): deliberately minimal — one `jobs` table via `TableDef` + our migration engine, implementing `IQueueConsumer` for the generic Kestrel poller, attempts/backoff/retry-delay/DLQ-state. ~a few hundred lines, no history UI, retention is a `DELETE` on read. It must reuse the SQLite single-writer gate so it can never contend with request latency, and Phase 0's Kestrel cron loop rides in the same worker. **This is not a feature showcase — it's the admission that a VPS has no platform queue.** Benchmark gate: `/health` and `/posts` numbers must be unchanged with the module present but idle.
3. **AWS SQS adapter** (Lambda consumers + any-target HTTP producer): parse the SQS event JSON → `HandleQueueBatchAsync`, return `batchItemFailures` for the event source mapping, map `RetryIn` to visibility-timeout extension. Producer side is pure HTTPS + SigV4 (HMAC-SHA256 exists in both `DevCrypto` and `WasmCrypto`), so it produces from *any* runtime. This makes the README's existing Lambda claim true.
4. **Azure + GCP: recipes first, code on demand.** Service Bus (complete/abandon/defer + `MessageId` dedup) and Storage Queues (pop receipt + `{queue}-poison` convention) and Pub/Sub (ack/nack + dead-letter topic) all fit the §4.1 port — each is an `IQueueClient` + `IQueueConsumer` pair over HTTP. An external adapter is an afternoon; we build in-repo when a real user asks, because each provider we ship is a test matrix we own.

### Phase 3 — Edge polish (optional, after demand) (~1 week)

1. **DO alarm bridge** (`alarm()` in `createReptarDurableObjectHost` → `HandleAlarmAsync`) — delayed wake-ups past Queues' 24h delay cap, and the `llms.txt` promise made true.
2. **Introspection:** queue depth/oldest-pending/recent-failures/schedules in `reptar inspect`/MCP — cheap reads over tables/bindings we already own. No dashboard UI ever.

### Explicitly deferred / non-goals

- Redis as a *core* adapter (external adapters welcome; core adds no dependencies). Same policy for Kafka/RabbitMQ/NATS: recipe docs, never core.
- Assembly-scanning job discovery, IoC, `[Job]` attribute magic — violates the one rule.
- A Hangfire-style dashboard UI. Ever.
- A workflow/orchestration engine — orchestration is the platform's job (Workflows on Cloudflare, Step Functions on AWS, Durable Task Framework on Azure). Document the enqueue-next-job pattern for chains.
- Pull-consumer (HTTP) client exposure — revisit if someone needs external workers draining a Reptar queue.

### Success criteria

- **Same code, all targets:** a signup route enqueues `SendWelcomeEmail` with a 30s delay; it executes on Kestrel (opt-in SQLite queue), on Workers (Queues producer→consumer with `delaySeconds`), on Lambda (SQS event adapter), and inline in tests (sync client) — zero per-target code.
- **Failure path:** a handler that always throws ends in the platform's dead-letter location (Queues DLQ / SQS DLQ / SQLite `dlq` state) with attempts and backoff timing recorded.
- **Hot path untouched:** `/health` and `/posts` benchmark parity with the queue module present but idle — the bloat guard.
- **Adapter honesty:** every `QueueSendOptions` field documents, per adapter, whether it's honored or ignored; nothing silently no-ops.
- **`ctx.WaitUntil` on Workers:** an e2e proves the post-response side effect persisted (the isolate was genuinely held open).
- **Cron on Kestrel:** a `*/5 * * * *` handler fires on schedule.
- **No regressions:** `check:netwasm --strict` clean, portable suite green, Wasm bundle delta within noise, no reflection in any hot path.

---

## 6. What this buys the framework

The AdonisJS audit (`plans/adonisjs-core-audit.md`) set the identity: *AdonisJS/Laravel ergonomics, Dapper-like explicit SQL, zero runtime reflection, Native AOT/NetWasm performance*. Queues close the loop with a third idea: **Reptar is a port layer over the best primitive each platform already runs.** The end state is small enough to state in one sentence: *"one typed enqueue API, one consumer handler, per-platform adapters — Cloudflare Queues, SQS, or a one-table SQLite queue — with retries, delay, dead-lettering, and schedules, no Redis, no reflection, no dashboard."* Users bring Cloudflare, AWS, Azure, GCP, or a bare VPS, and the app code never changes. Nothing in .NET offers that today: Hangfire and BullMQ-style systems need infra; TickerQ and MediatR went commercial; Quartz is in-process only; every "works everywhere" queue adds a database and a UI. The differentiator is precisely that ours doesn't.

---

## Verification notes

What was actually run/checked while writing this:

- Read the full queue surface: `ReptarApp.cs` (`OnQueue`/`OnScheduled`/`HandleQueueBatchAsync`/`HandleScheduledAsync`/`DefaultQueues`), `Services.cs` (`IQueueClient`/`NoopQueueClient`), `Ctx.cs` (`WaitUntil`/`BackgroundTasks`), `Reptar.Wasm/Data/QueueClient.cs`, `packages/host/src/host.ts` + `bindings.ts` + `durable-object.ts`, `Reptar.Kestrel/KestrelHost.cs`, `Reptar.Lambda/LambdaHost.cs`, `packages/adapter-lambda/src/index.ts`, `spikes/netwasm-worker` (Program.cs, index.ts, wrangler.jsonc), the queue tests, `design.md`, `llms.txt` §6, `background-tasks.mdx`, `bindings.md`, `deployment-targets.md`.
- Verified the `llms.txt` §6 rule-3 quote and located the only real `ctx.waitUntil` (`spikes/realworld-worker/src/index.ts:183`); confirmed no `ExecutionContext` reaches `createReptarHost`.
- Confirmed the reference Wasm app exports only `handle` (no `handleQueue`/`handleScheduled`), consistent with the documented Binaryen limitation.
- Web-verified: `@adonisjs/queue` + `@boringnode/queue` (features, adapters, heartbeats, dedup, schedules, fake adapter), TickerQ (AOT guide, license change, feature set), MediatR (commercial launch, AOT issue closed stale), Quartz.NET 4.x trimming/AOT guide (IsAotCompatible table, registered-type resolution, "Coming from Hangfire" migration), Hangfire AOT issue #2478, River (retry policy, unique jobs, periodic-job caveats, middleware), apalis (Backend trait, heartbeats/leases, `pipe_to`), worker-rs (queue feature flag, `#[event(queue)]`, `MessageBuilder.delay_seconds`, retry options), Cloudflare Queues (limits table, delay/batching/retries docs, consumer concurrency, DLQ docs), Cloudflare Workflows (steps/sleep/waitForEvent/limits).
- Provider matrix verified against: AWS SQS docs (`DelaySeconds` 0–900, visibility timeout 0–12h per-message, redrive `maxReceiveCount`, FIFO `MessageDeduplicationId` 5-min window, content-based dedup, retention 1 min–14 days), Azure Service Bus docs (duplicate detection 20s–7d on `MessageId`, `ScheduledEnqueueTimeUtc`, peek-lock/lock renewal/abandon/defer, `MaxDeliveryCount` default 10 → DLQ, sessions), Azure Storage Queues docs (64 KB messages, TTL 7d default, enqueue `visibilityTimeout` ≤7d, pop-receipt updates, Functions/WebJobs `MaxDequeueCount` 5 → `{queue}-poison` convention), GCP Pub/Sub docs (ack deadline 10s–600s, exactly-once opt-in pull, ordering keys, dead-letter topic `maxDeliveryAttempts` 5–100 best-effort, retention 10 min–31 days, subscription `retryPolicy`; no native publish-delay — Cloud Tasks workaround).
- Did **not** run a fresh `wrangler dev` queue-consumer e2e or attempt a NetWasm build with extra JS exports — the Binaryen export question remains open and is flagged as a Phase 1 risk.

## Sources

**Reptar (local):** everything listed in Verification notes.

**Ecosystem:**

- [`adonisjs/queue`](https://github.com/adonisjs/queue) · [AdonisJS Queues guide](https://docs.adonisjs.com/guides/digging-deeper/queues) · [`boringnode/queue`](https://github.com/boringnode/queue)
- [`Arcenox-co/TickerQ`](https://github.com/Arcenox-co/TickerQ) · [TickerQ Native AOT guide](https://tickerq.net/docs/5.x/guides/native-aot) · [What is TickerQ](https://tickerq.net/docs/what-is-tickerq)
- [Quartz.NET: Publishing Trimmed and Native AOT](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/trimming-and-native-aot.html) · [Quartz: Coming from Hangfire](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/coming-from-hangfire.html) · [Hangfire AOT issue #2478](https://github.com/HangfireIO/Hangfire/issues/2478)
- [MediatR commercial launch](https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/) · [MediatR AOT issue #892](https://github.com/jbogard/MediatR/issues/892)
- [`riverqueue/river`](https://github.com/riverqueue/river/) · [River job retries](https://riverqueue.com/docs/job-retries) · [River periodic jobs](https://riverqueue.com/docs/periodic-jobs)
- [apalis architecture](https://apalis.dev/docs/introduction/architecture) · [apalis piping backends](https://apalis.dev/docs/advanced-concepts/piping-backends) · [`apalis-cron`](https://docs.rs/apalis-cron/latest/apalis_cron/)
- [`cloudflare/workers-rs` queues example](https://github.com/cloudflare/workers-rs/blob/main/examples/queue/src/lib.rs)
- [Cloudflare Queues: limits](https://developers.cloudflare.com/queues/platform/limits/) · [batching, retries & delays](https://developers.cloudflare.com/queues/configuration/batching-retries/) · [consumer concurrency](https://developers.cloudflare.com/queues/configuration/consumer-concurrency/) · [DLQs](https://developers.cloudflare.com/queues/configuration/dead-letter-queues/) · [Workflows](https://developers.cloudflare.com/workflows/)

**Queue providers (for the §4.2 capability matrix):**

- [SQS: deduplication IDs](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagededuplicationid-property.html) · [SQS: dead-letter queues / redrive](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html) · [SQS: visibility timeout](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html) · [SetQueueAttributes (DelaySeconds, RedrivePolicy)](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html)
- [Service Bus: duplicate detection](https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection) · [Service Bus: DLQ / MaxDeliveryCount](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues) · [Service Bus: sessions (FIFO)](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions) · [Service Bus: message properties (ScheduledEnqueueTimeUtc, MessageId)](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messages-payloads)
- [Storage Queues: introduction (TTL, sizes)](https://learn.microsoft.com/en-us/azure/storage/queues/storage-queues-introduction) · [Update Message (pop receipt, visibility ≤7d)](https://learn.microsoft.com/en-us/rest/api/storageservices/update-message) · [Azure Functions queue trigger (poison convention, MaxDequeueCount)](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger)
- [Pub/Sub: dead-letter topics (maxDeliveryAttempts 5–100)](https://docs.cloud.google.com/pubsub/docs/dead-letter-topics) · [Pub/Sub: ordering keys](https://docs.cloud.google.com/pubsub/docs/ordering) · [Pub/Sub: exactly-once delivery](https://docs.cloud.google.com/pubsub/docs/exactly-once-delivery) · [Pub/Sub: subscription properties (ackDeadline 10–600s, retention)](https://cloud.google.com/pubsub/docs/reference/rest/v1/projects.subscriptions)
