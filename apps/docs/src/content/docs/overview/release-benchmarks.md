---
title: Verified Release Benchmarks
description: Reproducible Linux Native AOT measurements for the October 2026 integration, including limitations and comparison controls.
---

These measurements verify one release integration, not a universal framework ranking. The final confirmation **does not demonstrate an overall Reptar speedup**. Database-backed cases measured up to 6.4% below the repaired baseline and warrant profiling.

## Test conditions

- Same 2-vCPU / 4-GB Linux VPS; existing applications remained running.
- Server pinned to CPU 0 and load generator to CPU 1. These are **SMT siblings**, not independent physical cores.
- 64 connections, 2-second warmup, three 5-second measurements per scenario.
- Same harness, database fixtures, and Docker builder image for both snapshots.
- Remote toolchain: .NET 10.0.402, Bun 1.4.4, oha 1.16.0.
- All measured scenarios completed with **zero errors**.

The baseline was `6c23c71`, with its missing `SqlPagination.cs` restored from `5c86b40`; unmodified master did not compile. The candidate was `fb1c4e7`, rebuilt after the final codegen changes. This is a **repaired-baseline comparison**.

## Reptar before and after

| Scenario | Baseline req/s | Candidate req/s | Change | Candidate p95 |
| --- | --: | --: | --: | --: |
| Plaintext | 22,541 | 22,785 | +1.1% | 3.40 ms |
| JSON | 21,191 | 20,814 | −1.8% | 3.70 ms |
| Task detail | 8,864 | 8,442 | −4.8% | 9.90 ms |
| Task list | 4,075 | 4,033 | −1.0% | 20.37 ms |
| Authenticated task update | 4,033 | 3,775 | −6.4% | 27.47 ms |

An earlier paired candidate run measured +0.3%, −1.3%, −1.9%, −1.5%, and −3.4%, respectively. The table reports the **final rebuild**, rather than selecting the more favorable earlier result. These short shared-VPS runs are not a statistical-significance study or a production-capacity estimate.

## Comparison controls

Minimal API and compiled Hono used the same harness and fixtures. Their binaries were unchanged between the paired baseline and candidate rounds.

| Scenario                  | Minimal API req/s | Compiled Hono req/s |
| ------------------------- | ----------------: | ------------------: |
| Plaintext                 |            42,245 |              49,557 |
| JSON                      |            42,638 |              39,594 |
| Task detail               |            12,937 |              18,460 |
| Task list                 |             5,717 |               6,848 |
| Authenticated task update |             1,897 |               5,305 |

Control drift between paired rounds was approximately −0.5% to +0.6% for Minimal API and −2.2% to +1.1% for Hono. The later final Reptar-only confirmation did not rerun those controls.

Reptar trails both controls on these reads, exceeds Minimal API on this particular SQLite write fixture, and trails Hono on writes. These results do not establish rankings for other workloads, database providers, Cloudflare Workers, or cached application endpoints.

## Evidence and reproduction

- [Full methodology and caveats](https://github.com/cgRGM/reptar/blob/master/bench/results/release-20261026/README.md)
- [Exact measurements, repetition rates, machine manifests, and build identity](https://github.com/cgRGM/reptar/blob/master/bench/results/release-20261026/summary.json)
- [Benchmark harness and fairness rules](https://github.com/cgRGM/reptar/tree/master/bench)

The runner binds to loopback, refuses occupied ports, and cleans up its own processes. Existing containers are not stopped unless an operator supplies an explicit allowlist. Raw distributions and isolated build workspaces are retained in the operator's release evidence archive; the committed summary identifies the tested executable and toolchain.
