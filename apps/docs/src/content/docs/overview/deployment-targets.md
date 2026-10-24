---
title: Deployment Targets
description: Deploy Reptar across Cloudflare Workers, AWS Lambda Native AOT, Docker containers, and VPS instances.
---

## Overview

Reptar is engineered from day one as a **dual-target framework**.

Your application logic—routes, validators, database schemas, and business models—is written once in pure C#. The framework then compiles the same source code into two distinct runtime artifacts:

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
```

---

## 1. Cloudflare Workers (Primary Edge Target)

Reptar compiles to WebAssembly (`netwasm0.1`) and runs inside Cloudflare Workers isolates.

### Runtime Characteristics

- **Compilation:** NetWasm compiler emits optimized `.wasm` bytecode with compile-time source generation.
- **Isolate Execution:** Sub-2ms per request (`server-timing: reptar;dur=0.0`).
- **Memory Footprint:** Operates comfortably within the 128 MB Worker isolate limit.
- **Reflection:** Zero runtime reflection. All JSON serialization, route dispatching, and D1 row mapping are generated at build time.

### Platform Bindings

When running on Cloudflare Workers, your C# code accesses native platform surfaces directly through `ctx`:

```csharp
// Cloudflare D1 Relational SQLite
var posts = await ctx.Db.ListAsync<Post>(PostsTable);

// Cloudflare R2 Object Storage
var url = await ctx.Storage.PutAsync("avatars/user_1.png", bytes, "image/png");

// Cloudflare Workers AI (GPU LLMs)
var summary = await ctx.AI.Run("@cf/meta/llama-3-8b-instruct", prompt);
```

---

## 2. AWS Lambda (Native AOT)

When deploying to AWS, Reptar compiles to a single standalone Linux ELF binary via **.NET Native AOT**.

### Runtime Characteristics

- **Architecture:** `provided.al2023` custom serverless runtime (ARM64 or x86_64).
- **Binary Size:** ~12 MB self-contained binary (no .NET SDK or runtime installation required in the Lambda environment).
- **Cold Starts:** Under 15 milliseconds.
- **Memory Usage:** ~20 MB base RAM (eliminates the 100MB–300MB container overhead of standard ASP.NET Core).

### Deployment Configuration

The compiled binary listens directly on the AWS Lambda runtime interface:

```bash
# Publish self-contained Linux ARM64 binary
dotnet publish -c Release -r linux-arm64 --self-contained
```

---

## 3. Docker & VPS (Self-Hosted Linux)

For teams running on dedicated VPS instances (Hetzner, DigitalOcean) or container orchestrators (ECS, Google Cloud Run, Kubernetes), Reptar compiles into a minimal scratch container.

### Minimal Containerfile

```dockerfile
# Multi-stage build producing an 18 MB distroless container
FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -r linux-x64 -o /app

FROM gcr.io/distroless/static-debian12
COPY --from=build /app/app /app
EXPOSE 8080
ENTRYPOINT ["/app"]
```

### Performance on a $4/mo VPS

- **Image Size:** 18 MB total image footprint.
- **Boot Time:** 5 milliseconds from cold process start.
- **Throughput:** Capable of exceeding 10,000 requests per second on a single VPS CPU core.

---

## 4. Vercel (Edge & Serverless)

- **Vercel Edge Functions:** Runs the compiled `.wasm` module directly on Vercel's V8-based Edge Runtime with minimal compute overhead.
- **Vercel Serverless:** Deploys the Native AOT Linux binary using custom serverless function runtimes.

---

## Target Matrix Summary

| Platform | Target Architecture | Cold Start | Memory Base | Best For |
| :-- | :-- | :-- | :-- | :-- |
| **Cloudflare Workers** | `netwasm0.1` (Wasm) | Sub-50ms | ~25 MB | Global APIs, edge microservices, low latency |
| **AWS Lambda** | `net10.0` (Native AOT) | Sub-15ms | ~20 MB | Event-driven workloads, AWS VPC integration |
| **Linux VPS / Docker** | `net10.0` (Native AOT) | ~5ms | ~15 MB | High-volume APIs, predictable fixed compute costs |
| **Vercel Edge** | `netwasm0.1` (Wasm) | Sub-50ms | ~25 MB | Full-stack Next.js or edge middleware |
