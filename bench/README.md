# Reptar Bench

A calibrated, three-stack benchmark for Reptar:

1. **Reptar Native AOT** (Kestrel)
2. **.NET 10 Minimal API Native AOT** (Kestrel) — the control
3. **Hono on Bun** (JIT and compiled bundle)

All three implement the **same issue-tracker schema, seed data, SQL, JSON, and routes**. The point is to separate framework overhead from database I/O instead of collapsing everything into one RealWorld-style number.

## Encoding and historical comparisons

Both endpoint and mixed-workload drivers now explicitly request and record `Accept-Encoding: identity` by default. To measure compression, use `--accept-encoding br` or set `BENCH_ACCEPT_ENCODING=br` for `run-vps.sh`; label those results separately. A server that compresses a response is doing different work from a control that does not.

See the [2026-10-27 historical performance investigation](results/investigation-20261027/README.md) for the compression/trace setup findings, correctness-preserving fixes, measured improvements, and historical write-error caveat. The older [release comparison](results/release-20261026/README.md) remains unchanged as evidence of its original protocol.

## Verified comparison protocol

The per-endpoint driver captures **actual encoded HTTP entity bytes** through Node HTTP before and after each scenario (not transparently decoded `fetch` bodies). It records Content-Encoding, content type/status, encoded/decoded byte counts and SHA256s, sampled semantic signatures, fixture/request hashes, runner source hashes, tool versions and load-generator executable identity. Headers, chunk framing and TLS overhead are not included in entity byte counts. Credentials/tokens are not retained in response evidence.

`oha` now stops issuing requests at the deadline and waits for in-flight work, with its request timeout still bounded. Deadline aborts are not silently excluded from errors. Append-only timestamped JSONL journals retain warmup/load output and partial runs even if a later probe fails. Correctness probes add two extra endpoint requests per scenario, including mutations for write scenarios; these are not part of measured throughput.

For comparison eligibility, provide `--work-manifest <json>` and `--server-executable <artifact>`. The artifact file must match its declared SHA256. The operator must establish that the owned server actually runs that artifact; this flag is not remote PID attestation. The manifest requires:

- `mode`: `equal-work`, `framework-defaults`, or `representative-application`;
- `workloadId`, pristine `datasetSha256`, `authMode`, `middlewareId`, `cacheMode`;
- exact `sourceCommit`, `executableSha256`, and actual server `toolchain` versions.

Unknown/missing provenance is descriptive-only. Defaults and representative applications cannot be promoted to equal-work claims. Semantic probes are **sampled**, not checks of every load response, JWT signatures, or internal implementation work. Configuration identity is operator-attested, not inferred from a correct response. Actual response encoding/status/content type, byte work and sampled semantics must match. Variable generated IDs can change response byte work: keep those samples, but the strict equivalence gate can refuse them.

Use `--fixtures <json>` for run-specific fixtures. Do not reseed a shared benchmark database. A claimable comparison also needs four separate chronological A/B/B/A executions, identical A/A and B/B artifact identities, and bounded A/A noise. Put their exact result filenames in `comparison-session.json` beside the files:

```json
{
  "files": ["a1.json", "b1.json", "b2.json", "a2.json"],
  "maxAaDrift": 0.05
}
```

Run `bun bench/load/report.ts <directory> --labels=a1,b1,b2,a2`. Missing/legacy/mismatched evidence, nonzero errors, overlapping runs or noise beyond the ceiling produces **UNVERIFIED / descriptive only**, not improvement percentages. The ceiling cannot exceed 5%; passing it is not a statistical significance test. Retain all unfavorable samples; do not rerun until green. See `plans/reliability/r5-benchmark-protocol.md` for actual protocol proving and limits.

The mixed-workload driver accepts isolated fixtures but does **not** yet emit this per-endpoint verification schema; its output remains descriptive-only.

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

