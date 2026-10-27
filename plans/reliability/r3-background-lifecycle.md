# R3 — published queue/cron lifetime and delivery boundaries

This package covers the native Kestrel host and a **single SQLite-backed queue poller**. It does not promote remote queue adapters, multi-poller claiming, or exactly-once delivery.

## Failures reproduced before repair

1. Publishing the real durable queue exposed anonymous SQL parameter binding: the first `SendAsync` failed with `InvalidOperationException: Must add values for the following parameters:`. `DbQueueClient` now uses explicit tuple/dictionary parameters throughout send, receive, ack, retry and retention operations. The jobs example also configures its source-generated payload codec and uses explicit parameters/scalar count projection.
2. After that binding repair, SIGTERM caused the host to return while a queue handler was still running. Queue/cron tasks had been detached; modules and the database could be disposed underneath them. The native negative run failed with `Host returned before its in-flight queue handler drained`.

External logs are retained in `/tmp/reptar-reliability-evidence-20261027/r3-background-{before,before-lifetime}.log`. These failed runs are not counted as successful samples.

## Ownership and cancellation contract

- Queue/cron loops are `BackgroundService` tasks owned by the .NET host. Intake cancellation links to `ApplicationStopping`; host shutdown awaits both loops before disposing application modules/database.
- Queue and scheduled dispatch expose overloads accepting `CancellationToken`. Original overload signatures remain available. Both `Ctx.RequestAborted` and `Ctx.Req.Aborted` receive the token.
- Cancellation interrupts dispatch before starting another message/handler. An interrupted batch is not acknowledged or charged retry budget. Effects already performed earlier in that batch can be redelivered: application idempotency is still required.
- A handler that deliberately ignores cancellation can finish and be acknowledged during drain. `REPTAR_BACKGROUND_SHUTDOWN_SECS` configures the standard host shutdown budget (positive seconds; default 30). This is also the host's HTTP/service stop budget, not an independent per-message timeout.
- If background work outlives that budget, stop **throws**, logs the expired drain, and defers resource disposal until the actual work exits. It does not claim graceful success or close the database beneath live work. A caller that catches this failure must treat the host as failed, not reuse/dispose those resources while its work is active.
- Custom module stop hooks retain their existing best-effort semantics. This package does not establish a global timeout for arbitrary module hooks or automatic recovery from machine power loss.

The implementation follows the standard .NET hosted-service ownership model, not a new scheduler: [Microsoft worker-service lifecycle](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers).

## Published artifact gate

`scripts/fixtures/background-lifecycle` uses the actual `DbQueueClient`, native SQLite, source-generated typed queue payload decoding, the Kestrel host, and a module teardown witness. `scripts/test-background-lifecycle.py` verifies:

1. A blocked handler keeps SIGTERM shutdown pending; releasing it commits the effect and ack before module teardown.
2. Queue and cron handlers both observe cancellation and finish before module teardown. The unfinished queue delivery remains `pending`, attempts `0`.
3. Explicit first-attempt failure retries and commits with exactly one consumed retry attempt.
4. SIGKILL between a committed effect and ack leaves the message pending; a fresh process redelivers and completes it. An **application-owned idempotency key** prevents a second effect. This proves redelivery, not framework exactly-once behavior.
5. A noncooperative handler exceeds a one-second host budget, produces a nonzero exit without a core dump, leaves work pending, and does not run module disposal underneath it.

The test uses disposable OS-temp state, loopback readiness with a per-process identity, owned PID termination only, deadlines on readiness/process waits, and dumps diagnostic logs before removing its disposable workspace on failure. Linux CI publishes/runs the same fixture.

Local macOS Native AOT artifact SHA256: `2b69a6038372d942c280da56ee8a67fc99a3cd5764c06ff9c1bc234d93e006b8`. Full C# suite: **841 tests, zero failures/skips** in the initial successful run. A subsequent complete run exposed a pre-existing cross-collection cache purge race (840 pass/1 fail); its log is retained and the two missing cache-test collection annotations are corrected rather than hiding the failure with a retry. After that targeted test-isolation fix, **ten consecutive full-suite runs each passed 841/841 with zero failures/skips**; all ten logs are retained. Full Ultracite and whitespace checks passed. Final-head Linux acceptance is required before merge.

## Remaining limits

No durable lease/claim protocol for concurrent pollers; no equal semantics asserted for SQS/Azure/PubSub/Workers; no power-loss guarantee for SQLite WAL/NORMAL; no automatic side-effect deduplication; no bound on arbitrary custom module stop hooks; no sustained operational evidence earned by these short deterministic tests. AOT warning prevention remains R4, and operational upgrade/soak evidence remains R6.
