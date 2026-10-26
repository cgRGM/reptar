# Switchyard agent notes

- Read `README.md` and `FINDINGS.md` before changing behavior. The verified target is SQLite/Kestrel Native AOT, not Workers/D1.
- Keep `schema.cs` as the storage source of truth; DTOs have no ORM schema attributes.
- Publishing must atomically create the immutable version, move the active pointer, and write audit. Do not replace the database transaction with a process-local lock.
- Preserve owner/admin authorization before evaluation and before conditional-GET handling. Other tenants receive 404.
- Draft revision and active version are separate concurrency tokens. Changing rollout or publishing a version must not change subject buckets.
- Use named DTOs, source-generated JSON, explicit SQL parameters, and explicit database mappings. JIT success alone is not AOT evidence.
- Regenerate `rpc/` after changing routes. Frontend calls must use the generated `AppType`; do not hand-edit generated contracts to conceal missing metadata.
- `bun test acceptance.test.ts` and `bun bench.ts` create users/projects in the configured API database. Use disposable/lab targets only.
- From the repository root, `bash examples/switchyard/test-native.sh` builds and checks the actual native artifact with disposable data. `bun test scripts/diff-composite.test.ts` checks migration regressions.
- From this directory, run `bun install --frozen-lockfile`, `bun run check-types`, and `bun browser-smoke.ts` (the last needs a running frontend and Playwright Chromium).
- Run scoped formatting/lint, not repository-wide fixes while other agents work: `bunx ultracite check examples/switchyard scripts/diff.ts scripts/diff-composite.test.ts` from the root.
- On `remotedev`, use only `~/switchyard-lab`, container `switchyard-api`, and user service `switchyard-web`. Existing apps and other agents' processes are unrelated.
- Re-read shared files immediately before editing: concurrent agents may have corrected the same issue. Do not reset, stage, or commit unrelated work.
