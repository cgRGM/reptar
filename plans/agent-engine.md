# Reptar.AI Agent Engine — Build Report, Findings, and Learnings

**Date:** 2026-10-25 · **Scope:** `framework/Reptar/AI/`, `examples/agent-cli/`, `scripts/reptar.ts`

## What Was Built

### 1. Framework core — `framework/Reptar/AI/` (namespace `Reptar.AI`)

| File | Purpose |
| --- | --- |
| `LlmTypes.cs` | `LlmConfig` with env-based provider detection (OpenAI, Anthropic, Gemini, Ollama, OpenRouter, Vercel AI Gateway, offline Echo), `LlmStreamEvent` protocol, shared SSE/JSON plumbing. |
| `LlmOpenAi.cs` | Streaming client for OpenAI-compatible Chat Completions (also Ollama). Accumulates streamed `tool_calls` argument fragments. |
| `LlmAnthropic.cs` | Anthropic Messages API client (content-block deltas, `tool_use` blocks, groups tool results into user turns as the API requires). |
| `LlmGemini.cs` | Google `streamGenerateContent?alt=sse` client with `functionCall`/`functionResponse` mapping. |
| `AgentHarness.cs` | `StreamingAgentHarness.RunAsync` — provider-agnostic ReAct loop with rich `AgentEvent` surface (turn/text/tool lifecycle), max-steps guard, cancellation, in-process dispatch via `app.ExecuteAiToolAsync`. |
| `WorkspaceTools.cs` | `app.UseWorkspaceTools(root)` mounts `read_file`, `write_file`, `edit_file`, `list_dir`, `run_command` as standard Reptar routes marked `.AsAiTool(...)`, with path-traversal protection. |
| `SessionStore.cs` | `AgentSchema.Sessions/Messages` TableDefs + `AgentSessionStore` with **manually registered zero-reflection mappers** (AOT-safe without the source generator). |
| `Ansi.cs` | ANSI color helpers + line-oriented streaming markdown renderer (no terminal rewrites — fx-style scrollback). |
| `TerminalAgent.cs` | Interactive loop behind `bun reptar agent`: streaming output, slash commands, session resume, Ctrl+C interrupt handling. |

### 2. Example app — `examples/agent-cli/` (no `.csproj`)

- `app.cs` — file-based app: `--web [port]`, `--session <id>`, `--model`, `--self-test`, workspace/db flags.
- `schema.cs` — exactly the spec'd `agent_sessions` / `agent_messages` TableDefs.
- `features/chat.cs` — `POST /api/chat` (SSE with typed JSON events), `GET /api/sessions`, `GET /api/sessions/:id/messages`, `GET /api/tools`.
- `web.cs` — embedded single-file React SPA (ESM CDN, no build step, no node_modules).
- `selftest.cs` — 7-check offline verification of the tool bridge via `ExecuteAiToolAsync`.

### 3. CLI — `scripts/reptar.ts`

- `bun reptar agent` — boots the terminal agent with the caller's cwd as workspace.
- `bun reptar agent --web` — web companion (default 4040).
- `bun reptar run <app.cs>` — run any file-based app.

## Verified Numbers

| Metric | Result | Target |
| --- | --- | --- |
| Framework tests | **136/136 pass** (121 existing + 15 new `AgentEngineTests`) | all pass |
| `--self-test` through the AOT binary | **7/7** | 7/7 |
| Native AOT binary (osx-arm64) | **12 MB** + `libe_sqlite3.dylib` | ~10.4 MB (linux-x64 container) |
| AOT cold start (banner to prompt) | **~10–20 ms** | ~120 ms |
| Framework `Reptar.csproj` build | 0 errors | clean |

All web-mode endpoints (SSE streaming chat, sessions, SPA) verified against the **real AOT binary**, not just the JIT dev host.

## Bugs Found in the Framework (all fixed here)

1. **`ExecuteAiToolAsync` built GET query strings with a leading `?`** while `KestrelHost`/`ReptarRequest.QueryValue` expect none — so every AI tool with GET query parameters (`read_file`, `list_dir`) received _empty_ parameters when dispatched in-process. Fixed in `AiTools.cs` (no `?` prefix) plus a defensive `?`-strip in `ReptarRequest.QueryValue`.

2. **`doctor` misreported the SDK version requirement.** It checked `major >= 10` only while printing "required: >= 10.0.401", and it never looked at the portable SDK Reptar itself installs (`.reptar/sdk/dotnet`). On this machine it claimed `10.0.103` satisfied `>= 10.0.401`. Fixed in `scripts/inspect.ts`: portable SDK → `DOTNET_ROOT` → PATH resolution plus real `versionAtLeast` comparison.

3. **`ExecuteAiToolAsync` re-scanned the whole route table on every tool call.** Agent loops dispatch tools dozens of times per turn; each dispatch rebuilt the full `GetAiTools()` list and did a linear name search. Added `app.ResolveAiTool(name)` with a `ConditionalWeakTable`-cached index that rebuilds only when the route count changes.

