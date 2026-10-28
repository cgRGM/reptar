# R6 — bounded operational proving and recovery

## Status

**In progress.** The harness has actual-artifact short validation and negative controls. The two-hour local run and 24-hour isolated staging are not yet accepted. Production and multi-release maturity remain separate. Execution limits and stop/cleanup rules are in [the reviewed plan](r6-operational-plan.md).

## Implemented boundary

`scripts/test-operational-soak.py` runs published Native AOT Switchyard, Rooms, and offline Echo agent web/RPC against disposable SQLite/workspaces. It bundles the test workloads once, copies only the supplied native executables and SQLite sidecars, hashes runner/tool/artifacts/probes, and records owned process identities. Changing a checkout later cannot silently change the running probes.

Default full schedule is 7200 actual seconds, one serial cycle/minute; smaller runs are explicitly labelled **short harness validation only**. Monotonic elapsed time and UTC observation gaps are checked. A full run requires a clean pinned source revision. Sampled RSS, owned storage, request/probe deadlines and owner expiry are bounded; see the plan for what these bounds do and do not enforce.

Each cycle checks:

- existing Switchyard auth/tenant/draft/publish/audit acceptance tests;
- Rooms WS and REST stable IDs/history, presence/leave and settled peer/queue/write state;
- agent Echo web SSE completion, released run reservation and exact persisted user/assistant count;
- RPC persistence, cursor delta and settled run state;
- native process liveness/RSS and storage growth, plus cumulative Rooms failure/drop/fanout counters.

Rooms smoke now polls for **this room's message IDs** under its five-second deadline, rather than treating successful writes in earlier rooms as acknowledgement for a new room.

At the end, SQLite online backup APIs create consistent snapshots with deadlines; integrity, foreign-key and row-count checks run. Cold native services then verify an authenticated Switchyard sentinel, Rooms history/IDs, agent web history and RPC session/cursor recovery from those actual backups. This is same-version recovery, not a general backwards-compatible schema rollback guarantee.

## Actual short validation, retained separately from elapsed proving

Evidence root: `/tmp/reptar-reliability-evidence-20261027/`.

- `r6-preflight-inspect.json` and `r6-preflight-doctor.json`: actual SDK **10.0.401**, Bun **1.3.10**, local schema/migrations/RPC checks healthy.
- `r6-{switchyard,rooms,agent}-publish.log`: actual osx-arm64 Native AOT publications, not managed test substitutes.
- `r6-harness-short-first.log`: initial 12-second/four-cycle functional and backup/recovery validation, all owned processes cleaned; `earnedTwoHourElapsed: false`.
- `r6-harness-short-complete.log`: 12 seconds/four cycles including cold native Rooms backup read, all cleanup; no elapsed acceptance.
- `r6-harness-budget-negative.log`: deliberately lowered aggregate native RSS threshold to 1 KiB; failed for **Native RSS exceeded observation budget**, preserved observations and cleaned owned processes.
- `r6-harness-frozen-controls.log`: frozen native/probe copies; actual success control, RSS-refusal negative control and manager TERM control. Both negatives fail for their intended reasons, report `earnedTwoHourElapsed: false`, and all controls verify reaped owned processes. Private evidence roots are listed in that log; no authentication secrets are recorded in manifests/reports.

`r6-final-harness-controls.log` repeats those intended controls on the final pre-soak harness logic. Full local Ultracite, strict benchmark TypeScript, Bun (63 tests) and C# (844 tests, zero skipped) also passed; existing compiler warnings are retained in `r6-pre-soak-framework.log` rather than described as warning-free.

CI exercises the actual-artifact short success/budget/TERM controls after extracting the already-tested Switchyard native image and reusing published Rooms/agent binaries. CI explicitly does **not** label this as elapsed operational proving.

## Limits and remaining work

- Actual two-hour completion and final-head CI/security still required for this package; 24-hour remote staging has not started.
- Low-rate synthetic traffic and idle-boundary samples are not peak capacity, continuous profiling, multi-node queue claiming, power-loss testing or exactly-once delivery.
- Existing R3 prior-version/session crash/backup evidence remains useful, but does not establish upgrades across every released schema or multiple operational release cycles.
- No remote infrastructure, production data, existing services, DNS or paid-model usage has been changed for this package.
