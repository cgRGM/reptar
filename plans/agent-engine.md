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

## Follow-up: Provider Connection Flow (pi /login + opencode /connect parity)

Added after studying Pi and OpenCode reference implementations:

- **`/connect`** — provider picker with live per-row status (Pi's oauth-selector
  pattern: `✓ env: OPENAI_API_KEY` / `✓ saved` / `• not connected`), hidden
  API-key entry, base URL override, live connection test against the provider's
  model endpoint, model pick, save + switch. `Other` registers custom
  OpenAI-compatible endpoints (opencode parity).
- **`~/.reptar/agent/auth.json`** — flat credential map (opencode's auth.json
  shape) with `0600` permissions (pi) and a `_default` marker so the last
  connected provider wins on plain launches. `REPTAR_AGENT_AUTH` overrides the
  path (hermetic tests, sandboxes).
- **Precedence kept env-first** — OpenCode's issue #10950 documents stored
  credentials *silently overriding* explicit config, causing auth failures with
  no explanation. Reptar's rule: env wins over saved, and `/providers` always
  shows the live source.
- **`/models`** — live catalog fetch per provider wire format (OpenAI-compat
  `/models`, Anthropic `/v1/models`, Gemini `v1beta/models`, Ollama `/api/tags`)
  with a numbered picker that persists the chosen default.
- **`--connect [provider]` / `--providers`** CLI flags (opencode `auth login`
  parity).

### New bugs this uncovered (both fixed + regression-tested)

1. **Double tool execution on OpenAI-style streams.** OpenAI clients emit
   argument *deltas* and then a *complete* `ToolCall`; the harness treated
   them as two calls, so every streamed tool call executed twice. Caught only
   because the mock-server verification sent a real tool call over the wire —
   the scripted unit tests emitted a single complete event and passed. Fix:
   merge by call id (`StreamingAgentHarness`), regression test added.

2. **Reflection-based tool schema serialization failed under Native AOT.**
   `JsonSerializer.SerializeToNode(Dictionary<string, object?>)` throws
   "Reflection-based serialization has been disabled" in AOT — the JIT dev
   host and all unit tests masked it. Any AOT-published Reptar app making a
   tool-carrying LLM call would have failed. Fix: manual `LlmHttp.ToNode`
   converter; also fixed `GetAiToolsJson` which had the same pattern.

3. **Agent errors surfaced as blank text** — the harness returned an empty
   `FinalContent` on exceptions. Errors now carry the exception message
   (innermost included).

Verification highlights: full `/connect → connection test → save → chat with
in-process tool call → /models → /disconnect` run against a live local
OpenAI-compatible mock through the real 12 MB AOT binary, plus a
`--connect anthropic` test against the **real** Anthropic API confirming the
wrong-key path (401, clean display, save-anyway prompt, no crash).

## Exploration: Spectre.Console vs the hand-rolled ANSI layer

**Date:** 2026-10-25 · **Mode:** exploration only — nothing integrated.

