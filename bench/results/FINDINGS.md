# Reptar Bench — Findings

Measured on the Lightsail VPS (2 vCPU = 2 SMT threads on one physical core,
3.7 GB RAM, Ubuntu 24.04). Server pinned to CPU 0, `oha` pinned to CPU 1.
Logging/telemetry off. These are relative comparisons, not absolute capacity
numbers. Full details in `vps-20261024T230408Z/report.md`.

## 1. Baseline (framework before the latest perf commits)

Same app, same seed, same SQL. `Reptar` vs `.NET 10 Minimal API` (Native AOT,
raw ADO.NET) vs `Hono on Bun`.

| Tier | Scenario | Reptar | Minimal API | Hono JIT | Reptar vs Minimal |
| :--- | :--- | ---: | ---: | ---: | ---: |
| T0 | plaintext | 39,667 | 41,340 | 48,764 | -4% |
| T1 | json | 35,585 | 41,030 | 39,315 | -13% |
| T2 | router-params | 36,777 | 41,009 | 35,877 | -10% |
| T3 | validate | 29,753 | 33,236 | 17,504 | -10% |
| T4 | task-detail | 11,521 | 14,163 | 18,555 | -19% |
| T5 | task-list | 5,035 | 9,864 | 6,785 | -49% |
| T6 | task-comments | 9,313 | 10,263 | 10,101 | -9% |
| T7 | create-task | 3,282 | 7,478 | 4,415 | -56% |
| T8 | me | 10,463 | 15,449 | 12,069 | -32% |
| T9 | login (10 conn) | 15 | 15 | 22 | 0% |
| T10 | update-task | 3,459 | 2,182 | 5,273 | +59% |
| T11 | mixed | 5,778 | 5,281 | 7,742 | **+9%** |

Startup / binary: Reptar 372 ms / 10.6 MB; Minimal API 570 ms / 10.4 MB;
Hono 130 ms / 104.3 MB.

## 2. After the latest framework commits

Latest HEAD at measurement: `6090d75` (zero-allocation router, direct UTF-8
JSON, Kestrel PipeWriter + lazy headers, enhanced `[ReptarEntity]` generator).

| Tier | Scenario | Old Reptar | New Reptar | Change | New vs Minimal API |
| :--- | :--- | ---: | ---: | ---: | ---: |
| T0 | plaintext | 39,667 | **42,898** | +8% | **+4%** |
| T1 | json | 35,585 | **41,895** | +18% | **+2%** |
| T2 | router-params | 36,777 | **39,331** | +7% | -4% |
| T3 | validate | 29,753 | **34,548** | +16% | **+4%** |
| T4 | task-detail | 11,521 | **11,645** | +1% | -18% |
| T5 | task-list | 5,035 | **5,304** | +5% | -46% |
| T6 | task-comments | 9,313 | **9,658** | +4% | -6% |
| T7 | create-task | 3,282 | **3,640** | +11% | -51% |
| T8 | me | 10,463 | **11,709** | +12% | -24% |
| T10 | update-task | 3,459 | **3,738** | +8% | **+71%** |
| T11 | mixed | 5,778 | **5,450** | -6%* | **+3%** |

\* T11 is DB/write-bound and run-to-run variance is ~±6%; the new-framework
mixed run is 5,450 req/s vs the corrected Minimal API mixed at 5,281. Hono is
7,742.

Startup improved to **258 ms**. Binary grew to **18.3 MB** (new auth,
scaffolding, multi-DB, and source-generator code is included even when unused).

**What the changes bought:** the HTTP/JSON/routing/validation core is now at or
above raw .NET Minimal API. The DB-heavy tiers improved only 1–12%, so the
remaining gap is in the SQLite read/write path, not the router or serializer.

## 2.1 Latest HEAD `14df835`: read pool + zero-allocation auth + T7 breakdown

The latest master adds a 32-connection read pool (16–64 range) with
`Mode=ReadOnly` and zero-allocation JWT/PBKDF2. Re-measuring:

| Tier | Previous (`6090d75`) | Latest (`14df835`) | Change | Latest vs Minimal API |
| :--- | ---: | ---: | ---: | ---: |
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

| Stage | Avg | p50 | p95 | p99 |
| :--- | ---: | ---: | ---: | ---: |
| auth | 0.088 | 0.036 | 0.142 | 1.214 |
| **writeGate** | **27.073** | **27.857** | **38.471** | **55.414** |
| insert | 0.228 | 0.064 | 0.744 | 2.331 |
| assignee | 0.093 | 0.032 | 0.068 | 2.144 |
| handler | 27.408 | 28.332 | 38.825 | 56.062 |

**The write gate is 99% of T7.** The insert itself is 0.064 ms p50; auth and
assignee are negligible. T7 is queueing behind the single-writer semaphore, not
paying for JWT, JSON, or row mapping.

**The read-pool scaling did not fix T5.** 4 → 32 read connections with
`Mode=ReadOnly` moved T5 from 5,304 to 5,316 (+0.2%) and T4/T6 by +4%/+2%. The
list-query gap is in the query/mapping/serialization path, not connection
concurrency.

**Zero-allocation auth is fast but not the bottleneck.** Auth averages 0.088 ms
(0.036 ms p50); T8 is unchanged, and T9 is still PBKDF2-bound at 15 req/s.

## 2.2 Latest HEAD `f7e5ae6`: positional mapper, single-row reader, pre-gate writes, SQLite-only build

| Tier | Previous (`14df835`) | Latest (`f7e5ae6`) | Change | Latest vs Minimal API |
| :--- | ---: | ---: | ---: | ---: |
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

