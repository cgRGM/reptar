# Product Scope & Module Boundaries

**Status:** decision record + migration plan — Phase 1/2 implemented 2026-10-26  
**Date:** 2026-10-26  
**Decision:** Reptar is a **web framework** for CRUD, SaaS, AI apps, and AI agents. AI is a **first-class optional module**. Stripe and realtime stay in core. The terminal agent is an **example app**, not framework surface. The AI gateway becomes a **reusable SDK/package**, not an example-only app.

> **Implemented so far:** `framework/Reptar.AI` and `framework/Reptar.AI.Gateway` exist; terminal UI moved to `examples/agent-cli/terminal`; `examples/ai-gateway` is a thin composition; `scripts/check-architecture.ts --strict` reports core clean and runs in CI. Packaging/NuGet and template flags are still open.

**Related:** `plans/core-focus-roadmap.md`, `plans/adonisjs-core-audit.md`, `design.md`, `llms.txt`, `README.md`, `examples/ai-gateway/README.md`, `examples/agent-cli/README.md`.

---

## 0. TL;DR

The product definition resolves the main ambiguity in the framework:

- **Core = web + SaaS.** Router, `Ctx`, validation, data, auth, JSON/contracts, telemetry, caching, storage, queues, **Stripe**, **realtime** (SSE/WebSockets/streaming + protocol adapters), and the small route→AI-tool bridge.
- **`Reptar.AI` = optional AI SDK.** LLM clients, streaming, tool calling, structured output, agent harness, sessions, RAG/embeddings, evals, usage/cost, safety, workspace tools.
- **`Reptar.AI.Gateway` = optional gateway SDK.** Provider registry, OpenAI-compatible proxy, routing/failover, streaming protocols, prompt cache, usage sink. Auth/rate limits/dashboard stay in the example app.
- **Examples = compositions.** Terminal agent, AI gateway app, BaaS/Studio, realtime rooms, blog, RealWorld. They may use OS/process/filesystem APIs; the core may not.

This gives Reptar four supported stories without turning the core into a monolith:

| Use case | Core + optional modules |
| :-- | :-- |
| **CRUD** | Core + `make:*` + preload + migrations + generated contracts |
| **SaaS** | Core (auth, Stripe, realtime, queues, email, rate limits) + observability |
| **AI apps** | Core + `Reptar.AI` + `Reptar.AI.Gateway` + generated AI streaming client |
| **AI agents** | Core + `Reptar.AI` + `examples/agent-cli` (terminal UI) |
| **Mixed** | Compose the packages; no fork of the framework |

---

## 1. Target module graph

```text
Reptar                         # core: web + SaaS primitives
├── Reptar.Kestrel             # native host
├── Reptar.Lambda              # AWS Lambda host
├── Reptar.Wasm                # Cloudflare Workers target
├── Reptar.SourceGenerators    # build-time generators
│
├── Reptar.AI                  # optional: LLM + agent SDK
│   └── Reptar.AI.Gateway      # optional: OpenAI-compatible gateway SDK
│
└── examples/
    ├── agent-cli              # terminal agent + web companion (owns terminal UI)
    ├── ai-gateway             # gateway app (auth, keys, rate limits, dashboard)
    ├── baas                   # single-container BaaS + Studio
    ├── realtime-rooms         # WebSocket/DO realtime example
    ├── blog                   # canonical CRUD + auth + uploads
    ├── realworld              # spec compliance + benchmark fixture
    └── ...
```

Dependency rule:

```text
Reptar            ←  Reptar.AI            ←  Reptar.AI.Gateway
Reptar            ←  Reptar.Kestrel / Lambda / Wasm
Reptar.AI         ←  examples/agent-cli
Reptar.AI.Gateway ←  examples/ai-gateway
Reptar            ←  examples/* (core-only examples)
```

**Hard rules:**

1. `Reptar` must not reference `Reptar.AI`, terminal UI, `System.Diagnostics.Process`, termios/P/Invoke, or filesystem agent tools.
2. `Reptar.AI` may reference `Reptar`; it must not reference the terminal example.
3. `Reptar.AI.Gateway` may reference `Reptar.AI` and `Reptar`; it must not own auth, API-key storage, or a dashboard.
4. `Reptar.Wasm` must not compile `Reptar.AI` or terminal code. The current Wasm csproj already excludes `Core/AiTools.cs` and the `AI/` folder; keep that.
5. Stripe and realtime stay in `Reptar`, but they must remain thin and dependency-free. No `Stripe.net`, no heavy realtime client.
6. Examples may use OS/process/filesystem APIs, but must be gated with `#if !NETWASM` where relevant.

---

## 2. What belongs where

