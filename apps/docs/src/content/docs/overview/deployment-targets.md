---
title: Deployment Targets
description: Deploy Reptar across Cloudflare Workers, AWS Lambda Native AOT, Docker containers, and VPS instances.
---

## Overview

Reptar is engineered from day one as a **dual-target framework**.

Your application logic—routes, validators, database schemas, and business models—is written once in pure C#. The framework compiles the same source code into two distinct runtime artifacts:

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

---

## Target Readiness Matrix

| Platform | Target Runtime | Status | Cold Start | Base Footprint | Database Support |
| :-- | :-- | :-- | :-- | :-- | :-- |
| **Cloudflare Workers** | `netwasm0.1` (Wasm) | **Shipped & Verified** | Sub-50ms | ~25 MB Isolate | D1 (SQLite), Hyperdrive (Postgres) |
| **Linux VPS / Docker** | `net10.0` (Native AOT) | **Shipped & Verified** | Sub-5ms | 17.1 MB Binary | PostgreSQL (Npgsql), SQLite |
| **AWS Lambda** | `net10.0` (Native AOT) | **Shipped Adapter** | Sub-15ms | ~20 MB Memory | PostgreSQL, SQLite |
| **Vercel Edge** | `netwasm0.1` (Wasm) | **Roadmap / Evaluation** | Sub-50ms | ~25 MB Memory | Hyperdrive, HTTP APIs |

---

## 1. Cloudflare Workers (Primary Edge Target)

Reptar compiles to WebAssembly (`netwasm0.1`) and executes directly inside Cloudflare Workers V8 isolates.

### Runtime Characteristics

- **Compilation:** NetWasm compiler emits optimized `.wasm` bytecode with compile-time source generation.
- **Isolate Execution:** Sub-2ms execution per request, measured live via `server-timing: reptar;dur=0.0`.
- **Memory Footprint:** Runs comfortably within the 128 MB Worker isolate ceiling.
- **Reflection:** Zero runtime reflection. All JSON serialization, route dispatching, and D1 row mapping are generated at build time.

### Platform Bindings

When running on Cloudflare Workers, your C# code accesses native platform surfaces through `ctx`:

```csharp
// Cloudflare D1 Relational SQLite
var posts = await ctx.Db.ListAsync<Post>(PostsTable);

// Cloudflare R2 Object Storage
var url = await ctx.Storage.PutAsync("avatars/user_1.png", bytes, "image/png");

// Cloudflare Workers AI (GPU LLMs)
var summary = await ctx.AI.Run("@cf/meta/llama-3-8b-instruct", prompt);

// Cloudflare KV Namespace
await ctx.Cache.PutAsync("cache:key", "value", TimeSpan.FromMinutes(5));

// Cloudflare Queues
await ctx.Queues.SendAsync("notifications", new { userId = "u1" });
```

---

## 2. Docker & Linux VPS (Native AOT)

For teams running on dedicated VPS instances (Hetzner, DigitalOcean) or container orchestrators (ECS, Google Cloud Run, Kubernetes), Reptar compiles into a self-contained, Native AOT Linux binary.

### Key Metrics

- **Binary Size:** 17.1 MB standalone executable (no .NET runtime or SDK required on the host).
- **Image Size:** 18 MB total distroless Debian 12 container footprint.
- **Boot Time:** Under 5 milliseconds from cold process start.
- **Database Support:** Native connection pooling via Npgsql for PostgreSQL, plus local SQLite.

### Distroless Containerfile

Reptar includes a production-ready multi-stage Dockerfile under `deploy/Dockerfile`:

```dockerfile
# Stage 1: Build Native AOT Linux executable
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev
COPY framework/ framework/
COPY examples/blog/ examples/blog/
WORKDIR /src/examples/blog
RUN dotnet publish app.cs -r linux-x64 -c Release --self-contained -o /app/publish

# Stage 2: Distroless static runtime (<25MB)
FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /app
COPY --from=build /app/publish/app /app/reptar
ENV PORT=8787
EXPOSE 8787
USER nonroot:nonroot
ENTRYPOINT ["/app/reptar"]
```

### Turnkey Deployment

Deploying with PostgreSQL takes a single command via `deploy/vps-deploy.sh`:

```bash
./deploy/vps-deploy.sh
```

---

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

---

## 4. Vercel & Frontend Monorepos (Evaluation & Client Generation)

While Vercel Edge Wasm runtime support is currently in evaluation, Reptar provides first-class client contract generation for frontend monorepos:

- **Type Synchronization:** `bun rpc:gen` compiles the C# route tree and produces `@reptar/client` TypeScript types and OpenAPI schemas.
- **Standard Schema:** Generates zero-dependency `~standard` schemas consumed directly by TanStack Form and React Hook Form.
- **No Contract Drift:** Whenever backend routes or models change in C#, frontend contracts update instantly without manual OpenAPI maintenance.
