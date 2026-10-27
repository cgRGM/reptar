# R3a: session transactions, connection ownership, and crash durability

## Reproduced failures

Nine session fault/sequence cases now exercise the boundary, including eight cases that failed against the previous implementation:

- An injected leaf INSERT failure erased the previous leaf and left an orphaned appended entry.
- A failed leaf switch erased the acknowledged selection.
- A failed fork left a partially populated target; an existing target or the source itself could be overwritten by a fork.
- Foreign/missing entry IDs could be accepted as active leaves.
- Independent clients produced duplicate sequence numbers, detached branches, or conflicting leaf inserts under seeded concurrent append workloads.
- A failed legacy import must leave no partial target and must remain retryable after reopening.

Two additional SQLite ownership tests failed: the static AsyncLocal transaction slot attached a transaction from database A to database B's connection, or incorrectly rejected independent nested scopes as same-client nesting.

## Repairs and semantics

- Session append, leaf switch, fork and flat import each own a database transaction. Parent/sequence selection, entry writes and leaf advancement commit together. A successful append return is the acknowledgement boundary.
- Fork rejects a nonempty target or copying into itself without modifying either history. Leaf selection rejects unknown/foreign entries without mutation.
- SQLite ambient transaction state is now client-specific. Rollback in one database does not roll back an independently committed operation in another. Same-client nested SQLite transactions remain explicitly unsupported.
- These session guarantees require real transactional read/write isolation. The verified target is native SQLite; a buffered-write adapter is not evidence of equivalent isolation on another provider. Callers must not wrap transaction-owning store methods in nested SQLite transactions.

## Published Native AOT evidence

The test-only C# `scripts/fixtures/session-durability/app.cs` and stdlib Python runner `scripts/test-session-durability.py` verify cold process boundaries, not just in-process mappers:

- Two independent native processes append 64 records each to the same SQLite file. Every acknowledged ID appears exactly once in a complete, ordered, gap-free branch after a fresh reader process starts.
- Three writer processes are forcibly SIGKILLed after acknowledgement checkpoints 1, 7 and 19. Restart verifies all acknowledged writes, valid parent/sequence/leaf invariants, SQLite integrity, and a successful subsequent append.
- A fork preserves the source while the target continues independently.
- Legacy flat rows, including serialized tool-call metadata, import in a cold process, reimport as a no-op, and remain unchanged in the original table.
- A SQLite backup restores to a separate file and can be imported/read successfully.

Current source full C# suite: **838 pass**, no failures/skips. Current macOS arm64 published gate passes. CI adds the same release-blocking native multi-process/crash checks on Linux.

## Pinned previous-version check

Separately built the same small probe against the prior verified integration baseline `0be66d9d61d14765608fe654106fb03363a6be7b`, without modifying its framework sources. Data written by that old Native AOT library implementation upgrades through the current published implementation, preserves both original flat rows and tool-call metadata, remains idempotent, and passes backup/restore validation. Previous binary SHA-256: `a3bf70ea977757be14bdf1e91b92ca4719b7e1a82f54d65d9ca089a3c8fa346a`.

The runner distinguishes a supplied pinned previous binary from its always-on current-version legacy-format recovery test; it never labels the latter an old-version upgrade. This is a library/data compatibility fixture, not proof that every prior application executable can roll back after arbitrary migrations.

An initial absolute-path file-based publish from another checkout produced an SDK artifact with missing transitive SQLite assets and an ILC 'will always throw' diagnostic. It failed on execution and was rejected. Publishing from the previous checkout with its relative file-based entry restored the correct dependency graph. Preserve this tooling failure as an R4 fatal-artifact diagnostic, not an accepted warning.

## Limits

SQLite WAL/NORMAL, process crash, not physical power loss. Unacknowledged writes may or may not have committed; the test never asserts exactly-once retry semantics. No live model calls, production data, or remote resources are involved. Final-head Linux CI/security must pass before merge. Queue/background shutdown and broader state machine coverage remain separate work, not implied by these session tests.

Operator-local evidence: `/tmp/reptar-reliability-evidence-20261027/r3-*` (including original failures, native manifests and previous-version publish diagnostics).
