---
title: Project Layout
description: Anatomy of a Reptar repository and directory conventions.
---

## Directory Structure

A standard Reptar project separates authorable C# backend code from generated contracts and host infrastructure.

```text
apps/api/
  app.cs              # Application composition: configuration, middleware, feature mounts
  schema/             # Database tables — the single source of truth
    users.cs
    posts.cs
    index.cs          # Optional aggregate (auto-discovered if omitted)
  db/client.cs        # D1() / Sqlite() connection definitions
  features/           # One file per slice: records + rules + routes
    auth.cs
    posts.cs
    ai.cs
  migrations/         # Generated SQL DDL files (reptar db generate)

apps/worker/          # Generated TypeScript host shim — do not edit manually
packages/rpc/         # Generated typed client, types, and Standard Schema validators
packages/contract/    # reptar.spec.json and openapi.json
```

---

## Directory Responsibilities

### `apps/api/`

This is where all user-written backend code lives:

- **`app.cs`**: Registers middleware, platform bindings, and mounts feature route groups.
- **`schema/`**: Defines tables using `DefineTable`. Each file is a partial `Schema` class.
- **`features/`**: Structured by domain slice (e.g., `posts.cs`, `billing.cs`, `auth.cs`). Each file declares its input records, validation rules, handlers, and HTTP status contracts.

### `apps/worker/`

The generated TypeScript host shim that executes directly on Cloudflare Workers:

- Exposes WebCrypto hashing, cookies, and telemetry.
- Binds Cloudflare D1, R2, Workers AI, and RateLimiters.
- Bridges requests into `reptar.wasm`.

### `packages/rpc/`

The generated contract package consumed by frontend applications:

- **`app-type.d.ts`**: The complete route tree type definition (`AppType`).
- **`types.ts`**: TypeScript DTO interfaces corresponding to C# records.
- **`schemas.generated.ts`**: Zero-dependency Standard Schema (`~standard`) validators.
- **`client.ts`**: The lightweight `createClient<AppType>` runtime (~100 lines).

### `packages/contract/`

- **`reptar.spec.json`**: Language-neutral contract used for future mobile or multi-language code generators (Swift, Kotlin).
- **`openapi.json`**: Standard OpenAPI 3.1 specification for external tools, API gateways, and documentation viewers.
