# R6 — bounded operational proving and recovery

## Status

**Local stage passed; R6 remains open.** The harness has actual-artifact short validation, negative controls and pinned previous-application recovery checks. The two-hour local run started at **2026-10-28T03:30:10Z** on `7cfacca6ce1097ad529c50ea3e36dbf66c9f498c`, with frozen native/probe copies, and completed **7200.0118 actual seconds / 120 cycles** plus backup/cold native recovery and independently verified owned-process cleanup. The 24-hour isolated staging has not started. Production and multi-release maturity remain separate. Execution limits and stop/cleanup rules are in [the reviewed plan](r6-operational-plan.md).

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

## Actual two-hour local stage

[Derived attestation](evidence/r6-local-20261028.json) records exact source/runner/Bun/native/probe hashes, bounds, observations, backup counts, cleanup and original journal/result hashes. Raw private journals and disposable signing configuration remain in the owned evidence directory, not the repository.

- **120/120** serial minute-cadence cycles; **7200.011815791018** measured workload seconds. Maximum observation interval **60.1634 seconds**; no suspend/observation-gap refusal.
- Peak **observed aggregate native RSS 127648 KiB (~124.7 MiB)**; peak observed owned storage **62282881 bytes (~59.4 MiB)**, including copied artifacts. These are 120 idle-boundary samples, not continuous peaks or a hard allocation cap.
- Rooms: **240 persisted messages**, zero persistence failures/drops, zero fanout failures; **840/840 delivered fanout attempts**, settled peers/queue/active writes.
- Integrity/FK-checked backups and cold native recovery: Switchyard **241 users/sessions,121 projects,240 flags,360 versions/audit rows**; Rooms **240 messages**; Echo web **240 messages**; RPC **240 entries** with exact cursor replay.
- `ownedProcessesCleaned: true`; independent process-group absence checks found **250/250 reaped records**, no remaining/unconfirmed groups and no manager process. This checks owned identities only, never kills unrelated processes.

`r6-two-hour-verified-summary.json` / `r6-two-hour-cleanup-verified.json` retain the post-run checks. The application/framework and example source trees are unchanged between the frozen soak pin and `2bc23d8`; tooling follow-ups did not substitute a new application workload into the active run. That head also passed complete Linux CI/security (`r6-2bc-head-checks.log`) and its six-file native artifact bundle matched all hashes and the reviewed source tree. Subsequent documentation commits still require their own final-head checks before merge.

Read-only VPS inventory found resource headroom, systemd255/glibc2.39 and scoped sudo availability; **no staging resource has been created**. The installed host Bun is **1.4.4**, not the local/CI **1.3.10**, so do not silently reuse or replace it. Staging needs an independently pinned tool/runtime and separately validated hard CPU/memory/PID/storage/expiry/cleanup controls before starting its actual 24-hour clock.

## Pinned previous native applications: no-op schema upgrade and backup/old-code read

`scripts/test-native-upgrade.py` runs actual prior Switchyard and agent web/RPC executables built from **`0be66d9d61d14765608fe654106fb03363a6be7b`** with SDK10.0.401/osx-arm64, then current pinned executables, then the old executables against current SQLite backups. Prior tracked framework/application sources were not patched. Publications are retained in `r6-previous-{switchyard,agent-cli}-publish.log`; no fatal 'will always throw'/IL2104/IL3053 diagnostics were observed in these local publications.

`r6-cross-version-upgrade-verified.log` records actual preservation of all prior rows at current startup: Switchyard 3 users,3 sessions,2 projects,2 flags,3 immutable versions and3 audit rows. Both old and current native Switchyard acceptance suites pass. Echo web and RPC histories grow from2 old messages/entries to4 through the current executables; prior identities/content and cursor deltas remain exact. Current online backups pass integrity/FK checks, and cold old native executables read all4 records and the authenticated project without duplicate replay. All owned processes were cleaned. Signing configuration stays in the disposable owned HOME/workspace; this is not recovery without the required signing configuration.

