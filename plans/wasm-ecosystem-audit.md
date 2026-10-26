# Wasm Ecosystem Audit: Rust, Go, Swift, and the Contract Opportunity

**Status:** research/audit — no framework code changed  
**Date:** 2026-10-26  
**Scope:** the claims in the pasted agent response, the current Reptar baseline, and the relevant upstream ecosystems.  
**Method:** read the local design/code, cloned the upstream repositories at pinned commits, read their source and official docs, and ran small local size checks. No controlled cross-language benchmark was run.  
**Related:** [`design.md`](../design.md), [`README.md`](../README.md), [`plans/master-audit-and-roadmap.md`](./master-audit-and-roadmap.md), [`plans/adonisjs-core-audit.md`](./adonisjs-core-audit.md).

---

## 0. TL;DR

The pasted response is directionally right about the opportunity and wrong or imprecise about several important mechanics. It also underestimates how much of its "ultimate goal" Reptar already has.

**The useful conclusion is not "copy Rust, Go, or Swift."** It is:

> Every mature Wasm ecosystem invests in three things: a generated FFI glue layer, a familiar app-facing API, and explicit boundary failure/background-work semantics. Reptar has the familiar API and a strong contract generator, but its host glue is hand-written, its boundary can hang on an exception, and its contract loop is manual and incomplete.

The highest-value work is therefore:

1. **Make the Worker boundary trustworthy** — never leave a request/queue/scheduled promise pending, and make `ctx.waitUntil` real.
2. **Generate or validate the host contract** — `interop.json` already knows the imports/exports; the host shim and C# entrypoint should not be hand-maintained independently.
3. **Finish the contract loop** — automatic codegen, `--check`, no `unknown` routes, typed query/form/status, generated JSON contexts.
4. **Measure size/startup** — the 25 MB `HelloReptar.wasm` is an `O0` artifact, not the 12 MB the README claims; Cloudflare's limit is now 64 MiB, so startup time is the real constraint.
5. **Do not pivot languages.** Rust/Go/Swift validate the architecture; they do not replace Reptar's differentiator (C# + dual target + generated contracts).

---

## 1. Evidence and caveats

### 1.1 Local baseline

Inspected (among others):

- `design.md`, `README.md`, `apps/docs/src/content/docs/overview/*`
- `framework/Reptar/Core/{Ctx,ReptarApp,RpcGenerator}.cs`
- `framework/Reptar.Wasm/*`, `packages/host/src/*`
- `spikes/netwasm-worker/*`, `spikes/realworld-worker/*`, `examples/realtime-rooms/worker/*`
- `packages/rpc/src/*`, `scripts/reptar.ts`, `package.json`

### 1.2 Upstream snapshots

| Project | Snapshot | Notes |
|---|---|---|
| `cloudflare/workers-rs` | `1d668cf` (2026-10-19), `worker` 0.8.7 | Official Cloudflare Rust SDK; active |
| `syumai/workers-go` | `a38be30` (2026-09-24) | Experimental; Go/TinyGo on Workers, Deno, Neon, browser |
| `swiftwasm/WasmKit` | `1d87ccd` (2026-10-14) | WebAssembly runtime written in Swift; not a compiler |
| `swiftwasm/swift` (SwiftWasm distribution) | `78c9933` (2026-02-03), snapshot 2026-08-30 | Community distribution; compiler changes upstreamed |
| `swiftwasm/JavaScriptKit` | current `main` at audit time | BridgeJS typed Swift↔JS bindings |
| `zion-sati/netwasm` | `9d6dbc0` (2026-10-26), 0.10.3 preview | Reptar uses 0.10.1 |
| Cloudflare docs | limits updated 2026-10-12; size changelog 2026-09-04 | 64 MiB uncompressed limit |

### 1.3 Caveats

- Size numbers below are **local artifact measurements**, not a controlled benchmark. They are useful for order-of-magnitude decisions only.
- "Workers-rs hello" is a bare handler; Reptar includes router, validation, auth, JSON, D1/R2, and more. The comparison is deliberately labeled unfair.
- Some upstream claims are from release notes/docs, not independently reproduced. They are marked as **reported** where appropriate.
- The audit did not modify the repository. The only new file is this document.

---

## 2. Claim-by-claim audit

Legend: ✅ accurate · 🟡 partly accurate / needs qualification · ❌ inaccurate or unsupported.

