# Phase 2 & 3 Remediation Log — Multi-App, Auth, AOT, and CI

**Date:** 2026-10-26 · **Scope:** CLI targeting, multi-agent locks, docs truth, cookie security, Wasm rate limiting, AOT publish assertions, Worker asset size, Worker JSON/WebSocket correctness, D1 dictionary writes, and CI.

Companion to [`phase1-remediation-log.md`](./phase1-remediation-log.md). The working tree is shared; the numbers below were captured with no other build/test process running in this checkout.

---

## 1. Verification Gate (current)

| Gate | Command | Result |
| --- | --- | --- |
| Unit tests | `bun reptar test` | **388 passed**, 0 failed |
| Full self-check | `bun reptar self-check` | **ALL GREEN** (doctor, tests, host boundary, host contract, contracts, netwasm lint, two-host realtime) |
| Scaffold E2E | `bun scripts/test-scaffold.ts` | **PASS** (scaffold → build → codegen → tsc → health/auth/CRUD) |
| Worker E2E | `bun e2e` | **PASS** (blog Worker on workerd + D1; wasm compiled from the imported module, not a static asset) |
| Realtime smoke | `bun reptar test:realtime` | **PASS** on workerd + DO + D1 and Kestrel + SQLite |
| Docs snippets | `bun run docs:check:snippets` | **PASS** (121 snippets / 44 files) |
| Docs site build | `cd apps/docs && bun run build` | **PASS** (50 pages) |
| Docs markdown | `cd apps/docs && bun run check:markdown` | **PASS** |
| Generated contracts | `bun rpc:check` | **clean** |
| `create-reptar` types | `cd packages/create-reptar && bun run check-types` | **clean** |
| Worker size budget | `bun reptar check:worker` | **38.91 MiB uncompressed / 4.53 MiB gzip** (48 MiB hard ceiling, 32 MiB target) |
| Doctor (strict) | `bun reptar doctor --strict` | exit 1 locally on the PATH/pinned-SDK warning; CI installs 10.0.401 |

---

## 2. Phase 2 — Multi-App Context, Locks, Docs

### 2.1 `--app` targeting

`scripts/reptar.ts` now resolves an app directory for `dev`, `codegen`, and `db` commands:

1. explicit `--app <dir>` / `-a <dir>`;
2. a file-based `app.cs` in the current directory;
3. the reference blog (`examples/blog`).

- `codegen` refuses apps that do not implement `--codegen` instead of starting a server.
- `codegen --out <dir>` is supported; non-blog apps format their own output and are not synced into `packages/rpc`.
- `db status|plan|generate|migrate` use the target app's database and migrations directory. `plan`/`generate` work before a migrations directory exists.
- `inspect`/`doctor` already accepted `--app`; `inspect` now reports `project.root` as the app directory, discovers `packages/client/src` and `*.db` files (including `.reptar/agent.db` and `blog.db`), and computes contract freshness from the app's own `types.ts`.
- `scripts/diff.ts` no longer leaks `examples/blog`/`examples/realworld` schemas into an explicitly targeted app, and uses the pinned SDK for the `--schema` probe.

### 2.2 Multi-agent build locks

`scripts/reptar.ts` now runs a preflight on `codegen`, `test`, `build`, `test:scaffold`, and `self-check`:

- `lsof` checks the framework and test build outputs (`Reptar.dll`, `Reptar.deps.json`, `Reptar.Tests.*`).
- If a process holds them, it prints the PID and command and exits with a clear message instead of MSB4018.
- `--kill-orphans` releases stale locks automatically.

`scripts/test-realtime.ts` and `scripts/e2e.ts` now reap stale listeners on their ports before starting, and `e2e.ts` uses a correct `wrangler.*--port` pattern (the old `wrangler dev --port` pattern did not match the actual `wrangler-dist/cli.js dev --port` command).

### 2.3 Docs truth pass

