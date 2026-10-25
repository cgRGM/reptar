# Reptar Bench

A calibrated, three-stack benchmark for Reptar:

1. **Reptar Native AOT** (Kestrel)
2. **.NET 10 Minimal API Native AOT** (Kestrel) — the control
3. **Hono on Bun** (JIT and compiled bundle)

All three implement the **same issue-tracker schema, seed data, SQL, JSON, and routes**. The point is to separate framework overhead from database I/O instead of collapsing everything into one RealWorld-style number.

## Layout

```text
bench/
├── shared/
│   ├── schema.sql          # identical schema for every implementation
│   └── seed.ts             # deterministic 50k tasks / 250k comments
├── reptar/                 # C# file-based-style app (csproj for the source generator)
├── minimal-api/            # .NET 10 Minimal API control
├── hono/                   # Bun + Hono
├── load/
│   ├── scenarios.ts        # T0–T10 route definitions
│   ├── run.ts              # oha per-endpoint runner
│   ├── mixed.ts            # T11 weighted mixed workload
│   └── report.ts           # Markdown report generator
├── build.sh                # AOT + Hono builds (Linux/Docker)
├── run-vps.sh              # orchestration, CPU pinning, result capture
└── results/                # generated
```

## Quickstart (local, JIT)

```bash
# 1. Seed the database (deterministic)
bun bench/shared/seed.ts bench/out/bench.db

# 2. Run one implementation
cd bench/reptar && BENCH_DB=../out/bench.db PORT=9101 dotnet run -c Release
cd bench/minimal-api && BENCH_DB=../out/bench.db PORT=9102 dotnet run -c Release
cd bench/hono && BENCH_DB=../out/bench.db PORT=9103 bun run src/index.ts

# 3. Benchmark a running server
bun bench/load/run.ts \
  --base-url http://127.0.0.1:9101 \
  --label reptar \
  --oha "$(which oha)" \
  --duration 10 --warmup 3 --reps 3 --connections 100

# 4. Report
bun bench/load/report.ts bench/results/<dir>
```

`oha` is required. On macOS: download `oha-macos-arm64` from the [oha releases](https://github.com/hatoo/oha/releases). On Linux: `oha-linux-amd64-pgo`.

## VPS run

```bash
# Build both AOT binaries and the Hono bundle on Linux
bash bench/build.sh

# Full suite (4 implementations x 11 tiers x 3 reps)
bash bench/run-vps.sh

# Faster subset while iterating
IMPLS="reptar minimal-api" ONLY="T0,T1,T4,T5,T8" \
  DURATION=3 WARMUP=1 REPS=1 CONNECTIONS=50 bash bench/run-vps.sh

# SWR-cached reads (new middleware; T4/T5/T6 are GETs without auth)
IMPLS="reptar" ONLY="T4,T5,T6" BENCH_SWR=1 bash bench/run-vps.sh
```

The runner:

- stops the existing RealWorld/Campfire containers (and restarts them on exit)
- seeds a fresh database
- gives each implementation its own copy of the database
- pins the server to CPU 0 and `oha` to CPU 1
- captures `manifest.json`, per-implementation JSON/Markdown, and server logs

## Tiers

| Tier | Route | What it isolates |
| :-- | :-- | :-- |
| T0 | `GET /__bench/plaintext` | HTTP stack + routing |
| T1 | `GET /__bench/json` | + JSON serialization |
| T2 | `GET /__bench/router/:orgId/:projectId/:taskId` | + route params |
| T3 | `POST /__bench/validate` | + body parsing + validation |
| T4 | `GET /api/v1/tasks/:id` | + one DB read + mapping |
| T5 | `GET /api/v1/projects/:projectId/tasks?status=open&limit=20` | + list query + pagination |
| T6 | `GET /api/v1/tasks/:id/comments?limit=20` | + join + nested list |
| T7 | `POST /api/v1/projects/:projectId/tasks` | + auth + DB write |
| T8 | `GET /api/v1/me` | + JWT + user lookup |
| T9 | `POST /api/v1/auth/login` | + PBKDF2 (100k iterations) |
| T10 | `PUT /api/v1/tasks/:id` | + auth + read-modify-write |
| T11 | `bun bench/load/mixed.ts` | weighted realistic mix |

## Latest results

See [`bench/results/FINDINGS.md`](./results/FINDINGS.md) for the full
before/after tables, the SWR cache tier, and the remaining bottlenecks.

Headline (latest framework HEAD, 2 vCPU Lightsail, 100 connections):

- HTTP/JSON/routing/validation: **43.2k / 41.4k / 40.3k / 36.1k req/s** — at or
  above raw .NET Minimal API on plaintext/JSON/validation.
- DB reads/writes: **11.8k** single-task, **5.4k** list, **10.1k** comments,
  **3.5k** creates, **3.7k** updates.
- SWR-cached reads: **39.2k** task detail, **29.9k** list, **35.5k** comments
  (3.4x–5.6x over uncached).
- Mixed workload (70% read / 20% list / 10% write): **5,665 req/s**, +7% vs
  .NET Minimal API (5,281); Hono on Bun is 7,742.
- Startup 356 ms, binary **10.9 MB** with `ReptarSqliteOnly=true` (vs 104 MB
  for the compiled Hono bundle).
- T7 breakdown: write gate **26.3 ms avg** vs insert **0.064 ms p50** — creates
  are still queueing behind the single-writer semaphore.

## Fairness notes

- **Same data.** Every implementation reads a copy of the same 72 MB SQLite database (1,000 users, 100 projects, 50,000 tasks, 250,000 comments).
- **Same SQL.** No ORM in any implementation.
- **Same JSON.** Camel-case field names and the same response shapes.
- **Logging off.** Kestrel and Reptar telemetry are disabled for the main run. A separate run can measure their overhead.
- **CPU pinning.** The VPS is 2 vCPU = 2 SMT threads on one physical core. The server gets CPU 0 and the load generator CPU 1. This is a relative comparison; for absolute capacity, run the load generator on a second box.
- **AOT caveat.** Reptar's default `ctx.Json<T>` codec uses reflection and trims nested DTO properties under Native AOT. The benchmark app supplies a source-generated `JsonSerializerContext` via `ReptarConfig.Json`, which is the production-safe path. The .NET Minimal API control also uses source-generated JSON.
- **Validation caveat.** `Rule<T>` compiles expression trees and is not AOT-safe today. The Reptar app uses manual validation, matching the Minimal API control. The Hono app uses Zod.
- **Hono bundle.** The compiled Hono binary is ~105 MB because it embeds the Bun runtime. It is not AOT; label it accordingly.

## Known issues / next steps

- Add a Postgres variant for write-heavy tiers (SQLite serializes writers).
- Add the T11 mixed workload to `run-vps.sh` automatically.
- Run Reptar with `BENCH_MIDDLEWARE=1` to quantify request-id + telemetry cost.
- Compare the Wasm target against Hono on Cloudflare Workers with D1.