| # | Claim | Verdict | Correction / evidence |
|---|---|---|---|
| 1 | C# on Workers without runtime reflection is a major milestone | ✅ | Fair. Caveat: C# on Wasm is not new (Blazor, .NET WASI); the milestone is C# on Workers with NetWasm's reflection-free profile plus D1/R2/auth. |
| 2 | Rust has no GC and no runtime reflection by default | ✅ | True. Rust has `Any`/trait objects, but no tracing GC or C#-style runtime metadata. |
| 3 | `workers-rs` `#[event(fetch)]` translates "raw V8 request pointers" into `http::Request` | ❌ | It receives a `web_sys::Request` JS object and converts it through `wasm-bindgen`/`web-sys`; there are no raw V8 pointers. See `worker-macros/src/event.rs:116-152` and `worker/src/http/request.rs:24-49`. |
| 4 | Axum is supported via a workers-rs "adapter macro" | 🟡 | Axum is supported through the `http` + `axum` features and the same `#[event(fetch)]` macro; there is no separate axum adapter macro. |
| 5 | Serde gives "compile-time serialization" with no runtime metadata | 🟡 | Serde derives serialization code at compile time and avoids reflection, but serialization/deserialization still happens at runtime. `serde_json`/`serde-wasm-bindgen` are runtime codecs. |
| 6 | Go has a dual compiler ecosystem (standard Go vs TinyGo) | ✅ | True. Standard Go includes the GC/runtime and is larger; TinyGo uses LLVM and a reduced runtime. |
| 7 | TinyGo "strips out the standard runtime" | 🟡 | It strips/replaces much of it, but still has a GC, scheduler, and runtime. `net/http` support only arrived in TinyGo 0.42; reflection/`encoding/json` remain partial. |
| 8 | `workers-go` provides an explicit `js.Value` FFI bridge | ✅ | Internally true (`syscall/js`, `js.FuncOf`, generated runtime). The public API is idiomatic `net/http`; users rarely touch `js.Value`. |
| 9 | Go reflection-heavy JSON is a problem on TinyGo | ✅ | Documented by TinyGo: limited `reflect`; `encoding/json` is a common pain point. Standard Go is fine. |
| 10 | Swift has Embedded Swift and SwiftWasm | ✅ | Official Swift 6.2+ ships Wasm SDKs; Embedded Swift is an experimental size-focused subset. SwiftWasm is a community distribution whose compiler changes were upstreamed. |
| 11 | SwiftWasm and WasmKit are "toolchains" to study | ❌/🟡 | WasmKit is a **runtime**, not a compiler/toolchain. SwiftWasm is a distribution/toolchain. Conflating them leads to the wrong comparison. |
| 12 | Swift uses static compilation and ARC, not tracing GC | ✅ | True. Full Swift still carries runtime/metadata; Embedded Swift strips much of it. |
| 13 | WasmKit shows how Swift optimizes syscall abstractions in Wasm | 🟡 | WasmKit implements WASI syscalls as a host runtime; it is a reference for a host/syscall layer, not for a Swift guest's FFI. |
| 14 | Full-stack schema-driven contract generation is the right long-term goal | ✅/🟡 | Directionally right, but Reptar already implements most of it. The gap is completeness/automation, not a new direction. |
| 15 | Reptar uses "Roslyn-generated token schemas" for the whole system | 🟡 | `ColumnRef<T>` and entity mappers are Roslyn-generated. The RPC contract generator (`RpcGenerator`) runs the app and uses `System.Reflection` at codegen time. |
| 16 | One C# file defines API, DB layout, and rules | 🟡 | Reptar is file-based C#, but a real app is `app.cs` + `schema/*.cs` + `features/*.cs`; the design explicitly separates these. |
| 17 | It compiles to a 10 MB Native AOT binary | 🟡 | True for the SQLite-only profile (10.4–10.9 MB). PostgreSQL/MySQL builds are ~17–18 MB; the 10 MB figure is a floor, not a universal. |
| 18 | It cross-compiles "identically" to NetWasm | 🟡 | Same app source, but target-specific `#if NETWASM`, host adapters, and feature differences. The deployment-target matrix explicitly documents different capabilities. |
| 19 | It instantly spits out a TypeScript RPC client | 🟡 | The generator exists (`bun reptar codegen`), but it is a manual `dotnet run --codegen` step; no watch, no stale `--check`; 10 endpoints currently emit `unknown`. |
| 20 | It is the "fastest" type-safe full-stack micro-framework | ❌/unproven | The repo's benchmarks show parity/competitive CRUD performance, not a universal "fastest" claim. Avoid marketing language that the data does not support. |
| 21 | The binding constraint is the 10 MB Workers limit | ❌ | As of 2026-09-04, Workers allows **64 MiB uncompressed** on Free and Paid; compressed limits were removed. Startup time (1 s) and memory (128 MB) are the real constraints. |
| 22 | Poem is a Workers/Wasm framework to study | ❌ | Poem 3.1.x is a Tokio/hyper server framework; no Workers/Wasm adapter was found. The supported Rust-on-Workers path is `workers-rs` + axum. |

### 2.1 What the response got right

- **The ecosystems are the right ones to study.** Rust and Go are the closest structural analogues; Swift is a useful size/bridge reference even though it is not a direct Workers target.
- **The strategic direction is right.** The differentiator is developer experience and contracts, not merely "C# can run on Wasm."
- **The "one source, two targets, generated client" story is real.** Reptar already has most of it; the response correctly identifies it as the compelling long-term goal.
- **The emphasis on zero reflection and compile-time work is aligned with Reptar's constraints.** Source generation, AOT, and Wasm size are the right lens.
- **The milestone is significant.** C# on Workers with D1/R2/auth is not a trivial port, even if C# on Wasm itself is not new.

### 2.2 What it missed

- **Reptar already has the contract generator.** The gap is completeness/automation, not a greenfield build.
- **Codegen is runtime reflection, not Roslyn compile-time extraction.** The agent's "compiler knows the layout" claim is aspirational.
- **The Worker boundary has reliability gaps.** A thrown handler can hang the host; `WaitUntil` is not wired.
- **The size story is stale.** `HelloReptar` is 25 MB at `O0`; Cloudflare's limit is now 64 MiB, so startup is the constraint.
- **Swift is not a direct Workers target.** WASI P1 only, no P2; WasmKit is a runtime, not a toolchain.
- **"Fastest" is unproven.** The benchmarks show competitive CRUD performance, not a universal title.

---

## 3. What each ecosystem actually does

### 3.1 Rust / `workers-rs`

**Architecture**

- `worker-sys` holds low-level JS/Workers bindings.
- `worker` is the user-facing crate: `Request`, `Response`, `Env`, `Context`, `Router`, D1/KV/R2/Queues/DO/AI, etc.
- `worker-macros` provides `#[event(fetch)]`, `#[event(queue)]`, `#[event(scheduled)]`, `#[durable_object]`.
- `worker-build` builds the Wasm, runs `wasm-bindgen`, bundles/minifies the JS, and runs `wasm-opt`.

**The fetch macro**

`#[event(fetch)]` wraps the user function in an async `wasm-bindgen` export. The generated glue:

1. receives a `web_sys::Request`, `Env`, and `Context`;
2. converts the JS request to `worker::Request` or `http::Request<worker::Body>`;
3. awaits the user handler;
4. converts the response back to a JS `Response`;
5. returns a Promise to JavaScript.

This is not a raw-pointer ABI. It is a generated JS↔Wasm bridge with copies for strings/arrays and JS object handles for objects.

**Axum**

With the `http` + `axum` features, the handler can accept `http::Request<worker::Body>` and return `http::Response<axum::body::Body>`. Axum's router then runs inside the Worker. This is the closest thing to "bring your own framework" in the Rust Workers ecosystem.

**Async**

