# R2b: published HTTP semantics and streaming faults

## Failures found, not just additional tests

- Six new core response assertions failed: 204/205/304 responses retained buffered or streaming payloads. Core response finalization now strips forbidden bodies for every host, preserves cookies/headers, removes Content-Length on 204, forces zero on 205, and preserves an explicitly declared representation length on 304. Normal HEAD representation-length behavior is retained.
- Published Native AOT query roundtrip failed: URLSearchParams/form-encoded spaces were returned as literal plus signs. `ReptarRequest.QueryValue` now decodes keys and values with plus-to-space normalization before percent decoding. Literal `%2B`, percent escapes, encoded keys, bare keys, repeated parameters and Unicode have explicit cases.

## Published-artifact gate

`scripts/fixtures/native-boundaries/app.cs` is a test-only C# server, requiring a unique run identity and source-generated JSON. `scripts/test-native-boundaries.py` executes the actual binary, verifies its identity rather than accepting any process with a health endpoint, and checks:

1. Small buffered identity responses and negotiated large gzip responses preserve exact bytes.
2. HEAD has no body and retains the identity representation length; 204/205/304 send no body and have correct length rules.
3. Encoded route/query values roundtrip without changing their meaning.
4. Buffered and streaming request-body limits at 0/15/16/17 bytes, both Content-Length and actual chunked transfer.
5. Unknown-length response streams negotiate gzip and preserve bytes; SSE is not compressed and multiline framing is intact.
6. Pre-first-byte faults produce 500. Post-first-byte faults retain the already-sent status/prefix but terminate the incomplete body, never masquerading as a complete success.
7. Closing the client mid-stream cancels the request iterator and executes its finally block; active streams return to zero. A later request still works.
8. The Native AOT binary itself emits contracts; the real generated client typechecks and executes query/bodyless requests against that same binary.

Only stdlib Python is required for the wire-level assertions. The existing Bun/TypeScript toolchain validates the generated client. The fixture is an explicitly owned executable with unique disposable state and loopback binding, not a production endpoint.

## Evidence and limits

- Complete C# suite: **827 pass**, no failures/skips.
- macOS arm64 published artifact passed all probes; SHA-256 `14db1e25157c15aa56d44f5046bceae78881bfb8ee6eb85a0a8768a4ab85e737`.
- Stream counters at completion: active 0, completed 2, disconnected 1, deliberately failed 2. These are test outcomes, not production error statistics.
- Pre-fix six bodyless failures and published query corruption retained in `/tmp/reptar-reliability-evidence-20261027/r2-*`; lint/whitespace checks pass.
- A new Linux CI published-artifact gate makes these contracts release blocking. Its final-head result is required before merge.
- This does not establish all HTTP/2, TLS, proxy, slow-client, power-loss or remote Worker streaming behavior. Existing rebuilt Worker and host gates remain in force; the shared core response/query fixes apply there, but the new socket-level fault probe specifically proves Kestrel Native AOT behavior.