### 2.1 `Reptar` core (keep)

| Area | Current location | Notes |
| :-- | :-- | :-- |
| Router, `Ctx`, middleware | `Core/` | Keep. Static delegates; no container. |
| Validation (`Rule<T>`) | `Validation/` | Keep, but fix AOT/reflection/allocations (see core-focus-roadmap). |
| Data (`TableDef`, `ColumnRef`, DB clients, migrations) | `Data/` | Keep; add preload/batching and DB instrumentation. |
| Auth | `Auth/` | Keep; built-in auth is a differentiator. |
| JSON/contracts/codegen | `Core/JsonCodec.cs`, `Codegen/` | Keep; add generated JSON contexts. |
| Telemetry/health | `Core/Middleware.cs`, `Core/ProductionExtensions.cs` | Keep; implement the promised structured logs/metrics. |
| Caching/rate limits/security headers | `Core/ResponseCache.cs`, `Core/Middleware.cs` | Keep; harden cache correctness. |
| Storage/queues/Cloudflare bindings | `Core/Storage.cs`, `Data/Services.cs`, `Reptar.Wasm/Data/` | Keep. |
| **Stripe** | `Features/Stripe.cs` | Keep in core (decision). Zero-dependency, reflection-free. |
| **Realtime primitives** | `Core/Sse.cs`, `Core/WebSocketConnection.cs`, `Realtime/` | Keep in core (decision). SSE/WS/streaming + thin protocol adapters. |
| **AI tool bridge** | `Core/AiTools.cs` | Keep in core: `.AsAiTool`, tool schema export, in-process `ExecuteAiToolAsync`. It is a web-framework feature, not the agent runtime. Make it AOT-safe. |

**Why Stripe/realtime stay in core:** they are common SaaS primitives, small, and zero-dependency. They make the framework useful for SaaS without an add-on. Keep them thin; don't let them grow into full vendor SDKs.

**Why the AI tool bridge stays in core:** exposing routes as tools is a routing/metadata feature. The agent runtime (LLM calls, sessions, tool loops) is optional.

### 2.2 `Reptar.AI` (optional SDK)

Move from `framework/Reptar/AI/`:

| Group | Files |
| :-- | :-- |
| **LLM clients** | `LlmTypes.cs`, `LlmOpenAi.cs`, `LlmAnthropic.cs`, `LlmGemini.cs`, `LlmAuth.cs`, `LlmCatalog.cs` |
| **Agent runtime** | `AgentHarness.cs`, `AgentModules.cs`, `AgentEvals.cs`, `AgentObservability.cs`, `AgentSafety.cs`, `BackgroundAgents.cs`, `LoopEvaluators.cs`, `DeclarativeWorkflow.cs`, `RunCheckpoints.cs`, `StructuredOutput.cs`, `ToolApproval.cs`, `MagenticPrompts.cs`, `WorkflowGraph.cs`, `SkillsDirectory.cs` |
| **Sessions/memory** | `SessionStore.cs` |
| **Tools** | `WorkspaceTools.cs` (native-only; gate `#if !NETWASM`) |

**Target API shape:**

```csharp
using Reptar.AI;

var client = LlmClientFactory.FromEnvironment();
var history = new List<ChatMessage> { new("user", "summarize this ticket") };

var summary = await StreamingAgentHarness.RunAsync(
    app, client, history,
    systemPrompt: "You are a support agent.",
    maxSteps: 12,
    onEvent: evt => { /* stream to SSE/terminal */ });
```

**What not to put in `Reptar.AI`:**

- terminal rendering, raw-mode input, ANSI UI, P/Invoke;
- the AI gateway's auth/API-key/dashboard;
- app-specific prompts, tools, or database schemas;
- vendor-specific SDKs that break AOT or add heavy dependencies.

### 2.3 `Reptar.AI.Gateway` (optional gateway SDK)

Extract the reusable core from `examples/ai-gateway/app.cs`:

| Concern | SDK | Example app |
| :-- | :-- | :-- |
| Provider registry (OpenAI, OpenRouter, Groq, Anthropic, Gemini, Ollama, Workers AI) | ✅ | config only |
| Normalized chat request/response | ✅ | — |
| OpenAI-compatible `/v1/chat/completions` mapping | ✅ | mount + auth |
| SSE and Vercel AI Data Stream protocols | ✅ | — |
| Routing/failover on 429/5xx | ✅ | policy config |
| Prompt cache (TTL/SWR, keyed by model+messages) | ✅ | cache config |
| Usage/token/latency sink | ✅ | DB table + dashboard |
| API-key auth, rate limits, admin UI | ❌ | ✅ |
| A2A agent cards / app-specific tools | ❌ | ✅ |