`wasm-bindgen-futures` maps JS Promises to Rust futures and Rust async exports to JS Promises. `worker-build` uses `spawn_local`; there is no Tokio/threaded runtime on `wasm32-unknown-unknown`.

**Failure handling**

- Rust `Result` maps to an error response.
- `worker-build --panic-unwind` (nightly, `build-std`) catches panics and can reinitialize the Wasm instance after an abort. This is **opt-in**, not default.
- Without it, a panic can terminate the instance.

**Other useful primitives**

- `ctx.wait_until()` for background work.
- `worker::signals::is_near_cpu_limit()` for cooperative backoff.
- `worker::query!` is a convenience macro for D1 prepare/bind; it is **not** a compile-time SQL verifier.
- Experimental Workers RPC and WIT codegen; not production-ready.

**What to learn**

- Generated glue is the norm; hand-maintaining both sides is the exception.
- Async exports should be awaited; the host should see completion/failure, not a fire-and-forget void.
- A standard HTTP type adapter (axum) is how an ecosystem plugs in.
- Background work and CPU-budget signals belong in the runtime, not in app code.

**What not to copy**

- The `wasm-bindgen` dependency/JS-glue complexity and `Send` wrappers are consequences of Rust's model; C#/NetWasm can stay simpler.
- Rust's ecosystem is not batteries-included: no built-in schema/validation/auth/contract generation comparable to Reptar.

**Measured size (local, raw `cdylib`, no `worker-build`/`wasm-opt`):**

| Artifact | Raw | gzip -9 |
|---|---:|---:|
| `workers-rs` hello-world | 916 KB | 211 KB |
| `workers-rs` router example | 837 KB | 221 KB |

These are not apples-to-apples with Reptar, but they show the runtime floor of a Rust Worker.

---

### 3.2 Go / TinyGo / `workers-go`

**Architecture**

- Public API: `workers.Serve(http.Handler)`.
- `handler_js.go` registers a JS-callable `handleRequest` via `syscall/js`.
- `internal/jsutil.RegisterAsyncHandler` returns a JS Promise; the handler runs in a goroutine; a panic is recovered and rejects the Promise.
- `//go:wasmimport workers ready` is a handshake so the JS runtime knows the Go program is initialized.
- `workers-assets-gen` emits `wasm_exec.js`, `runtime.mjs`, and `worker.mjs`.
- Background tasks are tracked (`WaitBackgroundTasks`) so the program does not exit while `waitUntil` work is pending.

**The important design choice**

The FFI is explicit and low-level internally, but the app author writes ordinary `net/http`. This is why workers-go feels like Go, not like a JS bridge.

**Generated bindings**

`exp/cloudflare/*` is mechanically generated from `@cloudflare/workers-types`:

```
workers-types .d.ts
  → extracted IR (committed)
  → cfgen + per-package overrides
  → generated Go bindings
  → CI `gen-bindings-check`
```

This is the strongest pattern in the Go ecosystem for Reptar: a committed IR, a generator, and a CI drift check. Hand-written L2 wrappers add Go-idiomatic errors/options on top of generated L1 bindings.

**Go vs TinyGo**

- Standard Go: full stdlib, best compatibility, larger Wasm (our `net/http` hello: 4.6 MB raw / 1.41 MB gzip).
- TinyGo: much smaller, but partial `reflect`, `encoding/json` pain, and `net/http` only from 0.42. Still has a GC/runtime.
- workers-go recommends standard Go when a package fails under TinyGo.

**What to learn**

- Hide the FFI behind a familiar interface.
- Generate bindings from the platform's TypeScript types, and check them in CI.
- Track background work; do not let the guest exit while promises are pending.
- Recover panics at the async boundary and reject the Promise.
- Keep the host runtime portable (Workers, Deno, Neon, browser).

**What not to copy**

- The project is explicitly experimental; D1/DO/Workflows/RPC are alpha/experimental.
- Go's reflection-heavy stdlib is a poor fit for a size-constrained Wasm target; Reptar already avoids reflection.
- No built-in API contract/client generation.

---

### 3.3 Swift

**What is actually true**

- Swift 6.2+ has official Wasm SDKs distributed by swift.org.
- Embedded Swift is an experimental subset that can produce much smaller binaries (official docs: "multiple orders of magnitude smaller").
- SwiftWasm is a community distribution; its compiler/stdlib changes were upstreamed, and the official SDKs are now the main path.
- Swift uses ARC, not a tracing GC.
- WasmKit is a **WebAssembly runtime written in Swift**, included in Swift toolchains since 6.2. It is not a compiler or a Swift-to-Wasm toolchain.
- JavaScriptKit's BridgeJS generates typed Swift↔JS bindings and `.d.ts` declarations from `@JS` annotations (or Swift bindings from a TypeScript `.d.ts`).

**Why Swift is not a direct Workers target**

- The official Swift Wasm SDKs target WASI.
- Cloudflare Workers' documented WASI support is experimental and Preview-1-era; no Preview 2 / Component Model support was found.
- A Swift Wasm module on Workers would need a JS/WASI shim and a `wasm32-unknown-unknown`-style bridge; it is not a drop-in target today.
- WasmKit is useful as a host/runtime reference (WASI syscalls, Component Model), not as the guest FFI layer.

**What to learn**

- **Size tiers:** full Swift SDK vs Embedded Swift. Reptar should offer a minimal profile (no AI/agent/Stripe/LINQ) and measure it.
- **BridgeJS:** a single annotated interface can generate both sides plus `.d.ts`. This is the best model for Reptar's host contract.
- **Toolchain pinning:** `swift sdk install` versions the SDK; Reptar's `.reptar/sdk` does something similar but should verify/record the exact version.
- **Async/throws mapping:** generated glue handles async and thrown errors at the boundary.

**What not to copy**

- Swift-on-Workers as a target now; the WASI/JS mismatch is real.
- The WasmKit-as-toolchain comparison; it is a runtime.

---

### 3.4 Contract-generation peers