[Spectre.Console](https://github.com/spectreconsole/spectre.console) studied from
source plus two live Native AOT spikes (a bare console app and a full copy of
the agent-cli example with real widget usage).

### Measured (real AOT publishes, osx-arm64, Release, symbols stripped)

| Binary | Size |
|---|---|
| Bare `Console.WriteLine` AOT app | 1.2 MB |
| Bare app + Spectre (widgets exercised) | 4.4 MB |
| **Our agent CLI (baseline)** | **12,952,400 B ≈ 12.4 MiB** |
| **Our agent CLI + Spectre (SelectionPrompt, Status, Table, Markup exercised)** | **14,593,680 B ≈ +1.6 MB** |

Notes: merely referencing the package costs ~0 (ILC trims it — the first spike
stayed 12 MB); the +1.6 MB is what the four widgets we'd actually use cost.

### Key facts from source

- `IsAotCompatible=true` (net8/9/10 TFMs) — official AOT support; only
  reflection is exception rendering (annotation-guarded). Zero runtime
  package deps (Wcwidth is source-only).
- **Prompts throw `NotSupportedException` on any redirected stream**
  (`InteractionDetector` requires stdin+stdout+stderr all non-redirected).
  Our numbered pickers degrade to plain text when piped — which is exactly how
  every scripted/CI verification of `/connect` works today.
- `Live`/`Status`/`Progress` rewrite lines in place — incompatible with the
  append-only streaming chat output (fx scrollback philosophy), fine between
  streaming segments (e.g. the connection-test spinner).

### Verdict for Reptar

- **Framework core (`Reptar.AI`): stays raw ANSI, zero-dep.** Non-negotiable
  for the 10.4 MB claim, NetWasm constraints, and the zero-dependency pitch.
- **Agent CLI:** hybrid is viable — Spectre for interactive chrome
  (arrow-key provider/model pickers, `TextPrompt.Secret`, bordered
  `/providers` and `/sessions` tables, connection-test spinner) behind an
  `IsInputRedirected` check that falls back to the current numbered menus.
  Keep streaming chat output raw ANSI forever.
- **Cheaper alternative:** steal the ideas, not the package — a spinner is
  ~30 lines of frame rotation, box-drawing tables are ~80. The references
  back this: Pi built **pi-tui** from scratch, fx prints inline, OpenCode
  rolls its own TUI — none of the three use Spectre.
- Decision driver: whether ~+1.6 MB and the first example-app NuGet dep is
  acceptable for noticeably nicer interactive chrome. Product call.

## Follow-up: The Six Spectre Ideas, Implemented (plus reference-provider parity)

**Date:** 2026-10-25 (second round) · **Outcome:** all six ideas shipped as
~700 lines of raw-ANSI C# in `Reptar.AI/TerminalUi.cs` — same binary size
(12 MB AOT), zero new dependencies.

| Idea (Spectre) | Reptar implementation | Verified |
|---|---|---|
| `SelectionPrompt` | `TerminalUi.Select` — arrow keys, PageUp/Down, type-to-search filter, 8-row paging with scroll indicator; **numbered fallback when piped** | PTY: 3× arrow-down → Ollama selected; piped: all scripted flows still pass |
| `TextPrompt.Secret()` + validation | `TerminalUi.Ask` — re-ask loop with red error, `*` masking, ESC cancel; piped → plain ReadLine | PTY: masked `***************`, validated URL prompt |
| `Status` spinner | `TerminalUi.SpinAsync` — Braille frames + elapsed seconds, message updates mid-run; piped → static lines | PTY frames + clean cancel (fixed a `TaskCanceledException` crash and a double-glyph render) |
| `Table` | `TerminalUi.Table` — auto-measured box-drawing borders, per-cell color, terminal-width shrink loop | Fixed an 8-char border misalignment (border segments must be width+2 to match cell padding) |
| Color detection | `TerminalUi.Colors` → truecolor / 256 / basic from `COLORTERM`+`TERM`; `Rgb`/`Purple`/`Orange`/… downgrade via 6×6×6 cube or nearest-basic | banner/pickers use the brand purple when the terminal allows |
| Exception rendering | `TerminalUi.RenderException` — red type+message, dim shortened frames, nested causes indented | available for fatal paths in app.cs |

**models.dev catalog** (opencode's model source): `LlmCatalog.ListCuratedAsync`
fetches `https://models.dev/api.json` — free, no credentials — so `/models
anthropic` browses `Claude Opus 4.5 · 200k ctx` before any connection, and the
live-list picker enriches model ids with display names + context windows.

**New providers** (user request): `opencode` (OpenCode Zen,
`https://opencode.ai/zen/v1`, env `OPENCODE_API_KEY`) and
`cloudflare-gateway` (Cloudflare AI Gateway — `RequiresBaseUrl` prompt for
the `gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/{provider}` URL,
API key = upstream provider's).

**Verification:** 142/142 tests; piped + PTY flows against a live local
OpenAI-compatible mock; live failure paths against the real Gemini and
Anthropic APIs (400/401 handled gracefully); `/models` curated fallback with
model switch persistence; 12 MB Native AOT rebuild with `--self-test` 7/7.
Verified in an isolated tree — the shared checkout had another agent's
mid-edit compile errors (documented below).

**Shared-workspace race (note for the team):** two agents committing to
`framework/Reptar/AI/` in parallel left `StructuredOutput.cs` (unfixed
references) and `SessionStore.cs` (`IMessagePersistFilter`) briefly breaking
the main build mid-round. Nothing was lost (my round was verified in a
scratch copy at /tmp/reptar-iso with the WIP file excluded), but build gates
in CI would have caught both.

## Follow-up: Input UX (pi editor + opencode palette parity)

**Date:** 2026-10-25 (third round) · **Trigger:** user UX feedback — "when you
hit a / most terminal apps drop down the commands… the ui/ux isn't great."

Studied pi's actual source for the input loop this time (`custom-editor.ts`,
`footer.ts`, `interactive-mode.ts` autocomplete provider) plus opencode's
ctrl+p palette, and rebuilt the prompt as a real line editor:

| Reference behavior | Reptar implementation |
|---|---|
| `/` dropdown, filter-as-you-type, fuzzy over name **and** description | `TerminalEditor` slash mode, filtered live per keystroke |
| Arrow-key dropdown nav, Tab/Enter accept, Enter defaults to top suggestion | highlight starts at 0 (Enter accepts the top match — Claude Code behavior) |
| `@file` suggestions from cwd with fd index | workspace scan (ignore-listed, 4k cap, 3s cache), score by filename prefix |
| Enter accepts file completion, second Enter submits | same — with the `@` preserved so the mention survives |
| `!command` bash mode | `RunShellPassthroughAsync` via the workspace tools, output dim, never sent to the model |
| ctrl+p command palette (opencode) | palette mode from anywhere, typed filter |
| pi footer: cwd+branch, token stats, context %, cost, model | status bar at prompt — usage from provider streams, pricing/context from models.dev, branch parsed from `.git/HEAD` |
| @-attached images | `AgentImage` on `ChatMessage` → OpenAI `image_url`, Anthropic `image`, Gemini `inline_data` |

Also fixed this round:
- **Select "keeps printing" bug** — the frame eraser undercounted by
  `min(rows, 8) + 2` instead of the exact printed row count; every keystroke
  left stale frames. Erase now uses the exact count.
- **Enter dropped the `@` marker on file accept**, so mentions never attached.
- **Out-of-range cursor crashed `FilePrefix`** (caught by a test) — now clamps.

Token/cost plumbing: OpenAI `stream_options.include_usage`, Anthropic
`message_start`/`message_delta` usage, Gemini `usageMetadata` →
`LlmStreamEvent.Usage` → `AgentEvent.UsageReported` → session totals →
status bar.

**Verified:** 196/196 tests (main tree, merged with the parallel agent's
feature push), 148/148 in the isolated tree before their merge; PTY runs of
slash filter→select→execute, `!echo`, `@not`→`read @notes.md` with `[file: …]`
attached, `@logo.png` attached as an image block, status bar on every prompt;
AOT rebuild 13 MB (+1 MB for editor/usage/images), self-test 7/7.

## Follow-up: Honest Gaps Closed (pinned footer, persisted history, clipboard paste)

**Date:** 2026-10-25 (fourth round) · **Trigger:** the three "honest gaps
still open" from the input-UX round.

### Gap 1: Pinned status bar — ✅ shipped

`TerminalScreen.cs`: DECSTBM scroll region (`\x1b[1;{h-1}r`) reserves the
bottom line. The footer paints via CUP (`\x1b[{h};1H`), wrapped in
DECSC/DECRC (`\x1b7`/`\x1b8`) to restore the cursor. Re-applies the region
on terminal resize; restores cleanly on exit and Ctrl+C. Piped/non-TTY mode
falls back to the inline bar (previous behavior).

### Gap 2: Persisted input history — ✅ shipped

`TerminalEditor.HistoryPath` (`~/.reptar/agent/history`, override via
`REPTAR_AGENT_HISTORY`): HISTFILE-style, 500 entries, exact-duplicate
move-to-end (not just consecutive dedup — a bug caught by tests), rewritten
from memory on every submission. **Leading-space entries are never written
to disk** (bash `HISTCONTROL=ignorespace` privacy escape), but stay
recallable in-session.

### Gap 3: Clipboard image paste — ✅ shipped, with a discovery

Ctrl+V sends the OSC 52 clipboard query (`\x1b]52;c;?\x07`); the response
(`\x1b]52;c;<base64>BEL`) is parsed into an `AgentImage` and the buffer gets
a `[clipboard image]` marker. The `TerminalInput` reader thread is the
**single owner of stdin** — this was the non-negotiable architectural
requirement.

**The discovery:** .NET 10's `Console.ReadKey` on Unix keeps an internal
CSI/OSC parser that silently consumes (and discards) ESC-]-prefixed
sequences, even after it returns. No clipboard, arrow-key sequence, or
bracketed paste can be captured through it. The fix is a **dedicated reader
thread + termios P/Invoke**:

```
TerminalInput.Activate() →
  1. tcgetattr(0, blob)          // save the terminal mode
  2. cfmakeraw(blob)             // raw mode via libc (correct on all Unix)
  3. tcsetattr(0, TCSANOW, blob) // apply
  4. new Thread(ReadLoop)        // single reader owns stdin from here on
```

The reader thread parses: CSI sequences (arrows, Home/End, PageUp/Down,
Delete, bracketed paste `\x1b[200~`…`\x1b[201~`), OSC 52 clipboard
responses (base64 → magic-byte sniff → `AgentImage`), UTF-8 text, control
bytes, and Ctrl-combos — all surfaced as `TermKey` events. Every UI
component (editor, selection prompts, text prompts, secret entry) routes
through `TerminalInput` once active; `Console.ReadKey` is never called again.

**Verified via `expect` PTY driver** (with correct per-OS termios): single
bytes (`key=a`), Ctrl+V (`key=V ctrl=True`), and OSC 52 clipboard
(`clip=image/png`) all deliver correctly. Arrow-key delivery requires the
reader thread to be the *sole* stdin consumer; under `dotnet run` the
MSBuild parent process can race stdin during build — the fix for testing
is `dotnet exec <app.dll>` which has no parent.

### The AOT publish detour

`dotnet publish app.cs -p:PublishAot=true` with an explicit
`-r osx-arm64 --self-contained` produced a **silent JIT** fallback (no
"Generating native code" line, framework-dependent output) at least once
this round. The conditions are unclear — one successful 13 MB AOT binary
was produced earlier with identical flags, and the same command later
regressed to JIT without any error. Suspect: stale incremental state in
the shared `runfile` cache (concurrent agents building the same file-based
project). Documented as a known flake; do NOT treat a silent AOT skip
as a build error — always check for "Generating native code" in the log.
(For the record: the `dotnet run` MSBuild parent process also races stdin
during build, consuming input bytes before the app starts. This is a
**testing** problem only — production single binaries have no parent.)

### Verification

- 210/210 tests (merged with the parallel agent's ReptarTerm TUI work)
- Clipboard attach verified live (`clip=image/png` from a synthetic OSC 52
  response containing a 1×1 PNG)
- History persists across separate process launches (verified via `expect`)
- Pinned footer renders at the absolute bottom line and survives resize
- All input flows through the single reader thread; `.NET Console.ReadKey`
  is never invoked after activation

## Architecture Decisions Worth Keeping

- **In-process dispatch over HTTP hops.** Tool calls run through `app.ExecuteAiToolAsync` with zero network overhead and full `Rule<T>` validation — the agent harness never knows HTTP exists.
- **Framework owns the engine, example owns the product surface.** `Reptar.AI` provides the loop, providers, tools, terminal, and session primitives; `examples/agent-cli` only wires SQLite persistence, the chat API, and the web SPA. The split in the dispatch notes ("half in Reptar.AI, half in the example") is exactly what shipped.
- **Offline-first.** Every capability is demonstrable with zero API keys via the Echo provider — `bun reptar agent` works out of the box and says exactly which env var to set.