- `README.md`: 37 → **219** tests, package layout corrected (`@reptar/client` lives in `packages/rpc`), and a **cache caveat** under the VPS table (cached vs cache-busted throughput).
- `design.md`: scrypt → PBKDF2 (100k), 14 → 219 tests, and a status note that `Clerk(...)`/`Custom(...)` are roadmap-only.
- `overview/architecture.md` and `overview/the-one-rule.md`: scrypt → PBKDF2.
- `auth/adapters.md`: roadmap banner for Clerk/Custom; corrected the Better Auth mount example to `app.Route("/api/auth", BetterAuthProtocol.Routes(authOptions));`.
- `auth/builtin.md`: documents `SecureCookies` and the per-isolate Workers rate limiter.
- `ai/agent-harness.mdx`, `cookbook/ai-agent-tools.mdx`, and `llms.txt`: document the explicit-tool default and the `run_command` opt-in.
- `scripts/check-doc-snippets.ts` uses the pinned SDK and repo-root resolution, so the docs gate works on machines where PATH `dotnet` is older.

### 2.4 Duplicate `Set-Cookie`

`BetterAuthRoutes` now has a single `SetSessionCookie` helper. When `AuthOptions.CookieName` equals `better-auth.session_token`, it sets the cookie once; otherwise it sets both names as before. Sign-out uses the same deduplication. A regression test asserts a single header.

---

## 3. Phase 3 — Auth Security, AOT, Worker Size, CI

### 3.1 Secure cookies

- `CookieJar.Set`/`SetSigned`/`SetSignedAsync`/`Delete` accept `secure`.
- `AuthOptions.SecureCookies` defaults to **true**.
- The built-in and Better Auth session cookies, OAuth PKCE/state cookies, and sign-out deletions pass the option through on both Kestrel and Wasm.
- The scaffold template sets `SecureCookies = false` only under `#if DEBUG` (local HTTP); Release/AOT keeps `Secure`. `examples/blog` and `examples/baas` do the same explicitly.
- Tests: `Auth_session_cookie_is_secure_by_default`.

### 3.2 Wasm login throttling

- Moved `RateLimitMaxAttempts`, `RateLimitWindowMinutes`, and `EnableRateLimiting` out of `#if !NETWASM` in `AuthOptions`.
- Ported the portable sign-in limiter to `framework/Reptar.Wasm/Auth/Auth.cs` (per-isolate dictionary + lock; best effort, not a substitute for the Cloudflare RateLimit binding).
- Successful sign-in resets the counter; failures record it; 429 uses the standard error envelope.

### 3.3 Agent shell safety

- `WorkspaceToolOptions.AllowRunCommand` now defaults to **false**.
- `AllowedCommandPrefixes` adds an optional allowlist.
- `examples/agent-cli` opts in explicitly; the self-test still covers `run_command`.
- `WorkspaceToolOptions` properties are now settable so the documented `configure` callback actually works.
- Tests: default absence of `run_command`, allowlist allow/deny, symlink escape (Phase 1), and existing timeout coverage.

### 3.4 AOT publish assertion

`deploy/Dockerfile` and `templates/fullstack/deploy/Dockerfile` now fail the build if publish silently produces a JIT apphost:

```dockerfile
if [ -f /app/publish/app ]; then mv /app/publish/app /app/publish/reptar; fi
test -x /app/publish/reptar
test ! -f /app/publish/app.dll
```

`build:worker`, `build:realworld`, and `build:worker:rooms` also pass `-p:DebugType=None -p:DebugSymbols=false`.

### 3.5 Worker asset size (new blocker found by `bun e2e`)

`bun e2e` initially failed with:

```
Asset too large. Cloudflare Workers supports assets with sizes of up to 25 MiB.
We found HelloReptar.wasm with a size of 26 MiB.
```

The wasm does not need to be a static asset at all: it is imported as a `CompiledWasm` module and the patched loader compiles `globalThis.__REPTAR_WASM_MODULE`. The fix is to stop uploading it twice:

- `public/.assetsignore` excludes `browser/*.wasm` in the blog, realworld, and rooms Workers.
- `@reptar/host`'s `patchNetWasmHost` makes the loader return an empty byte array for the `application` artifact instead of fetching it.
- `refreshDeploymentHashes` records the empty SHA-256 for that artifact, so the integrity check passes.
- Only Cloudflare's 64 MiB Worker script limit applies. `check:worker` enforces a 48 MiB hard ceiling and warns above the 32 MiB optimization target.

The module is left unoptimized by default (38.4 MiB upload / ~4.5 MiB gzip on the current source). `wasm-opt` on this module is slow and fragile: core `-Oz` does not converge, and a post-link `-O3` pass invalidates the interop manifest. `REPTAR_OPTIMIZE_WORKER=1` remains an opt-in slow pass for size experiments; `REPTAR_SKIP_WASM_OPT=1` is still honored.

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

It uses Bun 1.3.10 and .NET 10.0.401, caches Bun/NuGet, and runs commands sequentially to avoid the shared-build-state flake.

### 3.7 Worker JSON and WebSocket envelopes

The realtime smoke exposed a NetWasm-only failure chain:

- `UseRegisteredContexts()` attached a resolver to `ReptarJson.Options` that returned `JsonTypeInfo` instances owned by the app context. System.Text.Json rejects metadata bound to a different options instance ("JsonTypeInfo ... does not match").
- Adopting the app context's `Options` fixed that, but app contexts must use `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase)]`; otherwise camelCase WebSocket frames silently bind to nulls.
- Exported JS-interop handlers (`handleWebSocketOpen/Message/Close`) were deserializing with `ReptarJson.Options` directly. `ReptarJson.Deserialize<T>` now resolves the registered metadata explicitly and is used by the rooms worker.
- `FindTypeInfo` and `SourceGenJsonCodec` now ignore `object` metadata and resolve `value.GetType()` for serialization. This prevents polymorphic `object` metadata from asking an app context for an unregistered concrete type (the Postgres provider tests caught this).
- `[JsonSerializable(typeof(Dictionary<string, object?>))]` is still not required for D1 dictionary writes (see 3.8).

### 3.8 D1 dictionary inserts

`D1DbClient` has generic and non-generic `InsertAsync` overloads. The generic one was selected for `Dictionary<string, object?>`, so it serialized the dictionary as JSON and threw `NotSupportedException` under NetWasm. Both the generic and non-generic paths now detect `IDictionary` and map keys straight to columns. `UpdateAsync` received the same treatment. This unblocked the rooms write-behind persister (`messagesPersisted` now increments, history round-trips).

### 3.9 Durable Object assets

With `assets.binding = "ASSETS"`, a DO could fetch small metadata files but failed on the ~20 MB `RoomsWorker.wasm` ("browser artifact fetch was unsuccessful"). The loader patch from 3.5 removes the wasm fetch entirely, so the DO only reads `deployment.json`, `interop.json`, and the other small metadata assets. `wrangler.jsonc` declares the `CompiledWasm` rule, the worker depends on `wrangler` directly (instead of a stale global 4.99), and `public/.assetsignore` excludes the wasm from the asset upload.

---

## 4. New Findings / Fixes Outside the Original Audit

| Finding | Status |
| --- | --- |
| Storage refactor split `LocalStorage`/models but dropped `S3Storage`/`S3StorageOptions`; native build failed | Restored as `framework/Reptar/Core/Storage/S3Storage.cs` |
| `BlogUploadTests` expected `ArgumentException`; storage now throws `StorageException` | Updated test to the storage-layer contract |
| `docs:check:snippets` used bare PATH `dotnet` (10.0.103) and failed | Uses pinned SDK now |
| `bun e2e` cleanup pattern didn't match `wrangler-dist/cli.js` | Fixed + pre-start port reaping |
| NetWasm host-tools `wasm-opt` crashes on the module | Unoptimized module is under the 64 MiB script limit; optimization is opt-in |
| Optimized wasm failed `raw artifact integrity check` | Refresh deployment/loader hashes; skip the application asset fetch |
| `public/.assetsignore` hid the wasm the loader fetched | Loader now compiles the imported module and skips the fetch |
| `WorkspaceToolOptions` init-only properties made the configure callback unusable | Changed to settable properties |
| NetWasm context resolver returned metadata bound to another options instance | `UseRegisteredContexts()` adopts the app context's options |
| `object` metadata serialized polymorphically against the wrong resolver | `FindTypeInfo`/`SourceGenJsonCodec` skip `object` and resolve runtime types |
| D1 generic `InsertAsync<T>` serialized `IDictionary` as JSON | Both overloads map dictionaries to columns |
| DO `ASSETS.fetch` failed on the large wasm | Loader skips the application wasm fetch; the DO only reads small metadata assets |
| Rooms worker resolved a stale global wrangler 4.99 | Added `wrangler` to its devDependencies |