**Binary: 18.4 MB → 10.9 MB (-41%)** with `-p:ReptarSqliteOnly=true`.
RSS also dropped (roughly 47–84 MB → 40–78 MB).

### T7 breakdown, latest

| Stage | Avg | p50 | p95 | p99 |
| :--- | ---: | ---: | ---: | ---: |
| auth | 0.073 | 0.031 | 0.169 | 0.924 |
| **writeGate** | **26.274** | **27.028** | **37.319** | **51.187** |
| insert | 0.223 | 0.064 | 0.709 | 2.176 |
| assignee | 0.070 | 0.030 | 0.065 | 1.590 |
| handler | 26.581 | 27.569 | 37.687 | 51.729 |

The pre-gate command build + tuple parameters cut the gate wait 27.1 → 26.3 ms
(-3%) and auth 0.088 → 0.073 ms (-17%), but the single-writer queue still
dominates 99% of the handler.

**Why the local +90% / +123% / +64% did not translate:** the local Mac has many
cores and a much faster CPU; the VPS is 2 SMT threads on one physical core. The
optimizations do reduce allocations/CPU (T5 CPU/req 0.190 → 0.188, T7 0.261 →
0.252, T8 0.086 → 0.083, T10 0.276 → 0.262), but the VPS throughput is limited
by SQLite read/write concurrency and the single-writer queue, not by mapper or
list allocations. The binary-size and memory wins are real and large.

## 3. SWR cache tier (new middleware)

`BENCH_SWR=1` enables `CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30)`.
The cache is populated during the warmup; measurements are fresh hits.

| Tier | Scenario | Uncached | SWR cached | Speedup |
| :--- | :--- | ---: | ---: | ---: |
| T4 | task-detail | 11,645 | **39,214** | 3.4x |
| T5 | task-list | 5,304 | **29,874** | 5.6x |
| T6 | task-comments | 9,658 | **35,485** | 3.7x |

Cached p50: 2.6–3.3 ms; p99: 3.5–4.2 ms. This is close to the plaintext
ceiling and is the strongest "what can it accomplish" number for read-heavy
endpoints.

## 4. Control bugs fixed during the benchmark

These were benchmark-harness bugs, not framework bugs, and the final numbers
above are after the fixes:

- **Minimal API / Hono write connections** did not set
  `PRAGMA synchronous=NORMAL`. SQLite `synchronous` is per-connection, so every
  insert fsynced the WAL; T7/T10 collapsed to ~300 req/s. Fixed.
- **Minimal API read connections** did not set `busy_timeout`; the mixed
  workload produced 11k HTTP 500s. Fixed. The corrected mixed result is
  5,281 req/s with zero errors (the earlier 7,434 included fast failures), so
  Reptar's 5,778 is actually **+9%** on the mixed workload.
- **The runner** copied a fresh DB over an old `-wal`/`-shm` pair, corrupting
  the database on repeated runs. Fixed by removing all three files first.
- **Reptar's source-gen JSON context** did not register `Reptar.ErrorBody`
  (framework 404/500 path), causing empty 500s. Fixed with a separate
  `FrameworkJsonContext`.

## 5. Remaining bottlenecks (next optimizations)

1. **T5 task-list (-46%)** — the positional mapper and single-row reader did
   not move the VPS (5,316 → 5,368, +1%). CPU/req is 0.188 ms vs Minimal API
   0.101 ms. The 32-connection `Mode=ReadOnly` pool also did not help. The cost
   is the read path itself: SQLite query/reader, the 20-row JSON payload, and
   the per-request allocations. Profile a direct `SqliteDataReader` loop vs
   `QueryAsync<T>`, and try a raw JSON aggregation query. The local +90% is
   real for the Mac but not the VPS bottleneck.
2. **T7 create-task (-53%)** — the breakdown still shows the write gate at
   **26.3 ms avg** vs insert **0.064 ms p50**. The pre-gate command build only
   shaved ~3%; the single-writer queue is the limit. Group commit (batch N
   inserts per transaction), a dedicated writer channel, or Postgres for
   write-heavy workloads are the ways forward.
3. **T8 `/me` (-22%)** — the single-row reader path helped +3%; auth is already
   0.073 ms. The remaining cost is the user DB lookup/read path. Cache user
   rows or put the user payload in JWT claims and skip the DB read.
4. **T9 login** — PBKDF2 100k iterations is CPU-bound: ~15 req/s at 10
   concurrent connections, p50 ~700 ms. Rate-limit login and use a bounded
   crypto worker pool; Hono's WebCrypto is ~47% faster.
5. **Binary size** — `-p:ReptarSqliteOnly=true` cut the AOT binary from
   **18.4 MB to 10.9 MB** and reduced RSS. Keep this flag for SQLite-only
   deployments; the remaining size is mostly the core/auth/realtime code.
6. **T2 router (-2%)** — the isolated rerun is **40,300 req/s**, within 2% of
   Minimal API; the full-suite 37,665 was run variance. No action needed.

## 6. Caveats

- Same-host load generator: server and `oha` share one physical core (2 SMT
  threads). Relative comparisons are valid; absolute capacity is not.
- Run-to-run variance is real (T0 ranged 29k–43k across runs; T5 5.0k–9.9k for
  the control). Use a second load-generator box and more reps for final numbers.
- SQLite serializes writers; T7/T10 are write-bound. Postgres would change the
  picture.
- Reptar uses a source-gen JSON codec and manual validation because the default
  reflection codec and `Rule<T>` are not AOT-safe today. That is a finding, not
  a hidden advantage.
- The Hono compiled bundle is ~105 MB because it embeds Bun; it is not AOT.
