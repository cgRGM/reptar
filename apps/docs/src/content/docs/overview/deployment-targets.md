---
title: Deployment Targets
description: Deploy Reptar across Cloudflare Workers, AWS Lambda Native AOT, Docker containers, and VPS instances.
---

## Overview

Reptar is engineered from day one as a **dual-target framework**.

Your application logic—routes, `Rule<T>()` validators, `[ReptarEntity]` models, and database schemas—is written once in C#. The framework compiles the same source code into two distinct runtime artifacts:

```text
                ┌──────────────────────────────┐
                │    C# Feature Slices & App   │
                │ (routes, schema, validation) │
                └──────────────┬───────────────┘
                               │
                ┌──────────────┴──────────────┐
                ▼                             ▼
       netwasm0.1 (Wasm)              net10.0 (Native AOT)
                │                             │
     ┌──────────┴──────────┐       ┌──────────┴──────────┐
     ▼                     ▼       ▼                     ▼
Cloudflare Workers   Vercel Edge  AWS Lambda       Docker / VPS
 (Shipped/Live)       (Roadmap)    (Shipped)       (Shipped)
```

## Target Readiness Matrix

| Platform | Target Runtime | Status | Cold Start | Base Footprint | Database Support |
| :-- | :-- | :-- | :-- | :-- | :-- |
| **Cloudflare Workers** | `netwasm0.1` (Wasm) | **Shipped & Verified** | Sub-50 ms | ~9.5 MB Wasm | D1 (SQLite), Hyperdrive (Postgres) |
| **Linux VPS / Docker** | `net10.0` (Native AOT) | **Shipped & Verified** | 258 ms | **10.9 MB** Binary | SQLite, PostgreSQL (Npgsql), MySQL |
| **AWS Lambda** | `net10.0` (Native AOT) | **Shipped Adapter** | Sub-15 ms | ~20 MB Memory | PostgreSQL, MySQL, SQLite |
| **Vercel Edge** | `netwasm0.1` (Wasm) | **Roadmap / Evaluation** | Sub-50 ms | ~25 MB Memory | Hyperdrive, HTTP APIs |

## Per-Target Feature Support Matrix

Because NetWasm `0.10.0` (`wasm-opt` / Binaryen) enforces a single `[JSExport]` entry point (`Program.Handle`) per WebAssembly module, some streaming and event-driven primitives execute directly in C# on `net10.0` (`Kestrel` and `Lambda`) while being terminated or bridged by the TypeScript `@reptar/host` Worker shim on `Cloudflare Workers`:

