---
title: Observability & Telemetry
description: Structured request logs, W3C trace context, database timing, Server-Timing, sampling, and health probes.
---

## Observability by Default

Reptar emits one structured request record per sampled request and exposes
per-request database timing without a sidecar or external agent.

## 1. Structured JSON Log Lines

Enable the telemetry middleware:

```csharp
app.Use(ReptarApi.RequestId());
app.Use(ReptarApi.Telemetry(new TelemetryOptions
{
    SampleRate = 0.1,       // log 10% of successful fast requests
    SlowRequestMs = 500,    // always log requests >= 500ms
    SlowQueryMs = 100,      // count queries >= 100ms as slow
}));
```

Each record is a single JSON line:

```json
{
  "timestamp": "2026-10-26T12:00:00.0000000+00:00",
  "request_id": "req_01J8F9...",
  "trace_id": "4bf92f3577b34da6a3ce929d0e0e4736",
  "span_id": "00f067aa0ba902b7",
  "parent_span_id": "a3ce929d0e0e4736",
  "method": "GET",
  "route": "/posts/:id",
  "path": "/posts/42",
  "status": 200,
  "duration_ms": 2.6,
  "db_ms": 0.4,
  "db_queries": 1,
  "cache": "HIT",
  "user_id": "usr_99a...",
  "cf_ray": "8d39f41b2...-CMH",
  "colo": "CMH",
  "slow": false
}
```

`route` is the matched template, not the raw path. `cf_ray`/`colo` are populated
on Cloudflare Workers when the headers are present.

## 2. W3C Trace Context

Reptar parses the incoming `traceparent` header and generates a local span id.
The parsed values are available on `Ctx` and are included in the request log:

```csharp
ctx.TraceId;
ctx.SpanId;
ctx.ParentSpanId;
```

The framework creates the local span id; it does not yet export spans to an
OTLP collector. `TraceContext.Current.ToTraceParent()` is available for
propagating the context to outbound calls.

## 3. Server-Timing Headers

The telemetry middleware adds database and total timings to the response:

```http
Server-Timing: db;dur=1.4;desc="2 queries", total;dur=2.2
```

Use `ctx.ServerTiming("auth", 1.8, "JWT verification")` for custom spans.

## 4. Database Timing

`InstrumentedDbClient` wraps the configured database client. When a telemetry
scope is active it records:

- query count and total time (`db_queries`, `db_ms`);
- slow query count and the slowest SQL (truncated, logged at warning level);
- query errors.

When telemetry is not active the fast path is a single `AsyncLocal` read and no
stopwatch is started.

## 4b. Prometheus Metrics

Mount the dependency-free metrics middleware and endpoint:

```csharp
app.UseMetrics();
app.MapMetrics(); // GET /metrics
```

It exposes `reptar_http_requests_total` (method/route/status) and
`reptar_http_request_duration_ms` histograms with fixed latency buckets.
Route labels use the matched template, not the raw path. Protect `/metrics`
with `Auth.RequireRole("admin")` or a private listener when exposed publicly.

`UseSWR()` reports cache outcomes as `reptar_cache_requests_total{status="HIT|MISS|STALE|BYPASS"}`
through the same registry; mount `UseMetrics()` before `UseSWR()`.

## 5. Sampling and Overhead

- `SampleRate` applies to successful, non-slow requests.
- Errors (`status >= 400`) and slow requests are always logged.
- Set `SampleRate = 0` to disable successful-request logs while keeping errors
  and slow requests.
- `SlowQueryMs` controls the slow-query counter. SQL text is truncated to keep
  log lines bounded.

## 6. Health and Readiness

`MapHealth` exposes liveness and readiness endpoints:

```csharp
app.MapHealth();
```

`GET /health/live` returns `{"status":"healthy"}`.
`GET /health/ready` executes `SELECT 1` against the configured database and
returns `503` when it is unavailable.

## 7. Not Yet Shipped

The following are roadmap items, not current behavior:

- OTLP/Axiom log export (`AxiomOptions` is not implemented);
- a `/_reptar` developer dashboard;
- span export and trace sampling beyond the request log.

Until those land, ship the structured JSON lines to your platform's log sink
(Cloudflare Logpush, Axiom, Datadog, etc.) and use `Server-Timing` for local
profiling.
