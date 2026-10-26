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

### Per-Target Feature Support Matrix

Due to NetWasm `0.10.0` Binaryen's single-`[JSExport]` constraint (`Program.Handle`), core request/response features run inside C# on all targets while streaming/event-loop hooks on Cloudflare Workers are bridged by `@reptar/host`:

| Feature | Cloudflare Workers (`netwasm0.1`) | Kestrel (`net10.0` AOT) | AWS Lambda (`net10.0` AOT) |
| :-- | :-- | :-- | :-- |
| **Routing, `Rule<T>` Validation, Auth, JWT** | C# (`Wasm`) | C# (`Native AOT`) | C# (`Native AOT`) |
| **Database (`c.Db`)** | C# `D1DbClient` (`[JSImport]`) / Postgres | C# `SqliteDbClient` / `PostgresDbClient` / `MySqlDbClient` | C# `SqliteDbClient` / `PostgresDbClient` / `MySqlDbClient` |
| **Storage (`c.Storage`) & `Files.Check`** | C# `R2Client` (`[JSImport]`) | C# `LocalStorage` / `S3Storage` (SigV4) | C# `S3Storage` (SigV4) / `LocalStorage` |
| **SSE (`Sse.cs`), WebSockets, Static Files** | TS `@reptar/host` Worker layer (`#if !NETWASM` in C#) | Native C# (`KestrelHost` / `ProductionExtensions`) | Buffered HTTP / API Gateway |
| **Queue & Cron Consumers (`OnQueue`, `OnScheduled`)** | TS `@reptar/host` `queue`/`scheduled` bridge | Native C# poller + cron scheduler (`DbQueueClient`, SQS, Azure Storage Queues, Pub/Sub adapters) | SQS event adapter (`ReportBatchItemFailures`) |

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
│   └── Reptar.Tests/    # Framework unit and integration tests (388 tests)
├── examples/
│   └── blog/            # File-based C# reference app (no .csproj)
├── packages/
│   ├── host/            # @reptar/host Worker shim (FFI bridge to Cloudflare env)
│   ├── rpc/             # @reptar/client runtime + generated contracts
│   │                    #   (AppType, types.ts, schemas.ts, openapi.json)
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
bun reptar make:model Post -f   # Generate TableDef + DTO + CRUD feature slice
bun reptar check:architecture   # Enforce the core/AI/terminal module boundary
bun bench:gate --results <json> --baseline bench/perf-baseline.json  # Performance regression gate
bun rpc:gen          # Generate contracts from C# route tree and sync packages/rpc
bun rpc:check        # Typecheck the generated client contracts
bun api:test         # Run all 388 framework unit tests
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

### 4. Optional Agent Engine (`Reptar.AI`)

The framework ships a provider-agnostic AI agent engine as an optional project (`framework/Reptar.AI/`), not a dependency of the web core:

- **Streaming LLM clients** for OpenAI-compatible endpoints (incl. OpenRouter and Vercel AI Gateway), Anthropic, Gemini, and local Ollama — plus an offline **Echo** provider so `bun reptar agent` works with zero API keys.
- **Interactive provider connection** (Pi `/login` + OpenCode `/connect` parity): `bun reptar agent --connect [provider]` or in-session `/connect` — provider picker with live status, hidden API-key entry, live connection test, model catalog picker, saved to `~/.reptar/agent/auth.json` (0600). `/providers` shows every provider's credential source; custom OpenAI-compatible endpoints register through `Other`; env keys always win over saved credentials.
- **`StreamingAgentHarness`**: a ReAct loop that dispatches tool calls **in-process** via `app.ExecuteAiToolAsync` (full `Rule<T>` validation, zero HTTP overhead) with live events for terminal and SSE frontends.
- **`app.UseWorkspaceTools(root)`**: one line mounts `read_file`, `write_file`, `edit_file`, `list_dir`, and `run_command` as normal `.AsAiTool(...)` routes with path-traversal protection.
- **`TerminalAgent`**: the interactive loop behind `bun reptar agent` — ANSI markdown streaming, slash commands, Ctrl+C interrupt, SQLite session persistence across launches (`--session <id>`).
- **AOT-clean session store**: `AgentSessionStore` with manually registered zero-reflection row mappers.

```bash
bun reptar agent                     # interactive agent in this workspace
bun reptar agent --web 4040          # web companion: POST /api/chat SSE + React SPA
bun reptar agent --self-test         # offline verification of the tool bridge
```

Showcase app: [`examples/agent-cli`](./examples/agent-cli) — file-based `app.cs` (no `.csproj`), single Native AOT binary (~12 MB, ~10–20 ms cold start). Build report and findings: [`plans/agent-engine.md`](./plans/agent-engine.md).

### 5. Cloudflare Platform Bindings

Inside the Cloudflare Worker isolate, C# interacts with Cloudflare platform primitives with zero network hops:

- **Storage (`ctx.Storage`)**: R2 / S3 / local object store with metadata (`HeadAsync`), byte ranges (`GetRangeAsync`), cursor listing (`ListAsync`), presigned uploads and downloads, server-side copy/move, batch delete, end-to-end streaming (`StreamBody` + `PutAsync(Stream)`), S3/R2 multipart with configurable part sizes, public CDN URLs, typed `StorageException` errors, and `ctx.File` / `ctx.Attachment` for `ETag`/`304`, `Range`/`206`, and `HEAD` responses.
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

> **Cache caveat:** the list, tag-filter, and single-article figures include
> Kestrel's anonymous SWR cache. On the same 2-core benchmark VPS,
> cache-busted `/api/tags` measured ~4.2k req/s and cache-busted
> `/api/articles?limit=20` ~236 req/s (p50 ~211 ms). Treat cached and
> uncached numbers separately; authenticated/write paths bypass the cache.

List responses now omit the article body and fetch tags, favorite counts, favorite state, and follow state in page-level batches instead of per-row correlated subqueries. The SQLite WAL read pool added in `461be2e` lets those reads run concurrently, and article creation batches the article, tag, and junction writes into one transaction. The image remains **38.3 MB uncompressed / 11.6 MB compressed** after removing the 22 MB `.dbg` symbols.

A separate Cloudflare Workers + D1 deployment was also verified with the same Hurl suite. Measured from the VPS with 50 read clients and 100 seeded articles, the edge run was `tags` 518 req/sec, `articles` 122, single article 242, feed 72, and writes 13. Those numbers use a smaller dataset and a different connection count, so they are not directly comparable to the VPS table. Cloudflare’s logs show 1–2 ms client RTT from the VPS to the Columbus (CMH) colo; the edge gap is Worker CPU and D1 time, not WAN latency.

The Native AOT run surfaced and fixed AOT-unsafe anonymous parameter binding and reflection-only row mapping. `QueryMappedAsync` is now the AOT-safe row-mapping path, and `SqliteDbClient` binds deletes/gets with dictionaries.

## Calibrated Benchmark (Reptar vs .NET Minimal API vs Hono)

`bench/` contains a calibrated three-stack benchmark: the same multi-tenant issue-tracker API implemented in Reptar Native AOT, .NET 10 Minimal API Native AOT, and Hono on Bun. It runs a complexity ladder from plaintext and JSON through routing, validation, database reads/writes, JWT auth, and PBKDF2 login, so the report separates framework overhead from database I/O.

```bash
bun bench:seed                       # deterministic 50k-task SQLite database
bun bench:load --base-url ...        # per-endpoint oha suite
bun bench:mixed --base-url ...       # weighted realistic mix
bun bench:report bench/results/<dir> # Markdown comparison report
```

See [bench/README.md](./bench/README.md) for the schema, tiers, fairness rules, VPS build/run scripts, and known AOT caveats.

## ReptarBase (Single-Container BaaS) & Studio

`examples/baas` demonstrates a single-file, production-ready Backend-as-a-Service (BaaS) that compiles to a 10.4 MB Native AOT binary running in ~15 MB RAM as a high-concurrency, zero-dependency alternative to PocketBase, Supabase, and OpenShip.

### Key Capabilities

- **Better Auth & Personal API Keys (`/api/auth/*`)**: WebCrypto-compatible PBKDF2 password hashing, session tokens, and instant Bearer API key validation.
- **Typed Collections with `ColumnRef<T>` (`/api/documents/*`)**: Zero-reflection filtering, sorting, pagination, and atomic view count increments.
- **Magic-Byte Object Storage (`/api/files/*`)**: Stack-allocated file sniffing (PNG, JPEG, GIF, WebP, PDF), SHA-256 ETag generation, and conditional `304 Not Modified` fast-pathing.
- **Real-Time Change Feeds (`/api/realtime`)**: Native Server-Sent Events (SSE) broadcasting database mutations with 15-second heartbeat keepalives.
- **Embedded Studio Web Console (`GET /_` or `GET /dashboard`)**: An OpenShip-style dark-mode management console for managing collections, uploading assets via drag-and-drop, generating API keys, and monitoring real-time mutation logs.

### Running ReptarBase & Benchmarks

```bash
bun baas:dev    # Launch ReptarBase and Studio on http://localhost:8787
bun baas:test   # Run automated 12-step verification suite (~2s)
bun baas:bench  # Run high-concurrency oha load benchmark suite
```

### High-Concurrency Benchmark Results (`oha -c 20 -z 5s` against SQLite WAL):

| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | Success |
| :-- | :-- | :-- | :-- | :-: | :-: | :-: | :-: |
| **T1** | Baseline Engine | `GET` | `/health` | **132,907** | 0.13 ms | 0.26 ms | 100% |
| **T2** | Filtered Query (`ColumnRef<T>`) | `GET` | `/api/documents?status=published` | **34,215** | 0.43 ms | 1.38 ms | 100% |
| **T3** | Atomic Read + View Counter | `GET` | `/api/documents/:id` | **55,689** | 0.30 ms | 0.96 ms | 100% |
| **T4** | Authenticated Writes + SSE | `POST` | `/api/documents` | **31,722** | 0.52 ms | 1.55 ms | 100% |
| **T5** | Conditional ETag Caching | `GET` | `/api/files/:key` | **68,052** | 0.27 ms | 0.46 ms | 100% |

Under concurrent write pressure, SQLite WAL mode with Reptar's group-commit batching sustained **31,722 authenticated writes/sec** (0.52 ms p50) while simultaneously publishing events to active SSE listeners—surpassing typical PocketBase (~2.5k–3.5k writes/sec) and Node.js BaaS solutions (~2k–4k writes/sec) by nearly an order of magnitude.

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