| Feature / Primitive | Cloudflare Workers (`netwasm0.1` + `@reptar/host`) | Cloudflare Durable Objects (NetWasm + DO) | Kestrel (`net10.0` Native AOT) | AWS Lambda (`net10.0` Native AOT) |
| :-- | :-- | :-- | :-- | :-- |
| **Routing, Groups & Middleware** | ✅ Native C# (`Wasm`) | ✅ Native C# (`Wasm`) | ✅ Native C# | ✅ Native C# |
| **`Rule<T>` Validation & Contracts** | ✅ Native C# (`Wasm`) | ✅ Native C# (`Wasm`) | ✅ Native C# | ✅ Native C# |
| **Database (`c.Db`)** | ✅ D1 (`[JSImport]`) & Postgres | ✅ D1 (`[JSImport]`) & DO Storage | ✅ SQLite, Postgres, MySQL | ✅ SQLite, Postgres, MySQL |
| **Object Storage (`c.Storage`)** | ✅ R2 (`[JSImport]`) | ✅ R2 (`[JSImport]`) | ✅ `LocalStorage` & `S3Storage` | ✅ `S3Storage` & `LocalStorage` |
| **Multipart & `Files.Check`** | ✅ Native C# (`ReadOnlySpan<byte>`) | ✅ Native C# (`ReadOnlySpan<byte>`) | ✅ Native C# (`ReadOnlySpan<byte>`) | ✅ Native C# (`ReadOnlySpan<byte>`) |
| **Built-in Auth, JWT & Signed Cookies** | ✅ Native C# + WebCrypto (`[JSImport]`) | ✅ Native C# + WebCrypto | ✅ Native C# (`HMACSHA256` / PBKDF2) | ✅ Native C# (`HMACSHA256` / PBKDF2) |
| **Background Tasks (`ctx.WaitUntil`)** | ✅ Edge `ctx.waitUntil()` bridge | ✅ Edge `ctx.waitUntil()` + DO Alarms | ✅ Unblocked ThreadPool + Error Trap | ⚠️ Buffered execution |
| **CORS, Rate Limiting & SWR Cache** | ✅ Native C# + `@reptar/host` edge headers | ✅ Native C# + `@reptar/host` | ✅ Native C# | ✅ Native C# |
| **SSE (`c.Sse` / `StreamLlmAsync`)** | ⚡ TS Worker `ReadableStream` shim (`#if !NETWASM` in C#) | ⚡ TS Worker `ReadableStream` shim | ✅ Native C# `PipeWriter` streaming | ⚠️ Buffered response (API Gateway v2) |
| **WebSockets (`app.OnWebSocket`)** | ⚡ Single-socket Worker (`WebSocketPair`) | ✅ Full cross-client Fanout & Presence | ✅ Native C# `System.Net.WebSockets` | ❌ Use API Gateway WebSocket API |
| **Multi-room Hubs (`Reptar.Realtime`)** | ❌ Cannot fanout across requests | ✅ Supported (`examples/realtime-rooms`) | ✅ Supported (ConcurrentDictionary) | ❌ External Pub/Sub required |
| **AI Workspace Tools (`app.UseWorkspaceTools`)** | ❌ Confined / In-memory tools only | ❌ In-memory tools only | ✅ Full Local Process & Filesystem | ⚡ Lambda /tmp filesystem |
| **Queue Consumers (`app.OnQueue`)** | ⚡ TS Worker `queue(batch, env)` host bridge | ⚡ DO Alarm queue drain | ✅ Native C# (`HandleQueueBatchAsync`) | ⚡ SQS event adapter |
| **Cron Triggers (`app.OnScheduled`)** | ⚡ TS Worker `scheduled(event, env)` host bridge | ⚡ DO scheduled alarms | ✅ Native C# (`HandleScheduledAsync`) | ⚡ EventBridge scheduled rule |
| **OpenAPI 3.1 (`/openapi.json`, `/reference`)** | ⚡ Generated at build time (`bun run rpc:gen`) | ⚡ Generated at build time | ✅ Native C# (`app.UseScalar()`) | ✅ Native C# (`app.UseScalar()`) |
| **Static Files (`app.UseStaticFiles`)** | ⚡ Cloudflare Workers Assets (`env.ASSETS`) | ⚡ Cloudflare Workers Assets | ✅ Native C# (`ETag` + SPA fallback) | ⚡ CloudFront / S3 static hosting |

## 1. Cloudflare Workers (Primary Edge Target)

Reptar compiles to WebAssembly (`netwasm0.1`) and executes directly inside Cloudflare Workers V8 isolates.

### Runtime Characteristics

- **Compilation:** NetWasm compiler emits optimized `.wasm` bytecode with compile-time source generation.
- **Isolate Execution:** Sub-2ms execution per request, measured live via `server-timing: reptar;dur=0.0`.
- **Memory Footprint:** Runs comfortably within the 128 MB Worker isolate ceiling.
- **Zero Reflection:** All JSON serialization, route dispatching, and D1 row mapping are generated at build time.

### Platform Bindings

When running on Cloudflare Workers, your C# code accesses native platform surfaces through `ctx`:

```csharp
// Cloudflare D1 Relational SQLite
var posts = await ctx.Db.ListAsync<Post>(PostsTable);

// Cloudflare R2 Object Storage
var url = await ctx.Storage.PutAsync("avatars/user_1.png", bytes, "image/png");

// Cloudflare Workers AI (GPU LLMs)
var summary = await ctx.AI.RunAsync("@cf/meta/llama-3-8b-instruct", prompt);

// Cloudflare KV Namespace
await ctx.Cache.PutAsync("cache:key", "value", ttlSeconds: 300);

// Cloudflare Queues
await ctx.Queues.SendAsync("notifications", new { userId = "u1" });
```

## 2. Docker & Linux VPS (Native AOT)

For dedicated VPS instances (Hetzner, Lightsail, DigitalOcean) or container orchestrators (ECS, Cloud Run, Kubernetes), Reptar compiles into a self-contained Native AOT Linux binary powered by `Reptar.Kestrel`'s zero-copy `PipeReader`/`PipeWriter` transport.

### Key Metrics

- **Binary Size:** **10.9 MB** standalone SQLite-only executable with `-p:ReptarSqliteOnly=true` (or **17.1–18.4 MB** with PostgreSQL Npgsql + MySqlConnector included). No .NET runtime is installed on the host.
- **Boot Time:** ~258 ms process boot with zero JIT warmup.
- **Database Support:** SQLite (32-channel `Mode=ReadOnly` WAL read pool), PostgreSQL (`NpgsqlDataSource` + transactional `NpgsqlBatch`), and MySQL (`MySqlConnector`).

### Distroless Containerfile

```dockerfile
# Stage 1: Build Native AOT Linux executable
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev
COPY framework/ framework/
COPY examples/blog/ examples/blog/
WORKDIR /src/examples/blog
RUN dotnet publish app.cs -r linux-x64 -c Release --self-contained \
    -p:PublishAot=true -p:ReptarSqliteOnly=true -o /app/publish && \
    rm -f /app/publish/*.dbg

# Stage 2: Distroless runtime
FROM gcr.io/distroless/cc-debian12:nonroot
WORKDIR /app
COPY --from=build /app/publish/app /app/reptar
ENV PORT=8787
EXPOSE 8787
USER nonroot:nonroot
ENTRYPOINT ["/app/reptar"]
```

## 3. AWS Lambda (Native AOT & Adapter)

Reptar supports two deployment patterns on AWS Lambda:

### Pattern A: Custom Runtime (`provided.al2023`)

Reptar provides `Reptar.Lambda` with an integrated polling loop for the AWS Lambda Runtime API. The compiled Native AOT binary runs as `bootstrap` with cold starts under 15 milliseconds:

```csharp
using Reptar;
using Reptar.Hosting.Lambda;

var app = ReptarApp.Create(...);
Blog.Configure(app);

await app.RunLambdaAsync();
```

### Pattern B: Node.js / Wasm Handler (`@reptar/adapter-lambda`)

When deploying behind API Gateway HTTP API (v2) or Lambda Function URLs via Node.js/Wasm:

```typescript
import { createReptarHost } from "@reptar/host";
import { createLambdaHandler } from "@reptar/adapter-lambda";

const host = createReptarHost({ ... });
export const handler = createLambdaHandler(host);
```
