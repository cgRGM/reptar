---
title: System Architecture & Lifecycle
description: The execution model, memory boundary, and request lifecycle in Reptar.
---

## High-Level Architecture

Reptar divides responsibilities between a thin TypeScript host shim on Cloudflare Workers and a compiled C# WebAssembly core.

```text
TanStack Start / Expo / Next / curl / anything
        │  createClient<AppType>  (generated)
        ▼
Cloudflare Worker — TS host shim (generated, ~thin)
   ├─ fetch, cookies, crypto (WebCrypto + native PBKDF2)
   ├─ D1, R2, Workers AI, RateLimit bindings
   ├─ telemetry (OTel/Axiom, logs)
   ├─ Better Auth mount (only when using the adapter)
   └─ calls reptar.wasm
        │
        ▼
C# Reptar core (compiled to Wasm via NetWasm)
   router → middleware → validation → handler → c.Db / c.AI / c.Storage
```

---

## Request Lifecycle

1. **Incoming Request:** An HTTP request hits the Cloudflare Worker isolate.
2. **Envelope Preparation:** The generated TS host shim packages request headers, parameters, and body into a raw JSON envelope.
3. **Wasm Invocation:** The host shim invokes `wasm.handle(envelope)` on the cached WebAssembly module.
4. **Pipeline Execution:**
   - **Router:** Matches the method and URL path against registered route trees.
   - **Middleware:** Executes global and route-level middleware (e.g., `RequestId`, `Telemetry`, `Cors`).
   - **Validation:** Executes source-generated `Rule<T>` validators against the input model.
   - **Handler:** Runs the user-defined C# route handler.
5. **Platform Calls (Imports):** Data operations (such as `c.Db.QueryAsync`, `c.Storage.PutAsync`, or `c.AI.GenerateAsync`) call host import functions provided by the TS shim.
6. **Response Envelope:** The C# core returns a serialized response envelope containing the status code, response headers, and payload back to the TS host shim.
7. **HTTP Response:** The Worker returns a standard Web `Response` object.

---

## Dual-Target Compilation

The same C# feature files compile into two target environments without code changes:

| Target | Toolchain | Primary Use Case |
| --- | --- | --- |
| `netwasm0.1` | NetWasm | Cloudflare Workers (Primary Target) |
| `net10.0` | Native AOT | AWS Lambda, Google Cloud Run, Container fallbacks |

This dual-target design guarantees portability: if your deployment needs shift from Cloudflare Workers to containerized infrastructure, the backend compiles directly to native machine code without rewriting route handlers.
