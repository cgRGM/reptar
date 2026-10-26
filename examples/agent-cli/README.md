# Reptar Agent CLI + Web Harness

A file-based Reptar app (no `.csproj`) that is both:

1. **An interactive terminal coding agent** — streaming output, ANSI markdown, slash commands, SQLite session persistence, in-process workspace tools.
2. **A web companion** — `POST /api/chat` SSE streaming + a single-file React SPA, serving from the same binary that runs the terminal.

It showcases `Reptar.AI`, the agent engine built into the framework (`framework/Reptar/AI/`). The primitives live in the framework; this app only adds the product surface: the session schema, the chat API, and the SPA.

## Quick Start

From the repo root (no API key needed — offline echo mode works out of the box):

```bash
bun reptar agent                    # interactive terminal agent in this workspace
bun reptar agent --model anthropic/claude-sonnet-4-5
bun reptar agent --session <id>     # resume a saved session
bun reptar agent --web 4040         # web companion → http://localhost:4040
bun reptar agent --self-test        # verify all workspace tools offline
```

Or run the app file directly:

```bash
bun scripts/reptar.ts run examples/agent-cli/app.cs
cd examples/agent-cli && dotnet run app.cs -- --web
```

## Connecting providers (pi `/login` + opencode `/connect` style)

```bash
bun reptar agent --connect            # pick a provider, paste the key (hidden), live test
bun reptar agent --connect anthropic   # connect a specific one directly
bun reptar agent --providers           # show connection status and exit
```

In-session: `/connect`, `/providers`, `/models`, `/disconnect`.

The flow mirrors Pi and OpenCode, with a zero-dependency terminal UI
(Spectre.Console ideas, ~0 MB: arrow-key selection with type-to-search and
paging, hidden API-key entry with validation re-ask, a live spinner for the
connection test, and bordered auto-measured tables — every component falls
back to plain numbered input when stdin is piped, so scripted and CI flows
keep working):

- **Provider picker** with live status per row (`✓ env: OPENAI_API_KEY`,
  `✓ saved`, `• not connected`) — arrow keys navigate, typing filters