This application pair has **no schema delta**: it proves compatibility of this no-op upgrade and a same-schema old-code read, not arbitrary migration reversibility. The earlier R3 old-library legacy import/crash evidence is separate. Prior Rooms application compatibility is not inferred from Switchyard/agent.

`r6-false-upgrade-negative.log` supplies the current binaries as purported previous binaries. The new guard rejects identical artifacts rather than calling current-to-current execution a pinned old-version upgrade. CI builds the exact prior source archive and exercises the actual pair plus this negative control.

## Migration planner must fail closed on unreadable existing stores

The first `r6-upgraded-switchyard-schema-plan.json` incorrectly proposed creating every table in an existing populated native store. Fresh reproduction isolated **cold native WAL-mode files with no WAL/SHM sidecars**: Bun1.3.10/SQLite3.51 readonly open returned `SQLITE_CANTOPEN`, while the original planner swallowed inspection failure and assumed an empty database. Canonicalizing the path did not fix it (`r6-cold-path-diagnostic.jsonl`).

A separate compatible SQLite3.42 read-only diagnostic read the store and created WAL/SHM sidecars; the main database SHA256 was unchanged before/during/after. Bun then read it and reported `in_sync`. Those exact facts are retained in `r6-cold-readonly-native-diagnostic.json` and `r6-after-readonly-wal-schema-plan.json`; this is not an automatic read-write workaround or permission to delete live WAL/SHM files.

The planner now **refuses to generate DDL whenever an existing database cannot be inspected**. The inspector disposes readonly connections even on errors, treats incomplete table metadata as inspection failure, and correctly quotes table/index PRAGMA identifiers rather than returning silently empty column lists. The malformed-store negative control previously returned CREATE statements instead of throwing (`r6-unreadable-plan-negative-before.log`). Malformed and locked-store regressions require refusal and preservation; valid missing stores still plan normally. Cold readonly Bun inspection remains a documented platform/tool limitation rather than false schema-drift evidence.

## Retained CI cleanup failure and regression

Head `f092715` failed Linux `Doctor (strict)` (`r6-ci-f092-doctor-failure.log`), despite successfully reading four applied migrations. Local reproduction showed Bun's `Database[Symbol.dispose]` calls strict `close(true)`; after cached statement eviction it throws **database is locked** and incorrectly marks the otherwise readable inspection disconnected. This was a cleanup error, not a missing database.

The inspector now uses `try/finally` with SQLite's deferred `close(false)` path instead of strict disposal. A 32-extra-table/migration regression covers cache eviction, actual metadata and continued `in_sync` planning. The original CI failure and disposal diagnostic are retained; local strict doctor with the pinned SDK is healthy again. This does not weaken readonly planning refusal or suppress an actual read failure. The updated complete local Bun suite passes **67 tests** (`r6-final-disposal-bun.log`); the final actual cross-version run and false-previous negative remain correct in `r6-cross-version-final.log` / `r6-false-upgrade-final-negative.log`.

CI also retains a three-day **native-artifact-only** bundle for subsequent bounded staging: explicit executable/SQLite sidecar files and hashes, build/head/tree provenance, actual tested Switchyard image ID, and host SDK version without pretending it is the Docker SDK version. No runtime DBs, signing files, environment dumps or credentials are packaged. This is neither a deployment nor elapsed evidence; download permissions and runtime provenance must be checked before use.

`r6-final-schema-bun.log` / `r6-final-schema-lint.log` record the expanded local suite (66 Bun tests) and full lint passing after the planner/inspection changes. C# implementation sources are unchanged by these follow-ups.

## Limits and remaining work

- The actual two-hour local stage passed; final-head CI/security is required after documentation changes. The separately bounded 24-hour remote stage has not started; R6 is not complete.
- Low-rate synthetic traffic and idle-boundary samples are not peak capacity, continuous profiling, multi-node queue claiming, power-loss testing or exactly-once delivery.
- Existing R3 prior-version/session crash/backup evidence remains useful, but does not establish upgrades across every released schema or multiple operational release cycles.
- No remote infrastructure, production data, existing services, DNS or paid-model usage has been changed for this package.
