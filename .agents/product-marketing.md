# Product Marketing Context

**Document version:** v1.0.0  
**Last updated:** 2026-10-24

---

## 1. Product Overview

- **One-line description:** Reptar is a high-performance C# web framework compiled to WebAssembly for Cloudflare Workers and Native AOT for AWS Lambda and Docker, with auto-generated TypeScript contracts for modern frontends.
- **What it does:** Reptar lets backend developers write clean, expressive C# with Hono-like routing, Drizzle-like schemas, and zero-reflection JSON serialization. It executes inside Cloudflare Workers isolates in under 2 milliseconds and generates end-to-end typed TypeScript clients and Standard Schema validators for TanStack Start, Next.js, and Expo.
- **Product category:** Edge Web Framework / Serverless Backend Framework
- **Product shelf:** Sits alongside Hono, Elysia, ASP.NET Core Minimal APIs, and tRPC.
- **Business model:** Open source framework core (Apache 2.0 / MIT) with enterprise support and turnkey edge deployment tooling.

---

## 2. Target Audience

- **Target company type:** Startups, high-growth SaaS, digital agencies, and engineering teams scaling web and mobile applications across global edge networks.
- **Target decision-makers:** Lead Backend Engineers, Full-Stack Architects, CTOs, and C# developers seeking edge performance without Node.js runtime churn.
- **Primary use case:** Building low-latency backend APIs, authenticated CRUD services, and compute microservices running on Cloudflare Workers or serverless edge without managing Kubernetes or virtual machines.
- **Jobs to be done:**
  1. Write backends in a rock-solid, statically typed language (C#) without paying the "heavy container" tax (memory bloat, slow cold starts, high server bills).
  2. Eliminate contract drift between backend APIs and TypeScript frontends through autonomous schema and client generation.
  3. Deploy globally distributed edge backends with native bindings to relational storage (D1, PostgreSQL/Hyperdrive), object storage (R2), and GPU AI (Workers AI).

---

## 3. Problems & Pain Points

- **The Heavy .NET Tax:** Traditional .NET backends require Docker containers, Kestrel, and full runtime assemblies. This incurs 100MB–300MB baseline memory footprints, 500ms–2s container cold starts, and $40–$100/mo minimum cloud hosting costs just to idle.
- **Edge TypeScript Fatigue:** Building edge backends in TypeScript means dealing with runtime type stripping, brittle npm dependency trees, lack of true immutable records, and memory overhead under heavy CPU or cryptography workloads.
- **Contract Drift:** Hand-maintaining OpenAPI schemas, TypeScript DTOs, and client fetch functions causes silent production bugs whenever a backend route or field changes.
- **Vendor Lock-in:** Edge TypeScript code written for Cloudflare Workers cannot easily run on AWS Lambda or self-hosted VPS instances without rewriting host adapters.

---

## 4. Competitive Landscape

- **ASP.NET Core / Kestrel (Traditional .NET):** Powerful and familiar, but too heavy for edge serverless isolates. Requires containers and dedicated server infrastructure.
- **Hono (TypeScript on Cloudflare Workers):** Fast and lightweight, but bound to JavaScript/TypeScript. Lacks native multi-threading, strict compile-time types, and true AOT compilation.
- **Next.js Route Handlers:** Convenient for frontends, but slow cold starts, vendor lock-in to Vercel/Node runtimes, and high execution costs at scale.
- **Go / Rust on Wasm:** Fast, but high syntactic complexity and steep learning curves compared to C# records and LINQ.

---

## 5. Differentiation

- **Zero-Reflection NetWasm Compilation:** Hot paths (JSON serialization, routing, table mapping) use compile-time source generation. No runtime reflection. Executes in <2ms per isolate with zero garbage collection spikes.
- **Dual-Target Architecture:** The exact same C# feature slices compile to `netwasm0.1` for Cloudflare Workers and `net10.0` Native AOT for AWS Lambda (<15ms cold start) or 18MB scratch Docker containers.
- **Living TypeScript Contracts:** Build tools generate `@reptar/client` typed SDKs and zero-dependency `~standard` schemas directly from C# route trees.
- **Native Platform Bindings:** First-class C# APIs for Cloudflare D1 (relational SQLite), R2 (S3-compatible object storage), KV, Queues, Workers AI, and PostgreSQL through Cloudflare Hyperdrive.

---

## 6. Objections & Anti-Personas

### Top Objections & Answers

1. _"Isn't C# on WebAssembly huge and slow to start?"_  
   **Answer:** Historically, Mono/Wasm runtimes were 30MB+. Reptar uses NetWasm and Native AOT with aggressive trimming and zero reflection. Cold start TTFB on Cloudflare Workers is sub-50ms, and warm isolate execution runs in <2ms.
2. _"Are we locked into Cloudflare Workers?"_  
   **Answer:** No. Reptar feature code has zero Cloudflare SDK dependencies. The same C# compiles directly to an 18MB Native AOT Linux binary for AWS Lambda or any Linux VPS.
3. _"Can we use PostgreSQL instead of SQLite/D1?"_  
   **Answer:** Yes. Reptar's `IDbClient` supports PostgreSQL directly via Cloudflare Hyperdrive connection pooling on edge, as well as native Npgsql on AWS Lambda/VPS.

### Anti-Persona

- Legacy enterprise teams requiring full Entity Framework Core, heavy Windows IIS hosting, or runtime reflection libraries.

---

## 7. Switching Dynamics

- **Push (Frustrations driving them away):** Expensive Kubernetes clusters for small microservices, unpredictable JavaScript runtime type bugs, container cold starts.
- **Pull (Attractions to Reptar):** Instant global deployment, C# type safety and records, sub-2ms edge execution, automatic typed client generation.
- **Habit (What keeps them stuck):** Comfort with standard `dotnet run` commands and existing ASP.NET controllers.
- **Anxiety (Concerns about switching):** Wasm maturity and edge debugging ergonomics.

---

## 8. Brand Voice

- **Tone Attributes:** Direct, high-signal, confident, technically precise, pragmatic.
- **Vocabulary:**
  - _Use:_ Compiled WebAssembly, Native AOT, zero-reflection, generated contracts, edge isolate, sub-2ms execution, dual-target.
  - _Avoid:_ "Revolutionary," "game-changer," "next-gen," "best-in-class," "seamless," "unlock."
- **Writing Standard:** Plain declarations, specific numbers, and falsifiable claims over marketing fluff.

---

## 9. Proof Points

- **Sub-2ms Isolate Execution:** Measured directly in production via `server-timing: reptar;dur=0.0`.
- **Zero Errors Under Load:** Sustains concurrent edge connections with 0 dropped requests and 0 memory leaks.
- **18 MB Distroless Docker Image:** Boots in 5ms and serves 10,000+ req/s on a single CPU core when compiled to Native AOT.
- **100,000-Iteration PBKDF2 WebCrypto Auth:** Native cryptographic edge security verified in production Cloudflare isolates.

---

## 10. Goals & Key Actions

- **Primary Goal:** Drive adoption of Reptar as the standard edge backend framework for C# and full-stack developers.
- **Primary Conversion Action:** Run `bun create reptar@latest` (or `npm create reptar@latest`) and deploy to Cloudflare edge.

---

## 11. Changelog

- **v1.0.0 (2026-10-24):** Initial product marketing context established covering dual-targeting (NetWasm + Native AOT), edge performance benchmarks, database roadmap (D1 + PostgreSQL/Hyperdrive), and competitive positioning.
