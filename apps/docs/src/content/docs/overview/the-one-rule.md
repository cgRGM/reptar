---
title: The One Rule & Non-Goals
description: Core philosophy, non-goals, and constraints of the Reptar framework.
---

## The One Rule

**Reptar is the backend. Full stop.**

- **Zero frontend opinions:** No UI kit, no frontend router, no styling system, and no client tooling inside the framework.
- **Generated contracts only:** The only frontend-facing surface is generated contracts: a typed `fetch` client, TypeScript types, Standard Schema validators, and OpenAPI specifications.
- **Backend infrastructure:** The TypeScript host shim is backend infrastructure — generated, versioned, and deployed alongside the C#. Like Kestrel in ASP.NET Core, developers do not write it by hand.

---

## Non-Goals

Reptar deliberately departs from enterprise .NET conventions in favor of a minimal, Wasm-shaped runtime:

1. **No Enterprise .NET Ceremony:**
   - No `IServiceCollection` or heavy dependency injection containers.
   - No MVC controllers or action filters.
   - No Entity Framework Core or repository abstraction layers.
   - No `.sln` or `.csproj` configuration bloat.

2. **No Hidden-SQL ORM Magic:**
   - The query builder is Dapper-like: explicit SQL, fully parameterized, with source-generated mappers.
   - SQL is transparent and predictable.

3. **Not Desktop .NET on Workers:**
   - Reptar is not a desktop .NET runtime squeezed onto serverless isolates. It is engineered from day one for minimal Wasm size, instant startup, and AOT compilation.

4. **Compile-Time Source Generation (Zero Hot-Path Reflection):**
   - Every hot path uses compile-time source generation: `[JsonSerializable]` contexts for JSON payloads, `[ReptarEntity]` for `IDataRecordMapper<T>` row readers and `IReptarEntity.ToDbParameters()` write binders, and compiled `Rule<T>` expression trees.
   - Untyped anonymous objects passed to `InsertAsync`/`UpdateAsync`/`QueryAsync` remain supported on `net10.0` via fallback property inspection, while `[ReptarEntity]` records and value-tuple parameters (`("id", id)`) execute with zero runtime reflection across both `netwasm0.1` and `net10.0` Native AOT.

---

## Constraints

- **Single Reactor Loop:** Because WebAssembly isolates on Cloudflare Workers are single-threaded without raw OS sockets or threads, the TS host shim owns all underlying network I/O.
- **CPU Limits:** Computationally heavy cryptography (such as scrypt password hashing) is delegated to the host environment’s native WebCrypto APIs.
- **Isolate Module Caching:** The compiled Wasm instance is instantiated once per isolate and cached across requests, eliminating per-request cold start penalties.
