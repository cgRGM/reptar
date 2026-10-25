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

**Model resolution:** `--model`/`--provider` flags → `REPTAR_AGENT_MODEL`/ `REPTAR_AGENT_PROVIDER` → first of `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `GEMINI_API_KEY` / `OLLAMA_HOST` / `OPENROUTER_API_KEY` / `AI_GATEWAY_API_KEY` → offline Echo provider (deterministic reply, tools still real).

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

## Web API

| Route | Description |
| --- | --- |
| `POST /api/chat` | `{ message, sessionId?, model?, provider? }` → SSE stream of typed JSON events: `text`, `tool_start`, `tool_end`, `done`, `error`, then `data: [DONE]`. |
| `GET /api/sessions` | Saved sessions (newest first). |
| `GET /api/sessions/:id/messages` | Full message history incl. tool calls. |
| `GET /api/tools` | Mounted tool definitions. |
| `GET /` | Embedded React SPA. |

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