| Model | How it works | Strength | Limitation |
|---|---|---|---|
| **tRPC** | TypeScript type inference; no codegen | Instant IDE feedback; TS-only monorepo | TS-only; no language-neutral contract; no OpenAPI by default; runtime validation is separate |
| **Hono RPC** | `AppType = typeof route`; `hc<AppType>` | Type inference, typed status, `$url/$path`, custom fetch | TS-only; known IDE/type-instantiation performance issues at scale |
| **Encore** | Static analysis of annotated Go/TS functions; generates clients/docs/infra | Closest to "compiler knows the system"; language-level schema | Platform/framework lock-in; not C#/Wasm/edge |
| **Reptar** | C# route metadata + `RpcGenerator`; AppType, types, Standard Schema, OpenAPI, schema.json | Language-neutral backend, dual target, zero-reflection runtime, batteries included | Codegen is a manual runtime reflection pass; incomplete metadata; no watch/check |
| **OpenAPI/GraphQL/WIT-first** | Separate IDL → generated server/client | Language-neutral, mature tooling | Schema drift; duplicate source of truth; less ergonomic than code-first |

**Reptar's real position:** it combines code-first C# with generated language-neutral contracts and a zero-reflection runtime. That is rare. The missing piece is not the idea; it is the **contract loop** (automatic, complete, checked).

---

## 4. Reptar baseline: what is already true

The pasted response talks as if the full-stack contract generator is a future goal. Most of it exists:

- **One app source, two targets:** the same `examples/blog` schema/features compile to `net10.0` (Kestrel/AOT) and `netwasm0.1` (Workers). See `design.md` §3 and the deployment matrix.
- **Generated contracts:** `RpcGenerator` emits `app-type.d.ts`, `types.ts`, `schemas.ts`, `openapi.json`, `schema.json`; `@reptar/client` provides `createClient<AppType>`.
- **Standard Schema:** `Rule<T>` → zero-dependency `~standard` validators.
- **Zero-reflection hot path:** source-generated JSON, entity mappers, `ColumnRef<T>`, D1 row mapping.
- **Built-in auth/bindings:** D1, R2, KV, Queues, AI, DO, Stripe, JWT, cookies.
- **RealWorld/Conduit:** passes the official Hurl suite on SQLite, PostgreSQL, Native AOT, local Wasm, and Cloudflare + D1 (per `design.md` §16).

This is why the "ultimate goal" framing is too late. The question is how to make the existing loop **complete and trustworthy**.

---

## 5. Findings, ranked

### P0 — Reliability: the Worker boundary can hang

**5.1 C# exceptions can leave the host promise pending.**

`spikes/netwasm-worker/csharp/Program.cs:66-70` exports a **synchronous void** `Handle` that starts `HandleAsync` fire-and-forget. `HandleAsync` has no top-level `try/catch`; if `App.HandleAsync` throws, `Respond` is never called and the host's `pending` promise never resolves (`packages/host/src/host.ts:877-904`).

`spikes/realworld-worker/csharp/Program.cs:73-93` does catch and respond 500. `examples/realtime-rooms/worker/csharp/Program.cs:173-221` does not. The behavior is app-specific, which is exactly backwards: the framework should guarantee it.

The same pattern exists for queue and scheduled events (`host.ts:946-1046`): a thrown C# exception can leave the host awaiting forever.

**Impact:** a single unhandled exception can hang a request, a queue batch, or a cron invocation until the client disconnects or the platform kills it. This is a production-blocking reliability bug.

**5.2 `Ctx.WaitUntil` is not wired to Cloudflare's `ctx.waitUntil`.**

`framework/Reptar/Core/Ctx.cs:62-95` collects background tasks. `framework/Reptar/Core/ReptarApp.cs:883-896` runs them with `Task.Run` and ignores the result. The host has no `waitUntil` import, and the Worker entrypoint does not receive/pass `ExecutionContext` (`spikes/netwasm-worker/src/index.ts:164-267`; `packages/host/src/host.ts:54-60`).

The docs say the opposite:

- `apps/docs/src/content/docs/core/context.md:121`: "Passes the promise to `executionCtx.waitUntil(promise)`."
- `apps/docs/src/content/docs/overview/deployment-targets.md:49`: "Edge `ctx.waitUntil()` bridge."

They are false today. Worse, `scripts/check-netwasm.ts:71-78` explicitly warns that `Task.Run` drain loops freeze between events on Workers.

**Impact:** background work can be silently canceled after the response; the API promises durability it does not provide.

**5.3 No host-side timeout.**

`host.ts` waits indefinitely for `respond`/`respondQueue`/`respondScheduled`. There is a 30 s safety net for WebSocket events (`host.ts:791-810`), but none for HTTP/queue/scheduled. A deadlocked handler holds the request until the client disconnects.

**5.4 The C# host entrypoint is hand-written per app.**

`HelloReptar` (252 lines), `RealWorld` (175 lines), and `Rooms` (311 lines) each duplicate `HostRequest`, `HostResponse`, cookie parsing, `JsonSerializerContext`, and `Respond` plumbing. This contradicts `design.md` §3 ("the shim is generated, nobody writes it") and creates drift.

`interop.json` already contains the exact import/export signatures (`spikes/netwasm-worker/public/browser/interop.json`), but nothing checks that `app.host.mjs` matches it. A missing/renamed host function fails at Wasm instantiation, not at build time.

**Impact:** app authors write infrastructure; errors are discovered at runtime; every app can diverge.

### P1 — Contract loop: generated, but manual and incomplete

**5.5 Codegen is a runtime reflection pass, not compile-time extraction.**

`RpcGenerator.cs:1` uses `System.Reflection`; `examples/blog/app.cs:39-46` runs the app with `--codegen`; `scripts/reptar.ts:813-843` invokes `dotnet run`. The app's *runtime* is reflection-free, but the *contract generation* is not compile-time. The agent's "Roslyn-generated token schemas / compiler knows the layout at compile time" is only true for `ColumnRef<T>` and entity mappers.

This is not necessarily bad — runtime metadata is a pragmatic way to handle fluent lambdas — but the claim and the docs should be precise. If Reptar wants compile-time extraction, it needs a more declarative route API or a Roslyn analyzer that understands a restricted subset.

**5.6 Codegen is manual; no watch, no stale check.**

