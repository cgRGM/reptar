# Switchyard: verified Reptar field notes

## Release-integration follow-up — 2026-10-26

The original observations below retain their baseline provenance. Combined-tree verification subsequently reached **396 passing framework tests**, **23 Bun tests / 73 assertions**, and a repeated **6-test / 265-assertion Linux Native AOT acceptance run**. Desktop/mobile Chromium verification also passed and now checks typed GET evaluation with `query.subject`.

- **S1/S2:** auth AOT and composite-migration fixes are included in integration. Published-native acceptance is now a CI gate.
- **S3:** `.WithQuery<EvaluateInput>()` now emits scalar query inputs for the client and OpenAPI. The frontend uses typed GET evaluation; metadata does not replace handler validation.
- **S4:** `.Produces<T>()` defaults to 200. The client has status-specific response bodies, no native `Response.json()` `any` leakage, and `never` JSON results for declared 204/205/304 responses. Undeclared auth/error metadata still needs author attention.
- **S5:** `inspect --db` now uses the requested database and `doctor` forwards it. Regression coverage was added.
- **S6/S7:** the safe AOT authoring lane and provider-specific transaction semantics remain genuine limitations. A passing framework provider matrix does not make Switchyard's interactive transaction portable to D1.

See the separate [controlled framework comparison](../../bench/results/release-20261026/README.md). The final confirmation does **not** demonstrate an overall throughput gain; database-backed cases measured up to 6.4% below the repaired baseline and deserve profiling. Do not compare those numbers directly with the older app benchmark below.

The original VPS lab services remain **stopped**, with source/data retained. Disposable release-verification containers were removed.

## Scope and provenance

This follows the supplied Switchyard and Relay design reviews, but replaces their uncompiled assumptions with runtime evidence where possible.

- Workspace: `remotedev:~/switchyard-lab/reptar`, an archive of commit **`2dd0871`** plus narrowly scoped fixes and this example. It is not the continuously changing shared worktree.
- Platform: Ubuntu Linux x64, two logical CPUs, approximately 3.74 GiB RAM, Intel Xeon Platinum 8259CL.
- Toolchain: Docker SDK **10.0.402**, Native AOT runtime **10.0.13**, Bun **1.4.4**. The host has no .NET SDK. Docker enables a reproducible native build without touching the existing applications.
- Final API image: `reptar-switchyard:lab`, digest `sha256:011d160ec2dd37064022747652317229beb426e559c94de04a1f31804171d261`, approximately **42 MB** uncompressed image content. An observed post-acceptance container memory sample was **17.35 MiB**, not a peak-memory claim.
- No existing application, shared root dependency file, or public firewall rule was changed. New example files and focused framework changes are returned to the shared checkout for review. A concurrent agent improved auth column-name normalization; that correction was preserved and re-tested.

The supplied reviews were useful test plans, not evidence that a defect existed in this checkout. In particular, the current baseline already has real SQLite read/write transactions, request-user memoization, composite primary keys, `Conflict`, and a source-generated JSON codec.

## What actually passed

| Evidence | Result |
| --- | --- |
| Native HTTP acceptance suite | **6 passed, 265 assertions**; also passes through `test-native.sh` against a newly built disposable native container |
| Concurrent publish | **24 requests → one 201, twenty-three 409s; exactly one version and audit row** |
| Transaction failure probe | Final audit insert intentionally fails; earlier version insert and pointer update roll back; JIT and Native AOT pass |
| Determinism | Five golden vectors match JIT/native; 104 runtime subjects match independent JS BigInt arithmetic, including Unicode |
| Auth | Native signup/signin, duplicate email, nested session JSON, signout and revoked-session behavior pass |
| Isolation | Other user gets 404 for flag list, history, audit, evaluation, draft update, and publish |
| Version semantics | Draft edits remain invisible until publish; stale drafts conflict; history retains earlier values |
| Cache semantics | Auth precedes GET revalidation; weak/list ETags accepted; 304 is empty; publish invalidates previous validator |
| Browser | Real Chromium account creation through live evaluation; desktop/mobile; no page exceptions or horizontal overflow |
| TypeScript | `tsc --noEmit` passes for frontend, server, test, and benchmark code |
| Contracts | Fresh JIT codegen matches all five committed files after the repository formatter |
| Framework suite | **319 passed, 0 failed** on the isolated snapshot plus auth fix |
| Migration planner regression | **2 passed, 7 assertions**, locally and on VPS |
| Schema lifecycle | Generated initial migration applies to a fresh database; subsequent plan returns `in_sync` |
| Scoped formatting/lint | Ultracite passes for the example and touched CLI files |

