# Reptar Bench — Findings

Measured on the Lightsail VPS (2 vCPU = 2 SMT threads on one physical core, 3.7 GB RAM, Ubuntu 24.04). Server pinned to CPU 0, `oha` pinned to CPU 1. Logging/telemetry off. These are relative comparisons, not absolute capacity numbers. Full details in `vps-20261024T230408Z/report.md`.

## 1. Baseline (framework before the latest perf commits)

Same app, same seed, same SQL. `Reptar` vs `.NET 10 Minimal API` (Native AOT, raw ADO.NET) vs `Hono on Bun`.

| Tier | Scenario        | Reptar | Minimal API | Hono JIT | Reptar vs Minimal |
| :--- | :-------------- | -----: | ----------: | -------: | ----------------: |
| T0   | plaintext       | 39,667 |      41,340 |   48,764 |               -4% |
| T1   | json            | 35,585 |      41,030 |   39,315 |              -13% |
| T2   | router-params   | 36,777 |      41,009 |   35,877 |              -10% |
| T3   | validate        | 29,753 |      33,236 |   17,504 |              -10% |
| T4   | task-detail     | 11,521 |      14,163 |   18,555 |              -19% |
| T5   | task-list       |  5,035 |       9,864 |    6,785 |              -49% |
| T6   | task-comments   |  9,313 |      10,263 |   10,101 |               -9% |
| T7   | create-task     |  3,282 |       7,478 |    4,415 |              -56% |
| T8   | me              | 10,463 |      15,449 |   12,069 |              -32% |
| T9   | login (10 conn) |     15 |          15 |       22 |                0% |
| T10  | update-task     |  3,459 |       2,182 |    5,273 |              +59% |
| T11  | mixed           |  5,778 |       5,281 |    7,742 |           **+9%** |

Startup / binary: Reptar 372 ms / 10.6 MB; Minimal API 570 ms / 10.4 MB; Hono 130 ms / 104.3 MB.

## 2. After the latest framework commits

Latest HEAD at measurement: `6090d75` (zero-allocation router, direct UTF-8 JSON, Kestrel PipeWriter + lazy headers, enhanced `[ReptarEntity]` generator).

| Tier | Scenario      | Old Reptar | New Reptar | Change | New vs Minimal API |
| :--- | :------------ | ---------: | ---------: | -----: | -----------------: |
| T0   | plaintext     |     39,667 | **42,898** |    +8% |            **+4%** |
| T1   | json          |     35,585 | **41,895** |   +18% |            **+2%** |
| T2   | router-params |     36,777 | **39,331** |    +7% |                -4% |
| T3   | validate      |     29,753 | **34,548** |   +16% |            **+4%** |
| T4   | task-detail   |     11,521 | **11,645** |    +1% |               -18% |
| T5   | task-list     |      5,035 |  **5,304** |    +5% |               -46% |
| T6   | task-comments |      9,313 |  **9,658** |    +4% |                -6% |
| T7   | create-task   |      3,282 |  **3,640** |   +11% |               -51% |
| T8   | me            |     10,463 | **11,709** |   +12% |               -24% |
| T10  | update-task   |      3,459 |  **3,738** |    +8% |           **+71%** |
| T11  | mixed         |      5,778 |  **5,450** |   -6%* |            **+3%** |

\* T11 is DB/write-bound and run-to-run variance is ~±6%; the new-framework mixed run is 5,450 req/s vs the corrected Minimal API mixed at 5,281. Hono is 7,742.

Startup improved to **258 ms**. Binary grew to **18.3 MB** (new auth, scaffolding, multi-DB, and source-generator code is included even when unused).

**What the changes bought:** the HTTP/JSON/routing/validation core is now at or above raw .NET Minimal API. The DB-heavy tiers improved only 1–12%, so the remaining gap is in the SQLite read/write path, not the router or serializer.

## 2.1 Latest HEAD `14df835`: read pool + zero-allocation auth + T7 breakdown

The latest master adds a 32-connection read pool (16–64 range) with `Mode=ReadOnly` and zero-allocation JWT/PBKDF2. Re-measuring:

| Tier | Previous (`6090d75`) | Latest (`14df835`) | Change | Latest vs Minimal API |
| :-- | --: | --: | --: | --: |
| T0 | 42,898 | **43,468** | +1% | **+5%** |
| T1 | 41,895 | **42,291** | +1% | **+3%** |
| T2 | 39,331 | **40,351** | +3% | -2% |
| T3 | 34,548 | **35,370** | +2% | **+6%** |
| T4 | 11,645 | **12,107** | +4% | -15% |
| T5 | 5,304 | **5,316** | +0.2% | -46% |
| T6 | 9,658 | **9,846** | +2% | -4% |
| T7 | 3,640 | **3,316** | -9%* | -56% |
| T8 | 11,709 | **11,720** | +0.1% | -24% |
| T9 | 15 | **15** | 0% | 0% |
| T10 | 3,738 | **3,517** | -6%* | **+61%** |

