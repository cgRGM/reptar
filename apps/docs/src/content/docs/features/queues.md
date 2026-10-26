---
title: Queues & Background Jobs
description: One typed enqueue API across Cloudflare Queues, SQS, Azure Storage Queues, Pub/Sub, and your own SQL database — retries, delays, dead-lettering, and cron schedules with zero reflection.
---

## Overview

Reptar queues are a **port**, not an engine. The framework defines a tiny vocabulary — enqueue with options, consume with handlers, retry with a delay — and per-target adapters map it onto the queue infrastructure you already run:

| Adapter | Ships in | Storage | Delay | Dedup | Consumer |
| :-- | :-- | :-- | :-- | :-- | :-- |
| **Cloudflare Queues** | NetWasm (default) | Cloudflare Queues | `delaySeconds` ≤24h | — | Worker `queue()` handler |
| **Sync** | Native (default) | In memory | — | — | Inline dispatch |
| **DB** | Native (opt-in) | Your SQLite / PostgreSQL / MySQL | `visible_at` | Dedup key | Kestrel poller |
| **SQS** | Native (opt-in) | Amazon SQS | ≤15 min | FIFO `MessageDeduplicationId` | Kestrel poller or Lambda event source |
| **Azure Storage Queues** | Native (opt-in) | Azure Queue Storage | ≤7 days | — | Kestrel poller |
| **Pub/Sub** | All targets (opt-in) | Google Cloud Pub/Sub | — (Cloud Tasks is the GCP workaround) | Exactly-once subscription | Kestrel poller |

No SDK dependency, no reflection: SQS and Azure sign requests with hand-rolled SigV4 / SharedKey HMAC (validated against the published AWS test vector), Pub/Sub is pure JSON + a bearer token, and the DB queue is plain SQL through the app's own `IDbClient`.

## The vocabulary

```csharp
public sealed record QueueSendOptions(
    int? DelaySeconds = null,   // honor-or-clamp per adapter
    string? DedupId = null,     // honored where the provider has dedup
    int? AttemptsHint = null);  // advisory max attempts for this message
```

Handlers register by queue name — the compile-time, reflection-free dispatcher:

```csharp
app.OnQueue<BenchJob>("jobs", async (ctx, msg, job) =>
{
    await ctx.Db.ExecuteAsync("update orders set processed = 1 where id = @id",
        new { id = job.OrderId });
});

// ack = return normally; retry = throw; snooze = throw RetryIn.
app.OnQueue("emails", (ctx, msg) =>
{
    if (RateLimited())
        throw new RetryIn(120);   // retry this message in 2 minutes
    return Task.CompletedTask;
});
```

`RetryIn` maps to the native primitive of every adapter: Cloudflare `retry({ delaySeconds })`, SQS `ChangeMessageVisibility`, Azure `Update Message` visibility, Pub/Sub `modifyAckDeadline`, and `visible_at` for the DB queue.