No test result here establishes D1/Worker, Postgres, NetWasm, or multi-process transaction behavior. Actual OAuth provider exchange, global-admin access, API-key authentication, and the full Better Auth lifecycle were not exercised in the native browser app; the existing JIT framework suite covers additional auth paths.

## Confirmed framework defects and fixes

### S1 — Built-in auth breaks in a real Native AOT app (high; fixed)

**Observed:** `/health` worked on the first native binary, but built-in `/api/auth/signup` returned 500. The first failure was missing SQL parameter values. Replacing anonymous parameter objects exposed a second error: `SQLite Error 1: 'near ")": syntax error'` from a generated insert with no surviving properties.

**Cause:** request-path SQL binding reflected over anonymous objects; auth's internal records also relied on reflection metadata surviving trimming. Decorating a record type with `DynamicallyAccessedMembers` did not make the unannotated object-based path safe. JIT success was not sufficient evidence.

**Returned fix:** `framework/Reptar/Auth/Auth.cs` now uses explicit parameter dictionaries, `IReptarEntity` implementations for internal write shapes, and explicit row mappers. Name normalization handles snake_case, PascalCase aliases, and camelCase provider keys without reflection.

**Regression caught during the fix:** the first direct mapper assumed snake_case column names. Two existing Better Auth tests failed because that code projects PascalCase aliases. The concurrent normalization correction resolves that; the full **319-test suite** then passed. This is why the fix was not stopped at the first successful signup.

**Coverage:** `framework/Reptar.Tests/AuthAotMappingTests.cs` protects explicit parameters and roundtripping. Switchyard's acceptance suite protects the actual native auth lifecycle. The app's own records are intentionally absent from the framework's internal auth mapper registrations.

**Remaining recommendation:** make a small published-native auth lifecycle test a framework CI gate. Do not suppress all trim warnings as a substitute. Longer term, wire source-generated row/write mappings into the default authoring lane so examples do not need to hand-maintain them.

### S2 — Migration planner emits invalid composite primary keys (high; fixed)

**Observed:** `db generate` produced an apparently valid initial migration, but `db migrate` failed with `table "sy_flags" has more than one primary key`. Runtime `EnsureCreatedAsync` had already created the same TableDef correctly.

**Cause:** compiler schema JSON marks every composite-key member as `primaryKey`. `scripts/diff.ts` interpreted each as a separate inline `PRIMARY KEY`, ignoring the table's key array.

**Returned fix:** emit one table-level `PRIMARY KEY (...)`, retain NOT NULL for composite members, and preserve reference `ON DELETE` behavior. Single-column keys remain inline.

**Evidence:** `scripts/diff-composite.test.ts` executes generated SQL, checks that different environments may share a flag key, rejects a duplicate composite key, verifies cascading delete, and confirms the next plan is in sync. The corrected `0001_initial.sql` was generated and migrated on the VPS rather than hand-edited to hide the planner bug.

## Confirmed limitations / DX findings

### S3 — Query reads do not generate query contracts (resolved with explicit metadata in integration)

`ctx.Req.QueryValue("subject")` does not produce a `query.subject` input in `AppType`. An agent can easily believe a generated client covers a route fully when it only covers path/body metadata.

