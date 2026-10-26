# Switchyard

A feature-flag control room and benchmark app for Reptar: **C# backend, generated TypeScript client, browser frontend, and persistent SQLite**. Built and exercised on `ssh remotedev`, rather than left as an uncompiled design.

## Verified scope

Tested on Linux x64 with .NET SDK **10.0.402**, Native AOT runtime **10.0.13**, and Bun **1.4.4**. The isolated framework baseline is commit **`2dd0871`**, plus the auth and migration-planner fixes described in [FINDINGS.md](FINDINGS.md). Local Bun **1.3.10** also passes the migration-planner regression tests.

- Native AOT API: six acceptance scenarios / **265 assertions**, including a 24-way publish race, tenant isolation, auth lifecycle, targeting, stable bucketing, immutable history, draft/live separation, and ETags.
- Forced final-write failure: version insert and active-pointer update roll back together. Verified on JIT and the native binary.
- Independent BigInt implementation checks runtime buckets for 104 subjects, including Unicode. Five golden vectors match JIT and Native AOT.
- Chromium: signup → create project → create flag → save draft → publish → evaluate. Desktop and mobile screenshots; no JS exceptions or mobile horizontal overflow.
- Framework regression suite: **319 passed** against the isolated snapshot with the auth fix.
- Migration regression tests: **2 passed**; generated initial migration applied to a fresh database; subsequent plan reports `in_sync`.
- TypeScript typecheck, scoped Ultracite checks, and regenerated-contract comparison pass.

**Not verified:** Workers/D1, Postgres, NetWasm, multi-process publishing, production TLS, or internet-facing deployment. This example deliberately uses SQLite's real interactive transaction; D1's write-only buffered transaction is not a drop-in substitute.

## Open the running VPS app

Both ports are loopback-only; no firewall or existing application was changed.

```sh
ssh -N -L 8796:127.0.0.1:8796 -L 8795:127.0.0.1:8795 remotedev
```

Open **http://localhost:8796**. Create your own account; no shared demo password is installed. The API is at http://localhost:8795.

VPS layout:

```text
~/switchyard-lab/reptar/                 isolated source snapshot + fixes
~/switchyard-lab/data/switchyard.db      persistent application database
~/switchyard-lab/*.log                  build, test, and benchmark evidence
```

- API container: `switchyard-api`, image `reptar-switchyard:lab`; Native AOT in the repository's chiseled runtime image, non-root, restart-unless-stopped.
- Frontend: user service `switchyard-web.service`; same-origin API proxy and bundled TypeScript client.
- Service template: [switchyard-web.service](switchyard-web.service), intentionally configured for this VPS workspace layout. The VPS currently has user lingering disabled; the frontend is supervised while the user manager is running, but unattended startup before login is not guaranteed. Enabling lingering is an operator decision; this exercise did not change that shared-account policy.

```sh
ssh remotedev 'docker logs --tail 30 switchyard-api'
ssh remotedev 'systemctl --user status switchyard-web --no-pager'
```

The HTTP-only lab sets `SWITCHYARD_INSECURE_LOCAL=1`. Do not expose that configuration publicly. Secure cookies are the default; public deployment additionally needs TLS, CSRF protection at the API, bounded/rate-limited public signup, and a restricted edge/proxy configuration. The development proxy rejects cross-origin browser mutations, but is not a production security boundary.

## Run from a checkout

From the repository root, with the repository's supported .NET SDK installed:

```sh
# Separate databases are recommended for schema/codegen, tests, and real data.
SWITCHYARD_DB=/tmp/switchyard-codegen.db bun reptar codegen --app examples/switchyard
SWITCHYARD_DB=/tmp/switchyard-codegen.db bun reptar codegen --app examples/switchyard --check

cd examples/switchyard
bun install --frozen-lockfile
SWITCHYARD_INSECURE_LOCAL=1 PORT=8795 dotnet run app.cs
# In another terminal:
bun serve.ts
```

If bare `dotnet` differs from the repository's pinned SDK, set `DOTNET_ROOT` and `PATH` to the pinned SDK first; `bun reptar doctor --json` explains this mismatch. The VPS used Docker instead of installing a host SDK.

Build the same native deployment from the repository root:

```sh
docker build -f deploy/Dockerfile \
  --build-arg APP_DIR=examples/switchyard \
  -t reptar-switchyard:lab .
mkdir -p /tmp/switchyard-data
# Override the image UID so the process owns this host-mounted development directory.
docker run --rm --name switchyard-api-local \
  --user "$(id -u):$(id -g)" \
  -p 127.0.0.1:8795:8795 -e PORT=8795 \
  -e SWITCHYARD_INSECURE_LOCAL=1 -e SWITCHYARD_DB=/data/switchyard.db \
  -v /tmp/switchyard-data:/data reptar-switchyard:lab
```

Publish into a new output directory/image rather than overwriting a running, bind-mounted ELF executable (`Text file busy` on Linux). The final VPS deployment uses an immutable image.

## Tests and benchmarks

