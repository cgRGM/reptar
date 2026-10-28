# R5 — Benchmark evidence and comparison refusal

## Guarantee

Throughput alone, equal request headers, and historical framework labels are not evidence of equivalent work. The endpoint runner now captures bounded raw **encoded HTTP entity bytes**, decodes them explicitly, and checks sampled response semantics before/after every scenario. It records actual Content-Encoding/status/content type, entity lengths and SHA256s, correctness signatures, request/fixture/source hashes, actual load-generator version/executable hash, server artifact hash match and declared workload/toolchain/auth/middleware/cache identities.

Server configuration is operator-attested. Supplying `--server-executable` verifies that file against the manifest; the operator must establish that the owned listening process runs it. Neither a matching file nor a response proves remote process identity or internal work. Semantic probes do not validate every load response or JWT signatures. Headers, chunk framing and TLS bytes are outside entity byte counts. Write probes add two mutations outside measured throughput.

`protocol.ts`, `report-evidence.ts` and `bracket.ts` refuse equal-work promotion for missing/legacy provenance, defaults/representative modes, malformed witnesses, different actual encoding/semantic/byte work, mismatched request/configuration/data identity, empty samples, or nonzero reported errors. Scenario names must be unique. Four chronological, nonoverlapping independent A/B/B/A executions must preserve A/A and B/B artifact identity. A/A drift must remain below a declared ceiling no greater than 5%; passing is not statistical significance. Report inputs must be the exact bracket artifacts, not unrelated results borrowing another bracket's status. Reports otherwise retain rates/errors but suppress relative improvement percentages and mark themselves UNVERIFIED/descriptive-only.

## Actual findings retained

- Published the real Reptar benchmark with .NET SDK **10.0.401**, **osx-arm64 Native AOT**. Executable SHA256: `6a307f695c7fcd3311c4b583c37ecf4315ac53f2df57388160cd76de3133f3e3`.
- Used actual **oha 1.16.0**, not a load-generator stub, against owned synthetic native servers and a pristine 72 MiB seed (50,000 tasks / 250,000 comments). No production data or external service was changed.
- The initial eleven-scenario run exposed two protocol issues: HTTP/1 deadline abortion was being excluded from errors, and generated create IDs changed response byte work between probes. The first partial run/log is retained.
- `oha --wait-ongoing-requests-after-deadline` now stops issuing work but waits for completion under its bounded request timeout. Deadline aborts are no longer filtered out. The revised short smoke passed sampled semantics for **all eleven endpoints** with **zero reported errors**. This was 1 connection, 0.1s warmup / 0.2s measurement, one repetition: functional protocol proving, **not performance evidence**. Variable byte work remains recorded and comparison-ineligible rather than being normalized away silently.
- Executed four fresh-process, same-binary calibration controls, mirrored A/B/B/A (in fact A/A/A/A because there was no optimization candidate), 4 connections, 1s warmup / 2s measure, one repetition, plaintext and JSON. Recorded A/A drift: plaintext **1.8647%**, JSON **7.2255%**. The gate **rejected the JSON noise**, emitted UNVERIFIED/descriptive-only and made **no improvement claim**. All four samples remain; no retry until green.
- Real HTTP tests send the same Accept-Encoding to servers returning different actual identity/gzip entities: decoded semantics match but the comparison fails on actual Content-Encoding. Invalid gzip bytes under a gzip header also fail. Additional negative tests cover correctness/status/identity, malformed witnesses, duplicate scenarios, missing artifact evidence, reported errors, defaults, concurrency/dataset differences, overlapping runs, changed baseline artifact and excessive A/A noise.

## Retention and orchestration

Timestamped append-only JSONL captures raw warmup/load results and witnesses, including partial runs. Synthetic JWT/password values are redacted from load output; response artifacts contain hashes/signatures, not bearer tokens. Old numeric tables are preserved but relabeled historical and not promoted to current verified claims. Report notes no longer assume historical Lightsail hardware, middleware or codec behavior for a new run.

Fixtures can be supplied with `--fixtures`. Linux VPS orchestration seeds/copies only below a new run-owned result directory, refuses existing evidence/databases and occupied ports, creates an owned server process group, and uses bounded TERM/KILL cleanup with an unresolved-group witness if cleanup fails. Existing containers remain untouched by default; explicit `BENCH_STOP_CONTAINERS` is operator opt-in, not blanket production authorization. An unrelated SDK-container probe no longer masquerades as the executable's build toolchain. Linux syntax/refusal tests are included; this package does not claim a new full shared-VPS execution.

The mixed driver accepts isolated fixtures but does not yet emit this verification schema. It remains descriptive-only. Existing benchmark features are not disabled to produce faster numbers.

Evidence remains in `/tmp/reptar-reliability-evidence-20261027/r5-*`, including the initial failed/partial run, actual native publish, complete endpoint smoke, four bracket inputs/journals/logs, source/artifact hashes and rejected report. Successful owned servers were stopped/reaped; synthetic databases/results remain as evidence. No remote resources were created.

## Acceptance

Local automated and actual-artifact checks are recorded above. Full lint, strict benchmark TypeScript, complete Bun suite, and final-head Linux CI/security must pass before acceptance. R6 elapsed local/staging proving and multi-release operational maturity remain separate, unearned work.