**Example adaptation:** retain GET evaluation for ETags; add POST evaluation with a named `{ subject }` input for the typed frontend. No handwritten replacement for generated `AppType` was introduced.

**Recommended framework work:** explicit typed query metadata/validation with generated OpenAPI and client inputs. Query values are part of the public contract, not implementation detail.

### S4 — Route contracts require more author discipline than the DX pitch suggests (partly resolved in integration)

The first compile failed on seven `.Produces<T>()` calls: this baseline requires an explicit status. One correction made the app compile. This was an agent mistake, not a compiler defect; a compact signature reference would have prevented it.

The generator only knows declared response metadata. Several built-in auth routes still emit `unknown`; undeclared authentication/validation errors do not appear automatically. A GET 304 has no JSON body even though the generated route's output type remains `Evaluation` alongside status 304.

**Recommendation:** add metadata completeness diagnostics and document status-aware client usage. Never call `.json()` on 204/304. A generated contract is not automatically proof of exhaustive runtime behavior.

### S5 — Inspection needs explicit deployment configuration (`--db` defect fixed in integration)

`inspect --app examples/switchyard --db <active-db>` silently used the app-local default instead; `DATABASE_URL=<active-db>` selected the right database. `db plan --db` worked. `doctor` reported the absent host SDK correctly, despite successful Docker builds.

**Recommendation:** share database-target argument handling across inspection/doctor/planning, and identify whether state is inferred, compiler-exported, or observed from the active deployment. An agent should not mistake “files exist / connected empty DB” for verification of the running service. This example documents the working environment-variable invocation rather than editing the heavily shared main CLI file during concurrent work.

### S6 — The safe AOT lane is still too manual

The published app works with source-generated JSON, explicit parameter binding, and explicit row/write mappings. Those choices are discoverable from source, but the amount of boilerplate is noticeable in a small app. The native build also emits framework trim/AOT warnings, including reflection fallbacks and codegen-only paths. The working binary is not evidence that all such paths are safe.

**Recommendation:** generate app JSON and database mappings as part of normal build/codegen, and isolate build-time reflection from runtime assemblies. Show a verified native example as the primary onboarding path.

### S7 — Transactions are more capable than the older review assumed, but not universally portable

The current SQLite provider supports real read/write transactions. That made atomic publish straightforward and survived a forced final-write failure. The portable buffered transaction intentionally rejects reads; the D1 path cannot run this handler unchanged.

**Recommendation:** advertise provider capabilities/guarantees explicitly. “Same transaction method name” does not imply the same semantics. A future D1 version should use a separately tested atomic batch/CAS design, not a process-local lock or a silent non-atomic fallback.

### S8 — Source inspection still matters for agent work

The older Relay report lists some missing features already present. Current machine-readable inspection helps, but did not answer transaction semantics, auth trimming behavior, or query-contract completeness. The highest-confidence loop was: inspect signatures → compile → execute the native artifact → run protocol assertions → inspect logs → add a regression.

There is a built-in `json:gen` command in the baseline, but this example deliberately keeps a small explicit JSON context while testing the boundaries. It should not be inferred that JSON generation is absent from the project.

## App/tooling mistakes distinguished from framework defects

- A test originally compared serialized Unicode text byte-for-byte. .NET escaped α in valid JSON; the assertion was corrected to compare parsed values. Not a runtime bug.
- Bun fetch decompresses proxied responses. Forwarding the original `Content-Encoding` made the browser decompress twice; the example proxy now removes encoding/length headers. This is not a Reptar backend defect.
- An implicit range-input label attached to its preceding `<output>`; browser testing found it, and the HTML now uses an explicit `for` attribute.
- Publishing over a running bind-mounted Linux binary failed with `Text file busy`. The final deployment uses a newly built immutable image, avoiding in-place executable replacement.
- Docker-generated files were root-owned, which interrupted one source sync. Ownership was corrected only inside the isolated Switchyard workspace.
- No precise time-to-first-compile metric was instrumented. The report does not fabricate one. There was one initial API-signature compile correction, followed by separately observed native runtime failures.

