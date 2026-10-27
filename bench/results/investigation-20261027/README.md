# Historical performance investigation — 2026-10-27

## Findings

The large historical gap was not explained by the last release's 1–6% movements alone. Rebuilding historical `223ac88` with the same present-day builder reproduced about 43–45k plaintext requests/sec. Merged `ba06d8c` measured about 23–24k with the load client's compression preference. The benchmark application's `Program.cs`, row records and project file were unchanged between these revisions.

Two added costs were identified:

1. **Compression was not controlled.** Native `perf` samples showed Brotli Huffman-tree/compressor work even on the 13-byte plaintext fixture. Reptar had acquired automatic response compression; the historical implementation and Minimal API control did not perform that work. Explicit `Accept-Encoding: identity` raised the current implementation to about 28–30k plaintext and 28–29k JSON requests/sec in these runs. Comparing implicit client defaults across servers with different compression policies was not an equal-work framework comparison.
2. **Trace setup was unconditional.** Every request generated trace/span IDs and opened an ambient trace scope even with benchmark logging/telemetry middleware off. Removing trace setup in a diagnostic-only build raised plaintext/JSON throughput about 20–25%. The actual fix defers identifier generation until a handler, logger, exporter or outbound call reads the trace. It retains cryptographic ID generation, incoming parent/sampling information, ambient propagation, parallel-consumer consistency and scope restoration.

A third, smaller issue was unnecessary repeated routing: the native host separately matched the route for its body limit and streaming flag. A combined body-policy lookup removes one match. A local JIT probe measured 944 versus 472 allocated bytes for the two parameterized metadata lookups versus one combined lookup. Isolating this change did **not** establish a whole-server throughput gain; the table below measures the combined feature-preserving candidate.

## Verified candidate versus merged implementation

Median of three separate samples for each variant, alternating order. Both clients explicitly requested Brotli; the candidate skips compressor setup for known bodies below 1 KiB. Larger bodies still negotiate compression, unknown-length streams retain ASP.NET Core policy, and SSE remains excluded. This is an end-user request-policy improvement, **not** a comparison of two implementations performing identical compression work.

| Scenario | Merged req/s | Candidate req/s | Change | Candidate p95 |
| --- | --: | --: | --: | --: |
| Plaintext | 23,921 | 40,951 | +71.2% | 1.9 ms |
| JSON | 21,965 | 38,718 | +76.3% | 2.0 ms |
| Task detail | 8,602 | 12,868 | +49.6% | 6.9 ms |
| Authenticated update | 4,039 | 4,770 | +18.1% | 21.8 ms |

All requests in these six comparison blocks completed with **zero errors**. Individual rates, latencies, CPU/request samples and diagnostic ablations are retained in [`summary.json`](./summary.json); no less-favorable sample is discarded.

With `Accept-Encoding: identity` on both sides, a separate single-sample comparison measured:

| Scenario             | Merged req/s | Candidate req/s |
| -------------------- | -----------: | --------------: |
| Plaintext            |       30,450 |          41,134 |
| JSON                 |       28,693 |          37,024 |
| Task detail          |        9,946 |          12,825 |
| Authenticated update |        4,438 |           4,870 |

The identity comparison demonstrates improvement beyond merely changing compression policy, but one sample is not a statistical-significance study. The benchmark harness now sets and records encoding explicitly; default framework comparisons use `identity`, and compression experiments use `--accept-encoding` or `BENCH_ACCEPT_ENCODING`.

## Historical and noise caveats

- The historical binary produced 37–69 update errors per sampled block: `The transaction object is not associated with the same connection object as this command`, in `ExecuteSinglePendingWrite`. Its higher write throughput is **not** a zero-error target and does not justify reverting transaction fixes. Historical read-only calibration remained error-free.
- Identical current binaries measured 3,753 and 4,008 update requests/sec in the initial bracketed experiment. That 6.8% same-binary movement means the previous 6.4% release comparison, by itself, did not establish the cause of a regression.
- CPU 0 and CPU 1 are SMT siblings on one physical core. Existing VPS applications remained running; no production databases or services were modified. Samples are 12 seconds after a 4-second warmup, 64 connections, Linux Native AOT, same .NET 10.0.402 builder, same seeded SQLite fixture and oha 1.16.0. No claim of dedicated-core isolation or universal capacity is made.
- No new control-framework ranking is claimed: the confirmation compares Reptar variants. Historical SWR-cached read numbers and simple BaaS read/write fixtures are different workloads and must remain separate.

## Correctness and boundaries

The local framework suite passes **418 tests**, including deferred/parallel trace identity, incoming unsampled parents, scope restoration, body-policy limits/HEAD/catch-all behavior, and actual Brotli/gzip middleware round trips. Host/client/CLI/docs/benchmark-header checks pass **31 Bun tests / 91 assertions**. The blog Worker was rebuilt and its E2E passed, including trace propagation, with NetWasm checks clean. Full CI remains a separate merge prerequisite.

No authentication, validation, body-size limits, database writes, tracing consumers or large-response compression were disabled in the candidate. All feature-disabling ablations are private experimental artifacts, never deployment candidates.

## Upstream patterns

- [ASP.NET Core request diagnostics](https://github.com/dotnet/aspnetcore/blob/12e90ca7cb7334d40f2dbf9752ee4d42f33628a7/src/Hosting/Hosting/src/Internal/HostingApplicationDiagnostics.cs): do diagnostic work only when needed.
- [ASP.NET Core endpoint routing](https://github.com/dotnet/aspnetcore/blob/12e90ca7cb7334d40f2dbf9752ee4d42f33628a7/src/Http/Routing/src/EndpointRoutingMiddleware.cs): reuse selected endpoints and specialize synchronous completion.
- [ASP.NET Core DFA matcher](https://github.com/dotnet/aspnetcore/blob/12e90ca7cb7334d40f2dbf9752ee4d42f33628a7/src/Http/Routing/src/Matching/DfaMatcher.cs): stack-backed tokenization and delaying route-value allocations are candidates for later measured work, not implemented claims here.
- [Hono compression middleware](https://github.com/honojs/hono/blob/bac5c4a8bcd76b165428ce757e1185753c7935bd/src/middleware/compress/index.ts): a 1 KiB known-length threshold is an established policy.

Both upstream repositories are MIT-licensed. The implementation uses framework extension points and original code rather than importing routing/DI/runtime-code-generation machinery into NetWasm.

Raw per-run distributions and native profiles are retained under the operator's `/tmp/reptar-20261026-release/perf-investigation/` and VPS `~/reptar-release-20261026/logs/profile-20261027/`. The summary pins source-file hashes and the actual candidate executable hash; subsequent test/documentation-only edits do not change that executable's framework source.
