# Reptar

> **Reptar is the backend. Full stop.**

A high-performance C# web framework compiled to **WebAssembly for Cloudflare Workers** and **Native AOT for Linux containers, VPS, and AWS Lambda** — with Hono-like routing, Drizzle-like schemas, Zod-like validation, built-in auth, zero-dependency Stripe checkout, first-class edge bindings, and auto-generated TypeScript contracts for any frontend.

- **Live Landing Page:** [https://reptar-web.rocktown-labs.workers.dev](https://reptar-web.rocktown-labs.workers.dev)
- **Live Documentation:** [https://reptar-docs.rocktown-labs.workers.dev](https://reptar-docs.rocktown-labs.workers.dev)
- **Production Edge API:** [https://reptar-api.rocktown-labs.workers.dev](https://reptar-api.rocktown-labs.workers.dev)

Read the canonical [design.md](./design.md) for architectural specifications, non-goals, and technical constraints.

---

## The One Rule

- **Zero frontend opinions:** No UI kit, no frontend router, no styling in the framework.
- **Generated contracts only:** The only frontend-facing surface is generated contracts: typed RPC clients, TypeScript types, Standard Schema validators, and OpenAPI specifications.
- **Unified C# app code:** The exact same C# schema, routing, and feature files compile to both WebAssembly (`netwasm0.1`) and Native AOT (`net10.0`).
- **Zero runtime reflection:** Every route, schema, JSON serializer, and database row mapper is statically analyzed and trim-safe.

---

## Deployment Targets

Reptar runs across four tier-one deployment targets from a single C# codebase:

| Target | Engine / Host | Database Support | Binary / Image Size | Memory (Cold) | Throughput (Benchmarked) |
| :-- | :-- | :-- | :-- | :-- | :-- |
| **Cloudflare Workers** | NetWasm / V8 isolate (`@reptar/host`) | Cloudflare D1, Hyperdrive | ~12 MB Wasm module | < 128 MB isolate | Sub-2ms isolate execution |
| **Native AOT Containers** | Kestrel on Google Distroless | PostgreSQL (Npgsql), SQLite | **10.8 MB** binary (38 MB img) | **~31 MB** RSS | **7,217 req/s** (in-memory) |
| **Linux VPS / Bare Metal** | Systemd / Docker Compose | PostgreSQL 17, SQLite | **10.8 MB** standalone binary | **~31 MB** RSS | **2,799 req/s** (database I/O) |
| **AWS Lambda** | Custom Runtime (`provided.al2023`) | PostgreSQL, SQLite | Self-contained AOT zip | ~31 MB RSS | Sub-15ms cold boot |

---

## Repository Layout

```text
reptar/
├── apps/
│   ├── web/             # Marketing site (Astro + Tailwind 4)
│   └── docs/            # Documentation site (Astro Starlight)
├── framework/
│   ├── Reptar/          # C# core: router, Ctx, validation, data, auth, codegen
│   ├── Reptar.Wasm/     # netwasm0.1 target: D1, R2, KV, Queues, AI, DO bridges
│   ├── Reptar.Kestrel/  # Native ASP.NET Core Kestrel host for Native AOT
│   ├── Reptar.Lambda/   # AWS Lambda custom runtime host (provided.al2023)
│   └── Reptar.Tests/    # Framework unit and integration tests (37 tests)
├── examples/
│   └── blog/            # File-based C# reference app (no .csproj)
├── packages/
│   ├── host/            # @reptar/host Worker shim (FFI bridge to Cloudflare env)
│   ├── client/          # @reptar/client type-safe fetch RPC runtime
│   ├── rpc/             # Generated contracts (app-type.d.ts, types.ts, openapi.json)
│   ├── adapter-lambda/  # @reptar/adapter-lambda handler wrapper for Node.js Lambda
│   ├── infra/           # Alchemy infrastructure (Cloudflare D1, R2, KV, Queues, AI)
│   └── config/          # Shared TypeScript configuration
├── deploy/
│   ├── Dockerfile       # Distroless Debian 12 multi-stage Native AOT container
│   ├── docker-compose.yml # Production compose stack with PostgreSQL 17
│   └── vps-deploy.sh    # Single-command VPS deployment script
├── scripts/
│   ├── reptar.ts        # CLI tool: dev, codegen, test, build, build:worker
│   ├── benchmark.ts     # Automated load test & latency suite (Autocannon)
│   └── e2e.ts           # End-to-end integration test suite
├── design.md            # Canonical design specification and roadmap
└── frontend.md          # Website and documentation brief
```

---

## Quickstart & CLI Commands

The backend is a file-based C# application (`examples/blog/app.cs`) with **no app `.csproj`**. The Bun CLI manages the build toolchain automatically:

```bash
bun api:dev          # Run portable blog API locally (Kestrel on http://localhost:8787)
bun rpc:gen          # Generate contracts from C# route tree and sync packages/rpc
bun rpc:check        # Typecheck the generated client contracts
bun api:test         # Run all 37 framework unit tests
bun api:bench        # Run high-concurrency benchmarks (--target, -c, -d)
bun e2e              # Run end-to-end integration tests
bun run check        # Run Ultracite linter and formatter check
bun run fix          # Auto-fix code style issues
```

---

## Key Features & Built-in Modules

### 1. Database Clients (`ctx.Db`)

- **Cloudflare D1**: In-isolate zero-overhead execution via `env.DB.prepare(sql).all()`.
- **PostgreSQL & Hyperdrive**: Native pooled connections via `Npgsql 10.0.3` with automatic `$1, $2` parameter translation via `SqlParamParser`, and full transactional rollback support (`TransactionAsync`).
- **SQLite**: Embedded fast storage via `Microsoft.Data.Sqlite` for local dev and edge containers.

### 2. Built-in Authentication (`ctx.Auth`)

- Zero external auth services required: built-in user registration, login, logout, password hashing (PBKDF2 WebCrypto-compatible), and session tokens.
- Trim-safe schemas (`Auth.Users`, `Auth.Sessions`) with HttpOnly cookie handling.

### 3. Zero-Dependency Stripe Integration (`Stripe.cs`)

- **No `Stripe.net` dependency:** `Stripe.net` is a multi-megabyte package with hundreds of reflection-heavy models that break under Native AOT trimming and blow up Wasm bundle sizes.
- Reptar includes its own **zero-dependency, reflection-free Stripe module**:
  - Direct URL-encoded form posting to `api.stripe.com/v1/checkout/sessions`.
  - Constant-time HMAC-SHA256 signature verification for Stripe webhooks (`Stripe.VerifyWebhookSignatureAsync`).
  - Runs identically under both Wasm and Native AOT.

### 4. Cloudflare Platform Bindings

Inside the Cloudflare Worker isolate, C# interacts with Cloudflare platform primitives with zero network hops:

- **Storage (`ctx.Storage`)**: R2 object store (`PutAsync`, `GetAsync`, `DeleteAsync`).
- **Cache (`ctx.Cache`)**: KV key-value store with TTL support.
- **Queues (`ctx.Queue`)**: Message queue producer.
- **Workers AI (`ctx.AI`)**: Edge LLM inferences (`@cf/meta/llama-3.2-1b-instruct`).
- **Durable Objects (`ctx.Durable`)**: Stateful actor invocations.

---

## RealWorld API & Benchmark

The repository includes a spec-compliant RealWorld (Conduit) backend at `examples/realworld`. The official Hurl suite passes 13/13 files (154 requests) on both the portable SQLite app and the Native AOT VPS container.

```bash
bun run realworld:dev          # SQLite/Kestrel on :8787
bun run realworld:worker:build # C# Wasm RealWorld Worker artifacts
bun run realworld:hurl         # requires REALWORLD_SPEC_DIR
bun run realworld:bench        # requires a seed file
```

VPS Native AOT results with 300 seeded articles and autocannon:

| Scenario                      | Req/sec |    p50 |
| ----------------------------- | ------: | -----: |
| `GET /api/tags`               |   4,220 |  22 ms |
| `GET /api/articles/:slug`     |   2,627 |  36 ms |
| `GET /api/articles?limit=20`  |     901 | 110 ms |
| `GET /api/articles/feed`      |     755 | 129 ms |
| `GET /api/articles?tag=bench` |     592 | 164 ms |
| `POST /api/articles`          |     210 |  42 ms |

List responses now omit the article body and fetch tags, favorite counts, favorite state, and follow state in page-level batches instead of per-row correlated subqueries. The SQLite WAL read pool added in `461be2e` lets those reads run concurrently, and article creation batches the article, tag, and junction writes into one transaction. The image remains **38.3 MB uncompressed / 11.6 MB compressed** after removing the 22 MB `.dbg` symbols.

The Native AOT run surfaced and fixed AOT-unsafe anonymous parameter binding and reflection-only row mapping. `QueryMappedAsync` is now the AOT-safe row-mapping path, and `SqliteDbClient` binds deletes/gets with dictionaries.

## Production Deployment

### 1. Cloudflare Workers (Edge Serverless)

Deploy the full stack (API Worker, Static Docs, Astro Web, D1, R2, KV, Queues) with Alchemy:

```bash
# Deploy to Cloudflare Workers
bun run deploy

# Deploy non-interactively
bun run deploy -- -y
```

### 2. Docker / Distroless Container (VPS or Kubernetes)

Build the minimal Google Distroless container (~38 MB uncompressed, ~11.6 MB compressed):

```bash
docker build -t reptar-app -f deploy/Dockerfile .
docker run -p 8787:8787 -e DATABASE_URL="postgres://user:pass@host:5432/db" reptar-app
```

Or deploy to any Ubuntu/Debian VPS with one command:

```bash
bash deploy/vps-deploy.sh
```

### 3. AWS Lambda

Deploy the compiled Native AOT binary to AWS Lambda using the custom runtime (`provided.al2023`) or via `@reptar/adapter-lambda`.

---

## Verified Benchmarks

Measured under 100 concurrent persistent connections (`autocannon -c 100 -d 5`):

```text
========================================================================================
  BENCHMARK RESULTS (Native AOT Standalone Binary vs. Edge Wasm)
========================================================================================
| Target / Route                     | Req/sec | Latency p50 | Latency p95 | Errors |
| :--------------------------------- | :------ | :---------- | :---------- | :----- |
| Native AOT - GET /health (Mem)     |   7,217 |        9 ms |       58 ms |      0 |
| Native AOT - GET /posts (SQLite)   |   2,799 |       33 ms |       76 ms |      0 |
| Cloudflare Worker - GET /health    |     129*|       68 ms |      644 ms |      0 |
| Cloudflare Worker - GET /posts (D1)|      98*|      116 ms |      624 ms |      0 |
========================================================================================
* Worker req/s measured over public residential internet; internal isolate execution is < 1.5ms.
```

---

## License

MIT &copy; Rocktown Labs.
