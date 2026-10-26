# Phase 2 & 3 Remediation Log — Multi-App, Auth, AOT, and CI

**Date:** 2026-10-26 · **Scope:** CLI targeting, multi-agent locks, docs truth,
cookie security, Wasm rate limiting, AOT publish assertions, Worker asset size,
and CI.

Companion to [`phase1-remediation-log.md`](./phase1-remediation-log.md). The
working tree is shared; the numbers below were captured with no other
build/test process running in this checkout.

---

## 1. Verification Gate (current)

| Gate | Command | Result |
| --- | --- | --- |
| Unit tests | `bun reptar test` | **219 passed**, 0 failed |
| Full self-check | `bun reptar self-check` | **ALL GREEN** |
| Scaffold E2E | `bun scripts/test-scaffold.ts` | **PASS** (scaffold → build → codegen → tsc → health/auth/CRUD) |
| Worker E2E | `bun e2e` | **PASS** after the Wasm size/optimizer fix |
| Docs snippets | `bun run docs:check:snippets` | **PASS** (121 snippets / 44 files) |
| Generated contracts | `bun rpc:check` | **clean** |
| `create-reptar` types | `cd packages/create-reptar && bun run check-types` | **clean** |
| Worker asset size | `stat -f%z spikes/netwasm-worker/public/browser/HelloReptar.wasm` | **~16 MB** (limit 25 MiB) |
| Doctor (strict) | `bun reptar doctor --strict` | exit 1 on the PATH/pinned-SDK warning, by design |

---

## 2. Phase 2 — Multi-App Context, Locks, Docs

### 2.1 `--app` targeting

`scripts/reptar.ts` now resolves an app directory for `dev`, `codegen`, and
`db` commands:

1. explicit `--app <dir>` / `-a <dir>`;
2. a file-based `app.cs` in the current directory;
3. the reference blog (`examples/blog`).

- `codegen` refuses apps that do not implement `--codegen` instead of starting
  a server.
- `codegen --out <dir>` is supported; non-blog apps format their own output
  and are not synced into `packages/rpc`.
- `db status|plan|generate|migrate` use the target app's database and
  migrations directory. `plan`/`generate` work before a migrations directory
  exists.
- `inspect`/`doctor` already accepted `--app`; `inspect` now reports
  `project.root` as the app directory, discovers `packages/client/src` and
  `*.db` files (including `.reptar/agent.db` and `blog.db`), and computes
  contract freshness from the app's own `types.ts`.
- `scripts/diff.ts` no longer leaks `examples/blog`/`examples/realworld`
  schemas into an explicitly targeted app, and uses the pinned SDK for the
  `--schema` probe.

### 2.2 Multi-agent build locks

`scripts/reptar.ts` now runs a preflight on `codegen`, `test`, `build`,
`test:scaffold`, and `self-check`:

- `lsof` checks the framework and test build outputs (`Reptar.dll`,
  `Reptar.deps.json`, `Reptar.Tests.*`).
- If a process holds them, it prints the PID and command and exits with a
  clear message instead of MSB4018.
- `--kill-orphans` releases stale locks automatically.

`scripts/test-realtime.ts` and `scripts/e2e.ts` now reap stale listeners on
their ports before starting, and `e2e.ts` uses a correct `wrangler.*--port`
pattern (the old `wrangler dev --port` pattern did not match the actual
`wrangler-dist/cli.js dev --port` command).

### 2.3 Docs truth pass

- `README.md`: 37 → **219** tests, package layout corrected
  (`@reptar/client` lives in `packages/rpc`), and a **cache caveat** under the
  VPS table (cached vs cache-busted throughput).
- `design.md`: scrypt → PBKDF2 (100k), 14 → 219 tests, and a status note that
  `Clerk(...)`/`Custom(...)` are roadmap-only.
- `overview/architecture.md` and `overview/the-one-rule.md`: scrypt → PBKDF2.
- `auth/adapters.md`: roadmap banner for Clerk/Custom; corrected the Better
  Auth mount example to
  `app.Route("/api/auth", BetterAuthProtocol.Routes(authOptions));`.
- `auth/builtin.md`: documents `SecureCookies` and the per-isolate Workers
  rate limiter.
- `ai/agent-harness.mdx`, `cookbook/ai-agent-tools.mdx`, and `llms.txt`:
  document the explicit-tool default and the `run_command` opt-in.
- `scripts/check-doc-snippets.ts` uses the pinned SDK and repo-root resolution,
  so the docs gate works on machines where PATH `dotnet` is older.

### 2.4 Duplicate `Set-Cookie`

`BetterAuthRoutes` now has a single `SetSessionCookie` helper. When
`AuthOptions.CookieName` equals `better-auth.session_token`, it sets the cookie
once; otherwise it sets both names as before. Sign-out uses the same
deduplication. A regression test asserts a single header.

---

## 3. Phase 3 — Auth Security, AOT, Worker Size, CI

### 3.1 Secure cookies

- `CookieJar.Set`/`SetSigned`/`SetSignedAsync`/`Delete` accept `secure`.
- `AuthOptions.SecureCookies` defaults to **true**.
- The built-in and Better Auth session cookies, OAuth PKCE/state cookies, and
  sign-out deletions pass the option through on both Kestrel and Wasm.
- The scaffold template sets `SecureCookies = false` only under `#if DEBUG`
  (local HTTP); Release/AOT keeps `Secure`. `examples/blog` and `examples/baas`
  do the same explicitly.
- Tests: `Auth_session_cookie_is_secure_by_default`.

### 3.2 Wasm login throttling

