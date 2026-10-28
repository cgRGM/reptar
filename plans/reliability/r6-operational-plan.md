# R6 operational execution plan — isolated, bounded, not production

## Authorization and order

The operator authorized end-to-end reliability work and suitable isolated Cloudflare/VPS staging, preserving unrelated services and data. R1–R5 are accepted through PR #17 (`70a521c57bdd80f23f973f98f652c1139340267b`). This plan does not authorize production deployment, JWT rotation, custom-domain changes, paid providers, or AWS provisioning.

Order: validate the operational harness and its negative controls; run **7200 actual seconds** locally; inspect results and recovery; only then inventory/plan a **24-hour isolated staging** run if resource bounds permit. Short harness runs cannot earn elapsed proving. One release and multiple restarts cannot earn multi-release maturity.

## Stage 1 — disposable local native proving

- Four long-lived owned processes: published Native AOT Switchyard, Rooms, agent web and agent RPC. All HTTP listeners bind explicitly to **127.0.0.1** on available ephemeral ports. No discovered listener is killed or replaced.
- Evidence/data/config live in a unique `reptar-reliability-soak-*` mode-0700 temporary directory. Existing workspaces, signing material, databases and ignored user data are never reused. Native executable/SQLite sidecars are copied into the owned directory and hashed. Probes and their imports are bundled once into owned immutable files, so later checkout edits cannot change the running workload. Runner/Bun/native/probe SHA256s and a pinned clean source commit are recorded; changing the Bun executable fails the run.
- Clean environment contains only required local runtime values. HOME/config/workspace/DB point inside that directory. Agent CLI and every web prompt explicitly select **Echo**. No provider credentials, Ollama traffic or paid-model calls.
- Default schedule: one serial synthetic cycle per 60 seconds for 7200 actual monotonic seconds; maximum 120 cycles. Each cycle runs existing Switchyard auth/tenant/draft/publish/audit tests, Rooms WS+REST/presence/history/leave smoke, persistent agent Echo web turns, and RPC turns/cursor checks.
- Observe completed workflows, process RSS, storage growth, Rooms queue/active/peer/fanout/failure/drop state, agent reservation settlement and exact history counts. Observe at idle boundaries, not a claim of measuring every transient queue peak or capacity under load.
- Bound long-lived native RSS at 1 GiB aggregate (sampled stop threshold, not an OS allocation cap), owned storage at 128 MiB, individual external probes at 45 seconds, HTTP/RPC calls at 10 seconds. Thresholds may be lowered but not raised beyond these ceilings. A watchdog requests owner stop at requested duration +180 seconds; cleanup is bounded and total budget including cleanup is duration +300 seconds, subject to OS signal/I/O behavior.
- Startup readiness is bounded. Fail a workflow, budget breach, unexpected exit or excessive observation gap immediately; retain its logs/state and do not silently retry a failed run until green. Manager TERM/INT triggers owned cleanup. Every child has an owned POSIX process group and is stopped/reaped once; historical PIDs are never signalled again.
- Use SQLite online backup APIs with deadlines; check integrity/foreign keys/counts. Cold native services read backups: authenticated Switchyard sentinel, Rooms IDs/history, agent web messages, RPC session/replay cursor. Code rollback, migration reversibility and backup restoration remain distinct claims.
- On completion/failure: stop/reap all owned processes, retain bounded data/logs/manifests as evidence, report actual elapsed duration and cleanup outcome. A successful 12-second harness validation is still labelled short-only.

## Stage 2 — isolated staging, conditional and not applied

No remote resource has been changed for this stage. Before apply:

1. Read-only inventory of the chosen host/account, existing apps/containers, available memory/storage/CPU, bindings/databases and resource names. Record a before snapshot without credentials.
2. Pin the exact source/artifacts and use unique `reptar-reliability-*` resources, manifests, resource caps, rates, expiry and cleanup. Use Alchemy for the reviewed infrastructure lifecycle where applicable. Do not reuse production DBs, DNS, signing keys, networks/volumes, workers.dev endpoints or other agents' resources.
3. Prefer native VPS isolation for Switchyard/agent, whose accepted targets are native SQLite; do not claim a Workers port by substituting an unrelated runtime. Remote Rooms/D1 can be a separately identified target, not an extrapolation from local workerd.
4. Set explicit workload/cost/storage/request bounds and abort/cleanup triggers before starting. Do not provision AWS or consume paid models for convenience. Halt if inventory shows insufficient headroom, missing access, uncontrolled spend, or any unrelated resource drift.
5. Run 24 actual hours only after the local stage passes. Keep complete observations/failures, verify backup/restart and clean up only manifest-owned resources. If the stage cannot safely start, record the precise blocker instead of inventing elapsed evidence.

## Remaining readiness limits

Two hours is bounded first operational evidence, not sustained production maturity. A 24-hour staging run is still one release. Multiple real release cycles and elapsed operational history remain open after both stages. Production deployment remains a separately reviewed task.
