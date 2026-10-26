# Reptar Jobs Example

One app source, five queue adapters — the `REPTAR_QUEUE` environment variable
is the only thing that changes:

```bash
REPTAR_QUEUE=sync        bun --cwd examples/jobs run app.cs   # inline (native default)
REPTAR_QUEUE=db          bun --cwd examples/jobs run app.cs   # durable, in your SQL database
REPTAR_QUEUE=sqs         ...                                   # Amazon SQS (AWS_* env)
REPTAR_QUEUE=azure-storage ...                                # Azure Queue Storage (AZURE_STORAGE_*)
REPTAR_QUEUE=pubsub      ...                                  # GCP Pub/Sub (GOOGLE_* env)
```

Routes: `POST /enqueue?n=<count>&delay=<seconds>` queues jobs,
`GET /stats` reports processed count, and an `OnScheduled` cron runs every
minute. On Kestrel, the built-in poller drains the durable adapters and the
cron scheduler fires registered expressions — watch the startup banner.

## Benchmarks (measured, this repo, M-series macOS, SQLite WAL)

`REPTAR_QUEUE=db` end-to-end through the HTTP surface:

| Count | Enqueue | Enqueue rate | Full drain (incl. 2s poll interval) |
| :-- | :-- | :-- | :-- |
| 2000 | 335 ms | ~5,970 jobs/s | < 8 s (~250 jobs/s sustained incl. handler writes) |

Enqueue cost is one indexed `INSERT` per job through the SQLite single-writer
gate; drain cost is dominated by the per-job handler write, not the queue.

## Bench script

Start the app, then drive it:

```bash
REPTAR_QUEUE=db PORT=8787 bun --cwd examples/jobs run app.cs &
REPTAR_QUEUE=db REPTAR_JOBS_URL=http://127.0.0.1:8787 bun examples/jobs/bench.ts 2000
```

The bench enqueues N jobs, waits for `/stats` to report them processed, and
prints enqueue and drain rates. Keep `HOST=127.0.0.1` on macOS 26 + .NET 10
dual-stack setups (see the note in `KestrelHost.cs`).

## Queue stats

`bun reptar inspect --app examples/jobs` reads the jobs table directly:

```
Queue:       3 pending, oldest 62s, 0 completed, 1 dead (reptar_jobs)
```

The same section rides into `inspect --json` and the `reptar_inspect` MCP tool.

## Live-fire against real cloud queues

```bash
bun examples/jobs/livefire.ts            # every provider with credentials
bun examples/jobs/livefire.ts sqs         # one provider
```

Each adapter runs a full enqueue → retry (via REPTAR_JOBS_FAIL_FIRST=1) →
process cycle against real infrastructure. Providers without credentials are
reported MISSING with the exact variables to set — see the harness header for
the per-provider setup commands.

## Durable Object alarms

`app.OnAlarm(ctx => ...)` registers a handler behind the reserved
`/_reptar/alarm` path. On Workers, `createReptarDurableObjectHost` implements
the runtime's `alarm()` handler and dispatches it through the normal fetch
pipeline; schedule wake-ups with `POST /_reptar/alarm/schedule {"runAtMs": ...}`
on the object. On native targets any HTTP client can hit the reserved path.