---

## 5. Still Open

1. Real `minimal-api` / `agent` templates (they now fail explicitly rather than silently scaffolding fullstack).
2. CI is written but not yet observed on GitHub Actions; the local equivalents are green, including the Postgres/MySQL/D1 provider matrix entry points.
3. The Wasm post-pass depends on the `netwasm.toolchain` package layout; a NetWasm upgrade should remove the workaround once its bundled optimizer handles reference types.
4. A full `bun reptar build:worker` clean build is slow (~40s) because the NetWasm compiler re-runs; acceptable for CI/deploy.
5. `bun reptar doctor --strict` still reports the PATH/pinned-SDK mismatch on developer machines that have an older PATH SDK; CI installs the pinned version, and `self-check` uses non-strict doctor.
6. The unoptimized Worker module is 38.4 MiB (4.5 MiB gzip). It is under the platform limit but above the 32 MiB optimization target; a minimal edge profile / working `wasm-opt` pass is the main Speed-release item.

---

## 6. Re-Run Commands

```bash
# Sequential in a shared checkout.
bun reptar test
bun reptar self-check
bun reptar test:realtime
bun scripts/test-scaffold.ts
bun e2e
bun reptar check:worker
bun reptar check:host
bun test packages/host/src
bun scripts/check-architecture.ts --strict
bun run docs:check:snippets
(cd apps/docs && bun run check:markdown && bun run build)
bun rpc:check
(cd packages/create-reptar && bun run check-types)

# Worker size / optimization (default: unoptimized, under the 64 MiB limit)
bun reptar build:worker
stat -f%z spikes/netwasm-worker/public/browser/HelloReptar.wasm
REPTAR_OPTIMIZE_WORKER=1 bun reptar build:worker   # slow, experimental -O1 pass
REPTAR_SKIP_WASM_OPT=1 bun reptar build:worker     # explicit skip (debug)

# Multi-app targeting
bun reptar inspect --app examples/blog --json
bun reptar db plan --app examples/blog --json
bun reptar codegen --app examples/blog
```

---

## 7. Final Verification (2026-10-26)

| Gate | Result |
| --- | --- |
| `bun reptar test` | **388 passed**, 0 failed |
| `bun reptar self-check` | **ALL GREEN** (7 stages) |
| `bun e2e` | **PASSED** (warm `/posts` p50 ≈ 3.5 ms, p95 ≈ 5.1 ms) |
| `bun reptar test:realtime` | **PASSED** (workerd + DO + D1 and Kestrel + SQLite) |
| `bun scripts/test-scaffold.ts` | **PASSED** |
| `bun reptar check:worker` | **38.91 MiB / 4.53 MiB gzip** (48 MiB hard ceiling, 32 MiB target warning) |
| `bun reptar check:host` | **PASSED** (application artifact uses the empty hash; every fetched artifact verified) |
| `bun test packages/host/src` | **16 passed** |
| `bun scripts/check-architecture.ts --strict` | **clean** |
| `bun run docs:check:snippets` | **PASSED** |
| `bun rpc:check` | **clean** |
| `cd packages/create-reptar && bun run check-types` | **clean** |
| `cd apps/docs && bun run check:markdown && bun run build` | **PASSED** (50 pages) |