**Target API shape:**

```csharp
using Reptar.AI.Gateway;

var gateway = AiGateway.Create(new GatewayOptions
{
    Providers = ProviderRegistry.FromEnvironment(),
    DefaultModel = "openai/gpt-4o-mini",
    Failover = true,
    PromptCache = new PromptCacheOptions { TtlSeconds = 120 },
    UsageSink = new DbUsageSink(app.Db),
});

app.Route("/v1", gateway.Routes());
app.Get("/v1/models", gateway.Models());
```

The example app keeps auth, API-key storage, rate limits, dashboard, and deployment. The SDK has no UI and no auth opinions.

### 2.4 Examples (compositions)

| Example | Owns | Depends on |
| :-- | :-- | :-- |
| `examples/agent-cli` | Terminal UI (`TerminalAgent`, `TerminalEditor`, `TerminalInput`, `TerminalScreen`, `TerminalUi`, `ReptarTerm`, `Ansi`, `TerminalBuffer`), CLI flags, web companion, self-test | `Reptar.AI` |
| `examples/ai-gateway` | API-key auth, rate limits, dashboard, provider config, usage DB | `Reptar.AI.Gateway` |
| `examples/baas` | BaaS engine + Studio | `Reptar` core |
| `examples/realtime-rooms` | WebSocket/DO hub | `Reptar` core + host |
| `examples/blog` | CRUD + auth + uploads | `Reptar` core |
| `examples/realworld` | Spec fixture + benchmark | `Reptar` core |

**Terminal move:** `framework/Reptar/AI/Terminal*.cs`, `ReptarTerm.cs`, `Ansi.cs`, `TerminalBuffer.cs` move to `examples/agent-cli/terminal/` (or a small example-local project). The `bun reptar agent` CLI already runs `examples/agent-cli/app.cs`; it can stay as a thin wrapper. The core no longer compiles terminal code.

**AI gateway move:** the provider/routing/streaming/cache logic moves to `Reptar.AI.Gateway`; `examples/ai-gateway/app.cs` becomes a thin composition (auth, keys, dashboard, DB). This avoids duplicating provider logic between the SDK and the example.

---

## 3. Use-case fit

### 3.1 CRUD

Core already covers routing, validation, data, auth, migrations, contracts. Add:

- `make:model` / `make:feature` (stubs + wiring);
- preload/batching for list endpoints;
- JSON contexts + analyzer;
- `api` template with health, logs, timeouts, tests.

**Do not add an ORM.** `TableDef` + `ColumnRef` + preload is the CRUD story.

### 3.2 SaaS

Core already covers auth, Stripe, realtime, queues, storage, rate limits, CORS/security headers. Add:

- observability (structured logs, metrics, traces, slow queries);
- idempotency keys for writes;
- background jobs/email hardening;
- Stripe webhook verification + idempotency + replay protection;
- audit logs (optional module/template);
- multi-tenant helpers only if a real app needs them (not core by default).

### 3.3 AI apps

`Reptar.AI` + `Reptar.AI.Gateway` + generated contracts:

- LLM streaming (SSE / Vercel AI Data Stream);
- tool calling and structured output;
- embeddings/RAG (add a vector adapter; keep it optional);
- usage/cost tracking;
- provider routing/failover;
- prompt caching;
- generated TS client for streaming/tool events (optional `@reptar/ai-client`).

### 3.4 AI agents

`Reptar.AI` + `examples/agent-cli`:

- agent harness / ReAct loop;
- sessions and message persistence;
- workspace tools (read/write/edit/list/run) with path traversal protection and command allowlists;
- background agents, evals, checkpoints, human approval;
- terminal UI is example-only; web agents use SSE + generated client.

### 3.5 Mixed apps

A SaaS with an AI assistant uses:

```text
Reptar (auth, Stripe, realtime, data)
  + Reptar.AI (LLM calls, tools, sessions)
  + Reptar.AI.Gateway (provider routing/cache, if needed)
  + examples patterns (agent-cli, ai-gateway)
```

No framework fork; no separate runtime.

---

## 4. Migration plan

### Phase 0 — Measure and freeze the boundary (1–2 days)

- Record current core AOT binary size, Wasm module size, build time, and dependency list.
- Add an architecture test/script that fails if `Reptar` references `Reptar.AI`, terminal namespaces, `System.Diagnostics.Process`, or termios P/Invoke.
- Document the dependency rules in this file and `AGENTS.md`.

### Phase 1 — Extract `Reptar.AI` (3–5 days)

