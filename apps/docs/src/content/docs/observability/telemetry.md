---
title: Observability & Telemetry
description: Structured JSON logs, Server-Timing headers, dev dashboard, and Axiom export.
---

## Observability by Default

Reptar is engineered to be fully observable in production without extra configuration.

---

## 1. Structured JSON Log Lines

Every request produces a single structured JSON log line in Cloudflare logs:

```json
{
  "timestamp": "2026-10-22T18:15:30.123Z",
  "request_id": "req_01J8F9...",
  "trace_id": "trace_4a2c...",
  "route": "GET /posts/:id",
  "status": 200,
  "duration_ms": 14.2,
  "db_ms": 8.1,
  "db_queries": 1,
  "user_id": "usr_99a...",
  "cf_ray": "8d39f41b2...",
  "colo": "IAD"
}
```

---

## 2. Server-Timing Headers

Reptar attaches granular `Server-Timing` headers to responses, making execution performance inspectable in browser developer tools:

```http
Server-Timing: reptar;dur=2.4, db;dur=8.1, total;dur=14.2
```

---

## 3. Developer Dashboard (`/_reptar`)

During development, navigating to `/_reptar` loads a real-time metrics dashboard:

- **Last 500 Requests:** Full inspection of request envelopes and execution traces.
- **Latency Percentiles:** Live p50, p95, and p99 statistics.
- **Query Performance:** Slow route identification and database query counts.
- **Validation Failures:** Detailed diagnostics for rejected payloads.

---

## 4. OTLP & Axiom Export

Reptar supports direct OTLP export with Axiom as the default destination:

```csharp
var app = Reptar(new ReptarConfig
{
    Telemetry = Axiom(new AxiomOptions
    {
        Dataset = "my-reptar-app",
        ApiKey = c.Req.Env("AXIOM_API_KEY")
    })
});
```
