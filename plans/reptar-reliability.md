# Reptar Reliability: guarantees, evidence, and execution

## Scope and baseline

This is a reliability milestone, not a feature expansion or a blanket production-readiness claim. A guarantee is accepted only when its named test exercises the real boundary and fails when that guarantee is violated.

- Audited source baseline: `0be66d9d61d14765608fe654106fb03363a6be7b`.
- Isolated branch: `reliability/boundary-hardening-20261027`.
- Reproduced locally: .NET 10.0.401, Bun 1.3.10; **809 C# tests**, zero failures/skips; **31 Bun tests / 91 assertions** for host/client/CLI/docs/benchmark headers.
- `bun reptar inspect --json`: 25 routes. A fresh checkout correctly reports an absent local database; after migrating ONLY the disposable worktree database, `doctor --json` reports all checks passing, 10 tables, four applied migrations and no pending migrations.
- Baseline logs: operator-local `/tmp/reptar-reliability-evidence-20261027/`. Historical green PR gates are useful evidence but are not represented as newly rerun published/remote tests.
- The shared checkout has additional agent-owned commits. Do not reset/rebase it or silently include its moving work. Coordinate a pinned handoff before final reconciliation.

The other agent owns Ollama/model verification. Its model/version, repeatable tests, fixes, and limitations will be incorporated as a separate evidence handoff. No provider keys are required for the initial framework work.

## Status definitions

- **Supported contract:** the precisely scoped behavior has a release-blocking gate. This does not certify every feature combination of its host/provider.
- **Experimental:** implemented but missing required artifact, failure, platform, or operational evidence.
- **Unsupported:** explicitly rejected/not offered. Never substitute silent fallback for an unsupported guarantee.

The matrix below is the initial evidence classification at the pinned baseline. Promotion requires the acceptance tests below, not a larger unit-test count. A green provider test does not certify a full application transaction model.

## Initial compatibility and evidence matrix

| Combination / guarantee | Initial classification | Existing evidence / gate | Missing evidence |
| --- | --- | --- | --- |
| Linux Native AOT + SQLite, Switchyard auth/publish workflows | Supported contract, bounded to assertions | `examples/switchyard/test-native.sh`, `acceptance.test.ts`; CI published-artifact gate | Prior-version upgrade/recovery, abrupt process loss, sustained concurrency |
| Native agent + SQLite, offline tools and persisted RPC branches | Supported contract, bounded to smoke | `scripts/test-agent-native.py`; CI Linux publish; macOS published smoke during integration | Crash during persistence, concurrent RPC mutation, shutdown/durability boundary |
| Core SQLite/Postgres/MySQL data contracts | Supported contract, scoped to provider assertions | `scripts/test-providers.ts --require-all`, `ProviderContract` tests, CI services | Published AOT provider combinations and full application-level transaction portability |
| D1 data contract in local workerd | Supported contract, local only | `packages/host/src/d1.contract.test.ts` through Miniflare, provider CI gate | Real Cloudflare fault/upgrade/operational evidence; do not infer arbitrary SQL transaction support |
| Rooms protocol on native SQLite and local Worker DO/D1 | Supported contract, scoped to smoke | `scripts/test-realtime.ts`, `examples/realtime-rooms/smoke.ts` | Test-runner ownership safety; abrupt disconnect/restart; remote multi-host soak |
| Worker host ABI, artifacts, compilation and size | Supported contract, scoped to checks | Rebuild all three Workers; `check:host`, `check:netwasm`, `check:worker` | Sustained remote workload and runtime resource trends |
| Generated TypeScript request/response contracts | Supported contract, scoped to generation/types | RPC tests, generated contract check, scaffold client byte parity | Generated client executing against a published server; runtime error/status/body conformance |
| Fullstack scaffold from repository checkout | Supported contract, scoped to current smoke | `scripts/test-scaffold.ts`: install/build/codegen/typecheck and HTTP auth/CRUD | Truly external workspace without ancestor module resolution; packaged artifact; native publication |
| Buffered HTTP compression and trace correlation | Supported contract, scoped to tests | Compression middleware round trips, deferred/concurrent trace tests | Published streaming/disconnect/cancellation interactions, malformed/partial request cases |
| General multi-process session mutation, queue shutdown durability, retry idempotency | Experimental | Partial unit/integration coverage | Explicit semantics plus adversarial boundary tests; no exactly-once promise |
| macOS/Windows release portability | Experimental beyond specifically recorded checks | macOS integration checks; primary release CI is Linux | Reproducible target-specific published gates; Windows evidence |
| Ollama/model behavior | Experimental pending independent handoff | Other agent's work, not yet attributed to this branch | Exact model/version, repeatable live results and limitations |
| Paid-provider-specific caching/thinking/accounting | Experimental/unverified | Request-shape tests, not provider acceptance | Credential-gated live tests and approved cost budget |
| Full application portability across every provider/host combination | Unsupported claim | No exhaustive gate | Promote individually; never advertise a universal guarantee |

