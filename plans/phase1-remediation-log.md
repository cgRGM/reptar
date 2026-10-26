# Phase 1 Remediation Log — Developer Trust & Agent Safety

**Date:** 2026-10-26 · **Scope:** `scripts/`, `packages/create-reptar/`, `templates/fullstack/`, `framework/Reptar/{Core,AI,Auth,Codegen}`, docs.

This log records what landed for Phase 1 of
[`master-audit-and-roadmap.md`](./master-audit-and-roadmap.md), the bugs the
new end-to-end gate uncovered, and what is still open. The working tree is
shared with other agents; the verification numbers below were captured with
no other build/test process running in this checkout.

---

## 1. Verification Gate (current)

| Gate | Command | Result |
| --- | --- | --- |
| Unit tests | `bun reptar test` | **215 passed**, 0 failed (was 212; +3 regression tests) |
| Full self-check | `bun reptar self-check` | **ALL GREEN** — doctor, tests, NetWasm lint, workers + Kestrel realtime smoke |
| Scaffold E2E | `bun scripts/test-scaffold.ts` | **PASS** — new → install → build → codegen → `tsc` ×2 → runtime health + auth |
| Generated contracts | `bun rpc:check` | **clean** |
| Doctor (normal) | `bun reptar doctor` | **healthy** (exit 0) |
| Doctor (strict) | `bun reptar doctor --strict` | **exit 1** on the PATH/pinned-SDK warning, as designed |
| PostgreSQL unreachable | `DATABASE_URL=postgres://…:59999/db bun reptar doctor --json` | `database.passed=false`, `healthy=false` |
| PostgreSQL reachable | `DATABASE_URL=postgres://…:5432/db bun reptar inspect --json` | `dialect=postgres`, redacted path, `connected=true` |

> **Do not run `bun reptar test` and `bun reptar self-check` concurrently in
> the same checkout.** Doing so produced one flaky test failure during this
> work (shared `bin/obj`/temp state). Item 4 (multi-agent locks) is still open.

---

## 2. What Landed

### 2.1 Scaffolder and template correctness

- **`packages/create-reptar/src/scaffold.ts`**
  - `findTemplateDir(template)` now resolves `templates/<template>` (default
    `fullstack`) instead of always using `fullstack`.
  - Added spaced mustache replacements: `{{ PROJECT_TITLE }}`,
    `{{ PROJECT_NAME }}`, `{{ PROJECT_PASCAL }}`.
  - Missing templates now fail with a clear list of available templates
    instead of silently producing the fullstack template.
- **`packages/create-reptar/src/index.ts`**
  - Added the `--database` alias for `--db`.
  - Help now states that only `fullstack` is currently shipped; `minimal-api`
    and `agent` are planned. (They previously appeared available but were
    ignored.)
- **`templates/fullstack/scripts/reptar.ts`**
  - Child `dotnet` invocations now receive `DOTNET_ROOT` when the pinned SDK
    is used.
  - Codegen post-processing:
    - rewrites `from "@reptar/client"` to `from "./client"`;
    - writes the generated contract to `app-type.ts` and removes the
      generated `app-type.d.ts`, so the scaffold's placeholder `.ts` cannot
      shadow the generated file.
- **Template AOT-safe JSON**
  - Added `features/responses.cs`: `HealthResponse`, `UserProfile`,
    `AuthMeResponse`, `ActionSuccessResponse` (all concrete records).
  - Added `json-context.cs`: `AppJsonContext` with `[JsonSerializable]` for
    every app DTO, registered via
    `Json = new SourceGenJsonCodec(AppJsonContext.Default)`.
  - Replaced anonymous `ctx.Json(new { … })` in `/health`, `/api/me`, and item
    deletion with the records above.
  - Renamed the template's `ItemRecord` to `Item` so the generated contract
    matches the frontend import.
  - Added `.Produces<T>(status)` metadata to the template routes; without it
    codegen has no output types and the frontend's `Item` import fails.
  - Added `apps/web/src/vite-env.d.ts` (`vite/client` types for
    `import.meta.env`).
  - `packages/client/src/index.ts` now re-exports `./types` and `./schemas`
    wholesale, so generated DTO/schema names never require hand-editing.
  - Dashboard uses the generated `$get` / `$post` method names.
- **New `scripts/test-scaffold.ts` + `bun run test:scaffold`**
  - Runs the real `bun reptar new` command in `.reptar/scaffold-smoke`.
  - Fails on any unreplaced `{{ UPPER_CASE }}` token.
  - Installs, builds with the pinned SDK, runs codegen, typechecks
    `apps/web` and `packages/client`.
  - Starts the API on a free port and verifies `/health`, signup
    (`201` + session cookie), `/api/me` (`authenticated: true`), and an
    authenticated create → list round-trip (`POST/GET /api/items`).
  - Cleans up by default; `--keep` retains the app for debugging.