- Create `framework/Reptar.AI/Reptar.AI.csproj` (`net10.0`, references `Reptar`).
- Move the AI SDK files listed in §2.2. Keep namespace `Reptar.AI` so app code changes are minimal.
- Move terminal files to `examples/agent-cli/terminal/`.
- Update `examples/agent-cli/app.cs` to `#:project ../../framework/Reptar.AI/Reptar.AI.csproj` and include the terminal files.
- Update `framework/Reptar.Tests` to reference `Reptar.AI`; split AI tests into `Reptar.AI.Tests` later if build time matters.
- Update `Reptar.Wasm` to keep excluding AI (already does).
- Remove AI/terminal files from `framework/Reptar/`.
- Acceptance: core builds without AI; `examples/agent-cli` builds/runs; AI tests pass; core AOT/Wasm size measured.

### Phase 2 — Extract `Reptar.AI.Gateway` (5–8 days)

- Create `framework/Reptar.AI.Gateway/Reptar.AI.Gateway.csproj` (references `Reptar.AI`).
- Move provider registry, request/response normalization, streaming protocols, failover, prompt cache, and usage sink from `examples/ai-gateway/app.cs`.
- Keep auth, API keys, rate limits, dashboard, and DB schema in the example.
- Add SDK tests: provider routing, failover, SSE framing, Vercel data stream, cache hit/miss, usage accounting.
- Acceptance: example app shrinks to composition + auth/dashboard; no duplicated provider logic; gateway tests pass.

### Phase 3 — Packaging and templates (1–2 weeks)

- Add NuGet packages: `Reptar`, `Reptar.AI`, `Reptar.AI.Gateway`, hosts.
- Add templates:
  - `api` — CRUD/SaaS baseline (auth, health, logs, timeouts, migrations, tests).
  - `ai-app` — API + `Reptar.AI` + streaming chat.
  - `ai-gateway` — gateway SDK + auth/keys/dashboard.
  - `agent` — example app, not core.
- Add `bun reptar add ai` / `add gateway` (or template flags) once the package feed works.
- Acceptance: scaffold smoke builds all templates; `tsc --noEmit`; AOT/Wasm smoke.

### Phase 4 — Cleanup and enforcement (2–3 days)

- Delete old paths and unused project references.
- Add CI architecture checks: core must not depend on AI/terminal; Wasm must not include AI; examples may.
- Update docs/README/`llms.txt` to reflect the boundary.
- Add size budgets per package.

---

## 5. Risks and mitigations

| Risk | Mitigation |
| :-- | :-- |
| **AI SDK API churn** | Keep `Reptar.AI` versioned separately; stable interfaces for `LlmClient`, `ChatMessage`, `AgentEvent`, `AgentSessionStore`; adapters for providers. |
| **Terminal example drifts from SDK** | `bun reptar agent` runs the example; CI builds it; terminal code is not in core. |
| **AI gateway duplication** | Extract provider/routing/streaming once; example only composes + auth/dashboard. |
| **NuGet/file-based reference friction** | Support both `#:project` (repo clone) and package references; `create-reptar` wires the right one. |
| **Core accidentally references AI** | Architecture test in CI; `Reptar.csproj` has no AI project reference; Wasm csproj excludes AI. |
| **Stripe/realtime bloat** | Keep them thin, zero-dependency, optional at the app level via route mounting/config; no vendor SDKs. |
| **AOT/Wasm size growth** | Size budgets per package; AI excluded from Wasm; trim unused code; measure after each phase. |

---

## 6. Success criteria

- `Reptar` core has **no** reference to `Reptar.AI`, terminal UI, process/filesystem agent tools, or termios.
- Core AOT/Wasm size and cold start stay within budget after the split.
- `Reptar.AI` can be added to a CRUD app without changing core code.
- `examples/agent-cli` builds/runs using `Reptar.AI`; terminal code lives only there.
- `examples/ai-gateway` is a thin composition over `Reptar.AI.Gateway`; no duplicated provider logic.
- CRUD/SaaS/AI/agent templates all pass `test:scaffold`, `codegen`, `tsc --noEmit`, AOT/Wasm smoke.
- Docs and `llms.txt` describe the boundary accurately.

---

## 7. Bottom line

Reptar should be **one web framework with two optional AI layers**, not an AI platform with a web server attached:

- **Core** earns the right to be fast and reliable: web + SaaS primitives, Stripe, realtime, observability.
- **`Reptar.AI`** makes AI apps and agents first-class without bloating every CRUD/SaaS deployment.
- **`Reptar.AI.Gateway`** turns the gateway example into a reusable SDK; the example keeps auth/UI.
- **Examples** own terminal UI, dashboards, and app-specific compositions.

That boundary is what lets the same framework serve CRUD, SaaS, AI apps, and agents without becoming a monolith.