## Coverage audit: extend, do not duplicate

The existing CI already checks dependency/security gates, C# and host tests, three rebuilt Workers, provider contracts, architecture/NetWasm, docs and Markdown, native Switchyard and agent artifacts, two-host realtime, scaffold HTTP behavior, and generated contract drift. These stay in force.

Important limits discovered by reading the harnesses:

1. `scripts/test-realtime.ts` cleanup currently invokes broad `pkill` patterns and kills listeners discovered by port. This can terminate an unrelated service. Fix runner ownership **before running it on the shared VPS**.
2. The scaffold smoke lives under the repository's `.reptar/scaffold-smoke`. It is not proof that ancestor workspace dependencies cannot leak into resolution. It tests HTTP with raw fetch, not the generated client.
3. The native agent smoke covers graceful process restart and completed appends. It is not a power-loss, partial-write, concurrent mutation, or acknowledged-durability test.
4. The native Switchyard gate starts with disposable current-version data. That is not an old-version upgrade/recovery test.
5. Provider contracts run managed C# and local Miniflare. They are not published AOT checks for every database or remote D1 application verification.
6. A single terminal line saying a suite passed must not conceal omitted required providers, unexecuted assertions, or silent fallback to a stub. Preserve selected environment, gate status, skips, and failure diagnostics.

## Ordered work packages

### R1 — Safe runners and bounded resource ownership (first code PR)

- Replace broad process/port cleanup with owned child/process-group handles.
- Refuse occupied ports before launch; a failed startup must not signal the existing listener.
- Give test databases, temporary directories, container names, and artifacts unique run identities.
- Record deadlines; terminate and await owned children before reporting cleanup complete.
- Regression tests: unrelated sentinel listener survives; concurrent runs do not share state; failure/timeout leaves no owned processes; cleanup is idempotent.
- **Acceptance:** safety tests pass without production access. Never fix a harness failure by killing unrelated services.

### R2 — Published HTTP and client/scaffold boundaries

- Run generated TypeScript clients against a published native server, including non-success responses, bodyless responses, query encoding, auth and CRUD.
- Scaffold in an OS temporary directory outside the checkout; no ancestor dependency lookup. Test installed/published contract sources explicitly.
- Exercise known/unknown-length response compression, payload equality, HEAD/bodyless status semantics, streaming and client disconnect.
- **Acceptance:** assertions exercise published runtime output, not just typecheck; introduce a controlled contract mismatch and prove the appropriate test fails.

### R3 — Stateful invariants and injected failures

- Define linearization/durability points before writing assertions. A broadcast/accepted event is not automatically durable completion.
- Sessions: no duplicate replay after resume; old continuations survive fork; invalid cursors fail; partial persistence has a documented recoverable outcome.
- Runs: one reservation per key; old cleanup cannot remove a newer owner; cancellation callbacks do not execute under a lifetime lock; disconnect/shutdown semantics are explicit.
- Database: transaction rollback and connection ownership hold under concurrent operations and injected failures.
- Queues/realtime: document retries and duplicate-delivery behavior; test what shutdown drains or rejects; do not promise exactly-once delivery.
- Use deterministic injected checkpoints first, then recorded randomized seeds. A timeout is a failing result, not an invitation to silently retry until green.
- **Acceptance:** invariant tests fail on representative broken implementations; repeated seed sequences reproduce failures; no accepted data loss or history corruption.

### R4 — AOT and contract prevention

- Audit supported-path parameter binding, row mapper initialization and runtime JSON metadata.
- Add focused guards and negative fixtures; use a compile-time analyzer only when a simpler reliable check is insufficient.
- Keep canonical/scaffold parity and generated output drift checks. Reject unknown serialization/binding shapes explicitly rather than emitting empty data.
- Establish a reviewed warning baseline with identifiable instances. No blanket suppression; new warnings in supported paths require triage.
- **Acceptance:** guard negative controls fail and supported published examples remain functional.

### R5 — Trustworthy performance protocol

