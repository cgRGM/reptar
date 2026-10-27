# Release integration comparison — 2026-10-26

Controlled Linux Native AOT comparison on the same 2-vCPU / 4-GB VPS. Existing applications were left running. Each scenario used 64 connections, a 2-second warmup, and three 5-second measurements. The server and load generator were pinned to CPU 0 and CPU 1, which are **SMT siblings**, not independent physical cores. All runs completed with **zero errors**.

## Reptar: repaired master versus final integration candidate

| Scenario | Baseline req/s | Candidate req/s | Change | Candidate p95 |
| --- | --: | --: | --: | --: |
| Plaintext | 22,541 | 22,785 | +1.1% | 3.40 ms |
| JSON | 21,191 | 20,814 | −1.8% | 3.70 ms |
| Task detail | 8,864 | 8,442 | −4.8% | 9.90 ms |
| Task list | 4,075 | 4,033 | −1.0% | 20.37 ms |
| Authenticated task update | 4,033 | 3,775 | −6.4% | 27.47 ms |

Baseline: `6c23c71baace69a082d11811586eb4d265d71678`, **with the missing `SqlPagination.cs` restored from `5c86b40`**. Unmodified master does not compile. This is explicitly a repaired baseline.

Candidate: `fb1c4e7`, rebuilt and remeasured after the final codegen changes. Candidate executable SHA-256: `038a7b0c5d354286571928c69eece82712facda327464208755102c0b4825bbb`.

An earlier paired candidate run measured changes of +0.3%, −1.3%, −1.9%, −1.5%, and −3.4%, respectively. The table uses the **final rebuild**, not the more favorable earlier result. No overall speedup is demonstrated; the read/write reductions warrant profiling rather than being dismissed as a performance win. These short shared-VPS runs are not a statistical-significance study.

## Control frameworks

The control implementations used the same fixtures and harness. Their binaries were unchanged between the paired baseline and candidate rounds.

| Scenario                  | Minimal API req/s | Compiled Hono req/s |
| ------------------------- | ----------------: | ------------------: |
| Plaintext                 |            42,245 |              49,557 |
| JSON                      |            42,638 |              39,594 |
| Task detail               |            12,937 |              18,460 |
| Task list                 |             5,717 |               6,848 |
| Authenticated task update |             1,897 |               5,305 |

Control drift between the two paired rounds was approximately −0.5% to +0.6% for Minimal API and −2.2% to +1.1% for Hono. The later final Reptar-only confirmation did not rerun those controls. Reptar trails both on these read fixtures, exceeds Minimal API on this particular SQLite write fixture, and trails Hono on writes. This does not establish a ranking for arbitrary applications or database providers.

## Evidence and reproducibility

[`summary.json`](./summary.json) contains exact measurements, individual repetition rates, both control rounds, tool versions, build-image identity, binary sizes, and machine manifests. The builder image was identical for baseline and candidate; the remote toolchain was .NET 10.0.402, Bun 1.4.4, and oha 1.16.0 (separate from the pinned .NET 10.0.401 / Bun 1.3.10 release-verification toolchain).

Full raw latency distributions, logs, and isolated source/build workspaces are retained on the VPS under `~/reptar-release-20261026/{baseline,candidate,logs}` and in the operator's `/tmp/reptar-20261026-release/perf/` evidence archive. The safe runner refuses occupied ports, binds benchmark listeners to loopback, and stops only its own processes unless an explicit container-stop allowlist is provided.
