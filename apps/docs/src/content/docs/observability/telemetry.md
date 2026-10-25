---
title: Observability & Telemetry
description: Structured JSON logs, Server-Timing headers, write-gate diagnostics, dev dashboard, and Axiom OTLP export.
---

## Observability by Default

Reptar is engineered to be observable in both Cloudflare Workers isolates and Native AOT containers without external agent sidecars.

## 1. Structured JSON Log Lines

Every request emits a structured JSON log line containing latency breakdowns and database query metrics:

```json
{
  "timestamp": "2026-10-25T05:15:30.123Z",
  "request_id": "req_01J8F9...",
  "trace_id": "trace_4a2c...",
  "route": "GET /posts/:id",
  "status": 200,
  "duration_ms": 2.6,
  "db_ms": 0.4,
  "db_queries": 1,
  "user_id": "usr_99a...",
  "cf_ray": "8d39f41b2...",
  "colo": "CMH"
}
```

## 2. Server-Timing Headers

Reptar attaches granular `Server-Timing` headers to responses so you can inspect Wasm/AOT execution vs database time directly in browser DevTools:

```http
Server-Timing: reptar;dur=0.8, db;dur=1.4, total;dur=2.2
```

## 3. SQLite Write-Gate Timing Observer

When benchmarking or diagnosing high-concurrency write workloads on SQLite, `SqliteDbClient` exposes an opt-in `WriteTimingObserver` (zero overhead when null) that separates single-writer semaphore queue wait time from actual SQLite `INSERT`/`UPDATE` execution time:

```csharp
sqliteClient.WriteTimingObserver = (gateWaitMs, executeMs) =>
{
    // Record p50 / p95 / p99 write-gate contention vs raw statement execution
};
```

## 4. Developer Dashboard (`/_reptar`)

During local development, navigating to `/_reptar` renders a real-time telemetry view:

- **Recent Request Ring Buffer:** Full inspection of request envelopes, status codes, and timings.
- **Latency Percentiles:** Live p50, p95, and p99 distributions.
- **Database Diagnostics:** Query counts and slow-query identification per route.
- **Validation Failures:** Structured inspection of rejected payloads.

## 5. OTLP & Axiom Export

Reptar supports direct OTLP telemetry export with Axiom as a first-class destination:

```csharp
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.D1(),
    Telemetry = Axiom(new AxiomOptions
    {
        Dataset = "my-reptar-app",
        ApiKey = Environment.GetEnvironmentVariable("AXIOM_API_KEY") ?? "",
    }),
});
```