## Benchmark results

Final run: **2026-10-26 10:34 UTC**, 16 concurrent closed-loop Bun clients on the same two-vCPU VPS, 100 warmup requests, approximately five seconds per scenario. API runs as a non-root Native AOT process in the chiseled image. Other VPS services were not stopped, CPU was not isolated, and no confidence interval is claimed.

| Workload | Requests/s | p50 | p95 | p99 | Unexpected statuses / transport errors |
| --- | --: | --: | --: | --: | --: |
| Health, unauthenticated, no DB | 7,657 | 1.94 ms | 3.59 ms | 5.73 ms | 0 |
| Evaluation, authenticated, DB-backed, uncached | 3,531 | 4.28 ms | 7.54 ms | 9.88 ms | 0 |
| Conditional evaluation, authenticated, DB-backed, 304 | 3,914 | 3.85 ms | 6.85 ms | 9.39 ms | 0 |

Raw summary: [results/benchmark.json](results/benchmark.json). The 304 path still authenticates, authorizes, and reads the active database state. These figures are useful as a repeatable app baseline, not evidence that Reptar beats ASP.NET, Hono, or another stack. Publish-race correctness is a separate test, not a throughput claim. Cold start, long-duration soak, mixed read/write load, saturation curves, and cross-target comparisons remain unmeasured.

## Assessment

### Framework

The core is substantive. This app has authenticated tenant-scoped state, explicit schema, immutable history, deterministic evaluation, real transaction rollback, and generated contracts in a small amount of application code. The native deployment is compact and the measured workload is credible for a small VPS. Switchyard exposed real bugs that a CRUD hello-world would miss.

The largest gap is **guarantee alignment**: runtime schema and migration generation diverged; JIT auth and AOT auth diverged; runtime query inputs and generated inputs diverged. These are more important than another peak-throughput headline.

### Developer experience

The `app.cs` composition and schema tokens are pleasant once the intended lane is known. Built-in auth and a generated client reduce integration work substantially. Real SQLite transactions are a meaningful strength.

The costly part was discovering which combinations are truly verified. AOT mapping boilerplate, metadata completeness, SDK selection, and provider semantics are still things an app author must understand. A canonical end-to-end example and capability matrix would be high-leverage improvements.

### Agent experience

Reptar is unusually promising for agent-built backends because the framework has inspection, schema planning, codegen, and a small authoring surface. However, agents need machine-readable uncertainty, not just machine-readable state. Clear status defaults, query metadata, explicit target capabilities, and a one-command published-artifact check would reduce guessing.

The fixed-snapshot remote workspace was essential with many agents editing the shared repo. Keep changes isolated, preserve other agents' corrections, and verify an immutable artifact rather than reporting a moving worktree as “tested.”

### Should it be an example?

**Yes, as a verified SQLite/Native AOT example and regression workload.** It covers a different failure surface from the existing task/outbox apps and demonstrates the typed frontend connection. It should not be presented as a verified portable Workers/D1 example until that implementation and its behavior tests exist.

## Recommended next work, in order

1. Complete release review of the integrated auth/migration fixes, typed query/status contracts, inspection fixes, and published-native CI gate.
2. Add metadata-completeness diagnostics for undeclared authentication/validation responses.
3. Profile the database-backed benchmark reductions before making performance-improvement claims.
4. Publish a target capability/semantics matrix; design and test a separate D1 publish adapter.
5. Default to generated JSON/DB mappings, with actionable diagnostics for reflection-dependent runtime code.
6. Extend the workload with multi-process concurrency, longer mixed-load runs, project roles, pagination, and deployed Worker parity before broadening claims.