`package.json:61` defines `rpc:check` as `tsc --noEmit`, not a generated-file freshness check. `design.md` §8 promises "`--check` fails CI on stale generated files"; it is not implemented. `bun reptar dev` does not run codegen. Generated files are committed, so drift is easy.

**5.7 The generated client is not complete.**

`packages/rpc/src/app-type.ts` has 10 `unknown` response types (auth API keys, social, change-password, sessions, etc.). Root cause: those routes lack `.Produces<T>` metadata. The generator cannot infer a handler's return type from arbitrary C# lambdas, so metadata is mandatory — but nothing enforces it.

`packages/rpc/src/client.ts:49-53` only types `json`, `param`, and a generic `query`. There is no `form`, no per-route query schema, no per-status response narrowing, no `$url`/`$path`, and no `InferRequestType`/`InferResponseType` equivalent. `apps/docs/src/content/docs/client/codegen.mdx:87` claims "full type inference for ... query strings"; the implementation does not support that.

**5.8 JSON contexts are manual.**

Every app maintains a `JsonSerializerContext` list of DTOs. A missing entry works on Kestrel (default resolver) and fails under NetWasm/AOT. The existing `check:netwasm` catches anonymous/dictionary JSON but not an unregistered record. A generator/analyzer should emit the context from route metadata and fail the build on gaps.

### P2 — Size/performance: the numbers in the README are stale

**5.9 `HelloReptar` is 25 MB, not 12 MB.**

`spikes/netwasm-worker/csharp/HelloReptar.csproj:8` sets `<NetWasmOptimization>O0</NetWasmOptimization>`. The committed artifact is ~25 MB raw, 2.89 MB gzip, 685 KB brotli. `wrangler deploy --dry-run` reports **27,137.79 KiB** total upload / 3,038.71 KiB gzip.

For comparison:

| Artifact | Raw | gzip -9 | brotli 11 |
|---|---:|---:|---:|
| `HelloReptar.wasm` (O0) | 25 MB | 2.89 MB | 685 KB |
| `RealWorld.wasm` | 9.8 MB | 1.17 MB | 534 KB |
| `RoomsWorker.wasm` | 12 MB | — | — |

Cloudflare now allows 64 MiB uncompressed, so the size limit is not the binding constraint. The 1 s startup budget and memory are. The README/design "~10–12 MB" claim is stale for `HelloReptar`; `RealWorld` is the closer data point.

**5.10 The body envelope is base64 + JSON.**

`host.ts` reads `request.arrayBuffer()`, base64-encodes it, and puts it in a JSON envelope (`host.ts:871-895`). C# decodes base64 (`Program.cs:95-98`), and the response is base64-encoded again (`Program.cs:107-119`). This is a 33% size overhead plus multiple copies, and it is worst for large uploads/downloads.

NetWasm 0.10.3 adds zero-copy `Span<T>`/`ReadOnlySpan<T>` JS imports and direct scalar imports. A binary response path (`respondBytes`) or a two-part metadata+body envelope would avoid base64.

**5.11 Reptar is on NetWasm 0.10.1; 0.10.3 has relevant improvements.**

- 0.9.0: `Task` exports return Promises; async exports accept strings/number arrays/JSObject.
- 0.10.1: async `Main`; faster code/smaller runtime.
- 0.10.3: direct scalar `JSImport` (reported 2,281 ns → 101 ns), span views, library-level `[JSExport]`, WIT bindings, smaller page bundle.
- `HelloReptar` still uses a sync void export and a manual `respond`; upgrading could let the host await completion/errors and could move the entrypoint into the framework library.
- NetWasm is still pre-1.0, single-maintainer, and under the Community License (free under 250 employees / $10M revenue). The Native AOT fallback remains important; pin versions and avoid depending on unreleased behavior.

**5.12 `O0` is unexplained technical debt.**

The commit that added `O0` (`65e608a`) also grew the committed Wasm from ~13 MB to ~23 MB. There is no recorded reason in the repo. It may be a workaround for a Binaryen/optimizer failure, but that should be documented and either fixed or bounded.

### P3 — Strategic opportunities

**5.13 Typed Workers RPC / service bindings are not exposed.**

Cloudflare Workers RPC supports methods, streams, stubs, and promise pipelining. Reptar's contract generator could eventually emit typed clients for service bindings/Durable Objects, not just HTTP routes. This is a natural extension of "the backend is the contract," but it is a larger project and should follow the P0/P1 work.

**5.14 WIT/Component Model is a design reference, not a Workers deployment target.**

NetWasm 0.10.3 can generate reflection-free C# from WIT and emit WASI P2 components. Workers does not support WASI P2 components today. So WIT is useful for formalizing an internal host contract (capabilities, imports, types), not for shipping a component to Workers.

**5.15 Multi-runtime host portability is possible but not free.**

workers-go runs the same `http.Handler` on Workers, Deno, Neon, and the browser. Reptar's `ReptarApp` is already host-agnostic; only `@reptar/host` is Cloudflare-specific. A `HostRuntime` abstraction could enable Deno/Node/Vercel, but it is a separate expansion, not the next milestone.

**5.16 Scope risk.**

The repo already has a framework, auth, AI agent engine, BaaS, realtime rooms, and a marketing site. The external audit's strongest message is that the Wasm platform layer — host reliability, contracts, size/startup — is the bottleneck. Adding another language target or more surface area would dilute the advantage.

**5.17 Observability is mostly aspirational.**

The docs describe structured JSON logs, `db_ms`/`db_queries`, `cf_ray`/`colo`, `trace_id`, Server-Timing, and OTLP/Axiom export (`apps/docs/src/content/docs/observability/telemetry.md`). The implementation is much smaller:

- `Middlewares.Telemetry()` logs one interpolated string: `"{requestId} {method} {path} -> {status} ({ms}ms)"` (`framework/Reptar/Core/Middleware.cs:155-165`).
- `TelemetryOptions` only has `Enabled` (`framework/Reptar/Core/ReptarApi.cs:65-68`); `Axiom()` returns an empty options object (`ReptarApi.cs:94`).
- There is no `traceparent` parsing/propagation, no `db_ms`/`db_queries` counters, no `cf_ray`/`colo` extraction, and no OTLP exporter.
- `Server-Timing` is added by `RequestId()` (`Middleware.cs:41-54`), not by `Telemetry()`, and only reports total Reptar time.
- `IAppLogger` is a string-only interface (`framework/Reptar/Data/Services.cs:174-186`).