- **Connection test under a live spinner** against the provider's model list
- **Model picker** enriched with the [models.dev](https://models.dev)
  curated catalog (display names like `Claude Opus 4.5 · 200k ctx`, and it
  works with **zero credentials** — `/models anthropic` browses before you
  ever connect)
- Saves to `~/.reptar/agent/auth.json` (flat map, `0600` permissions,
  last-connect becomes the default provider)

Built-in providers: OpenAI, Anthropic, Google Gemini, Ollama (local),
OpenRouter, Vercel AI Gateway, **OpenCode Zen** (`opencode.ai/zen/v1`), and
**Cloudflare AI Gateway** (your `gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/{provider}`
URL + the upstream provider's key). `Other` registers any custom
OpenAI-compatible endpoint (id + base URL + key + model), like OpenCode's
custom provider flow.
**Precedence — env always wins over saved, and the status table shows which source is live:**
`--model`/`--provider` flags → `REPTAR_AGENT_*` env vars → provider env keys
(`OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `GEMINI_API_KEY` / `OLLAMA_HOST` /
`OPENROUTER_API_KEY` / `AI_GATEWAY_API_KEY`) → credentials saved by
`/connect` → offline Echo provider (deterministic reply, tools still real).

## The Input Editor

The prompt is a pi-style line editor (raw ANSI, zero dependencies):

- **`/` → command dropdown** — every slash command with its description,
  filtered as you type (name *and* description, pi-style fuzzy), arrow keys to
  navigate, Enter to accept the top suggestion, ESC to dismiss
- **`@path` → file attach** — workspace files (respecting the ignore list)
  match as you type; Enter accepts the top match, a second Enter submits.
  Text files inline as `[file: path]` context blocks; **images
  (`.png .jpg .jpeg .webp .gif`) attach as vision content blocks** for
  OpenAI, Anthropic, and Gemini
- **`!command` → shell passthrough** — runs in the workspace, shows output,
  never goes to the model
- **Ctrl+P → command palette** — open the command menu from anywhere
- **Status bar** above every prompt: `~/workspace (branch) · model · tokens/ctx · $cost`
  — tokens come from each provider's usage stream, cost/context from models.dev,
  git branch parsed from `.git/HEAD`
- ↑/↓ history, cursor editing, and piped-stdin fallback for scripts/CI

## Terminal Session

```
  █ ▄▄ █  REPTAR AGENT
  █ ▀▀ █  anthropic/claude-sonnet-4-5
  ▀▀▀▀▀

▸ fix the failing test in features/posts.cs
→ read_file {"path":"features/posts.cs"}
✓ read_file 3ms
→ run_command {"command":"dotnet test"}
✗ run_command 8.4s

**Root cause:** …
```

Slash commands: `/reset` `/history` `/tools` `/model <p/m>` `/sessions` `/session <id>` `/new <title>` `/help` `/exit`. Ctrl+C cancels the current turn; press twice to quit.

## Workspace Tools (mounted by `app.UseWorkspaceTools`)

| Tool | What it does |
| --- | --- |
| `read_file` | Reads a file with line numbers, `start_line`/`end_line`, 50 KB cap. |
| `write_file` | Creates/overwrites files (auto-mkdirs) and returns a unified diff. |
| `edit_file` | Exact string replacement (first match or all) with diff preview. |
| `list_dir` | Recursive tree (depth 1–6) ignoring `.git`, `bin`, `obj`, `node_modules`, `.reptar`, `dist`. |
| `run_command` | Runs shell commands in the workspace root with a clamped timeout. |

Every path is resolved against the workspace root and **any escape is rejected** (`../../`, absolute paths outside the root). Tool calls dispatch **in-process** through `app.ExecuteAiToolAsync` — same route pipeline, same validation, zero HTTP overhead.

## Web UI & API

The web companion (`bun reptar agent --web`) is a modern, responsive chat interface inspired by **shadcn/ui AI Elements**:
- **Centered thread layout** with fluid reading column and collapsible glass sidebar
- **Interactive Tool Cards**: expandable execution inspector for `read_file`, `write_file`, `edit_file`, `list_dir`, `run_command` with stdout viewer, elapsed ms, and diffs
- **Trace / Reasoning Accordion**: collapsible multi-step thinking indicator
- **Rich Markdown**: styled headings, tables, blockquotes, codeblocks with language badges and instant one-click copy
- **Floating Prompt Dock**: auto-growing textarea, `/` slash commands autocomplete, `@` workspace files autocomplete, model pill, and stop/stream button
- **Provider & Credentials Modal**: test and persist provider keys directly from the browser to `~/.reptar/agent/auth.json` (0600 permissions)
- **Empty State Hero**: quick-starter cards to inspect the workspace, run tool tests, map routes, and ask coding tasks

| Route | Description |
| --- | --- |
| `POST /api/chat` | `{ message, sessionId?, model?, provider? }` → SSE stream of typed JSON events: `turn_start`, `text`, `tool_start`, `tool_end`, `usage`, `error`, `done`. |
| `GET /api/status` | Workspace root, git branch, active provider/model, and provider credential sources. |
| `GET /api/workspace/files` | Workspace file tree for `@` context autocomplete. |
| `GET /api/sessions` | Saved sessions list. |
| `POST /api/sessions` | Create a new session with custom title. |
| `DELETE /api/sessions/:id` | Delete a saved session and its messages. |
| `PATCH /api/sessions/:id` | Rename session title. |
| `GET /api/sessions/:id/messages` | Full message history including tool calls and parameters. |
| `GET /api/tools` | Mounted tool definitions and methods. |
| `GET /api/models` | Live or curated model catalog for a provider. |
| `POST /api/providers/connect` | Test connection and save credentials to `~/.reptar/agent/auth.json`. |
| `POST /api/providers/disconnect` | Clear stored credentials for a provider. |
| `GET /` | Embedded zero-build React SPA. |

Example:

```bash
curl -N -X POST localhost:4040/api/chat \
  -H 'content-type: application/json' \
  -d '{"message":"list the files in this workspace"}'
```

The same endpoints work with `@reptar/client`'s typed `createClient()` in any frontend; the built-in SPA keeps everything in one binary by loading React from an ESM CDN with zero build step.

## Session Persistence

`schema.cs` defines the tables with plain `TableDef`:

```
agent_sessions: id (PK), title, created_at
agent_messages: id (PK), session_id (indexed), role, content, tool_calls, created_at
```

Sessions are shared across the terminal and web modes (same `--db` file), and row mapping uses **manually registered zero-reflection mappers**, so the whole app stays Native AOT clean.

## Build (single binary)

```bash
cd examples/agent-cli
dotnet publish app.cs -c Release -r linux-x64 --self-contained \
  -p:PublishAot=true -p:DebugType=None -p:DebugSymbols=false -o publish
./publish/app --self-test   # 7/7
```

Measured on osx-arm64: **12 MB** binary, **~10–20 ms** cold start. The `deploy/Dockerfile` produces the linux-x64 container variant.

## AOT rule of thumb

Under Native AOT, `ctx.Json(new { ... })` and `ctx.Json(record)` serialize via reflection and **fail at runtime**. Use `JsonObject`/`JsonArray` construction or `ctx.JsonRaw(...)` in code paths you want AOT-clean — see `features/chat.cs` and `framework/Reptar/AI/WorkspaceTools.cs` for the pattern, and `plans/agent-engine.md` for the full findings.