- Moved `RateLimitMaxAttempts`, `RateLimitWindowMinutes`, and
  `EnableRateLimiting` out of `#if !NETWASM` in `AuthOptions`.
- Ported the portable sign-in limiter to `framework/Reptar.Wasm/Auth/Auth.cs`
  (per-isolate dictionary + lock; best effort, not a substitute for the
  Cloudflare RateLimit binding).
- Successful sign-in resets the counter; failures record it; 429 uses the
  standard error envelope.

### 3.3 Agent shell safety

- `WorkspaceToolOptions.AllowRunCommand` now defaults to **false**.
- `AllowedCommandPrefixes` adds an optional allowlist.
- `examples/agent-cli` opts in explicitly; the self-test still covers
  `run_command`.
- `WorkspaceToolOptions` properties are now settable so the documented
  `configure` callback actually works.
- Tests: default absence of `run_command`, allowlist allow/deny, symlink
  escape (Phase 1), and existing timeout coverage.

### 3.4 AOT publish assertion

`deploy/Dockerfile` and `templates/fullstack/deploy/Dockerfile` now fail the
build if publish silently produces a JIT apphost:

```dockerfile
if [ -f /app/publish/app ]; then mv /app/publish/app /app/publish/reptar; fi
test -x /app/publish/reptar
test ! -f /app/publish/app.dll
```

`build:worker`, `build:realworld`, and `build:worker:rooms` also pass
`-p:DebugType=None -p:DebugSymbols=false`.

### 3.5 Worker asset size (new blocker found by `bun e2e`)

`bun e2e` initially failed with:

```
Asset too large. Cloudflare Workers supports assets with sizes of up to 25 MiB.
We found HelloReptar.wasm with a size of 26 MiB.
```

Findings and fix:

- The blog Worker was built with `NetWasmOptimization=O0`; O1/Oz inside
  NetWasm 0.10.1 fail with `NW1010: wasm-opt failed ... unknown error`.
- The `wasm-opt` binary in `netwasm.hosttools` crashes (SIGBUS) on the module.
  The Binaryen binary in `netwasm.toolchain` succeeds with:
  `--enable-bulk-memory --enable-exception-handling
  --enable-nontrapping-float-to-int --enable-reference-types -Oz`.
- `scripts/reptar.ts` now runs that post-pass on `build:worker`, reducing the
  asset to ~15 MB, then refreshes the `deployment.json` artifact SHA-256 and
  the generated loader's `manifestSha256` so NetWasm's integrity check passes.
- `build:worker` fails early if the optimized asset is still over 25 MiB.
- Set `REPTAR_SKIP_WASM_OPT=1` to debug the unoptimized module.

### 3.6 CI

New `.github/workflows/ci.yml` runs, sequentially in one job:

1. `bun install --frozen-lockfile`
2. `bun reptar db migrate` (creates the local SQLite DB for doctor)
3. `bun reptar test`
4. `bun reptar check:netwasm --strict`
5. `bun reptar doctor --strict`
6. `bun reptar test:realtime`
7. `bun scripts/test-scaffold.ts`
8. `bun rpc:check`
9. `create-reptar` typecheck
10. `bun run docs:check:snippets`

It uses Bun 1.3.10 and .NET 10.0.401, caches Bun/NuGet, and runs commands
sequentially to avoid the shared-build-state flake.

---

## 4. New Findings / Fixes Outside the Original Audit

| Finding | Status |
| --- | --- |
| Storage refactor split `LocalStorage`/models but dropped `S3Storage`/`S3StorageOptions`; native build failed | Restored as `framework/Reptar/Core/Storage/S3Storage.cs` |
| `BlogUploadTests` expected `ArgumentException`; storage now throws `StorageException` | Updated test to the storage-layer contract |
| `docs:check:snippets` used bare PATH `dotnet` (10.0.103) and failed | Uses pinned SDK now |
| `bun e2e` cleanup pattern didn't match `wrangler-dist/cli.js` | Fixed + pre-start port reaping |
| NetWasm host-tools `wasm-opt` crashes on the module | Use `netwasm.toolchain` binary + feature flags |
| Optimized wasm failed `raw artifact integrity check` | Refresh deployment/loader hashes |
| `WorkspaceToolOptions` init-only properties made the configure callback unusable | Changed to settable properties |

---

## 5. Still Open

1. Real `minimal-api` / `agent` templates (they now fail explicitly rather than
   silently scaffolding fullstack).
2. CI is written but not yet observed on GitHub Actions; the local equivalents
   are green.
3. The Wasm post-pass depends on the `netwasm.toolchain` package layout; a
   NetWasm upgrade should remove the workaround once its bundled optimizer
   handles reference types.
4. `MapAiTools`/`GetAiTools` docs are updated; `packages/rpc/llms.txt` is
   regenerated by `bun reptar codegen`.
5. A full `bun reptar build:worker` clean build is slow (~40s) because the
   NetWasm compiler re-runs; acceptable for CI/deploy.

---

## 6. Re-Run Commands

```bash
# Sequential in a shared checkout.
bun reptar test
bun reptar self-check
bun scripts/test-scaffold.ts
bun e2e
bun run docs:check:snippets
bun rpc:check
(cd packages/create-reptar && bun run check-types)

# Worker size / optimization
bun reptar build:worker
stat -f%z spikes/netwasm-worker/public/browser/HelloReptar.wasm
REPTAR_SKIP_WASM_OPT=1 bun reptar build:worker   # debug escape hatch

# Multi-app targeting
bun reptar inspect --app examples/blog --json
bun reptar db plan --app examples/blog --json
bun reptar codegen --app examples/blog
```