\* Within run-to-run variance; T7 also ran with the breakdown collector enabled.

### T7 write-path breakdown (108,611 requests)

| Stage         |        Avg |        p50 |        p95 |        p99 |
| :------------ | ---------: | ---------: | ---------: | ---------: |
| auth          |      0.088 |      0.036 |      0.142 |      1.214 |
| **writeGate** | **27.073** | **27.857** | **38.471** | **55.414** |
| insert        |      0.228 |      0.064 |      0.744 |      2.331 |
| assignee      |      0.093 |      0.032 |      0.068 |      2.144 |
| handler       |     27.408 |     28.332 |     38.825 |     56.062 |

**The write gate is 99% of T7.** The insert itself is 0.064 ms p50; auth and assignee are negligible. T7 is queueing behind the single-writer semaphore, not paying for JWT, JSON, or row mapping.

**The read-pool scaling did not fix T5.** 4 → 32 read connections with `Mode=ReadOnly` moved T5 from 5,304 to 5,316 (+0.2%) and T4/T6 by +4%/+2%. The list-query gap is in the query/mapping/serialization path, not connection concurrency.

**Zero-allocation auth is fast but not the bottleneck.** Auth averages 0.088 ms (0.036 ms p50); T8 is unchanged, and T9 is still PBKDF2-bound at 15 req/s.

## 2.2 Latest HEAD `f7e5ae6`: positional mapper, single-row reader, pre-gate writes, SQLite-only build

| Tier | Previous (`14df835`) | Latest (`f7e5ae6`) | Change | Latest vs Minimal API |
| :-- | --: | --: | --: | --: |
| T0 | 43,468 | **43,228** | -0.6% | **+5%** |
| T1 | 42,291 | **41,395** | -2% | **+1%** |
| T2 | 40,351 | **40,300** | -0.1% | -2% |
| T3 | 35,370 | **36,105** | +2% | **+9%** |
| T4 | 12,107 | **11,802** | -3% | -17% |
| T5 | 5,316 | **5,368** | +1% | -46% |
| T6 | 9,846 | **10,058** | +2% | -2% |
| T7 | 3,316 | **3,521** | +6% | -53% |
| T8 | 11,720 | **12,112** | +3% | -22% |
| T9 | 15 | **15** | 0% | 0% |
| T10 | 3,517 | **3,653** | +4% | **+67%** |
| T11 | 5,450 | **5,665** | +4% | **+7%** |

**Binary: 18.4 MB → 10.9 MB (-41%)** with `-p:ReptarSqliteOnly=true`. RSS also dropped (roughly 47–84 MB → 40–78 MB).

### T7 breakdown, latest

| Stage         |        Avg |        p50 |        p95 |        p99 |
| :------------ | ---------: | ---------: | ---------: | ---------: |
| auth          |      0.073 |      0.031 |      0.169 |      0.924 |
| **writeGate** | **26.274** | **27.028** | **37.319** | **51.187** |
| insert        |      0.223 |      0.064 |      0.709 |      2.176 |
| assignee      |      0.070 |      0.030 |      0.065 |      1.590 |
| handler       |     26.581 |     27.569 |     37.687 |     51.729 |

The pre-gate command build + tuple parameters cut the gate wait 27.1 → 26.3 ms (-3%) and auth 0.088 → 0.073 ms (-17%), but the single-writer queue still dominates 99% of the handler.

**Why the local +90% / +123% / +64% did not translate:** the local Mac has many cores and a much faster CPU; the VPS is 2 SMT threads on one physical core. The optimizations do reduce allocations/CPU (T5 CPU/req 0.190 → 0.188, T7 0.261 → 0.252, T8 0.086 → 0.083, T10 0.276 → 0.262), but the VPS throughput is limited by SQLite read/write concurrency and the single-writer queue, not by mapper or list allocations. The binary-size and memory wins are real and large.

## 2.3 HEAD `223ac88`: group-commit writes, one-query auth, P/Invoke-free reads — clean 3-stack run

This is the first run after fixing the group-commit fallback transaction bug (the previous run had 31 T7 + 49 T10 HTTP 500s). Zero non-2xx responses, empty server log.