### 2.2 Doctor must fail closed

- **`scripts/inspect.ts`**
  - New `resolveDatabaseTarget()` detects `postgres://` / `postgresql://`
    URLs, parses host/port, and redacts credentials in output.
  - PostgreSQL connectivity is verified with a bounded TCP probe
    (`probeTcpSync`). SQLite connectivity still requires the file to exist
    and open.
  - `ReptarInspection.database` now reports `dialect`, `driver`, `host`,
    `port`, and an optional `error`.
  - The database check is now `passed: database.connected` — compiler schema
    classes no longer mask a missing/unreachable database.
  - PostgreSQL migration state is a **warning** (the built-in runner is
    SQLite-only), so normal doctor stays healthy while `--strict` fails.
  - `CheckItem.warning` and `ReptarDoctorReport.strict` were added.
- **`scripts/reptar.ts`**
  - `doctor --strict` passes the flag through and renders `⚠` for warnings;
    exit code is non-zero when strict + warnings.
  - `db status|plan|generate|migrate` now fail clearly for PostgreSQL instead
    of treating the URL as a SQLite filename. `db new` still works.
- **`scripts/diff.ts`**
  - `planMigrations()` resolves the target first and throws a clear
    "SQLite only" error for PostgreSQL.
- **Docs:** `getting-started/cli.mdx`, `ai/agent-harness.mdx`, and `llms.txt`
  now document `--strict`, the real doctor JSON shape, and the new
  explicit-tool default.

### 2.3 Safe agent defaults

- **`framework/Reptar/Core/AiTools.cs`**
  - `GetAiTools`, `GetAiToolsJson`, and `MapAiTools` now default to
    `onlyExplicit: true`. Exposing every HTTP route to an LLM is opt-in.
  - `ResolveAiTool` indexes explicit tools only.
- **`scripts/inspect.ts`**
  - `toolName` is emitted only when `isAiTool: true`; standard HTTP
    operationIds are no longer presented as callable tools.
- **`framework/Reptar/AI/WorkspaceTools.cs`**
  - `WorkspaceRoot.Resolve` canonicalizes every path segment, resolving
    symlinks in intermediate directories and dangling links. A symlink inside
    the workspace can no longer read/write outside the root.
  - Case comparison is ordinal on Linux, case-insensitive on Windows/macOS.
- **Tests added**
  - `read_file_rejects_symlink_that_escapes_the_workspace_root`
  - `write_file_rejects_dangling_symlink_that_escapes_the_workspace_root`
  - `GetAiTools_defaults_to_explicit_routes_only`

### 2.4 Codegen correctness (found by the scaffold E2E)

- **`framework/Reptar/Codegen/RpcGenerator.cs`**
  - `app-type` imports only route-level input/output types. Previously it
    imported every indexed DTO, producing unused imports that fail projects
    with `noUnusedLocals`.
- **Generated contracts** were regenerated with `bun reptar codegen`;
  `bun rpc:check` is clean.

### 2.5 Built-in auth JSON metadata (found by the runtime E2E)

The template now uses `SourceGenJsonCodec`, which exposed missing metadata in
the framework's own auth routes:

- **`framework/Reptar/Core/JsonCodec.cs`**
  - Registered auth inputs (`SignInInput`, `SignUpInput`,
    `BuiltInSocialInput`, `RevokeSessionInput`, `ChangePasswordInput`,
    `CreateApiKeyInput`, `BetterAuth*Input`), auth outputs
    (`SessionInfo`, `ApiKeyInfo`, `ApiKeyResponse`, `Auth*Response`,
    `BetterAuth*Response`), `SessionUser`, `SessionResponse`,
    `Dictionary<string, bool>`, and `object`.
  - The native-only auth registrations are guarded with `#if !NETWASM`
    because the Wasm target links its own auth slice.
- **`framework/Reptar/Auth/Auth.cs`**
  - Replaced anonymous auth responses with `AuthErrorResponse`,
    `AuthUrlResponse`, and `AuthSuccessResponse` so they can be registered
    (anonymous types cannot be source-generated).

---

## 3. New Findings (and status)