4. **`llms.txt` documented APIs that do not exist:** `ctx.Status(404).Json(...)` (no such chain), and `{slug}` route syntax (Reptar uses `:slug`). Any agent following the canonical reference would produce broken code. Fixed both examples; also added the real `Ctx.Param(name)` convenience to the framework.

5. **Anonymous types and positional records throw under Native AOT.** `ctx.Json(new { ... })` and `ctx.Json(record)` hit the reflection-based `SmallObjectWithParameterizedConstructorConverter` and fail at runtime in AOT-published apps. This is the single most important DX finding (see below).

## Key Learnings (and remaining recommendations)

### L1 — AOT-safe JSON is the make-or-break rule for "AI-native runtime" claims

The self-test caught two runtime serialization failures **that the JIT dev host and the unit tests never surfaced**. Rules that now hold for all agent-path code:

- Responses: build `JsonObject`/`JsonArray` (or `ctx.JsonRaw(string)`), never `ctx.Json(new { ... })`.
- DB rows: register mappers manually (`EntityMapper<T>.Factory = _ => Map;`) and implement `IReptarEntity.ToDbParameters()` by hand — no `[ReptarEntity]` source-generator dependency needed for AOT apps.
- **Recommendation for the framework:** either ship a `SourceGenJsonCodec` example for file-based apps (registering an app's DTO types), or add an analyzer warning for `ctx.Json(new { ... })` in AOT builds. Every existing example (`features/posts.cs` uses anonymous `new { id, slug }`) will fail the same way under `PublishAot`.

### L2 — File-based `dotnet publish` can silently skip AOT

The first `dotnet publish app.cs -p:PublishAot=true` produced a **self-contained JIT** folder (238 MB) with exit code 0 — a stale runfile artifact. A clean re-run produced the real 12 MB native binary. No warning, no error. **Recommendation:** `bun reptar build` should assert the publish output contains a native executable (no `app.dll`), because `deploy/Dockerfile` masks the same failure with `mv /app/publish/app ... || true`.

### L3 — Keep the terminal renderer append-only

Re-approach (erase-and-redraw) TUIs break in scrollback, tmux copy mode, and piped output. The `AnsiMarkdown` renderer prints each completed line once with highlighting — same philosophy as fx's "inline output" — and works identically when piped to a file.

### L4 — Tool-call UX: stream arguments on start, results on finish

The event pair `ToolCallStarted(args)` → `ToolCallFinished(success, ms)` is enough for both the terminal (`→ read_file {...}` / `✓ read_file 12ms`) and the SSE web protocol (`tool_start`/`tool_end` frames) without any extra machinery — mirrors Flue's chunk model and Pi's tool lifecycle events.

### L5 — Accept both snake_case and camelCase in tool inputs

LLMs follow whatever the JSON schema says, but tool descriptions say `wait_ms` while record-derived schemas emit `waitMs`. The workspace tools parse both spellings manually. Framework-level fix worth considering: `OpenApiGenerator.GenerateTypeSchema` should emit the same name the default deserializer binds (today it reflects over `PropertyInfo` with camelCase).

### L6 — Escape noise in tool results

`ReptarJson.Options` (web defaults) escapes `+`, `<`, `&` as `\u002B` etc. — functionally fine for LLMs but wasted tokens in every tool result. Consider an `UnsafeRelaxedJsonEscaping` encoder for the AI tool result path only.

### L7 — Onboarding notes (smaller DX friction)

- `bun reptar` with no args prints nothing; a bare-args hint would help.
- `db plan` on this checkout reports perpetual drift for auth tables (`users.image`, `sessions_token_hash_uq`, `api_keys`) that `EnsureCreated` apparently doesn't create from `Auth.Configure` — worth reconciling so `doctor` doesn't normalize "drift_detected" as healthy.
- The portable SDK check `ensureSdk()` runs even for `inspect`/`doctor`/ `db status` — those are pure Bun paths; moving them before `ensureSdk()` would make `bun reptar doctor` feel instant on cold machines. (The MCP server path already does this.)

## Architecture Decisions Worth Keeping

- **In-process dispatch over HTTP hops.** Tool calls run through `app.ExecuteAiToolAsync` with zero network overhead and full `Rule<T>` validation — the agent harness never knows HTTP exists.
- **Framework owns the engine, example owns the product surface.** `Reptar.AI` provides the loop, providers, tools, terminal, and session primitives; `examples/agent-cli` only wires SQLite persistence, the chat API, and the web SPA. The split in the dispatch notes ("half in Reptar.AI, half in the example") is exactly what shipped.
- **Offline-first.** Every capability is demonstrable with zero API keys via the Echo provider — `bun reptar agent` works out of the box and says exactly which env var to set.
