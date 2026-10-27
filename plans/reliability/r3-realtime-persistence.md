# R3c — Rooms persistence identity, admission and drain

## Contract and reproduced failures

A fanout frame, sender echo, REST `202`, or successful in-memory queue admission is **not a durable-write acknowledgement**. Native `PersistedCount` advances after the database insert completes. Full/stopped admission increments `DroppedCount`; storage exceptions increment `FailedCount` and retain an error. Queue depth excludes active writes. Counters are process-local, not a persistent acknowledgement ledger.

The new published history assertion failed against the prior native binary: **`history preserves the relayed message id`**. Fanout and persistence independently generated IDs. New snapshots now store the same ID used in the frame, for both WebSocket and REST fanout. Existing data is not rewritten. REST fanout now awaits the persistence operation rather than discarding the Worker/D1 task; native enqueue completion still does not acknowledge a commit.

Previously the native persister owned detached tasks without a shutdown hook, and counted overflow privately. It now implements the application module stop contract: close admission, await all consumers, then permit database disposal. The native app registers that module. Stats expose dropped admission and active writes; `lastPersistError` is JSON null when no error exists. Invalid worker/capacity configuration fails explicitly. Documentation removes the unsupported “zero throughput bottleneck” claim.

## Actual native fault evidence

`scripts/fixtures/realtime-faults/app.cs` and `scripts/test-realtime-faults.py` use real SQLite, not a fake database:

1. Hold the same client's transaction/write gate; one consumer becomes active without committing. With capacity one, admit one pending message and reject the next. Assert pending/active/dropped telemetry separately from completed persistence.
2. Stop while the write gate is held. Stop must remain incomplete, close further admission, then drain both admitted IDs after release. Query exact IDs and counters.
3. A SQLite `RAISE(ABORT)` trigger rejects one identified insert. A subsequent good insert must succeed; assert one failure, one completion, zero overflow, and the original error.
4. Application-module disposal drains 128 records across four consumers before SQLite disposal. Reopen the file and assert record count and `integrity_check`.
5. A separate owned producer holds its write gate, admits a message, and publishes an explicit **ADMITTED_NOT_COMMITTED** checkpoint. Kill only that PID with SIGKILL; reopen using a fresh native process. The uncommitted admission is absent and SQLite integrity remains valid. This demonstrates the in-memory queue limitation rather than hiding it.

Replacing the actual drain with an immediate completed task makes the native gate fail at **`Stop returned before accepted writes drained.`** The mutant and failure state are retained; the real implementation is restored. The complete native fault gate passed **three consecutive runs**, each including owned SIGKILL. The two-host rebuilt Worker/native smoke also passes WebSocket and REST fanout, stable history IDs, presence and leave/close behavior. There are no automatic retries of failed test runs.

Final local macOS Native AOT artifacts:

- Fault probe SHA256: `bb2eaf133c10a9f4af52d96714f84fa8834cd047daeda9a113a5ff3cc6cc8a03`.
- Rooms SHA256: `3f62a77f9af1255300ca1a9de316450566e7c4994ce71daddf1a9428151982f1`.
- .NET SDK 10.0.401, osx-arm64. Linux CI republishes independently and checks the seventh selected native diagnostic log against the exact-instance inventory.
- Full C# suite: **844 passed**, no failures/skips; Bun suite, Ultracite and whitespace checks pass.

Evidence is retained outside the checkout at `/tmp/reptar-reliability-evidence-20261027/r3-*`: identity failure, mutant publish/failure, three complete fault runs, native publishes, rebuilt Worker manifest, WS+REST smoke and full test/lint outputs. An initial fixture compiler error and incorrect CLI invocation are also retained. Rebuilt Worker output is not committed. All successful disposable state is removed; failed state is preserved at the paths printed in its logs. No external resource or unrelated process is modified.

## Limits

- This is best-effort realtime snapshot persistence, **not a durable inbox**. A crash can lose queued work; failed writes are not retried. A stable message ID does not make HTTP retries idempotent or delivery exactly once.
- SIGKILL/process recovery is not power-loss testing. Completed SQLite operations retain the database's configured durability assumptions.
- Application-module drain is proven under these checkpoints; arbitrary module stop hooks have no host-wide timeout. Caller cancellation does not authorize disposing resources still used by unfinished work.
- Protocol parity covers published native SQLite and rebuilt local workerd/D1, not remote multi-isolate staging or every deployment.
- Short repetitions do not establish operational maturity; elapsed soak and multi-release evidence remain R6 work.

Acceptance remains pending final-head CI/security and review.