| # | Finding | Status |
| --- | --- | --- |
| 1 | Template used `.get()`/`.post()` while codegen emits `$get`/`$post` | Fixed |
| 2 | Codegen wrote `app-type.d.ts`, but the placeholder `app-type.ts` shadowed it | Fixed in scaffold codegen |
| 3 | `app-type` imported every indexed DTO → `noUnusedLocals` failures | Fixed in generator |
| 4 | Template routes had no `.Produces<T>()` → generated `Item`/`HealthResponse` missing | Fixed |
| 5 | Template DTO `ItemRecord` vs frontend `Item` | Fixed (renamed) |
| 6 | `import.meta.env` had no Vite types | Fixed (`vite-env.d.ts`) |
| 7 | `SourceGenJsonCodec` lacked auth input metadata → signup 500 | Fixed (native-only registrations) |
| 8 | Signup sets the same session cookie twice (`Set-Cookie` duplicate) | **Open** — harmless but noisy; dedupe in `StartSession`/BetterAuth response |
| 9 | Concurrent `test` + `self-check` in one checkout can flake (shared build state) | **Open** — Item 4 |
| 10 | `doctor` cannot inspect PostgreSQL migration state | Mitigated as a warning; `--strict` fails |
| 11 | `db` commands are SQLite-only | Now fail clearly for PostgreSQL |
| 12 | `--template minimal-api` / `agent` have no template directories | Now fail clearly; templates still to be built |

---

## 4. Still Open (Phase 1/2)

1. **`AllowRunCommand` is still `true` by default.** The roadmap calls for
   `false` + an allowlist/approval path. This was intentionally not flipped
   in this pass because the agent CLI self-test and terminal workflow depend
   on it; it needs a coordinated change (explicit opt-in in `examples/agent-cli`
   + a command allowlist).
2. **`--template minimal-api` / `agent`** need actual template directories
   (or removal from the roadmap until they exist).
3. **AOT publish assertion.** `deploy/Dockerfile` still masks a failed publish
   with `mv … || true`. Add a post-publish check for a native ELF and no
   `app.dll`.
4. **CI workflow.** No `.github/workflows/ci.yml` yet. The new
   `scripts/test-scaffold.ts` is ready to be a job, alongside
   `bun reptar test`, `bun reptar check:netwasm --strict`, and
   `bun reptar doctor --strict`.
5. **Docs truth pass (Phase 2).** README still says 37 tests (now 215);
   `npm create reptar` is unpublished; `Clerk`/`Custom` adapters are
   documented but do not exist; quickstart uses `Db.D1()` locally; "scrypt"
   should be PBKDF2.
6. **Multi-app `--app` targeting** for `codegen`/`db` (Phase 2). `inspect` and
   `doctor` already accept `--app`.
7. **Duplicate `Set-Cookie`** on signup (finding #8).
8. **Regenerate `packages/rpc`** whenever `llms.txt` or the generator changes;
   `doctor`'s `rpc_contracts` check currently only verifies file existence,
   not content freshness.

---

## 5. Files Touched

```
scripts/inspect.ts
scripts/reptar.ts
scripts/diff.ts
scripts/test-scaffold.ts                  (new)
package.json
packages/create-reptar/src/scaffold.ts
packages/create-reptar/src/index.ts
templates/fullstack/scripts/reptar.ts
templates/fullstack/apps/api/app.cs
templates/fullstack/apps/api/features/auth.cs
templates/fullstack/apps/api/features/items.cs
templates/fullstack/apps/api/features/responses.cs   (new)
templates/fullstack/apps/api/json-context.cs         (new)
templates/fullstack/apps/api/schema/schema.cs
templates/fullstack/apps/web/src/vite-env.d.ts       (new)
templates/fullstack/apps/web/src/routes/dashboard.tsx
templates/fullstack/packages/client/src/index.ts
framework/Reptar/Core/AiTools.cs
framework/Reptar/Core/JsonCodec.cs
framework/Reptar/Auth/Auth.cs
framework/Reptar/AI/WorkspaceTools.cs
framework/Reptar/Codegen/RpcGenerator.cs
framework/Reptar.Tests/AgentEngineTests.cs
framework/Reptar.Tests/AiToolTests.cs
apps/docs/src/content/docs/getting-started/cli.mdx
apps/docs/src/content/docs/ai/agent-harness.mdx
llms.txt
packages/rpc/* (regenerated)
examples/blog/rpc/* (regenerated)
```

---

## 6. How to Re-Run

```bash
# Run these one at a time in a shared checkout.
bun reptar test
bun reptar self-check
bun scripts/test-scaffold.ts          # add --keep to inspect the generated app
bun rpc:check
bun reptar doctor --strict            # expected to fail on this machine (PATH SDK warning)

# PostgreSQL probe sanity checks
DATABASE_URL='postgres://user:pass@localhost:59999/db' bun reptar doctor --json
DATABASE_URL='postgres://user:pass@localhost:5432/db' bun reptar inspect --json
```