**Impact:** the framework cannot currently answer the first three production questions — which route is slow, which dependency caused it, and what happened to this specific request — with the data the docs promise. This is a direct hit on the user's "observable" goal.

**Recommendation:** do not build an APM. Build **one structured request event** and a **host-owned exporter**. Details below.

---

## 6. Cross-cutting lessons

| Theme | Rust | Go | Swift | Reptar today | Action |
|---|---|---|---|---|---|
| **FFI glue** | `wasm-bindgen` + `worker-build` generated | `syscall/js` + generated runtime/bindings | BridgeJS generated Swift/TS | Hand-written `app.host.mjs` + C# entrypoint | Generate/validate from `interop.json` or a shared contract |
| **App-facing API** | `http`/axum | `net/http` | `@JS` macros / JavaScriptKit | `Ctx`, `DbClient` | Keep idiomatic; hide the bridge |
| **Async boundary** | Async export returns Promise | Async handler returns Promise | async/throws mapped | Sync void + manual `respond` | Use async `Task` export (void) + `respond`; await in host |
| **Error boundary** | `Result`; opt-in panic recovery | Panic recovery in async handler | throws → rejected Promise | No top-level catch in some apps | Always catch and respond; add timeout |
| **Background work** | `ctx.wait_until` | tracked background tasks | async tasks | `Ctx.WaitUntil` not wired | Use the async export promise as the `ctx.waitUntil` handle (or a token-based import) |
| **Typed bindings** | Serde/wasm-bindgen | Generated from workers-types | BridgeJS `.d.ts` | Manual `[JSImport]` | Generate/validate host bindings |
| **Size** | `wasm-opt`, strip, LTO | standard vs TinyGo | full vs Embedded Swift | O0/25 MB | Restore optimization; feature profiles; measure startup |
| **Contracts** | none built-in | none built-in | JS interop only | AppType/OpenAPI/Standard Schema | Complete the contract loop |

**The pattern:** every mature ecosystem treats the boundary as generated infrastructure, not app code. Reptar's boundary is still app code.

### 6.1 The feature filter

For the next cycle, judge every candidate against five questions:

1. Does it remove work from the hot path (CPU, copies, startup, memory)?
2. Does it prevent a class of production failure?
3. Does it make a production failure diagnosable in one request?
4. Is it build-time/generated, or can it be opt-in with zero overhead when disabled?
5. Does it add a new concept to the app author's mental model?

A feature that answers fewer than three should wait. This filter deliberately rejects "more framework surface" and favors the runtime spine.

### 6.2 Recommended core (small, high-leverage)

| Candidate | Borrowed from | Perf | Reliability | Observability | Bloat | Verdict |
|---|---|---|---|---|---|---|
| Async export + boundary catch + timeout | Rust, Go | + | +++ | + | none | **Do now** |
| Real `ctx.waitUntil` + background-task events | Rust, Go | + | +++ | ++ | none | **Do now** |
| Host contract validation/generation from `interop.json` | Rust, Go, Swift | + | ++ | + | low | **Do now** |
| One structured request event + W3C `traceparent` | Rust `tracing`, Encore | + | ++ | +++ | low | **Do now** |
| Codegen `--check`/watch + `.Produces` enforcement | Encore, Hono | + | ++ | + | none | **Do now** |
| Trap recovery / instance re-init | Rust | + | +++ | + | low | **Do now** |
| Typed query/form/status + `$url`/`$path`/`Infer*` | Hono | + | + | + | low | **Do next** |
| Generated `JsonSerializerContext` | Encore | + | ++ | + | none | **Do next** |
| Binary body path / `Span<byte>` | NetWasm 0.10.3 | +++ | + | + | medium | **Do next** |
| Feature profiles / trimming | Swift Embedded, TinyGo | +++ | + | + | low | **Do next** |
| Request cancellation (`ctx.RequestAborted`) | Go, Rust | ++ | ++ | + | low | **Do next** |
| Typed Workers RPC/service bindings | Cloudflare RPC | + | + | + | high | **Later** |
| Event/job contracts (queue/cron/WS) | Cloudflare | + | + | + | medium | **Later** |
| Full `@cloudflare/workers-types` generation | Go | 0 | + | 0 | high | **No — partial only** |
| WIT/Component Model target | NetWasm/WasmKit | 0 | 0 | 0 | high | **No — reference only** |
| Own APM/dashboard/log store | — | — | — | + | high | **No** |
| ORM/DI/controllers | .NET | — | — | — | high | **No** |
| Multi-runtime host | workers-go | 0 | + | 0 | high | **Later** |

### 6.3 Runtime invariants worth enforcing

These are the "definition of done" for the spine:

**Reliability**

1. Every HTTP/queue/scheduled/WebSocket entrypoint returns a response/ack or a typed error; no pending promise is left behind.
2. Every non-streaming host await has a timeout; a timeout returns a typed 504/500, not a hang.
3. Every background task is attached to `ctx.waitUntil` (or the export promise passed to it).
4. Every Wasm trap/abort recreates the instance on the next request; a rejected `open()` is not cached forever.
5. Every request has `request_id` and a W3C `traceparent`; errors carry both.

**Performance**

1. No base64 on the body hot path; large bodies use a binary/span path or a presigned direct-to-R2 path.
2. No `Task.Run` on NetWasm; no per-request Wasm instantiation; no runtime reflection.
3. Telemetry is zero-allocation when disabled; sampling is host-controlled.
4. A minimal edge profile exists and is measured; size/startup are CI budgets, not README claims.
5. p50/p95 overhead over Hono for plaintext/JSON CRUD is measured on every release.

**Contracts**