A single published-artifact gate builds a native image, runs rollback/golden-vector probes, launches a disposable loopback container on a dynamically assigned port, typechecks, and runs the full HTTP suite. Its container and databases are removed on exit. This command was verified on the VPS:

```sh
# From repository root; requires Docker, Bun, and curl.
bash examples/switchyard/test-native.sh
```

With an existing API listening on port 8795:

```sh
cd examples/switchyard
bun run check-types
bun test acceptance.test.ts
# Install browser + OS dependencies once, then exercise the actual UI:
bun x playwright install --with-deps chromium
bun browser-smoke.ts
CONCURRENCY=16 DURATION_MS=5000 bun bench.ts
```

`SWITCHYARD_URL` overrides the test/benchmark API origin. `SWITCHYARD_WEB_URL` overrides the browser-test origin. Tests and benchmarks create uniquely named users and projects in the target database; run them against a lab database, not production.

Pure bucketing and transaction rollback checks, using a **fresh disposable database**:

```sh
SWITCHYARD_DB=/tmp/switchyard-probe.db dotnet run app.cs -- --self-test
# Or run --self-test on the published native image, with a writable DB mount.
```

From the repository root:

```sh
bun test scripts/diff-composite.test.ts
bunx ultracite check examples/switchyard scripts/diff.ts scripts/diff-composite.test.ts
```

[results/benchmark.json](results/benchmark.json) contains the final raw summary. These are same-machine, closed-loop measurements with 100 warmup requests and five seconds per scenario; they are **not** a controlled comparison against another framework. Both evaluation variants hit the database, including the 304 path. There is no in-process flag cache masquerading as an uncached result.

## Schema and migrations

The app calls `EnsureCreatedAsync` for convenient first-run demos. That is not an upgrade strategy. The committed [initial migration](migrations/0001_initial.sql) was generated by the corrected planner and applied to a fresh probe database.

```sh
# Run from repository root; specify the actual DB, not a guessed app-local default.
bun reptar db migrate --app examples/switchyard --db /absolute/path/switchyard.db
bun reptar db plan --app examples/switchyard --db /absolute/path/switchyard.db --json
DATABASE_URL=/absolute/path/switchyard.db bun reptar inspect --app examples/switchyard --json
```

At the tested baseline `inspect` does not honor `--db`, although `db plan` does; use `DATABASE_URL` for inspection. App runtime configuration is `SWITCHYARD_DB`, so tooling cannot infer that override. `doctor` correctly reports the absent VPS host SDK even though Docker builds work.

## Design

- Projects have an owner; that owner and configured global admins may access project resources. Other authenticated users receive 404. Set `SWITCHYARD_ADMINS` to comma-separated bootstrap email addresses **before creating those accounts**. This is not a project-member/editor role system.
- Each `(project, environment, flag key)` has a mutable draft, revision, and active-version pointer. Environments are `development`, `staging`, and `production`.
- Draft updates use an expected revision. Publish requires both expected revision and expected active version. Version creation, pointer update, and audit insert share one real SQLite transaction.
- Versions are immutable through the API. Histories/listings are bounded to 100 rows; keyset pagination is future work, not silently implied.
- Evaluation reads the active version with one join. Disabled overrides targeting; explicit subject targeting overrides percentage; otherwise a stable FNV-1a bucket selects the cohort.
- Bucketing hashes four **length-prefixed UTF-8 fields**. Percentage or version changes do not reshuffle subjects. This is deterministic cohorting, not a cryptographic or adversary-resistant assignment.
- GET evaluation supports private revalidation, weak/comma-separated ETags, and empty 304 responses. Auth runs before conditional handling. ETags include all decision fields.
- POST evaluation provides a fully typed `{ subject }` request because arbitrary `QueryValue` reads do not become generated query metadata.
- App DTOs use a source-generated JSON context and explicit database mappings. Built-in auth no longer depends on reflection surviving trimming.

### Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET/POST | `/api/projects` | List/create owned projects |
| GET/POST | `/api/projects/:projectId/flags` | List/create environment-specific flags |
| PUT | `/api/projects/:projectId/flags/:environment/:key/draft` | Revision-guarded draft |
| POST | `/api/projects/:projectId/flags/:environment/:key/publish` | Atomic immutable publish |
| GET | `/api/projects/:projectId/flags/:environment/:key/versions` | Recent versions |
| GET | `/api/projects/:projectId/audit` | Recent publish audit |
| GET | `/api/evaluate/:projectId/:environment/:key?subject=...` | Conditional evaluation |
| POST | `/api/evaluate/:projectId/:environment/:key` | Typed evaluation body |
| GET | `/health` | Runtime health |
| Various | `/api/auth/*` | Built-in Reptar auth |

The browser is intentionally framework-light TypeScript. All application API calls use `createClient<AppType>` from `@reptar/client`; types/contracts are generated, not handwritten. Its scoped tsconfig resolves the in-repo client without installing the entire monorepo. Fonts are optional Google Fonts assets with local fallbacks.
