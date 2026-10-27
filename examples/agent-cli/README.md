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

The flow mirrors Pi and OpenCode, with a zero-dependency terminal UI (Spectre.Console ideas, ~0 MB: arrow-key selection with type-to-search and paging, hidden API-key entry with validation re-ask, a live spinner for the connection test, and bordered auto-measured tables — every component falls back to plain numbered input when stdin is piped, so scripted and CI flows keep working):

- **Provider picker** with live status per row (`✓ env: OPENAI_API_KEY`, `✓ saved`, `• not connected`) — arrow keys navigate, typing filters
- **Connection test under a live spinner** against the provider's model list
- **Model picker** enriched with the [models.dev](https://models.dev) curated catalog (display names like `Claude Opus 4.5 · 200k ctx`, and it works with **zero credentials** — `/models anthropic` browses before you ever connect)
- Saves to `~/.reptar/agent/auth.json` (flat map, `0600` permissions, last-connect becomes the default provider)

Built-in providers: OpenAI, Anthropic, Google Gemini, Ollama (local), OpenRouter, Vercel AI Gateway, **OpenCode Zen** (`opencode.ai/zen/v1`), and **Cloudflare AI Gateway** (your `gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/{provider}` URL + the upstream provider's key). `Other` registers any custom OpenAI-compatible endpoint (id + base URL + key + model), like OpenCode's custom provider flow. **Precedence — env always wins over saved, and the status table shows which source is live:** `--model`/`--provider` flags → `REPTAR_AGENT_*` env vars → provider env keys (`OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `GEMINI_API_KEY` / `OLLAMA_HOST` / `OPENROUTER_API_KEY` / `AI_GATEWAY_API_KEY`) → credentials saved by `/connect` → offline Echo provider (deterministic reply, tools still real).

## The Input Editor

The prompt is a pi-style line editor (raw ANSI, zero dependencies):

- **`/` → command dropdown** — every slash command with its description, filtered as you type (name _and_ description, pi-style fuzzy), arrow keys to navigate, Enter to accept the top suggestion, ESC to dismiss
- **`@path` → file attach** — workspace files (respecting the ignore list) match as you type; Enter accepts the top match, a second Enter submits. Text files inline as `[file: path]` context blocks; **images (`.png .jpg .jpeg .webp .gif`) attach as vision content blocks** for OpenAI, Anthropic, and Gemini
- **`!command` → shell passthrough** — runs in the workspace, shows output, never goes to the model
- **Ctrl+P → command palette** — open the command menu from anywhere
- **Status bar** above every prompt: `~/workspace (branch) · model · tokens/ctx · $cost` — tokens come from each provider's usage stream, cost/context from models.dev, git branch parsed from `.git/HEAD`
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

Slash commands: `/reset` `/history` `/tools` `/model <p/m>` `/sessions` `/session <id>` `/new <title>` `/compact [focus]` `/help` `/exit`.

**The prompt stays live while the agent works.** Typing Enter during a run queues a
*steering* message, delivered after the current turn's tool calls and before the next
model request. Esc or Ctrl+C aborts the turn and returns you to the editor with the
queue drained back into it — press Ctrl+C again to quit.

The status ribbon shows context pressure (`45.2k/200k (23%)`) and running cost. `PgUp` /
`PgDn` scroll the transcript. When context crosses the window minus the reserve, the
agent compacts automatically: older messages are replaced by a structured checkpoint,
the raw transcript stays on screen, and the session continues.

## RPC Mode — use Reptar as an agent server

```bash
bun reptar agent --mode rpc            # JSONL on stdin/stdout
```

A long-lived process that speaks the same protocol shape as pi's RPC mode, so the C#
TUI, the web SPA, and a client in any other language drive **one** agent implementation.

```
stdin   → {"id":"1","type":"prompt","message":"review this repo"}
stdout  ← {"id":"1","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}
stdout  ← {"type":"agent_start","maxSteps":24}
stdout  ← {"type":"turn_start","step":1}
stdout  ← {"type":"message_update","step":1,"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Sure"}}
stdout  ← {"type":"tool_execution_start","step":1,"toolCallId":"c1","toolName":"read_file","args":{"path":"a.cs"}}
stdout  ← {"type":"tool_execution_update","step":1,"toolCallId":"c1","partialResult":{...}}
stdout  ← {"type":"tool_execution_end","step":1,"toolCallId":"c1","isError":false,"durationMs":3.1}
stdout  ← {"type":"agent_end","stopReason":"completed","finalContent":"..."}
stdout  ← {"type":"agent_settled","aborted":false}
```

Rules that matter when writing a client:

- **Split records on LF only.** Do not use a line reader that also splits on `U+2028`/
  `U+2029` — they are legal inside JSON strings.
- **Correlate by `id`, not order.** Commands are handled asynchronously; a long prompt
  never blocks the next command.
- **`prompt` succeeding means accepted, not finished.** Wait for `agent_settled`;
  retries, compaction, and steering can follow `agent_end`.
- **stdout is protocol only.** Build diagnostics and logs go to stderr.

Commands: `prompt` `steer` `follow_up` `abort` `clear_queue` `new_session` `get_state`
`get_messages` `get_last_assistant_text` `set_model` `get_available_models`
`set_steering_mode` `set_follow_up_mode` `set_auto_compaction` `set_auto_retry`
`compact` `get_session_stats` `set_session_name` `bash` `shutdown`.

### Clients

The **C# client is first-class**: [`rpc/RpcClient.cs`](rpc/RpcClient.cs) spawns the server,
correlates responses by id, decodes events through the same `AgentEventJson` the server
writes, and exposes typed helpers.

```bash
bun reptar agent --rpc-demo "explain this repo"   # C# client → C# server, round trip
```

```csharp
await using var rpc = RpcClient.Start(workspace: "/path/to/project");

var result = await rpc.PromptAndWaitAsync(
    "fix the failing test",
    onText: delta => Console.Write(delta));

Console.Error.WriteLine($"{result.Summary.StopReason} in {result.Summary.StepsTaken} steps");
```

Because the protocol is plain JSONL, any language works. This Python client is complete:

```python
import json, subprocess

proc = subprocess.Popen(["bun", "reptar", "agent", "--mode", "rpc"],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE)

proc.stdin.write(json.dumps({"id": "1", "type": "prompt",
                             "message": "hello"}).encode() + b"\n")
proc.stdin.flush()

for line in proc.stdout:                      # binary read: splits on LF only
    record = json.loads(line)
    if record.get("type") == "message_update":
        update = record["assistantMessageEvent"]
        if update["type"] == "text_delta":
            print(update["delta"], end="", flush=True)
    elif record.get("type") == "agent_settled":
        break
```

## Workspace Tools (mounted by `app.UseWorkspaceTools`)

| Tool | What it does |
| --- | --- |
| `read_file` | Reads a file with line numbers, `start_line`/`end_line`, 50 KB cap. |
| `write_file` | Creates/overwrites files (auto-mkdirs) and returns a unified diff. |
| `edit_file` | Exact string replacement (first match or all) with diff preview. |
| `list_dir` | Recursive tree (depth 1–6) ignoring `.git`, `bin`, `obj`, `node_modules`, `.reptar`, `dist`. |
| `run_command` | Runs shell commands in the workspace root with a clamped timeout. Streams output line-by-line while it runs. |

Every path is resolved against the workspace root and **any escape is rejected** (`../../`, absolute paths outside the root). Tool calls dispatch **in-process** through `app.ExecuteAiToolAsync` — same route pipeline, same validation, zero HTTP overhead.

Tool output is bounded, never silently clipped: `run_command` keeps the **tail** (that is
where a failing build says why) capped at 2 000 lines / 50 KB, spills the full log to
`.reptar/tool-output/`, and reports the path so the model can read it back.

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

## The SDK — five lines to an agent

`framework/Reptar.AI/AgentSdk.cs` is the C# equivalent of pi's `createAgentSession`:

```csharp
await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
{
    WorkspaceRoot = "/path/to/project",
    AllowShell = true,
    Tools = ToolSelection.Only("read_file", "edit_file", "*_dir"),
});

var summary = await agent.PromptAsync("fix the failing test", onText: Console.Write);
Console.WriteLine($"{summary.StopReason} · {agent.Stats().ContextPercent}% context");
```

`CreateAsync` resolves the provider (flags → env → saved auth → echo), mounts the workspace
tools, opens or resumes a session, applies the tool selection, and looks up the context window
so auto-compaction works. `Attach` does the same over an app you already configured.

**Tool selection** has three shapes, and the differences matter:

| Shape | Meaning |
| --- | --- |
| `Only("read_file", "*_dir")` | Allowlist — nothing outside it is callable |
| `AllExcept("run_command")` | Denylist, applied after the allowlist |
| `Adjusting("+run_command", "-write_file")` | Not an allowlist — edits the default selection |

Mixing bare names with `+`/`-` throws, because "only these" and "add this one" are contradictory.

## Codemode — let the model write a script

`framework/Reptar.AI/CodeMode.cs` registers a `codemode` tool: the model writes a small script
that calls other tools, and **only the script's output enters the conversation**. That turns
"find every TODO and group it by file" from dozens of context-filling tool calls into one turn.

```csharp
var agentTools = app.GetAiTools(onlyExplicit: true).ToList();
var codemode = CodeMode.Register(app, agentTools);

var prompt = mySystemPrompt + CodeMode.DescribeTools(agentTools);
// agent tool list: agentTools.Append(codemode)
```

```
let files = tools.list_dir({ path: ".", depth: 3 });
foreach (let f in files.entries) {
    if (f.type == "file" && f.path.endsWith(".cs")) {
        let src = tools.read_file({ path: f.path });
        if (src.content.includes("TODO")) { text(f.path); }
    }
}
```

pi runs JavaScript in a QuickJS VM. Reptar targets Native AOT and NetWasm, so there is no JS
engine to embed — `ScriptInterpreter` is a hand-written interpreter for a C#-flavoured subset
instead. Same capability model:

- **Tools are the only way out.** No filesystem, no network, no timers, no `eval`.
- **Only `text()`, `log()`, and the return value reach the model** — a script can read a 2 MB
  log and hand back three lines.
- **`spawn()` + `join()`** run tool calls concurrently inside one turn.
- **Bounded three ways:** step budget, cancellation token, output cap. An infinite loop returns
  `StepLimit`, not a hang.

Lambdas work: `xs.filter((x) => x > 1)`, `xs.map(x => x * 2)`, block bodies, and closures that
capture their defining scope.

Not yet: `image()`, script-level key/value store (`store`/`load`).

## MCP — every MCP server is a tool source

`framework/Reptar.AI/McpClient.cs` speaks MCP over stdio: JSON-RPC 2.0, `initialize` →
`tools/list` → `tools/call`.

```csharp
await using var source = await McpToolSource.ConnectAsync([
    McpServerConfig.Npx("files", "@modelcontextprotocol/server-filesystem", "/tmp"),
    McpServerConfig.Npx("github", "@modelcontextprotocol/server-github"),
]);

source.Mount(app);                                // tools become in-process Reptar routes
var tools = app.GetAiTools(onlyExplicit: true);   // mcp__files__read_file, ...
```

Tools are namespaced `mcp__<server>__<tool>` (pi's convention), with `-` → `_` so the name is
also a valid codemode identifier — MCP tools are therefore scriptable:

```
let hits = tools.mcp__files__search_files({ path: ".", pattern: "TODO" });
text(hits.length);
```

A server that fails to start is reported and skipped; one bad server does not take the agent
down. Only stdio transport is implemented.

## Prompt templates

Drop markdown files under `.reptar/prompts/` and they become slash commands:

```markdown
---
description: Fix failing tests
argument-hint: <path>
---
Look at $1 and make the tests pass. Run the suite before answering.
```

`/fix-tests src/storage.cs` expands to the body with `$1` substituted. Also supports `$@`,
`$ARGUMENTS`, and `$0` (the template name). `/templates` lists what is loaded.

Project templates shadow user templates. They only load when the workspace is **trusted** —
see below.

## Model aliases

`--model fast` resolves through an alias instead of a full `provider/model` spec:

```
alias    model                        description
fast     openai/gpt-4o-mini           Cheap and quick
deep     anthropic/claude-sonnet-4-5  Stronger reasoning
local    ollama/qwen2.5-coder         Runs on your machine
offline  echo/echo                    Deterministic echo provider
```

`bun reptar agent --aliases` prints the table. Layer your own with `ModelAliases.Merge`.

## Project trust

An agent that reads `.reptar/settings.json` or `.agents/skills/*` from a directory it did not
create will follow instructions someone else wrote. Cloning a repo should not execute that
repo's configuration.

So nothing project-local loads until the absolute path is trusted, and trust lives in
`~/.reptar/trust.json` — never in the project. `Unknown` is not trust; a corrupt store fails
closed. A directory with no agent resources auto-trusts so you are not prompted for nothing.

## Durable Mode — crash-safe agent runs

`framework/Reptar.AI/DurableAgent.cs` is a focused port of pi's `pi-durable` harness: the
transcript and task state are committed **before** anything is reported, so a process that
dies mid-turn resumes where it stopped instead of starting over.

```csharp
await using var harness = await DurableAgentHarness.OpenAsync(app, new DurableHarnessOptions
{
    Agent = new DurableAgentChoices { Model = "anthropic/claude-sonnet-4-5" }
});

var submission = await harness.SubmitAndRunAsync("migrate the storage layer", client);

// After a crash, on the next process start:
await harness.ResumeAsync(client);      // picks up orphaned tasks
var again = await harness.SubmissionAsync(submission.Id);   // same submission
```

What it guarantees:

- **Commit before reveal.** The submission and its input entry land in one transaction; a
  crash either admits both or neither.
- **Idempotent submits.** Re-submitting with the same `requestId` returns the original.
- **Resume.** A task left `running` is marked `orphaned` on open and re-run. The transcript is
  rebuilt *from storage*, so the resumed run sees exactly what the model saw.
- **Per-turn checkpoints.** `AgentLoopConfig.FinishTurn` commits after every assistant turn
  plus its tool results.
- **Read-only observers.** `ReadOnly = true` refuses writes and task execution.

Deliberately not ported yet: extensions, code mode, forks, child tasks, telemetry spans.
`agent_entries.parent_id` is already there for branching.

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
