# R1: realtime smoke isolation — initial implementation

## Defect and guarantee

At the audited baseline, `scripts/test-realtime.ts` called `pkill` with name patterns, discovered arbitrary listeners through `lsof`, killed their PIDs, and deleted the example's `rooms.db` plus WAL/SHM files before testing. This was unsafe on a developer machine or shared VPS even when the application itself was correct.

The new runner refuses occupied ports, launches its servers in owned POSIX process groups, and creates unique OS-temporary SQLite and Worker/D1/DO state. It never deletes the shared example database or selects a cleanup target by port/name. The example retains its normal `rooms.db` default, with `REPTAR_ROOMS_DB` available for isolated runs.

Process cleanup is idempotent and awaits the child lifecycle, allows graceful termination, then escalates to SIGKILL for its own group after a bounded grace period. Windows process-group operation is explicitly rejected rather than pretending Unix guarantees apply there. Readiness requests and websocket smoke subprocesses have deadlines; failed local D1 migration is no longer ignored.

## Evidence on this branch

- Seven runner tests pass, including an unrelated sentinel remaining available after occupied-port refusal and owned cleanup, duplicate cleanup, already-exited child, missing executable, invalid/conflicting configuration, and a child deliberately ignoring SIGTERM.
- The forced-termination test initially exposed a group-probe/child-reaping race on macOS. Cleanup now awaits the parent's close lifecycle with a bound before probing after either termination signal; the original error is retained in operator-local logs.
- Combined host/client/CLI/docs/benchmark/runner suite: **38 pass / 103 assertions**, no failures.
- Native Rooms websocket smoke passes with an isolated SQLite database and loopback bind.
- Local Worker Rooms websocket smoke passes with an isolated `--persist-to` directory for migration and dev server; no remote binding or deployed Worker is used.
- Full source Ultracite check and diff whitespace check pass. The pre-change C# baseline is 809 passing tests.
- All logs are under operator-local `/tmp/reptar-reliability-evidence-20261027/` (`r1-*`). Generated Worker outputs used for validation were restored, not committed as unrelated binary churn.

## Acceptance still pending

This is the first R1 implementation, not completion of the reliability program. Linux CI must exercise the same tests and full two-host smoke. Extend explicit descendant-process and concurrent-run failure assertions before considering all runner ownership criteria closed. A busy-port bind probe cannot permanently reserve the port while a separate server starts; runtime identity/readiness hardening remains a further boundary to test. Other harnesses still require their own ownership audit.

No production applications, Cloudflare resources, VPS services, credentials, shared checkout edits, or paid provider calls were modified by this verification.