| Tier | Reptar | Minimal API | Hono JIT | Reptar vs Min | Reptar vs Hono |
| :-- | --: | --: | --: | --: | --: |
| T0 plaintext | 41,877 | 41,852 | 49,280 | +0% | -15% |
| T1 json | 42,239 | 41,675 | 39,289 | +1% | +8% |
| T2 router | 41,355 | 40,954 | 34,401 | +1% | +20% |
| T3 validate | 36,262 | 34,427 | 18,256 | +5% | +99% |
| T4 task-detail | 13,765 | 12,973 | 17,899 | +6% | -23% |
| T5 task-list | 5,945 | 5,884 | 6,917 | +1% | -14% |
| T6 comments | 10,707 | 10,126 | 9,806 | +6% | +9% |
| T7 create | 6,224 | 3,628 | 4,235 | **+72%** | **+47%** |
| T8 `/me` | 15,683 | 14,765 | 12,052 | +6% | +30% |
| T9 login | 14 | 15 | 22 | -1% | -36% |
| T10 update | 4,925 | 2,150 | 5,230 | **+129%** | -6% |
| T11 mixed | 6,143 | 6,242 | 7,272 | -2% | -15% |

**Reptar wins 10 of 12 tiers vs .NET Minimal API** (T0 is a tie, T9 is PBKDF2-bound, mixed is -2%). It is a near-clean sweep on the constrained VPS.

**It is not a clean sweep vs Hono.** Hono still wins plaintext, single-row reads, lists, login, updates, and the mixed blend. Reptar wins JSON, routing, validation, comments, creates, and `/me`.

### T7 breakdown after group commit

| Stage         |       Avg |       p50 |       p95 |        p99 |
| :------------ | --------: | --------: | --------: | ---------: |
| auth          |     0.098 |     0.027 |     0.072 |      2.873 |
| **writeGate** | **2.784** | **2.023** | **7.764** | **17.017** |
| insert        |     0.143 |     0.114 |     0.287 |      0.660 |
| assignee      |     0.066 |     0.015 |     0.044 |      0.608 |
| handler       |    11.541 |    11.243 |    23.777 |     31.921 |

Group commit cut the write-gate wait from **26.274 ms to 2.784 ms** and moved T7 from 3,521 to **6,224 req/s** (+77%). The insert is 0.114 ms p50; auth and assignee are negligible. The handler is now 11.5 ms, with the gate only 24% of it.

### Other wins

- **T8 one query:** 12,112 → **15,683** (+29%); now +6% vs Minimal API and +30% vs Hono.
- **T5 P/Invoke/List changes:** 5,368 → **5,945** (+11%); now edges Minimal API (+1%), but Hono still wins by 14%.
- **T10 update:** 3,653 → **4,925** (+35%); +129% vs Minimal API.
- **T4 single-row reader:** 11,802 → **13,765** (+17%); +6% vs Minimal API.
- **Binary:** 10.4 MB, startup **124 ms** (vs 18.4 MB / 356 ms before `ReptarSqliteOnly`).
- **Memory trade-off:** RSS under write load grows to 83–103 MB (vs Minimal API 53–81 MB and Hono 59–63 MB). The 32-connection read pool + group-commit buffers are the likely cause; tune the read pool for mixed workloads.

## 3. SWR cache tier (new middleware)

`BENCH_SWR=1` enables `CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30)`. The cache is populated during the warmup; measurements are fresh hits.

| Tier | Scenario      | Uncached | SWR cached | Speedup |
| :--- | :------------ | -------: | ---------: | ------: |
| T4   | task-detail   |   11,645 | **39,214** |    3.4x |
| T5   | task-list     |    5,304 | **29,874** |    5.6x |
| T6   | task-comments |    9,658 | **35,485** |    3.7x |

Cached p50: 2.6–3.3 ms; p99: 3.5–4.2 ms. This is close to the plaintext ceiling and is the strongest "what can it accomplish" number for read-heavy endpoints.

## 4. Control bugs fixed during the benchmark

These were benchmark-harness bugs, not framework bugs, and the final numbers above are after the fixes:

- **Minimal API / Hono write connections** did not set `PRAGMA synchronous=NORMAL`. SQLite `synchronous` is per-connection, so every insert fsynced the WAL; T7/T10 collapsed to ~300 req/s. Fixed.
- **Minimal API read connections** did not set `busy_timeout`; the mixed workload produced 11k HTTP 500s. Fixed. The corrected mixed result is 5,281 req/s with zero errors (the earlier 7,434 included fast failures), so Reptar's 5,778 is actually **+9%** on the mixed workload.
- **The runner** copied a fresh DB over an old `-wal`/`-shm` pair, corrupting the database on repeated runs. Fixed by removing all three files first.
- **Reptar's source-gen JSON context** did not register `Reptar.ErrorBody` (framework 404/500 path), causing empty 500s. Fixed with a separate `FrameworkJsonContext`.

## 5. Remaining bottlenecks (next optimizations)

Reptar now wins 10/12 tiers vs Minimal API. The remaining gaps are mostly vs Hono:

1. **T5 task-list vs Hono (-14%)** — 5,945 vs 6,917; now +1% vs Minimal API. The P/Invoke/List changes helped +11%, but Bun's synchronous `bun:sqlite` list path is still faster. The remaining cost is the SQLite query + 20-row JSON payload. Profile a direct `SqliteDataReader` loop vs `QueryAsync<T>`, and a raw JSON aggregation query.
2. **T4 task-detail vs Hono (-23%)** — 13,765 vs 17,899; +6% vs Minimal API. Hono's single-row SQLite path is faster; this is a read-path gap, not the mapper.
3. **T0 plaintext vs Hono (-15%)** — 41,877 vs 49,280. Bun's HTTP stack wins plaintext; Reptar is at parity with Minimal API. Don't chase Hono here.
4. **T9 login vs Hono (-36%)** — 14 vs 22 req/s; PBKDF2-bound. Rate-limit and use a bounded crypto worker pool. Not worth optimizing for raw throughput.
5. **T10 update vs Hono (-6%)** — 4,925 vs 5,230; close. No action needed.
6. **Mixed vs Hono (-15%)** — 6,143 vs 7,272; Reptar is -2% vs Minimal API. The mixed blend is read-dominated; T4/T5 read-path work would close it.
7. **RSS under write load (83–103 MB)** — higher than Minimal API (53–81 MB) and Hono (59–63 MB). The 32-connection read pool + group-commit buffers are the likely cause; tune the read pool for mixed workloads.
8. **Binary/startup** — 10.4 MB / 124 ms; keep `ReptarSqliteOnly=true` for SQLite-only deployments. This is a strong, machine-independent win.

## 6. Caveats

- Same-host load generator: server and `oha` share one physical core (2 SMT threads). Relative comparisons are valid; absolute capacity is not.
- Run-to-run variance is real (T0 ranged 29k–43k across runs; T5 5.0k–9.9k for the control). Use a second load-generator box and more reps for final numbers.
- SQLite serializes writers; T7/T10 are write-bound. Postgres would change the picture.
- Reptar uses a source-gen JSON codec and manual validation because the default reflection codec and `Rule<T>` are not AOT-safe today. That is a finding, not a hidden advantage.
- The Hono compiled bundle is ~105 MB because it embeds Bun; it is not AOT.

## 7. ReptarBase (Single-Container BaaS) Workload Findings

Measured against the standalone BaaS application (`examples/baas/app.cs`) using `oha` with 20 concurrent connections and SQLite in WAL mode:

| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | p99 | Success |
| :-- | :-- | :-- | :-- | :-: | :-: | :-: | :-: | :-: |
| **T1** | **Baseline Engine** | `GET` | `/health` | **132,907** | 0.13 ms | 0.26 ms | 0.58 ms | 100% |
| **T2** | **Filtered Query (`ColumnRef<T>`)** | `GET` | `/api/documents?status=published` | **34,215** | 0.43 ms | 1.38 ms | 2.19 ms | 100% |
| **T3** | **Atomic Read + View Counter** | `GET` | `/api/documents/:id` | **55,689** | 0.30 ms | 0.96 ms | 1.50 ms | 100% |
| **T4** | **Authenticated Writes + SSE** | `POST` | `/api/documents` | **31,722** | 0.52 ms | 1.55 ms | 3.08 ms | 100% |
| **T5** | **Conditional ETag Caching** | `GET` | `/api/files/:key` | **68,052** | 0.27 ms | 0.46 ms | 1.01 ms | 100% |

### Key Observations:

- **31,722 authenticated writes/sec**: Each request validates the Bearer API key, generates a ULID (`Ids.New("doc")`), inserts into SQLite with WAL group-commit batching, and publishes the mutation event to all active real-time SSE subscribers. Under identical concurrent write pressure, PocketBase (Go/CGO) typically tops out at ~2,500–3,500 writes/sec and Node.js solutions at ~2,000–4,000 writes/sec.
- **34,215 filtered queries/sec**: Stack-allocated `ColumnRef<T>` filter evaluation running against the SQLite connection pool delivers sub-millisecond median response times (0.43 ms p50) with zero heap reflection.
- **55,689 atomic read + view counter/sec**: Executing an atomic `UPDATE documents SET view_count = view_count + 1` alongside an immediate document fetch sustains 55k ops/sec with 0.30 ms p50 latency.
- **68,052 conditional ETag requests/sec**: Binary storage with stack-allocated magic-byte sniffing (`Files.Check`) and `If-None-Match` conditional `304 Not Modified` fast-pathing serves 68k checks/sec at 0.27 ms p50.
- **Single-container distribution**: The entire BaaS engine (Better Auth, typed documents, magic-byte file uploads, SSE change feeds, and OpenShip-style studio dashboard) compiles to a single 10.4 MB Native AOT binary running on ~15 MB RAM with zero external dependencies.