- Record request AND actual response encoding, auth/middleware/cache state, workload/data identity, concurrency/timing, source/toolchain/executable hashes, errors and correctness assertions.
- Distinguish equal-work comparisons, framework defaults, and representative applications.
- Compare baselines in controlled runs with A/A brackets and alternating order. Shared-runner fluctuations alone do not establish a small regression.
- **Acceptance:** a compression/configuration mismatch cannot be silently reported as equivalent work; zero correctness errors is required for an improvement claim.

### R6 — Upgrade, recovery and operational proving

- Create data using the previous pinned release in disposable stores; upgrade, migrate and assert preservation and application invariants.
- Test the documented recovery path. Code rollback, reversible migration and restoring a backup are separate capabilities.
- Run Switchyard, Rooms and agent CLI/web synthetic workflows with memory, outstanding work, queue growth, errors and contention observations.
- Sequence: bounded CI failure cases, a two-hour local/disposable soak, then a scheduled 24-hour isolated staging run if resource inventory and workload bounds permit. Durations are proposed test stages, not evidence already earned.
- **Acceptance:** publish actual elapsed time, resource configuration, failure counts and data checks. Multiple simulated restarts are not multiple real release cycles. Longer operational evidence remains open after an initial soak.

## Release and evidence rules

Every work package PR records: exact guarantee, existing coverage reused, new failure reproduced or gap closed, commands/artifact hashes/versions, negative controls, remaining limitations and cleanup outcome. CI/security must pass on the actual final head before merge. Preserve full local logs and concise reviewable repository summaries; redact credentials and synthetic-user identifiers where appropriate.

Do not promote a configuration because another provider passed. Do not silently skip a required backend, turn off security/telemetry, weaken assertions, or discard unfavorable benchmark samples to make a gate green. Fix flaky mechanisms, and preserve seeds and diagnostics.

## Infrastructure and access contract

The operator authorizes Cloudflare/VPS staging and Alchemy deployment for this program. This is not permission to delete unrelated resources or treat production as disposable.

- Inventory first; use separate stage/resource names (proposed prefix `reptar-reliability-*`) and disposable data. Never copy credentials into source or logs.
- Alchemy plan must identify every change. Stop if unrelated resources or existing production binding/data identities would change. No bulk DNS edits or stopping existing applications for tests.
- Each provisioned resource records owner/run ID, intended lifetime, workload/request bounds and cleanup procedure. Do not destroy resources outside that manifest.
- Use existing VPS and isolated Cloudflare resources first. AWS credits are available, but no AWS resource is provisioned without a specific test gap, estimated cost, lifetime and cleanup plan. No unlimited spending is inferred.
- Bound worker request rates, object writes, queued work and storage growth. Budget configuration and kill conditions precede remote soaks. Stop on quota/cost anomalies, integrity failures, uncontrolled growth or interference with existing apps.
- Live paid model tests remain out of this branch unless separately authorized. The Ollama handoff is independent.

## Completion ledger

- [x] Isolated source baseline and fresh-worktree unit/CLI/host baseline reproduced.
- [x] Existing release gates inventoried and evidence limits stated.
- [x] R1 runner ownership accepted: PR #10, `babd9929837f6e345724c4e9d9a509e514df2f5d`; final-head Linux CI/security and native/Worker smokes passed. Explicit port-reservation/runtime-identity and broader harness limitations remain documented.
- [x] R2 artifact/client/external scaffold boundaries accepted: PR #11 (`433a0d1969f2f0b84e367aae9d8d459a4b2fd49b`) and PR #12 (`6f2b69e9c531224908e2eb53049f24913e711e3e`), final-head Linux CI/security green. Native published query/bodyless/compression/stream-fault and generated-client gates added; remote Worker streaming/TLS/proxy combinations are not inferred.
- [ ] R3 stateful failure invariants accepted. Session/transaction package merged as PR #13 (`edd87b229fc915709ee50edc84bc9882e8dcd8e4`); native queue/cron ownership package merged as PR #14 (`240f93e02cf1e9ba8d7a53d23e000fa770e637ca`), both after final-head CI/security. See named evidence/limits in `plans/reliability/r3-*.md`; realtime fault review remains open.
- [ ] R4 prevention/warning controls accepted.
- [ ] R5 benchmark protocol checks accepted.
- [ ] R6 upgrade/recovery and first operational proving run accepted.
- [ ] Ollama handoff integrated with exact model/version attribution.
- [ ] Sustained production-like evidence over subsequent release cycles recorded.

The last item cannot be marked complete by adding tests or running a short soak. The milestone reports verified guarantees, implemented-but-unproven behavior, unsupported combinations, and real elapsed operational evidence separately.