1. Every route has `.Produces<T>` metadata; `AppType` contains zero `unknown`.
2. Codegen runs in `dev`/`build`; `--check` fails CI on stale artifacts.
3. JSON contexts are generated from route metadata; unregistered DTOs fail the build.
4. Query/form/status/error types are generated, not hand-written.
5. The host bridge and C# entrypoint are validated against `interop.json`.

---

## 7. Candidate plan (for discussion, not started)

### Phase 0 — Guardrails (1–2 days, low risk)

- Add a **Worker boundary test suite**: malformed JSON, handler throws, queue handler throws, scheduled handler throws, `WaitUntil` work. Assert a response/ack always arrives.
- Add a **host contract check**: parse `interop.json` and verify every import/export exists in `app.host.mjs`/`@reptar/host`; fail CI on drift.
- Add a **codegen `--check`**: regenerate to a temp dir and compare; fail if committed artifacts are stale.
- Add a **size/startup budget**: run `wrangler deploy --dry-run` and record `Total Upload`; measure `startup_time_ms` where possible.
- Add **one structured request event** (request_id, trace_id, route template, status, duration, host/wasm/db phases, error code) and W3C `traceparent` propagation. Keep the exporter host-owned; do not build an APM.
- Fix the docs that claim `WaitUntil` is wired (or wire it first).

### Phase 1 — Host bridge reliability (3–5 days, medium risk)

- Spike `[JSExport] async Task Handle(string)` (void result) + `respond` on NetWasm 0.10.1/0.10.3. The host awaits the export, catches exceptions, and returns 500 if no response was produced. If this works, remove the fire-and-forget path.
- Do the same for queue/scheduled exports.
- Add a host-side timeout/AbortSignal for non-streaming requests and queue/scheduled events.
- Wire `ctx.waitUntil`:
  - pass `ExecutionContext` into `ReptarHost.fetch`;
  - make the C# export `async Task` and have the framework await `ctx.BackgroundTasks` after `respond` (without `Task.Run` under NetWasm);
  - let the host call `ctx.waitUntil(exportPromise)` so the Worker holds the event open until the export — including background work — completes;
  - if NetWasm cannot keep the export Promise pending that way, fall back to a token-based `waitUntilStart`/`waitUntilDone` import that resolves a host-owned Promise passed to `ctx.waitUntil`;
  - on Kestrel, keep the current behavior.
- Move the host envelope/cookie/JSON-context boilerplate into a framework base or generated partial class; the app should provide `BuildApp()` only.

### Phase 2 — Contract loop completeness (1–2 weeks, medium risk)

- Require `.Produces<T>` on every route (analyzer or runtime validation in codegen); eliminate `unknown` from `AppType`.
- Generate typed `query` and `form` inputs; add per-status response narrowing, `$url`/`$path`, and `InferRequestType`/`InferResponseType`.
- Generate the app's `JsonSerializerContext` from route metadata; fail the build on missing DTOs.
- Run codegen automatically in `bun reptar dev`/`build`; add `--check` to CI.
- Consider a Roslyn analyzer for the declarative subset (`.Produces<T>`, route paths, DTO registration), but do not rewrite the runtime codegen until the analyzer proves it can handle real apps.

### Phase 3 — Size/performance (3–5 days, medium risk)

- Restore `NetWasmOptimization=Oz` (or document why O0 is required); re-measure raw/gzip/brotli and startup.
- Upgrade to NetWasm 0.10.3; use direct scalar imports/spans where they matter; re-measure.
- Add a minimal feature profile (no AI/agent/Stripe/LINQ) for edge deployments; make it a first-class build option.
- Replace base64 body transport with a binary path; benchmark large uploads/downloads and memory.

### Phase 4 — Differentiators (later)

- Typed Workers RPC/service binding clients and Durable Object method contracts.
- Event/job contracts (queue, cron, WebSocket) in the generated spec.
- Optional multi-runtime host (Deno/Node/Vercel) if the product needs it.
- WIT-like internal host contract for typed bindings, not a Workers component target.

### Suggested order and success criteria

| Order | Work | Success criterion |
|---|---|---|
| 0 | Boundary tests + host contract check + codegen `--check` + request event | No hang on any boundary test; CI fails on host/codegen drift; one traceable event per request |
| 1 | Async export + timeout + `waitUntil` + boilerplate removal | A thrown handler returns 500; `WaitUntil` survives response; app entrypoint shrinks |
| 2 | Contract completeness | Zero `unknown` in AppType; query/form/status typed; codegen runs in dev and `--check` in CI |
| 3 | Size/startup | `HelloReptar` back under ~12 MB or a documented budget; startup measured; binary body path benchmarked |
| 4 | RPC/events/multi-runtime | Only after 0–3 are stable |

### 7.1 If we only do five things

1. **Boundary safety:** async export + top-level catch + timeout + trap recovery + real `ctx.waitUntil`.
2. **One request event:** structured, sampled, trace-correlated, with host/wasm/db phase timings; host-owned exporter.
3. **Drift checks:** `interop.json` ↔ host shim, and generated contracts ↔ source; both fail CI.
4. **Binary body path:** remove base64 from large request/response bodies; upgrade NetWasm and use spans where they matter.
5. **Contract completeness:** zero `unknown`, typed query/form/status, generated JSON contexts.

These five improve speed, reliability, and observability without adding a new app-facing abstraction.

### 7.2 Release shape

- **Trust release:** boundary safety, `waitUntil`, host/codegen drift checks, one request event, size/startup budget. No new features.
- **Contracts release:** typed query/form/status, client helpers, generated JSON contexts, codegen watch.
- **Speed release:** binary bodies, NetWasm upgrade, `Oz`, minimal edge profile, startup/memory measurements.

This sequence keeps the feature surface frozen while the runtime spine catches up.

---

## 8. Spikes / open questions