## Choosing the adapter

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite("app.db"),

    // Native default: Sync (inline execution — dev, tests, honest behavior).
    // Change one line to switch infrastructure; app code stays identical.
    Queues = mode switch
    {
        "db" => Queues.Db(database),
        "sqs" => Queues.SqsFromEnvironment(),          // AWS_REGION, AWS_ACCESS_KEY_ID,
        "azure-storage" => Queues.AzureStorageFromEnvironment(), // AZURE_STORAGE_ACCOUNT/KEY
        "pubsub" => Queues.PubSubFromEnvironment(),    // GOOGLE_CLOUD_PROJECT, tokens
        _ => null, // null = platform default (Workers binding / Sync)
    },
});
```

**The DB adapter** (`Queues.Db`) is the zero-infrastructure durable default for single-node deployments: one `reptar_jobs` table (a regular `TableDef`, migrated by `bun reptar db generate`, visible in `reptar inspect`), pending/complete/dead states, exponential backoff, per-message max attempts, dedup keys, and a completed-row retention sweep. For multi-node throughput use a platform adapter instead — that is what SQS and Queues are for.

## How each runtime consumes

- **Cloudflare Workers**: the platform invokes you. The `@reptar/host` `queue()` handler dispatches to `HandleQueueBatchAsync`, acks successes, and retries failures — honoring `RetryIn` delays through `message.retry({ delaySeconds })`. Consumer batching, `max_retries`, `dead_letter_queue`, and `max_concurrency` come from `wrangler.jsonc`. **One mapping to remember:** workerd delivers the *physical* queue name (`batch.queue`), while `OnQueue` registers *logical* names — map it in the Worker's `queue()` handler (`{ ...batch, queue: "events" }`) exactly like the producer binding does.
- **Kestrel / native**: the framework is the scheduler. A bounded poller drains every configured `IQueueConsumer` (`REPTAR_QUEUE_POLL_SECS=2`, `REPTAR_QUEUE_BATCH=25`), and a cron scheduler evaluates `OnScheduled` expressions (`REPTAR_CRON_TICK_SECS=15`). Both start with the module lifecycle and stop with the host.
- **AWS Lambda**: an SQS event source mapping with `FunctionResponseTypes: ReportBatchItemFailures` feeds `HandleQueueBatchAsync` directly; failed IDs return to SQS for redrive.

## Cron schedules

```csharp
// 5-field everywhere; 6-field (with seconds) on the Kestrel scheduler.
app.OnScheduled("*/5 * * * *", async (ctx, evt) => { ... });
```

On Workers, Cron Triggers invoke the `scheduled` handler and the registered expression string matches the trigger. On Kestrel, the built-in scheduler parses the expression (minute/hour/day/month/weekday with `*`, steps, ranges, lists, and the classic day-of-month OR day-of-week rule) and fires at most once per matched tick.

## Durable Object alarms

`app.OnAlarm(ctx => ...)` registers the alarm handler behind the reserved
`/_reptar/alarm` path. `createReptarDurableObjectHost` implements the runtime's
`alarm()` handler and dispatches through the normal fetch pipeline — no extra
JS exports — and a throwing handler rethrows so the runtime retries with its
built-in backoff. Schedule a wake-up from C# through the Durable bridge:

```csharp
// POST the reserved schedule path on the scheduler object; the DO host
// stores the wake-up time with state.storage.setAlarm.
await ctx.Durable.FetchAsync(
    "SCHEDULER", "jobs", "/_reptar/alarm/schedule", "POST",
    DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds().ToString());
```

Alarms are the Workers-native primitive for delayed work past Queues' 24-hour
delay cap and for "run exactly once at time T". On native targets the
equivalent is a delayed DB-queue message — or any HTTP client hitting the
reserved path.

## Observing the queue

- `reptar inspect --app .` prints queue stats directly from the jobs table
  (`3 pending, oldest 62s, 0 completed, 1 dead`), and the same section rides
  into `inspect --json` and the `reptar_inspect` MCP tool.
- `ReptarConfig.QueueObserver` is a null-by-default lifecycle hook — zero cost when unset, no tracing dependency in the framework:

```csharp
public sealed class JobMetrics : IQueueObserver
{
    public void Enqueued(string queue, string messageId, QueueSendOptions? options) { ... }
    public void Retried(string queue, string messageId, int attempt, int delaySeconds) { ... }
    public void DeadLettered(string queue, string messageId, int attempts) { ... }
    // Completed, Failed ...
}
```

## Testing

The default `SyncQueueClient` executes the registered handler inline through the exact production dispatch path — so tests exercise real Ctx/error/RetryIn behavior. `FakeQueueClient` records every send for assertions and replays them through the pipeline on `DrainAsync()`.

## Live-fire against real providers

`bun examples/jobs/livefire.ts` runs a full enqueue → retry → process cycle
against real cloud infrastructure for every adapter whose credentials are
present (SQS, Azure Storage Queues, Pub/Sub), reporting PASS/FAIL/MISSING
with the exact variables to set — nothing is silently skipped:

```bash
aws sso login && export SQS_QUEUE_URL=https://sqs.us-east-1.amazonaws.com/.../reptar-livefire
bun examples/jobs/livefire.ts sqs
```

The harness sets `REPTAR_JOBS_FAIL_FIRST=1` so every job fails its first
delivery and exercises the adapter's retry path before landing.

## Recipes (not shipped in core)

- **Azure Service Bus** needs AMQP (the REST management API does not carry data plane): use `Azure.Messaging.ServiceBus` behind `IQueueClient`/`IQueueConsumer`, or ask for an in-repo adapter.
- **Redis / BullMQ**: a minimal RESP client (`LPUSH` + `BRPOPLPUSH` + `LREM`, plus `ZADD` for delays) fits the port; BullMQ-compatibility does not.
- **Kafka / RabbitMQ / NATS**: binary protocols or broker-specific semantics — wrap the official client in the two interfaces.

Each is an `IQueueClient` (one send method) plus optionally an `IQueueConsumer` (receive/ack/retry) — an afternoon of adapter work. We ship one in-repo when a real workload needs it.