- leaves existing services alone by default; `BENCH_STOP_CONTAINERS` is an explicit operator opt-in, never implicit authorization
- refuses existing evidence directories and seeds run-owned data below the new result directory
- gives each implementation a run-owned database copy; never deletes a shared `bench/out/*.db`
- pins the server to CPU 0 and `oha` to CPU 1
- refuses occupied ports, owns a new server process group and applies bounded TERM/KILL cleanup
- captures `manifest.json`, per-implementation JSON/Markdown, journals and server logs

This Linux orchestration is descriptive by default; it does not invent verified build provenance or A/B/B/A evidence. `BENCH_BUILD_DOTNET_VERSION` is explicitly an operator attestation, not a version inferred by running an unrelated SDK container.

## Validation microbenchmark

`Rule<T>` (the declarative validator) should stay within the same order of magnitude as hand-written checks, without allocating on the success path.

```bash
bun run bench:validation
# or write JSON results:
bun run bench:validation bench/results/validation.json
```

The harness compares `Rule<T>` against a manual validator for valid and invalid payloads on the current runtime. For Native AOT numbers, publish the project with `-p:PublishAot=true` and run the resulting binary.

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

## Archived results — original protocol

See [`bench/results/FINDINGS.md`](./results/FINDINGS.md) for the full before/after tables, the SWR cache tier, and the remaining bottlenecks.

Historical headline (the original run, not current framework HEAD). These numbers are preserved, but lack the new wire/provenance/bracket evidence and are **not promoted to current verified improvement claims**:

- HTTP/JSON/routing/validation: **41.9k / 42.2k / 41.4k / 36.3k req/s** — at or above raw .NET Minimal API.
- DB reads/writes: **13.8k** single-task, **5.9k** list, **10.7k** comments, **6.2k** creates, **15.7k** `/me`, **4.9k** updates.
- SWR-cached reads: **39.2k** task detail, **29.9k** list, **35.5k** comments (3.4x–5.6x over uncached).
- Mixed workload (70% read / 20% list / 10% write): **6,143 req/s**, -2% vs .NET Minimal API (6,242); Hono on Bun is 7,272.
- Startup **124 ms**, binary **10.4 MB** with `ReptarSqliteOnly=true` (vs 104 MB for the compiled Hono bundle).
- T7 breakdown after group commit: write gate **2.8 ms avg / 2.0 ms p50** vs insert **0.114 ms p50** — the queue is 10x shorter than before.
- **Reptar wins 10 of 12 tiers vs .NET Minimal API** (T0 is a tie, T9 is PBKDF2-bound, mixed is -2%). Hono still wins plaintext, single-row reads, lists, login, updates, and the mixed blend.

## Fairness notes

- **Same data.** Every implementation reads a copy of the same 72 MB SQLite database (1,000 users, 100 projects, 50,000 tasks, 250,000 comments).
- **Same SQL.** No ORM in any implementation.
- **Same JSON.** Camel-case field names and the same response shapes.
- **Logging off.** Kestrel and Reptar telemetry are disabled for the main run. A separate run can measure their overhead.
- **CPU pinning.** The VPS is 2 vCPU = 2 SMT threads on one physical core. The server gets CPU 0 and the load generator CPU 1. This is a relative comparison; for absolute capacity, run the load generator on a second box.
- **AOT caveat.** The default data codec now rejects missing generated metadata under Native AOT rather than silently emitting incomplete shapes. The benchmark app supplies a source-generated `JsonSerializerContext` via `ReptarConfig.Json`, which is the production-safe path. The .NET Minimal API control also uses source-generated JSON.
- **Validation caveat.** `Rule<T>` compiles expression trees and is not AOT-safe today. The Reptar app uses manual validation, matching the Minimal API control. The Hono app uses Zod.
- **Hono bundle.** The compiled Hono binary is ~105 MB because it embeds the Bun runtime. It is not AOT; label it accordingly.

## Known issues / next steps

- Add a Postgres variant for write-heavy tiers (SQLite serializes writers).
- Add the T11 mixed workload to `run-vps.sh` automatically.
- Run Reptar with `BENCH_MIDDLEWARE=1` to quantify request-id + telemetry cost.
- Compare the Wasm target against Hono on Cloudflare Workers with D1.