1. Does NetWasm 0.10.1/0.10.3 support `[JSExport] async Task Handle(string)` under workerd? Does the host await it correctly? Does it hold the event open?
2. Can the `respond` channel be removed for small JSON responses by returning a `Task<JSObject>`/string, or is the async-export result restriction (void/primitive scalar) still binding?
3. Can `ReadOnlySpan<byte>` JS imports eliminate base64 without violating the single-threaded model? What is the copy cost at 1 MB/10 MB/100 MB?
4. Why was `NetWasmOptimization=O0` added? Does `Oz` build? What fails, and can it be fixed or bounded?
5. Does the 25 MB `HelloReptar` module meet the 1 s startup budget on deployed Workers? What is `startup_time_ms`?
6. Can codegen run in watch mode without holding build locks or executing app side effects? How fast is it on the RealWorld app?
7. Can a Roslyn analyzer enforce `.Produces<T>` and JSON-context registration without false positives on real apps?
8. Can `interop.json` drive `app.host.mjs` generation, or is validation enough for now?
9. Can the async export's own Promise be passed to `ctx.waitUntil` after `respond`, or do we need a token-based `waitUntilStart`/`waitUntilDone` bridge?
10. Can the generated `AppType` support query/form/per-status types without exploding TypeScript instantiation (the Hono problem)?

---

## 9. Sources

### Upstream

- workers-rs: <https://github.com/cloudflare/workers-rs> (commit `1d668cf`, `worker` 0.8.7)
- Cloudflare Rust docs: <https://developers.cloudflare.com/workers/languages/rust/>
- Cloudflare Wasm docs: <https://developers.cloudflare.com/workers/runtime-apis/webassembly/>
- Cloudflare Wasm in JS: <https://developers.cloudflare.com/workers/runtime-apis/webassembly/javascript/>
- Cloudflare Workers limits: <https://developers.cloudflare.com/workers/platform/limits/>
- Cloudflare size-limit changelog: <https://developers.cloudflare.com/changelog/post/2026-09-04-increased-worker-size-limit/>
- Cloudflare Workers RPC: <https://developers.cloudflare.com/workers/runtime-apis/rpc/>
- workers-go: <https://github.com/syumai/workers-go> (commit `a38be30`)
- workers-go docs: <https://workers-go.syumai.dev/>
- TinyGo language support: <https://tinygo.org/docs/reference/lang-support/>
- TinyGo 0.42: <https://tinygo.org/blog/2026/tinygo-0.42-recover-is-real/>
- Swift Wasm getting started: <https://swift.org/documentation/articles/wasm-getting-started.html>
- SwiftWasm book: <https://swiftwasm.github.io/swiftwasm-book/>
- WasmKit: <https://github.com/swiftwasm/WasmKit> (commit `1d87ccd`)
- JavaScriptKit / BridgeJS: <https://github.com/swiftwasm/JavaScriptKit>
- NetWasm: <https://github.com/zion-sati/netwasm> (commit `9d6dbc0`)
- NetWasm releases: `docs/releases/0.9.0.md`, `0.10.1.md`, `0.10.2.md`, `0.10.3.md`
- NetWasm licensing: <https://github.com/zion-sati/netwasm/blob/main/docs/licensing.md>
- tRPC: <https://trpc.io/docs>
- Hono RPC: <https://hono.dev/docs/guides/rpc>
- Encore APIs: <https://encore.dev/docs/go/primitives/defining-apis>

### Local

- `design.md` §3, §8, §13, §16
- `README.md` deployment matrix and size claims
- `framework/Reptar/Core/Ctx.cs:62-95`
- `framework/Reptar/Core/ReptarApp.cs:883-896`
- `framework/Reptar/Codegen/RpcGenerator.cs:1-48`
- `packages/host/src/host.ts:54-60, 855-1046`
- `packages/host/src/bindings.ts`
- `packages/rpc/src/app-type.ts` (10 `unknown` responses)
- `packages/rpc/src/client.ts:49-53`
- `scripts/reptar.ts:813-843`
- `package.json:61`
- `spikes/netwasm-worker/csharp/Program.cs:66-120`
- `spikes/netwasm-worker/csharp/HelloReptar.csproj:8`
- `spikes/realworld-worker/csharp/Program.cs:73-93`
- `examples/realtime-rooms/worker/csharp/Program.cs:173-221`
- `apps/docs/src/content/docs/core/context.md:98-121`
- `apps/docs/src/content/docs/client/codegen.mdx:87`
- `apps/docs/src/content/docs/overview/deployment-targets.md:49`

### Measurements run

```bash
# Rust (workers-rs router example, raw cdylib, no worker-build)
cargo build -p router-on-workers --release --target wasm32-unknown-unknown
ls -lh target/wasm32-unknown-unknown/release/router_on_workers.wasm
gzip -9 -c target/wasm32-unknown-unknown/release/router_on_workers.wasm | wc -c

# Go (net/http hello, standard Go)
GOOS=js GOARCH=wasm go build -o app.wasm .
ls -lh app.wasm
gzip -9 -c app.wasm | wc -c

# Reptar artifacts
ls -lh spikes/netwasm-worker/public/browser/HelloReptar.wasm
gzip -9 -c spikes/netwasm-worker/public/browser/HelloReptar.wasm | wc -c
brotli -q 11 -c spikes/netwasm-worker/public/browser/HelloReptar.wasm | wc -c
ls -lh spikes/realworld-worker/public/browser/RealWorld.wasm
gzip -9 -c spikes/realworld-worker/public/browser/RealWorld.wasm | wc -c
brotli -q 11 -c spikes/realworld-worker/public/browser/RealWorld.wasm | wc -c

# Worker upload size
cd spikes/netwasm-worker
./node_modules/.bin/wrangler deploy --dry-run --outdir /tmp/reptar-wrangler-dry
```

---

## 10. Bottom line

The pasted response is a useful pointer to the right ecosystems, but it reads like an ecosystem survey rather than a Reptar audit. Its biggest miss is that Reptar's contract generator already exists; its biggest correct instinct is that developer experience and contracts are the differentiator, not "C# can run on Wasm."

The next milestone should not be "add another language" or "chase a 10 MB binary." It should be:

> **Make the edge runtime trustworthy and the contract loop automatic.**

If Reptar can guarantee that a Worker request never hangs, background work actually survives, the host bridge cannot drift, and every route is fully typed in the generated client, then it has something none of the surveyed ecosystems offer: **C# on Workers with a complete, checked, language-neutral contract pipeline.**
