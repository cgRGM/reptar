# Pi Parity Port — Reptar Agent Deep Dive & Plan

**Status:** phases 1–4 shipped · extensions out of scope **Sources:** `github.com/earendil-works/pi` @ main (`packages/agent`, `packages/coding-agent`, `packages/ai`), plus the installed `@earendil-works/pi-coding-agent@1.4.0` docs bundle. **Subject:** `framework/Reptar.AI/` + `examples/agent-cli/` in this repo.

---

## 0. Executive summary

The Reptar agent is **not missing features so much as missing the loop contract**. Pi's "feel" is not the TUI — it is a small number of guarantees the loop makes that the client can then render naively. Reptar's `StreamingAgentHarness` breaks four of them, and the terminal client is built in a way that cannot recover from the breakage.

The five things that actually make pi feel like pi:

1. **The run never blocks the input.** You can type, steer, and abort while the agent works. Reptar's TUI `await`s the whole run inside its key handler (`ReptarTerm.cs:698`), so the UI is frozen for the entire turn and its own Ctrl+C abort branch is unreachable.
2. **Every message has an authoritative end.** `message_end` carries the final message; the client never has to reconstruct state from deltas. Reptar has `TextDelta` only, and `ReptarTerm` accumulates into a `StringBuilder` that is thrown away.
3. **Tools stream partial output.** `tool_execution_update` exists so `bash` shows life. Reptar tools return only a final result, so any long command looks like a hang.
4. **History is an append-only tree, not a list.** Forking, branching, and compaction are properties of the storage format, not of the UI.
5. **The loop is a scheduler, not a `while`.** Steering, follow-up, retry, compaction-recovery and overflow-recovery all feed the same queue.

Reptar currently has a `while (true)` with a `maxSteps` counter. That is a ReAct demo, not an agent harness. This document maps pi's architecture onto Reptar's, lists the concrete defects found with file:line, and lays out a four-phase port that keeps Reptar as the server and adds a thin C# client.

---

## 1. How pi is layered

Pi separates three concerns that Reptar currently mixes:

| Layer | Package | Responsibility | Reptar equivalent today |
| --- | --- | --- | --- |
| Provider transport | `pi-ai` | Stream events (`text_delta`, `thinking_delta`, `toolcall_delta`), model catalog, usage/cost, `convertToLlm` message types | `Reptar.AI/LlmTypes.cs` + `LlmOpenAi/Anthropic/Gemini.cs` — **roughly at parity** |
| Agent loop | `pi-agent` (`agent-loop.ts`, 959 lines) | Turn scheduling, tool dispatch, steering/follow-up queues, abort, retry hooks | `Reptar.AI/AgentHarness.cs` (302 lines) — **far behind** |
| Product | `pi-coding-agent` | Sessions, tools, prompt composition, compaction, skills, extensions, TUI/RPC | `examples/agent-cli/` + `SessionStore.cs` — **partial** |

The important structural insight: **`pi-agent` knows nothing about files, bash, or sessions.** It takes an `AgentContext` (messages + tools) and an `AgentLoopConfig` (callbacks), and emits events. Everything product-specific arrives through config callbacks:

```ts
// packages/agent/src/types.ts
interface AgentLoopConfig {
  model: Model;
  convertToLlm: (AgentMessage[]) => Message[];      // AgentMessage -> provider Message
  transformContext?: (AgentMessage[]) => AgentMessage[];  // prune/inject before convert
  getSteeringMessages?: () => AgentMessage[];       // typed-while-running
  getFollowUpMessages?: () => AgentMessage[];       // queued after stop
  prepareNextTurn?: (turn) => NextTurnSnapshot;     // compaction, model switch
  prepareRequest?: (ctx) => RequestUpdate;          // per-request context edit
  finishTurn?: (turn) => { action: "end" | "continue" };
  beforeToolCall?: (call) => { block?: boolean; reason?: string };
  afterToolCall?: (call) => Partial<AgentToolResult>;
  getApiKey?: (provider) => string;                 // expiring OAuth tokens
}
```

Reptar's harness takes `(stream, toolExecutor, tools, maxSteps)` and nothing else. That is why every feature has to be bolted into the product layer, and why the product layer is 5,500 lines of TUI that still cannot abort a turn.

---

## 2. The agent loop contract (what we must replicate)

Source: `packages/agent/src/agent-loop.ts`. Behaviour that matters:

### 2.1 Event surface

```
agent_start
turn_start
  message_start { message }
  message_update { assistantMessageEvent }   // text_/thinking_/toolcall_ start|delta|end
  message_end   { message }                  // authoritative
  tool_execution_start  { toolCallId, toolName, args }
  tool_execution_update { toolCallId, partialResult }
  tool_execution_end    { toolCallId, result, isError, durationMs }
turn_end { message, toolResults }
agent_end { messages }
agent_settled { aborted }                    // pi-coding-agent layer: no more auto work
```

`agent_end` closes one low-level run; **`agent_settled`** is the one clients wait on, because retry/compaction/steering can start another run. Reptar has no equivalent of either — it has `RunFinished`, which is emitted from five different places and means different things.

### 2.2 Two nested loops

```
outer: while (true)                              // follow-up re-entry
  inner: while (hasMoreToolCalls || pending)     // tool calls + steering
    prepareNextTurn()                            // <-- compaction happens here
    inject pending steering messages
    prepareRequest()
    stream assistant response
    if stopReason in {error, aborted}: turn_end, agent_end, RETURN
    if toolCalls: execute batch (parallel unless a tool says sequential)
    finishTurn() -> end | continue
    poll steering
  poll follow-up; if any, continue outer
  break
```

Two details Reptar lacks and which change UX materially:

- **Steering is polled before the next request, not after the run.** A message typed at second 3 of a 40-second run lands before the 4th LLM call, not after the 12th.
- **`prepareNextTurn` is the compaction hook.** Auto-compaction is not a background job; it runs in the loop between turns, using the canonical projected context after tool results land.

### 2.3 Tool batch semantics

- **Parallel by default**, sequential when `tool.executionMode === "sequential"` or `config.toolExecution === "sequential"`. Reptar is always sequential.
- **`terminate`**: if every finalized call in a batch returns `terminate: true`, the run ends. This is how a `finish` tool stops the agent without a special case in the loop.
- **`stopReason === "length"` fails the whole batch.** Truncated JSON args can still _parse_ (there is a salvage parser), so executing them silently runs a half-formed command. Pi reports each as an error and lets the model re-issue. Reptar has no such guard and `finishReason` is captured but never read (`AgentHarness.cs` — `Completed` event stored into a local).
- **Abort is checked between tool calls**, and a mid-batch abort breaks out of the loop rather than unwinding the whole run.

### 2.4 Message model: `AgentMessage` vs `Message`

Pi keeps a rich application message and converts at the provider boundary:

| `AgentMessage` role | Provider form |
| --- | --- |
| `system` | system (with `sections`, `toolsAdded`, `toolsRemoved`) |
| `user` | user |
| `assistant` | assistant |
| `toolResult` | tool |
| `bashExecution` | **user** (``Ran `cmd`\n```output``` ``) unless `excludeFromContext` |
| `custom` | user |
| `branchSummary` | user |
| `compactionSummary` | user |

This is why `!command` output can be in the transcript but _not_ be a tool result, and why a compaction summary can be a first-class entry without being a fake assistant message.

Reptar's `ChatMessage(Role, Content, Name, ToolCallId, ToolCalls, Images)` is close to the provider `Message` and has no room for these roles. `convertToLlm` has no analogue.

### 2.5 System prompt composition

Pi's prompt is a **named section map**, not a string:

```ts
sections = { preamble, tools, guidelines, cwd, contextFiles, skills, ... }
```

The transcript stores `system` messages carrying `sections`, and later system messages _patch_ sections by name (`null` deletes). Replaying them in order reproduces current prompt state — so changing the tool loadout or adding a skill is an append, not a rewrite, and it is cache-friendly.

Reptar passes one `systemPrompt: string` and inserts it once if absent (`AgentHarness.cs:73-74`). Model switching, skill addition, or tool gating therefore requires rewriting history.

### 2.6 Session format

JSONL, append-only **tree** (`id`/`parentId`), entry types: `session`, `message`, `model_change`, `thinking_level_change`, `usage`, `compaction`, `context_edit`, `branch_summary`, `custom`, `custom_message`, `label`, `session_info`.

Consequences a flat table cannot reproduce:

- Fork/branch/clone are cheap (`parentId` repoint).
- `context_edit` omits or rewrites a single earlier message **for model context only**, leaving the raw transcript, exports, and billing intact. This is how overflow recovery hides a bad attempt without lying about history.
- Compaction stores `firstKeptEntryId` + a full prompt/tool checkpoint.
- `usage` entries bill work that is not an assistant message (cache warming, summaries).

Reptar: `SessionStore.cs` = sessions + messages tables, linear. No branching, no compaction entries, no context edits, no usage accounting beyond token counters in the TUI.

---

## 3. Reptar's current state — verified defects

Every item below was confirmed by reading the file, not inferred.

### 3.1 Loop-level (`framework/Reptar.AI/AgentHarness.cs`)

| # | Defect | Impact |
| --- | --- | --- |
| L1 | No steering / follow-up queue | Cannot type while the agent runs; cannot redirect it |
| L2 | No `tool_execution_update` | Long `run_command` looks like a hang |
| L3 | Sequential tool execution only | 3 independent reads take 3× as long |
| L4 | `finishReason` captured, never used | A `length`-truncated batch executes malformed args |
| L5 | No retry on 429/5xx/overloaded | One rate limit kills the turn |
| L6 | No compaction hook (`prepareNextTurn`) | Long sessions hard-fail at the context limit |
| L7 | No `convertToLlm` / rich message roles | Bash output, summaries, custom context have nowhere to live |
| L8 | `ToolApproval` / `ToolApprovalGate` exist but are **not wired into the streaming harness** — only exercised in `Reptar.Tests/AgentEngineTests.cs` | Approval is a library, not a behaviour |
| L9 | `RunFinished` emitted from 5 sites with 3 different meanings | Client cannot tell "done" from "gave up" from "cancelled" |
| L10 | `systemPrompt` injected once, never updatable | Model/skill/tool changes require history rewrite |

### 3.2 Terminal client (`examples/agent-cli/terminal/`)

| # | Defect | Location |
| --- | --- | --- |
| T1 | **Run is `await`ed inside the key handler** → UI frozen for the whole turn | `ReptarTerm.cs:698` → `SendChatMessageAsync` |
| T2 | **Ctrl+C abort is dead code** — the `if (_isStreaming)` branch can never run because the read loop is blocked in `await` | `ReptarTerm.cs:268-296` |
| T3 | `StreamingAgentHarness.RunAsync` called with **no `CancellationToken`** → no abort path exists at all | `ReptarTerm.cs:1119` |
| T4 | `@file` mentions are parsed into `resolvedPrompt` which is **never used**; `prompt` is sent instead | `ReptarTerm.cs:1081/1093/1101` |
| T5 | `_totalCost` is read by the status ribbon but **never written** → cost never displays | `ReptarTerm.cs:1229` vs `:120` |
| T6 | Chat view renders only the **last 6 messages**, no scrollback | `ReptarTerm.cs:1377` |
| T7 | Assistant text rendered as **raw `WrapText`**, not markdown — `AnsiMarkdown` exists but is unused here | `ReptarTerm.cs:1391` |
| T8 | **Tool results are never rendered**; `_activeTools` shows one line, last tool only | `ReptarTerm.cs:1408` |
| T9 | Reasoning/thinking deltas are dropped | no `ReasoningDelta` case |
| T10 | `Render()` (full double-buffered frame) called **per text delta** | `ReptarTerm.cs:1135` |
| T11 | `TerminalAgent` (line-mode fallback) cancels `_shutdown` on Ctrl+C, which is the _same_ token gating the REPL → Ctrl+C quits instead of aborting, despite printing "cancelling current turn" | `TerminalAgent.cs:59-71` + `:128` |

### 3.3 Web client (`examples/agent-cli/features/chat.cs`)

Better than the TUI (it uses a `Channel` + `Task.Run`, so streaming is decoupled), but:

| # | Defect |
| --- | --- |
| W1 | SSE event names (`turn_start`/`text`/`tool_start`/`tool_end`/`usage`/`error`/`done`) diverge from pi's canonical names → no shared client code with the terminal |
| W2 | No `steer`/`follow_up`/`abort` endpoints — `POST /api/chat` is fire-and-forget |
| W3 | Tool results truncated at 2000 chars in the event, no `tool_execution_update` |
| W4 | No session branching, no compaction, no token-budget awareness |

---

## 4. Gap matrix (pi → Reptar)

| Capability | pi | Reptar today | Port cost |
| --- | --- | --- | --- |
| Streaming text | `message_update` w/ cumulative snapshot | `TextDelta` only | S |
| Streaming tool output | `tool_execution_update` | — | S |
| Authoritative message end | `message_end` | — | S |
| Abort mid-turn | `AbortSignal` threaded everywhere | token exists, unused | S |
| Steering while running | `getSteeringMessages` | — | M |
| Follow-up queue | `getFollowUpMessages` | — | S |
| Parallel tools | default on | never | S |
| Per-tool sequential | `executionMode` | — | S |
| Truncation guard | `stopReason === "length"` | — | S |
| Tool approval hooks | `beforeToolCall`/`afterToolCall` | library unwired | M |
| Retry w/ backoff | `set_auto_retry` | — | M |
| Compaction | `prepareNextTurn` + entries | — | L |
| Context edits | `context_edit` entries | — | M |
| Session tree / fork / branch | JSONL tree | flat tables | L |
| Prompt sections | named map + patches | one string | M |
| Skills on demand | `SkillsDirectory.cs` ✔ | partial | S |
| Thinking levels | 7 levels, per-model | — | M |
| Usage/cost accounting | `Usage` per message + `usage` entries | counters only, cost broken | S |
| RPC protocol | JSONL stdin/stdout, 30+ commands | HTTP+SSE, 3 endpoints | M |
| Extension surface | TS modules in-process | — | XL (skip) |

**Legend:** S ≈ hours, M ≈ days, L ≈ a week, XL ≈ out of scope.

---

## 5. Target architecture for Reptar

Keep the user's framing: **Reptar is the server, the client is dumb.** The goal is that the C# TUI, the React SPA, and a future `reptar-agent` RPC mode all consume the _same_ event stream, and the loop guarantees the same contract as pi.

```
┌─────────────────────────────────────────────────────────────┐
│ clients                                                      │
│   C# TUI (ReptarTerm)   React SPA   JSONL RPC   CI script    │
└───────────────┬──────────────┬───────────┬──────────────────┘
                │   AgentEvent stream (one vocabulary)         │
┌───────────────▼──────────────▼───────────▼──────────────────┐
│ framework/Reptar.AI                                          │
│                                                              │
│  AgentLoop            <- the scheduler (port of runLoop)     │
│   ├ AgentContext      messages + tools                       │
│   ├ AgentLoopConfig   convertToLlm, steering, prepareNextTurn │
│   └ AgentEvent        agent_/turn_/message_/tool_execution_   │
│                                                              │
│  AgentSession         <- product state (port of agent-session)│
│   ├ SessionStore      append-only tree (JSONL or SQLite)      │
│   ├ Compaction        summary + firstKeptEntryId              │
│   ├ PromptComposer    named sections + patches                │
│   └ ToolRegistry      execute(id,args,signal,onUpdate)        │
└──────────────────────────────────────────────────────────────┘
```

### 5.1 Non-negotiable design decisions

1. **`AgentLoop` is transport-agnostic and callback-driven.** No `ReptarApp` reference, no session, no filesystem. It takes context + config and emits events. This is what makes the loop testable and lets the RPC mode reuse it.
2. **Events are a closed union with pi's names.** Not `TurnStarted`/`TextDelta` — use `agent_start`/`turn_start`/`message_start`/`message_update`/`message_end`/ `tool_execution_start|update|end`/`turn_end`/`agent_end`/`agent_settled`. One vocabulary across C#, TS, and JSONL. Renaming later is expensive; do it now.
3. **`AgentMessage` gains roles instead of overloading `ChatMessage`.** Add a discriminator so `bashExecution`, `custom`, `compactionSummary`, `branchSummary` can exist, and give `AgentLoopConfig.convertToLlm` the mapping. Keep `ChatMessage` as the provider-facing type.
4. **Tools take `(id, argsJson, signal, onUpdate)`.** `onUpdate` is how `run_command` streams. Signature change is unavoidable and cheap to absorb (5 tools).
5. **`AgentSession` owns the queue, the loop does not.** Steering/follow-up are `AgentSession` state polled through config callbacks — exactly pi's split.
6. **Storage becomes append-only entries.** Even in SQLite: one `agent_entries` table with `id`, `parent_id`, `type`, `payload_json`. Flat message tables cannot express branching or context edits, and retrofitting is a migration nightmare.

---

## 6. Phased plan

### Phase 1 — Loop contract ✅ shipped

**New files**

| File | What it is |
| --- | --- |
| `framework/Reptar.AI/AgentLoop.cs` | The scheduler. Port of pi's `runLoop`. Transport-agnostic: takes context + `AgentLoopConfig`, emits events. |
| `framework/Reptar.AI/AgentSession.cs` | Queue owner (`steer`/`followUp`/`abort`/`clearQueue`), queue modes, `AgentRetryPolicy`. |
| `framework/Reptar.AI/AgentEventJson.cs` | JSONL serialization using pi's record names — the client/server wire format. |
| `framework/Reptar.Tests/AgentLoopTests.cs` | 14 contract tests for the guarantees below. |

**Guarantees now enforced**

- [x] Event union renamed to pi vocabulary: `agent_start`/`turn_start`/`message_start`/ `message_update`/`message_end`/`tool_execution_start|update|end`/`turn_end`/ `agent_end`/`agent_settled`
- [x] `message_end` carries the authoritative final message; clients never reconstruct
- [x] `tool_execution_update` + `OnUpdate` on `AgentToolInvocation`; `run_command` now streams line-by-line through `AgentToolStreaming` instead of buffering to exit
- [x] Parallel tool execution by default, `SequentialTools` to opt out
- [x] `stopReason == length` → the whole batch fails with a re-issue instruction, nothing executes
- [x] `terminate` honoured at batch level
- [x] `BeforeToolCall` / `AfterToolCall` hooks (composes with `ToolApprovalGate`)
- [x] `agent_settled` distinct from `agent_end`
- [x] Cancellation observed between tool calls and mid-stream
- [x] Steering + follow-up queues with `All` / `OneAtATime` modes
- [x] `PrepareNextTurn` hook in place for compaction
- [x] `AgentEventJson.ToJson()` — one JSONL record per event, pi field names
- [x] `AgentRetryPolicy` (exponential backoff + jitter, transient-status classifier)

**Client fixes**

- [x] TUI no longer blocks: run executes on a background task, events flow through a `Channel`, the prompt stays live
- [x] Typing while streaming steers the agent; Esc / Ctrl+C abort the turn and keep the REPL
- [x] Ctrl+C in line mode aborts the turn instead of quitting
- [x] `@file` mentions actually reach the model (was parsed then discarded)
- [x] Cost display wired to models.dev pricing
- [x] Markdown rendering, full scrollback (PgUp/PgDn), tool cards with live output, thinking blocks — replacing "last 6 messages as raw wrapped text"
- [x] Render throttled to a dirty flag instead of one full frame per token
- [x] Empty-state `1`/`2`/`3` starters reachable again (tab-switch shortcut was eating them)
- [x] Web SSE renamed to the canonical event names + `tool_execution_update`, `message_end`, `agent_settled`

**Breaking changes made** (safe: pre-release framework)

- `AgentEvent` replaced wholesale; old `TextDelta`/`ToolCallStarted`/`TurnFinished`/ `RunFinished` are gone
- `StreamingAgentHarness` reduced to an app+client binding over `AgentLoop`; the 8-positional-parameter overload is gone, replaced by `AgentToolExecutors.FromNameArgs`
- `AgentRunSummary` gained a trailing `Truncated` flag

### Phase 2 — Session scheduler ✅ shipped

| File | What it is |
| --- | --- |
| `framework/Reptar.AI/AgentSession.cs` | Queue owner: `Steer`/`FollowUp`/`Abort`/`ClearQueue`, `All` / `OneAtATime` modes, `WaitForIdleAsync`. |
| `framework/Reptar.AI/AgentLoop.cs` | `Retry` policy wired into the stream loop; `PrepareNextTurn` honoured including `ContextMessages` replacement. |
| `framework/Reptar.AI/AiTools.cs` | `AgentUsage` on `ChatMessage` — provider-reported input/output/cache tokens + cost. |

- [x] Steering polled before each request, not after the run
- [x] Follow-up delivered only when the agent would otherwise stop
- [x] Retry with jittered exponential backoff; `retry_scheduled` / `retry_attempt_start` / `retry_finished` events; transient-status classifier (408/409/429/5xx)
- [x] Non-transient errors fail immediately — no pointless retries on a 400
- [x] Per-message `AgentUsage`, so context estimation is provider-accurate

### Phase 3 — Context management ✅ shipped

| File | What it is |
| --- | --- |
| `framework/Reptar.AI/ContextCompaction.cs` | Token estimation, cut-point search, pi's summarization prompts, `Apply` projection rewrite, `ContextCompactor` end-to-end. |
| `framework/Reptar.AI/ToolOutput.cs` | `Head`/`Tail` truncation (2 000 lines / 50 KB, whole lines only), spill-to-file, truncation notes. |

- [x] Token estimation prefers the last provider usage, estimates only the trailing messages
- [x] Cut points never land on a tool result
- [x] Split-turn detection with the prefix summarized alongside
- [x] Iterative summarization (previous summary as input) + `/compact <instructions>`
- [x] Truncated (`length`) summaries are refused — a partial checkpoint is never persisted
- [x] Auto-compaction in `PrepareNextTurn`, driven by `ContextMessages` so raw history survives
- [x] `compaction_start` / `compaction_end` events
- [x] `run_command` tail-truncates and spills the full log to `.reptar/tool-output/`
- [ ] `PromptComposer` with named sections + patch messages — deferred, see below
- [ ] `context_edit` entries for overflow/length recovery — deferred, see below

### Phase 4 — Clients ✅ shipped

- [x] C# TUI: non-blocking run (`Task` + `Channel`), live input, Esc abort, Enter-while-running steers, markdown, scrollback, tool cards with streamed output, thinking blocks, context/cost ribbon, `/compact`
- [x] RPC mode: `reptar agent --mode rpc` — JSONL commands and pi-shaped events
- [x] TypeScript RPC client (`examples/agent-cli/rpc/rpc-client.ts`) with typed helpers
- [x] `bun reptar agent --mode rpc` keeps build output off stdout
- [x] Web SSE renamed to the canonical event names + `tool_execution_update`, `message_end`, `agent_settled`

### Deliberately deferred

- **`PromptComposer` (named prompt sections + patches).** Valuable for cache efficiency and for changing tools/skills mid-session without rewriting history, but nothing in the current client needs it: the system prompt is set once per run and Reptar rebuilds the request from the transcript each turn. Port it when a client wants to add/remove tools at runtime.
- **`context_edit` entries.** These exist in pi to hide a bad attempt from model context without lying about history during overflow recovery. Reptar's compaction already rewrites only the projection, which covers the common case.
- **Extensions** (TS modules in-process), themes, packages, HTML export, `pi-ai` provider breadth. Out of scope.

### Out of scope

Extensions (TS modules in-process), themes, packages, `pi-ai` provider breadth, HTML export.

---

## 7. Mapping table for the port

Useful when reading pi source and looking for the Reptar home.

| pi | Reptar target |
| --- | --- |
| `packages/agent/src/agent-loop.ts` | `framework/Reptar.AI/AgentLoop.cs` |
| `packages/agent/src/types.ts` `AgentLoopConfig` | `AgentLoopConfig` record |
| `packages/agent/src/agent.ts` | `framework/Reptar.AI/AgentSession.cs` |
| `packages/ai/src/types.ts` `Message`/`Usage` | `framework/Reptar.AI/LlmTypes.cs` |
| `core/agent-session.ts` (4458 lines) | split: `AgentSession.cs` + `Compaction.cs` + `PromptComposer.cs` |
| `core/session-manager.ts` | `framework/Reptar.AI/SessionStore.cs` (tree) |
| `core/compaction/compaction.ts` | `framework/Reptar.AI/Compaction.cs` |
| `core/system-prompt.ts` | `framework/Reptar.AI/PromptComposer.cs` |
| `core/tools/*.ts` | `framework/Reptar.AI/WorkspaceTools.cs` (already close) |
| `modes/rpc/rpc-types.ts` | `examples/agent-cli/rpc/` |
| `modes/json-event.ts` | `AgentEvent.ToJson()` |
| `core/skills.ts` | `SkillsDirectory.cs` (already present) |

---

## 8. Notes for the next session

- **Do not port `agent-session.ts` wholesale.** It is 4,458 lines because it accumulates years of product policy (auth guidance, crash logs, bug reports, cache warming, trust, share). Port the _scheduler_ and the _context builder_; leave the rest.
- **`nested-tool-calls.ts` is worth reading** before implementing sub-agents: it shows how a tool can call other tools through the same hooks so approvals apply transitively.
- **`output-accumulator.ts` / `truncate.ts`** are the right model for `run_command` output: accumulate, cap, spill to a temp file, and hand back a `fullOutputPath`. Reptar currently truncates silently.
- **`bash-executor.ts`** handles the `bashExecution` message path (`!command`). Reptar has this in `TerminalAgent.RunShellPassthroughAsync` but it never enters the transcript, so the model cannot see what you ran.

---

## 9. Learnings from pi's server and client that shaped this port

Beyond the loop, four things in pi's codebase were worth stealing verbatim.

### 9.1 `truncate.ts` / `output-accumulator.ts` — bounded tool output

Pi treats tool output as a resource with two independent limits (lines _and_ bytes), never returns a partial line, and spills the full text to a temp file. The important insight is that **silent truncation is a correctness bug**: the model reasons about a command's output and a clipped tail makes it draw the wrong conclusion. `ToolOutput` reports which limit hit, how much was dropped, and where the full log lives.

### 9.2 `compaction.ts` — token accounting is the whole trick

The naive approach (estimate every message at chars/4) drifts badly and either compacts too early or blows the window. Pi's approach is better and cheap:

1. Walk backwards for the last assistant message with non-empty usage.
2. Trust that usage as the context size up to that point.
3. Estimate only the trailing messages.

That makes `ShouldCompact` accurate without a tokenizer. Also worth copying: compaction refuses a `length`-truncated summary, because a partial checkpoint silently corrupts the session.

### 9.3 `messages.ts` — convert at the boundary, not in storage

Pi stores rich application messages (`bashExecution`, `custom`, `compactionSummary`, `branchSummary`) and flattens them to `user` at the provider boundary. This is why `!command` output can live in the transcript without pretending to be a tool result, and why a compaction summary is a first-class entry rather than a fake assistant message. Reptar's `AgentNextTurn.ContextMessages` implements the same idea with less machinery: the raw transcript is the truth, the projection is what the model sees.

### 9.4 `rpc-mode.ts` — the discipline, not the code

The protocol is boring on purpose. What makes it usable is the contract:

- One JSON object per line, LF only, no Unicode line separators.
- Correlate by `id`; handle commands asynchronously.
- A `prompt` response means _accepted_. `agent_end` closes one run; `agent_settled` means no further automatic work.
- stdout is protocol only.

The last one bit us immediately: `dotnet run` writes build diagnostics to stdout, which corrupts the stream. `scripts/reptar.ts` now builds with output diverted to stderr and runs with `--no-build` when `--mode rpc` is present. That single line is the difference between a protocol and a debugging session.

---

## 10. Remaining work, ranked

1. **Visual QA of the TUI.** The layout code is new (markdown, scrollback, tool cards) and has not been looked at by a human. Highest-value next step.
2. **Session tree.** `SessionStore` is still a flat messages table. Fork/branch/clone need `id`/`parentId` entries. This is the last big structural gap versus pi.
3. **`PromptComposer`.** Needed only when tools or skills change mid-session.
4. **Web SPA parity.** The SSE stream is now correct; the React app still needs steer/abort controls and a compaction indicator.

---

## 11. `pi-durable` → `DurableAgent.cs` (C#)

pi ships `@earendil-works/pi-durable`: a durable agent harness where "conversations, model turns, tool calls, and your own state are committed to storage before anything is shown. If the process dies mid-turn, reopening the storage picks the work up where it stopped."

The package is ~15,000 lines across a task scheduler, extensions, code mode, telemetry, and four storage backends. Porting it wholesale would be a quarter of work and most of it is product surface Reptar does not need yet. **The guarantee is the valuable part**, and that maps onto Reptar's loop almost exactly:

| pi-durable concept | Reptar C# |
| --- | --- |
| Harness | `DurableAgentHarness` |
| Conversation | `agent_conversations` row + append-only `agent_entries` |
| Entry (immutable record) | `DurableEntryRow` — `kind` is the message role, `payload` is JSON |
| Commit (atomic) | `Db.TransactionAsync` — entries + task state together |
| Task (durable state machine) | `agent_tasks` with `turns_completed` / `last_entry_id` |
| Submission (idempotent input) | `agent_submissions` with unique `request_id` |
| **Checkpoint per step** | **`AgentLoopConfig.FinishTurn`** |
| `resume()` | `ResumeAsync()` — re-runs tasks left `pending` or `orphaned` |
| `readOnly: true` | `DurableHarnessOptions.ReadOnly` |

The key realisation: `FinishTurn` already fires after every assistant turn plus its tool results, which is exactly the boundary pi-durable commits at. No new loop hook was needed — just a commit inside it.

What the port keeps:

- Nothing is reported before it is committed (the submission and its input entry land in one transaction).
- A retried submit with the same `request_id` returns the original submission.
- A task left `running` is marked `orphaned` on open and picked up by `ResumeAsync`.
- The transcript is rebuilt **from storage**, so a resumed run sees what the model saw.
- Attempt budget stops an endlessly failing task.
- Read-only mode for observers.

What it drops (deliberately, for now): extensions, code mode, forks, child tasks, task graph, per-conversation provider session IDs, and telemetry spans. All are additive on top of the same tables — `parent_id` is already in `agent_entries` for branching.

Tests: `framework/Reptar.Tests/DurableAgentTests.cs` (13 cases) including two that simulate a process restart by reopening the same SQLite file with a fresh `ReptarApp`.

---

## 12. Language split, as of this port

| Area | Language | Files |
| --- | --- | --- |
| Agent loop, session, events, protocol | **C#** | `AgentLoop.cs`, `AgentSession.cs`, `AgentEventJson.cs`, `AgentMessages.cs` |
| Context management | **C#** | `ContextCompaction.cs`, `ToolOutput.cs` |
| Durability | **C#** | `DurableAgent.cs` |
| Server | **C#** | `rpc/RpcMode.cs` |
| Client | **C#** | `rpc/RpcClient.cs` (+ a Python snippet in the README proving interop) |
| TUI / web | **C#** | `terminal/*.cs`, `features/chat.cs` |
| Tests | **C#** | `AgentLoopTests`, `AgentMessagesTests`, `ContextCompactionTests`, `ToolOutputTests`, `DurableAgentTests` |

The only TypeScript touched is `scripts/reptar.ts` (the `bun reptar` launcher), and only to keep build diagnostics off stdout for RPC mode. Everything the agent _does_ is C#.

---

## 13. What pi's test suite tells us to add next

pi's `packages/agent/test/agent-loop.test.ts` and `packages/coding-agent/test/*` are worth mining as a conformance checklist. Highest-value gaps not yet covered by Reptar's tests:

1. **Steering mode `All` vs `OneAtATime`** under multiple queued messages mid-run.
2. **Follow-up after a terminating tool batch** — the interaction between `terminate` and the follow-up queue.
3. **Abort during parallel tool execution** — what happens to sibling calls in flight.
4. **Compaction immediately after a tool batch** — the `prepareNextTurn` ordering guarantee.
5. **Tool loadout changes mid-run** (needs `PromptComposer`, still deferred).
6. **Retry interaction with steering** — a queued message must survive a retry.

These are the cases where a scheduler port usually drifts from its original.

---

## 14. `Reptar.AI.SDK` — the facade

pi's `createAgentSession` is the difference between "a library of parts" and "something you can use in five lines". Reptar had the parts; the SDK is the five lines.

| File | Contents |
| --- | --- |
| `framework/Reptar.AI/AgentSdk.cs` | `ReptarAgent`, `ReptarAgentOptions`, `ToolSelection`, `AgentSessionStats`, `ReptarAgentSchema` |

```csharp
await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
{
    WorkspaceRoot = "/path/to/project",
    AllowShell = true,
    Tools = ToolSelection.Only("read_file", "edit_file", "*_dir"),
});

var summary = await agent.PromptAsync("fix the failing test", onText: Console.Write);
var stats = agent.Stats();      // tokens, cost, context percent
await agent.CompactAsync();     // manual checkpoint
```

`CreateAsync` resolves the provider (flags → env → saved auth → echo), mounts the workspace tools, opens or resumes a session, applies the tool selection, and looks up the context window so compaction works. `Attach` is the same thing over an app you already configured.

### Tool selection (ported semantics)

`ToolSelection` reproduces pi's three shapes, because the differences matter:

| Shape | Meaning |
| --- | --- |
| `Only("read_file", "*_dir")` | Allowlist. Nothing outside it is callable. |
| `AllExcept("run_command")` | Denylist applied after the allowlist. |
| `Adjusting("+run_command", "-write_file")` | **Not** an allowlist — edits the default selection. |

Mixing bare names with `+`/`-` modifiers throws, because "only these" and "add this one" are contradictory instructions. Presets: `Default`, `NoTools`, `ReadOnly`.

---

## 15. Codemode — the one that needed a different answer

pi runs model-written JavaScript in a QuickJS VM compiled to WebAssembly. **Reptar cannot do that**: it targets Native AOT and NetWasm, so there is no JS engine to embed, no `eval`, and no runtime compilation.

But the _capability_ is what matters, not JavaScript:

> Let the model write a program that calls tools, and let only the program's output enter the conversation.

So `ScriptInterpreter` is a small AOT-safe interpreter for a C#-flavoured subset. ~1,400 lines across three files, no dependencies, no reflection, no code generation.

| File | Role |
| --- | --- |
| `framework/Reptar.AI/ScriptValue.cs` | Value model: null/bool/number/text/array/object/handle + JSON bridge |
| `framework/Reptar.AI/ScriptParser.cs` | Lexer + recursive-descent parser → AST |
| `framework/Reptar.AI/ScriptInterpreter.cs` | Async AST walker, tool bridge, sandbox limits |
| `framework/Reptar.AI/CodeMode.cs` | The `codemode` tool: registration, description, allow/deny |

### Language

```
let files = tools.list_dir({ path: ".", depth: 3 });
foreach (let f in files.entries) {
    if (f.type == "file" && f.path.endsWith(".cs")) {
        let src = tools.read_file({ path: f.path });
        if (src.content.includes("TODO")) { text(f.path); }
    }
}
return files.entries.length;
```

`let`/`const`, `if`/`else`, `while`, `for`, `foreach (x in xs)`, `return`, `break`, `continue`, arrays, objects, string methods (`upper`/`lower`/`trim`/`split`/`includes`/`startsWith`/`endsWith`/`slice`), array methods (`push`/`join`/`filter`/`map`/`find`), `json.parse`/`json.stringify`, `len`, `str`, `num`.

### The capability model, which is the point

- **Tools are the only way out.** No filesystem, no network, no timers, no `require`, no `eval`. A test asserts that `require(...)`, `fetch(...)`, `process.exit(...)`, and `eval(...)` all fail.
- **Only `text()`, `log()`, and the return value reach the model.** A test proves a tool result containing `SECRET-INTERNAL-PAYLOAD` never appears in the rendered output — a script can read a 2 MB log and hand back three lines.
- **`spawn()` + `join()`** run tool calls concurrently inside one model turn. `spawn(tools.x({...}))` captures the call unevaluated; `join([a, b])` awaits. Tested with a gate that deadlocks under sequential execution.
- **Bounded three ways:** step budget (default 5 000 statements), cancellation token, output item cap. An infinite loop returns `StepLimit`, not a hang.

### Where it differs from pi, honestly

| pi | Reptar |
| --- | --- |
| JavaScript via QuickJS/WASM | C#-flavoured subset via a hand-written interpreter |
| `image()`, `store()`/`load()`, `searchTools()`, `models` | Not yet — all additive |
| Nested tool calls carry full structured results | JSON round-trip, so `details` is dropped |
| Scripts can define functions | No `function` yet; `filter`/`map` take a native callable |

The last row is the real gap: `xs.filter((x) => x > 1)` does not parse yet. That is the next increment, and it is a parser + closure change, not an architecture change.

---

## 16. What is left in pi, ranked by value to Reptar

Sizes are pi's line counts, which is a fair proxy for port cost.

| pi | Size | What it gives us | Verdict |
| --- | --- | --- | --- |
| `mcp` | 3.3k | MCP client: any MCP server becomes a tool source | **Highest value.** Reptar has zero MCP. Fits `AiToolDefinition` cleanly. |
| `codemode` (remaining) | — | lambdas in scripts, `image()`, script-level `store()` | Finish the interpreter; small increments. |
| `prompt-templates` | 325 | `/template` expansion from markdown files | Cheap, immediately useful. |
| `nested-tool-calls` | 263 | A tool calling tools through the same hooks, so approvals apply transitively | Matters once sub-agents exist. Reptar has `AskUserTool` + request ports already. |
| `virtual-models` | 238 | `fast`/`cheap` aliases routing to a physical model | Cheap; good for a TUI model picker. |
| `model-registry` | 244 | Model catalog with capabilities (vision, reasoning, context) | Reptar has `LlmCatalog`; this is the capability half. |
| `project-trust` | 97 | Trust a directory before loading project config | Relevant — Reptar runs tools with process permissions. |
| `cache-warmer` | 453 | Pre-warm the provider prompt cache | Real cost win for long sessions. |
| `settings-manager` | 1.4k | `settings.json` with per-project overrides | Reptar has `LlmAuthFile`; this is the rest. |
| `durable` (remaining) | 28k | Forks, child tasks, task graph, per-conversation provider session ids | Additive on the tables already shipped. |
| `env` | 2.3k | Remote execution environments (tools run on another machine) | Interesting for durable; large. |
| `evals` | 1.4k | Behavioral eval harness | Reptar has `AgentEvals.cs`; pi's is a better harness. |
| `telemetry` | 1.6k | Typed spans/metrics schema | Reptar already exports OTLP. Low priority. |
| `chord` | 15k | Cross-process document objects | Out of scope; different architecture. |
| `tui` | 20k | Terminal UI framework | Reptar has `ReptarTerm`; not worth replacing. |
| `ai` | 28k | Unified LLM API, every provider | Reptar's `LlmTypes` covers the needed providers. |

**Recommended next three, in order:** `mcp` (unlocks an entire ecosystem), script lambdas (finishes codemode), `prompt-templates` (cheap UX win).

---

## 17. The rest of the list — shipped

### MCP (`McpClient.cs`)

The one that changes what the agent can do rather than how it does it. A stdio MCP client: JSON-RPC 2.0, newline framing, `initialize` → `notifications/initialized` → `tools/list` → `tools/call`.

```csharp
await using var source = await McpToolSource.ConnectAsync([
    McpServerConfig.Npx("files", "@modelcontextprotocol/server-filesystem", "/tmp"),
    McpServerConfig.Npx("github", "@modelcontextprotocol/server-github"),
]);

source.Mount(app);                          // tools become in-process Reptar routes
var tools = app.GetAiTools(onlyExplicit: true);   // mcp__files__read_file, ...
```

Naming follows pi: `mcp__<server>__<tool>`, with `-` → `_` so the name is also a valid codemode identifier. A server that fails to start is reported and skipped — one bad MCP server must not take the agent down. Tests drive a shell script that plays the server, so the handshake, tool list, call path, error mapping, and mounting are all covered without a network or Node.

Only stdio is implemented. HTTP and OAuth transports are a different shape; guessing at them without a server to test against would have produced code that looks right and is not.

### Prompt templates (`PromptTemplates.cs`)

`/fix-tests src/app.cs` → the template body with `$1`, `$@`, `$ARGUMENTS`, `$0` substituted. Frontmatter supplies `description` and `argument-hint`; the first body line is the fallback. Project templates shadow user templates. Substitution is a single left-to-right scan, so an argument containing `$2` stays literal — a test pins that.

### Model aliases (`ModelAliases.cs`)

`fast` / `deep` / `local` / `offline` resolve to a concrete provider+model. Gateways (OpenRouter, Vercel, OpenCode Zen, Cloudflare) all collapse to the OpenAI wire protocol, which is why `ModelAlias.WireProtocol` exists. `ModelAliases.Merge` layers project aliases over the defaults. `bun reptar agent --aliases` prints the table.

### Project trust (`ProjectTrust.cs`)

An agent that reads `.reptar/settings.json` or `.agents/skills/*` from a directory it did not create will follow instructions someone else wrote. Cloning a repo should not execute that repo's config. So: nothing project-local loads until the absolute path is trusted, and trust is stored in `~/.reptar/trust.json`, never in the project. An unreadable store fails closed. `Unknown` is not trust. A directory with no agent resources auto-trusts so the user is not prompted for nothing.

### Codemode lambdas

`xs.filter((x) => x > 1)`, `xs.map(x => x * 2)`, block bodies, and closures that capture their defining scope. The parser needed two things: `=>` in the operator table (longest-first, or it tokenizes as `=` `>`), and the single-parameter shorthand checked _before_ the `switch` in `ParsePrimary` — the `Identifier` case consumes the token and returns.

### Wiring

`ReptarAgentOptions` gained `Aliases`, `McpServers`, `OnWarning`, `LoadPromptTemplates`, and `Trust`. `CreateAsync` now resolves aliases before the environment, refuses to load project templates from an untrusted workspace, and mounts MCP tools into the same list the workspace tools come from. `bun reptar agent --model fast` works; `--aliases` and `--templates` print their tables.

---

## 18. TUI: adopting pi's shape

The tabbed workspace was the wrong model. pi has no tabs — it is one scrolling conversation with pickers that appear over it. That is what the TUI does now.

| Before | After |
| --- | --- |
| 4-tab strip (Chat / Sessions / Tools / Providers) | No tab strip. One conversation. |
| `/model`, `/connect` jumped to the Providers tab | Both open a modal **model picker** over the chat |
| `/providers` switched views | Prints a credential-status table into the transcript |
| `/tools` switched views | Prints the mounted tool table into the transcript |
| `/sessions` switched views | Modal **session picker** |
| Enter on a slash suggestion _executed_ it | Enter/Tab **completes** the name + space; the next Enter submits |
| Substring match on name | pi's fuzzy scoring on name **and** description (`terminal/Fuzzy.cs`) |

The Enter behaviour is the subtle one. Executing from the dropdown means you cannot add arguments without a second gesture, and it hides what is about to happen. Completing first — which is what pi does — makes the command visible in the editor before it runs.

The fuzzy port (`Fuzzy.cs`) is a direct translation of pi's `packages/tui/src/fuzzy.ts`: all query characters in order, with bonuses for consecutive matches, word boundaries, and early positions, plus the second pass that re-scores contiguous occurrences so a greedy alignment cannot pick up letters from an unrelated earlier word.

Also fixed while in there: `/compact` and `/templates` were reachable by typing but missing from the dropdown, and the empty-state `1`/`2`/`3` starters were being swallowed by the tab-switch shortcut.

---

## 19. The last four

**Codemode `image()` and script store.** `image()` accepts a base64 `data:` URL, an `{ image_url }` object, or an image block; remote URLs are refused, matching pi, because a script must not be able to make the host fetch an arbitrary address. `store(key, value)` / `load(key)` persist across runs of the same `codemode` registration, so a script that records a cursor is useful to the next script.

**Nested tool calls** (`AgentToolContext`). A running tool can call other tools through the same path the model uses, so `BeforeToolCall` and `AfterToolCall` apply transitively — a "search" tool cannot quietly do what "write" was denied. Ambient (`AsyncLocal`) rather than a parameter so existing tool signatures do not change, depth-limited to 4, and it returns a failed result rather than throwing when no loop is active so a tool that is also reachable over HTTP keeps working. `AgentLoop.RunToolCallAsync` is the exported helper, mirroring pi's `runToolCall`.

**Prompt caching + cache warmer.** `LlmConfig.PromptCache` puts `cache_control: ephemeral` breakpoints on the Anthropic system block and the last tool definition, and `LlmStreamEvent.Usage` now carries `cache_read_input_tokens` / `cache_creation_input_tokens` so caching is measurable. `CacheWarmer` sends one throwaway request when the prefix is large enough to pay for itself (≥1 024 estimated tokens). On a coding agent the system prompt plus tools is 2–5k tokens re-sent every turn, so this is the difference between paying for the prefix 10 times and paying 1.25× once.

**Durable forks.** `ForkAsync(conversationId, throughEntryId?, title?)` copies a transcript up to a cut point into a new conversation inside one transaction — the original is untouched, so a fork is "explore this alternative without losing where I was". `ConversationsAsync` lists them for a picker.

---

## 20. TUI, second pass: pi's real layout

The first pass removed the tabs. This one fixes where things live and what the footer says.

### Selectors are inline, not floating

Floating a dialog centred on screen is OpenCode's idiom, and it needs a compositor that Reptar's immediate-mode buffer TUI does not have. pi does something simpler and better: the TUI is a vertical stack — transcript, then whatever picker is open, then the editor, then the footer — so a picker appears as a list that grows _upward out of the input line_.

Reptar now does the same. `RenderProvidersOverlay` builds its body as lines and draws them bottom-anchored above the prompt card, no border and no shadow. Same for the `/` and `@` dropdowns.

Row shape follows pi's `SelectList`: `→ ` for the selection, the primary column padded to a fixed width, a two-space gap, then the description, and `(3/42)` when the list scrolls.

### Provider scoping and the key flow

The picker is scoped: a scope row (`[All] Anthropic OpenAI …`, Tab/←/→ to change) filters the model list. Enter on a model whose provider has no credential **does not fail** — it swaps the list for the key prompt, inline where the composer sits, with the provider's portal URL above it and the key masked. That is pi's login dialog, reduced to what a terminal line editor can do.

### The footer

Two dim lines, exactly pi's:

```
~/dev/RocktownLabs/projects/reptar (master)
↑1.4M ↓296k R109M CH99.8% $1.442 46.3%/1.0M (auto)      deepseek-v4.1-flash
```

- Line 1: working directory with `~` substitution, then `(branch)`.
- Line 2 left: `↑` input, `↓` output, `R` cache read, `W` cache write, `CH` cache hit rate, `$` cost, then context pressure as `46.3%/1.0M (auto)`.
- Line 2 right: the model id, right-aligned, with the provider in parentheses only when it fits.

Details worth naming, all from pi's `footer.ts`:

- `formatTokens` is piecewise: exact below 1k, one decimal in the thousands, rounded thousands below a million, one decimal in the millions.
- The cache hit rate is `cacheRead / (input + cacheRead + cacheWrite)` of the **latest** response, not the session. A session average drifts toward 100% and stops telling you anything about the turn you just paid for.
- Context percentage is coloured: >90% red, >70% amber.
- The provider prefix is dropped when it would push the model name out of view.

### Prompt caching, the part that actually matters

The first pass put breakpoints on the system prompt and the last tool — that caches the static prefix. pi does **three**, and the third is the one that saves real money: a breakpoint on the last content block of the last message, which caches the whole conversation prefix. Without it every turn re-bills the entire history at full input price.

`AnthropicLlmClient.MarkLastBlockCacheable` implements it, covering text, image, `tool_result`, and `tool_use` blocks (the kinds Anthropic accepts `cache_control` on) and converting a string-content last message into a cached block. `LlmStreamEvent.Usage` now carries `cache_read_input_tokens` / `cache_creation_input_tokens` so the footer can show the hit rate at all.

`CacheWarmer` sends one throwaway request when the estimated prefix is at least 1 024 tokens, which is the point at which the 1.25× write costs less than re-sending the prefix.

---

## 21. Thinking levels, provider scoping, and the connect flow

Three things the TUI had wrong, all fixed by reading pi rather than guessing.

### Thinking levels (`ThinkingLevels.cs`)

pi's `ModelThinkingLevel` is a seven-value ladder and `shift+tab` walks it:

```
off → minimal → low → medium → high → [xhigh → max]
```

`xhigh` and `max` are only surfaced when a model explicitly maps them, so `ThinkingLevels.Supported(reasoning, extended)` returns five levels for a normal reasoning model and one (`off`) for a model that does not reason.

The part worth copying carefully is the token arithmetic. pi does not send the level name to Anthropic; it sends `thinking.budget_tokens` and **raises `max_tokens`**, because thinking shares the response ceiling with the answer:

| Level   | Budget |
| ------- | ------ |
| minimal | 1 024  |
| low     | 2 048  |
| medium  | 8 192  |
| high    | 16 384 |

`AdjustMaxTokens` grows the ceiling by the budget, caps it at the model limit, and — if the ceiling is still tight — squeezes the budget to leave `MinAnswerTokens` (1 024) free. Without that last step you get a model that thinks for 16k tokens and then has no room to reply.

`xhigh`/`max` collapse to `high` for budget purposes (`ClampReasoning`). Temperature is dropped from the Anthropic request when thinking is on, because the two are incompatible.

The footer shows the level pi-style: `model • high`, or `model • thinking off`.

### Models are scoped to providers you can actually use

pi computes `available = all.filter(model => configuredProviders.has(model.provider))` and the picker only ever shows that list. Reptar's picker was showing a hardcoded table including providers with no credentials — you would pick a model, get a 401, and have to work out that the provider was never connected.

`RefreshCatalogAsync` now walks the known providers, keeps only those with a credential (or none required), loads their real catalogs from models.dev, and derives the scope row from the result. `CuratedModel` gained `Reasoning`, `ExtendedReasoning`, `Vision`, and `ToolCall` so the specs column and the thinking ladder have something real to read.

### `/connect` asks who, then asks for the key

The old flow dropped you into a key box wired to whatever provider happened to be active. pi's `/login` is two stages: an auth-type selector, then a **provider selector** showing each provider's status, then the credential dialog. `/login <provider>` skips to that provider.

Reptar now has `ProviderStage` — `Models` / `PickProvider` / `EnterKey` — and `/connect` opens the provider list with every provider and its status (`✓ env: ANTHROPIC_API_KEY`, `✓ saved`, `○ needs key`, `✓ no key needed`). Enter on a local provider activates it immediately; Enter on one that needs a key opens the inline key prompt; Escape goes back to the list rather than closing everything. `/connect anthropic` skips straight to the key.

---

## 22. What digging further into pi actually found

Three of the last four changes came from reading pi's source instead of reasoning about it, and two of them corrected things I had guessed wrong.

### My reasoning-capability guess was wrong, twice

I had assumed models.dev exposed `capabilities.reasoning` and an `extended_reasoning` flag. It exposes neither. Reading `packages/ai/scripts/generate-models.ts` and `models-dev-reasoning-options.ts` shows the real shape:

```ts
interface ModelsDevModel {
  reasoning?: boolean; // top level, not under `capabilities`
  reasoning_options?: (
    | { type: "toggle" }
    | {
        type: "effort";
        values: (
          | "none"
          | "minimal"
          | "low"
          | "medium"
          | "high"
          | "xhigh"
          | "max"
          | "default"
          | null
        )[];
      }
    | { type: "budget_tokens"; min?: number; max?: number }
  )[];
  modalities?: { input?: string[]; output?: string[] }; // vision = input includes "image"
}
```

So `xhigh` and `max` are not a boolean — they are members of an explicit effort list, and `off` is only selectable when the list contains `none`. `ParseReasoningOptions` now implements pi's `getEffortThinkingLevelMap` plus the `toggle` and `budget_tokens` shapes, and `CuratedModel.SupportedThinking` is the result. `Shift+Tab` can no longer offer a level the provider would reject.

### `max_tokens` was not clamped to the context

pi's `clampMaxTokensToContext`:

```
available = contextWindow - estimateContextTokens(context) - 4096
maxTokens = min(maxTokens, max(1, available))
```

Without it, a 64k-output model on a 200k window with 180k tokens of conversation asks for 64k when only ~16k is left — a 400 on every provider, and easy to hit. `ThinkingLevels.ClampToContext` ports it, `LlmConfig.ContextWindow` carries the window, and the Anthropic path re-derives the thinking budget after clamping.

### Enter on a slash command needed two presses

pi's editor has a subtlety I had read past:

```ts
if (kb.matches(data, "tui.select.confirm")) {
    applyCompletion(...);
    if (this.autocompletePrefix.startsWith("/")) {
        this.cancelAutocomplete();
        // Fall through to submit      <-- this
    } else {
        this.cancelAutocomplete();
        return;                          // file completion stops here
    }
}
```

**Tab** completes. **Enter** completes _and_ submits — but only for slash commands. `@file` completion returns without submitting, because attaching a file is a step in composing a prompt, not the end of one. My port returned in both cases, so `/exit` needed two Enters.

The fix required extracting `SubmitInputAsync` from the key handler so the dropdown could reuse the exact same submit path, and making `HandleDropdownKeyAsync` async.

### Attribution headers were missing entirely

`packages/ai/src/utils/identity.ts` sends provider-specific identity headers:

| Target | Headers |
| --- | --- |
| OpenRouter | `HTTP-Referer`, `X-OpenRouter-Title`, `X-OpenRouter-Categories` |
| NVIDIA NIM | `X-BILLING-INVOKE-ORIGIN` (capitalised origin) |
| OpenCode | `x-opencode-client`, and `x-opencode-session` for per-conversation routing |

This is not decoration: OpenRouter only credits an application in its rankings when `HTTP-Referer` and `X-OpenRouter-Title` are present, so every request without them is attributed to "unknown". `ClientIdentity.Headers` ports the lookup, recognises gateways by host as well as by provider key, and keeps the OpenCode session header independent of attribution because it is routing, not identity.

---

## 23. Where this stands, and what is next

### Done and verified by tests (669)

| Area | Files |
| --- | --- |
| Agent loop, events, JSONL protocol | `AgentLoop`, `AgentEventJson`, `AgentSession` |
| Message model and boundary conversion | `AgentMessages` |
| Context: estimation, compaction, tool output | `ContextCompaction`, `ToolOutput` |
| Durability: commit-before-reveal, resume, forks | `DurableAgent` |
| Codemode: interpreter, lambdas, images, store | `ScriptValue`, `ScriptParser`, `ScriptInterpreter`, `CodeMode` |
| SDK facade and tool selection | `AgentSdk` |
| MCP client and tool mounting | `McpClient` |
| Thinking levels, context clamp | `ThinkingLevels` |
| Prompt caching (3 breakpoints) + warmer | `LlmAnthropic`, `CacheWarmer` |
| Provider attribution headers | `ClientIdentity` |
| Prompt templates, model aliases, project trust | `PromptTemplates`, `ModelAliases`, `ProjectTrust` |
| Server and client | `rpc/RpcMode`, `rpc/RpcClient` |

### Not verified against a live provider

Everything below is unit-tested at the level of "the request body has the right shape" and inferred from reading pi. None of it has been observed against a real API.

1. **Attribution headers.** Header _names_ come from pi's `identity.ts`, but whether OpenRouter credits the app is only visible in their dashboard.
2. **The three cache breakpoints.** Anthropic caps at four; we send three. Whether the last-message breakpoint actually produces `cache_read_input_tokens > 0` on turn 2 is unmeasured. This is the single most valuable thing to check, because the whole cost model depends on it.
3. **`thinking.budget_tokens` plus `max_tokens` arithmetic.** The clamp logic is tested; whether Anthropic accepts the resulting pair for a specific model is not.
4. **`reasoning_options` parsing.** Written from pi's generator, never fed a real models.dev payload for a reasoning model.
5. **`max_tokens` context clamp.** Only triggers on a conversation long enough to matter.

### Structural gaps, in priority order

1. **`SessionStore` is still a flat table.** `DurableAgent` has forks; the ordinary session store does not. Fork, branch, and clone need `id`/`parentId` entries. This is the last big structural difference from pi and the one that is most expensive to retrofit later.
2. **No settings persistence.** Aliases, thinking level, and auto-compaction reset on every start. pi has `settings.json` with project overrides.
3. **`PromptComposer`.** Named prompt sections with patch messages. Needed only when tools or skills change mid-session, which no current client does.
4. **Web SPA.** The SSE stream is correct; the React app has no steer/abort controls and no compaction indicator.
5. **RPC mode is read-mostly.** No `fork`, `get_tree`, or `get_entries`, so a client cannot render or navigate a session tree.

### Recommended order

1. **Live smoke test** with one provider key: one prompt, then confirm `cache_read_input_tokens` on the second turn. That single check validates or invalidates the most load-bearing unverified assumption in the codebase.
2. **TUI pass** with the user looking at it. Everything else is tested; the layout is not.
3. **Session tree.** Expensive to add later, and it unlocks fork/branch in the TUI, RPC, and web at once.
4. **Settings file**, so the TUI stops forgetting your choices.

---

## 24. Matching pi's actual terminal layout

Two reference screenshots of pi settled several things I had guessed at.

### The composer has no frame

I had a bordered input card. pi has a bare `❯` line with a block cursor, sitting directly under the transcript like one more line of it. The frame was a Codex-ism. `RenderComposer` now draws a plain prompt and the pickers anchor to it.

### The vertical order

```
[transcript]
✻ Working (12s) · compacting context…      ← only while running
❯ input
~/dev/project (branch)
↑743k ↓315k R81M CH99.7% $13.186 54.7%/272k (auto)     model • level
```

The `✻ Working` line is its own row with an animated spinner and a running timer. It only exists while a run is in flight, which is why an idle screen stays quiet.

### Tool calls render as boxes

pi shows a header naming the call, then a bordered box with the output, then `Took 1.4s`. The border colour _is_ the success signal — green for ok, red for error — so there is no need for a separate status word. `BuildTranscriptLines` now emits exactly that.

### Assistant messages have no label

I was prefixing `❯ You` and `⚡ Reptar`. pi just renders the content, with a single `❯` glyph on the user's turn. The label was noise; the indentation carries the structure.

### Settings persist

`AgentSettingsStore` ports the parts of pi's `settings.json` that Reptar acts on:

```jsonc
// ~/.reptar/settings.json
{
  "model": "anthropic/claude-sonnet-4-5",
  "thinking": "high",
  "promptCache": true,
  "compaction": { "enabled": true, "keepRecentTokens": 20000 },
}
```

Two files — user then project — with the project file winning field by field. Every field is nullable so "not set here" is distinguishable from "set to the default", which is what makes the layering work. The project file only loads when the workspace is trusted, because it is project-local configuration and that is exactly what trust gates.

Writes always go to the user file. The agent should not modify a repository's configuration behind your back.

`/settings` prints the effective values and both file paths, with a warning block for anything that failed to parse — a broken settings file must not stop the agent from starting.

### Also fixed from the screenshot

- The header read `/ providersesc to close` — the overlay title and the hint were printed at overlapping columns.
- The empty-state dinosaur was rendering behind an open picker.
- The key prompt floated in dead space because the composer was skipped whenever an overlay was open. The composer is now always drawn, and during key entry it _becomes_ the key field, which is pi's behaviour of swapping the editor for the login input.

---

## 25. pi's selector framework, ported

Ten screenshots of the real menus settled what the pickers actually look like. They are not dialogs and they are not overlays — **a selector takes over the composer's slot entirely**. When `/model` is open there is no `❯` prompt, because the picker _is_ the input.

### One component, four menus

`TermSelector` is the C# port of pi's selectable-list pattern. Every menu is the same vertical block, bottom-anchored above the footer:

```
─────────────────────────────────────────────   rule
  Only showing models from configured providers. Use /login to add providers.
Select provider to configure:                   title (optional)
  → search█                                     search row, cursor included
  → ✓ Amazon Bedrock   us.anthropic.claude   ✓ stored
    Anthropic          claude-sonnet-4-5     · not configured
  (1/42)
  Model Name: Claude Opus 4.6 (US)              detail for the selection
  Model catalogs refreshed.                     status line
  Type to search · Enter to select · Esc cancel
~/dev/project (branch)
↑stats                              model • level
```

Details worth naming, all read off the screenshots:

- **The search row is a row.** It has its own `→` and a block cursor, so the list reads as a form with a focused field rather than a list with a filter somewhere else.
- **`(1/42)` is a position counter, not a page indicator.** It shows where the selection sits in the filtered set.
- **Status vocabulary is `✓ stored` / `· not configured`.** I had invented `○ needs key`; pi's phrasing is shorter and distinguishes "has a credential" from "needs one" without editorial.
- **A notice line above the title** states the scoping rule. That is where "only configured providers" belongs — not as a silent filter.
- **A detail line under the list** names the selected model, because `us.anthropic.claude-opus-4-6-v1` is not a name a human can read.

### Search is fuzzy, and it searches the status too

`Fuzzy.Rank` runs over label, detail, **and** status. That is why typing `stored` filters to configured providers and `sonnet` finds the Anthropic row by its model id. The settings menu shows the same behaviour: typing `mer` matches both `Mermaid diagrams` and `Install telemetry`.

### The settings menu

`/settings` is a searchable list of the knobs Reptar actually has, each with its current value and a one-line explanation. Enter cycles a value and persists it to `~/.reptar/settings.json` immediately — a menu you have to confirm is a menu you forget.

### The slash list is different from a selector

A selector replaces the composer; the `/` list does not. pi keeps the `❯` prompt visible, draws a rule under it, and lists the commands below. That is the right call: you are still typing into the prompt, so hiding it would break the illusion that the list is an extension of the line.

The transcript viewport now shrinks by the dropdown's height instead of being painted over, which is what made the earlier list overlap the footer.

### The `ctrl+o` startup panel

pi's first screen is a resources panel: version, key hints, then `[Context]`, `[Skills]`, `[Extensions]`, and `[Skill conflicts]` naming which copy won and which was skipped with full paths. `ctrl+o` toggles it.

That is worth porting for one reason: **skill conflicts are silent otherwise.** Two `SKILL.md` files with the same name and the agent picks one, and nothing tells you which. Reptar's `SkillsDirectory` already resolves them; the panel is what makes the resolution visible.

---

## 26. Skill conflicts, and why the panel matters

pi's startup screen has a `[Skill conflicts]` section:

```
"copywriting" collision:
  ✓ auto (project) ~/dev/repo/.agents/skills/copywriting/SKILL.md
  ✗ ~/.agents/skills/copywriting/SKILL.md (skipped)
~/.agents/skills/some-skill/SKILL.md
  description is required
```

That section is the reason to port the panel. Two `SKILL.md` files with the same name resolve silently — one wins, the other vanishes, and the agent behaves differently from what you expect with nothing to explain why. The same is true of a skill that fails to parse: it just is not there.

### `SkillsDirectory.DiscoverAll`

The old `Discover` walked one directory and did `skills[name] = ...`, so a duplicate was an invisible overwrite. `DiscoverAll` walks the user root then the project root and returns three lists:

| List       | Contents                                           |
| ---------- | -------------------------------------------------- |
| `Skills`   | what will be available, each with its `SkillScope` |
| `Shadowed` | the losing file and which file beat it             |
| `Invalid`  | the file and a specific reason                     |

Reasons are specific on purpose: `description is required`, `name must be a lowercase slug`, `missing or unreadable frontmatter`. "Invalid skill" tells you nothing; those three lines each tell you what to change.

This required loosening `TryParseFrontmatter`: it used to return false when `description:` was absent, which collapsed "no description" and "no frontmatter" into one unhelpful message. It now requires only a name, and the description check lives in the caller where it can be reported.

### `AgentResourcesReport`

The data behind the panel, gathered best-effort:

```
[Context]   AGENTS.md (4.1 KB)
[Skills]    12 · copywriting, seo-audit, …
[Templates] /fix-tests, /explain
[MCP]       files, github
[Skill conflicts]  …
[Skill errors]     …
[Settings warnings] …
```

`ctrl+o` toggles between a one-line summary (`12 skills · AGENTS.md · ctrl+o`) and the full list. The summary turns amber when anything was rejected, so the panel is discoverable exactly when it matters and quiet when it does not.

An untrusted workspace says so explicitly rather than silently loading nothing:

> This workspace is **not trusted**, so project-local skills, templates, and settings were not loaded.

### Two things worth noting

**The user skills root is injectable.** Gathering the report scans `~/.agents/skills` by default, which made the first version of the tests non-hermetic — they picked up this machine's real skills and reported conflicts that had nothing to do with the fixture. `Gather(..., userSkillsRoot: "")` fixes that and matters for sandboxes too.

**`Ctrl+S` in the model picker** now sets the default without switching, matching pi's "Enter to select · Ctrl+S to set as default". Conflating the two meant you could not browse a catalog without committing to it.

---

## 27. The session tree

The last structural gap. `SessionStore` was a flat `agent_messages` table, which cannot express two continuations from the same point — and therefore cannot express fork, branch, or `/tree`.

### Why a separate table instead of a migration

`SessionTreeStore` writes to its own tables:

```
agent_session_entries  (id, session_id, parent_id, seq, role, content, tool_calls, name, tool_call_id, created_at)
agent_session_state    (session_id, leaf_id, updated_at)
```

Adding `parent_id` to the existing message table would have required an `ALTER TABLE` on every database already in the wild. A parallel table costs one extra `CREATE TABLE IF NOT EXISTS` and zero migration risk, and `ImportFlatAsync` lifts an old session into the tree the first time it is opened, so nothing becomes unreadable.

### The shape

Every entry names its parent. The **leaf is the only mutable thing** — everything else is immutable history — which is why it lives in a one-row-per-session table rather than as a column on the session.

The active branch is the path from the leaf back to a root, returned oldest-first. That one walk gives you all three behaviours:

| Operation | Implementation                                               |
| --------- | ------------------------------------------------------------ |
| append    | new entry whose parent is the current leaf; advance the leaf |
| branch    | move the leaf to an earlier entry, keep appending            |
| fork      | copy the path into a new session; the source is untouched    |

Two continuations from one point are just two entries with the same `parent_id`. Nothing is ever deleted, so switching back to the abandoned branch is a leaf move.

### Defensive details

- **No recorded leaf** (a session written by another process, or before the tree existed) falls back to the newest entry rather than returning an empty branch.
- **A dangling `parent_id`** makes the entry a root instead of losing it — `BuildTree` checks that the parent exists before linking.
- **A corrupt cycle** is bounded by a depth guard in `BuildTree` and a visit counter in the branch walk, so bad data degrades to a partial tree instead of hanging.

### `/tree` and `/fork`

```
**Session tree**

● user: hi
└─ assistant: **Reptar Agent is running in offline echo mode.** … ◀ leaf
```

`/fork` copies the active branch into a new session and switches to it — "explore this alternative without losing where I was" — and reports how many messages came along.

### What this unlocks

Fork and branch were the last things blocked on storage. `/tree` in the TUI, `fork` in RPC mode, and branch navigation in the web client are now all the same three methods on one class.

---

## 28. Branch switching, RPC tree commands, and a live web client

All three remaining items, done.

### `/tree` is now a picker, not a printout

The store could already move the leaf; the TUI could only display the tree. `/tree` now opens the shared `TermSelector` with one row per entry, indented by depth:

```
Session tree
  → ● user: hello
    └─ assistant: **Reptar Agent is running in offline…** ◀ leaf
```

Enter moves the leaf there and reloads the branch. That is the branch switch: the next message continues from that point and the previous continuation stays in the tree. The selector framework from §25 paid for itself here — the tree needed no new rendering code at all.

### RPC tree commands

Four commands, all backed by `SessionTreeStore`:

| Command | Returns |
| --- | --- |
| `get_tree` | `{ tree, leafId }` — nested `{ entry, children }` |
| `get_entries` | `{ entries, leafId }`, with an optional `since` cursor |
| `fork` | `{ sessionId, messageCount }` — copies the branch, switches to it |
| `switch_leaf` | `{ leafId, messageCount }` — moves the leaf, reloads the branch |

`since` is a durable cursor, not an offset: pass the last entry id you saw and get only what came after, even across client restarts. An unknown cursor is an error rather than a silent full replay, because a silent replay would make a polling client duplicate everything.

### The web client can now steer and abort

This needed a structural change. `/api/chat` was stateless per request, so a second HTTP call had no way to reach the run started by the first. `AgentRunRegistry` (framework, not example — any HTTP host needs this) is a map of key → cancellable run with a steering queue each.

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/chat/steer` | queue a message for the running agent; 409 when nothing runs |
| `POST /api/chat/abort` | cancel the run; 409 when nothing runs |
| `GET /api/chat/status/:id` | `{ running, elapsedMs }` |
| `POST /api/chat/compact` | run a checkpoint, return the summary and token counts |

The SPA changes that matter:

- **Enter while streaming steers** instead of being blocked. Previously the send button was disabled during a run, which made the web client strictly worse than the terminal.
- **The send button becomes a stop button** while streaming, wired to `/api/chat/abort`.
- **Compaction and retry events render** as steps: `Compacted 45,000 → 12,000 tokens`, `Retrying in 2000ms (rate limited)`.
- `/compact` and `/clear` are handled locally instead of being sent to the model.

Verified against a running server: `/api/health` 200, `/api/chat/status/:id` reports idle, `/api/chat/steer` with no run returns 409 `no_active_run`.

### Placement note

`AgentRunRegistry` started life inside `examples/agent-cli/features/chat.cs`. It moved to `framework/Reptar.AI/` for two reasons: the test project only compiles `terminal/*.cs` from the example, so it was untestable there; and it is genuinely generic — nothing about it is Reptar-specific, let alone web-chat-specific.

---

## 29. The layout bugs, and why they kept happening

Five screenshots showed the same class of failure three times over: **rows being drawn on top of other rows**. Three distinct causes, all now fixed and all now tested.

### Cause 1: the dropdown was positioned on the footer

`RenderSlashDropdown` drew at `ComposerRow + 1`, and `ComposerRow` was `_height - 4` — so the list started at `_height - 3`, which is exactly the footer's location row. The composer never moved out of the way, so the list was painted _over_ the footer and the footer was painted back over it. That is the "input covers the list" the user saw.

### Cause 2: nothing cleared the rows it drew into

`TermSelector` printed over a live transcript without clearing first. `Print` with `maxWidth` stops at the text, so wherever the new line was shorter than the old one, the old text showed through:

```
  → gl_in_command    Execute a shell command in the workspace root with a timeout.
  → ✓ deep-research-max-preview-04-2026  131k ctx · reasoning · vision · Google Gemini
    gemini-2.5-pro-preview-tts          8.2k ctx · Google Gemini a workspace file.
```

`list_dir … ignoring .git, bin,…` bleeding into a model row. Every selector row now `FillRect`s its full width first, and the dropdown band does the same.

### Cause 3: the geometry was spread across the file

`_height - 4` here, `_height - 5` there, `_height - 6 - dropdownRows` somewhere else. Each number was individually reasonable and collectively wrong.

`TermLayout` is now the single owner of the bottom-of-screen geometry:

```
height-1   footer: stats
height-2   footer: working directory
height-3   composer, or the selector's last row
...        the dropdown band, when open
...        working indicator
...        transcript
```

`Composer` moves up by exactly `DropdownRows`; a selector takes the composer's slot entirely (`Composer == -1`); `TranscriptBottom` stops above whichever of the two is present. `Fits` reports when a band cannot be placed, and the dropdown caps itself at a third of the screen instead of drawing over the footer.

Twelve tests pin it, including a sweep across heights and band sizes asserting that no two bands ever share a row. **That is the fix for the meta-problem**: I cannot see the TUI, so the layout had to become something a test can check.

### The other four issues

| Issue | Cause | Fix |
| --- | --- | --- |
| Model list "doesn't show models" | It did — the transcript was bleeding through it | Row clearing (cause 2) |
| Notice says `/login` | Copied from pi verbatim, but our command is `/connect` | Reworded |
| Can't see `deepseek-v4.1-flash` | Image, embedding, TTS, and realtime models were in the list | `IsChatModel` filters them |
| Footer overflows on a long model id | Right side had no budget | Fit → drop provider → truncate |

The model filtering matters more than it sounds: a picker offering `gemini-embedding-001` and `gpt-image-1` is not just noise, it is a trap — selecting one produces a confusing provider error instead of a reply.

### Also changed

- The header now names the view you actually opened: `/ models`, `/ connect`, `/ settings`, `/ tree` — it said `/ providers` for all of them.
- Submitted user turns use `▌ you` instead of `❯`, so a sent message and the live prompt no longer look like the same thing.

---

## 30. Providers that need more than a key, and a dropdown that stopped vanishing

### Cloudflare Workers AI

The model the user could not find — `@cf/deepseek-ai/deepseek-v4.1-flash-1017` — is a **Workers AI** model, not an AI Gateway one. Those are different providers in pi (`cloudflare-workers-ai` and `cloudflare-ai-gateway`) and only the gateway existed here. Workers AI is now registered:

| Provider | URL | Needs |
| --- | --- | --- |
| Cloudflare Workers AI | `api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1` | key + account id |
| Cloudflare AI Gateway | `gateway.ai.cloudflare.com/v1/{account_id}/{gateway_id}/compat/chat/completions` | key + account + gateway id |

### Base-URL templating

Both URLs contain `{account_id}`-style placeholders. `LlmProviders.ResolveBaseUrl` substitutes them from the saved credential or the environment, and **leaves an unfilled placeholder visible rather than blanking it** — a request to a URL containing a literal `{account_id}` fails in a way that looks like an auth error, so it should be obvious in the UI instead.

`MissingRequirements` reports what a provider still needs, and the provider picker now says `· needs api key + Cloudflare account id` rather than a flat `· not configured`. That is the difference between "I have not set this up" and "I set the key but forgot the account id".

### `/connect` became a short sequence

The key prompt now steps through fields: `step 1/2: api key`, `step 2/2: Cloudflare account id`. The key is masked; the id is not, because it is not a secret and hiding it makes typos invisible. The prompt previews the resolved URL, so you can see it stop containing braces as you fill fields.

### The dropdown vanishing was a matching bug

`/s` did not narrow the list — it still showed `/model`. The cause: matching ran over the **description as well as the name**, and `/model`'s description is "Browse and **s**elect a model". With a one-letter query almost everything matched, so the ranking was effectively random and the command being typed fell out of the eight visible rows. It read as "the results disappeared".

pi matches slash commands on the **name only** and displays the description separately (`autocomplete.ts` passes `getText: (item) => item.name`). `SlashCommands` now does the same, and the tests assert the negative case — `/summarize` must find nothing, because that word only exists in a description.

Two secondary fixes in the same area:

- **A no-match query keeps the list open** showing `no command matches "zz"` with a hint. pi closes it; keeping it open is friendlier, and the band is now always reserved at least one row so the message has somewhere to be drawn instead of the whole block collapsing.
- The `@` file list had the identical bug and got the identical fix.

### What the tests pin now

`SlashCommandsTests` (11 cases) covers the open/closed rule, name-only matching, the negative description case, and that every single letter still leaves the list open. That last one exists because "the menu disappears when I type" is not a thing a screenshot can prove fixed.

---

## 31. The connect list, and small interactions that were not captured

### The provider list had a model column

```
OpenAI          gpt-4o-mini        · needs api key
Anthropic       claude-sonnet-4-5  · needs api key
```

That reads like a model picker with a provider prefix. pi's is:

```
→ Anthropic • not configured
  GitHub Copilot ✓ stored
  OpenAI ✓ stored
```

Name and status. The model is a separate decision made in `/models`, and putting it here made the list answer a question nobody asked. The column is gone.

### The two-list split

pi asks _how_ you want to sign in before asking _who_:

```
Select authentication method:
→ Sign in with an account
  Sign in with an API key
  Sign in with Radius • not configured
```

…and the two provider lists are **different sets**, because subscription-backed providers are not the same as API-key ones. `ProviderInfo.Auth` (`ApiKey` / `Account` / `Both`) records the capability, and `/connect` now opens the method selector first, then filters.

Reptar has no OAuth device flow, so the account list is honest about it: selecting a provider says "account sign-in is not implemented yet — use an API key for now" rather than dropping into a key prompt that would look like the same flow under a different name. The structure matches pi so the gap is visible instead of hidden, and the plumbing is there when OAuth lands.

`/connect <provider>` still skips both steps for the common case.

### The settings list jumped when you changed something

`ToggleSettingAsync` rebuilt the selector after every change, which reset `SelectedIndex` to 0 — so the list scrolled to the top under you and you had to find your place again. `TermSelector` gained `SelectByLabel`, and the rebuild restores the selection to the row you just changed.

That is the general shape of the remaining gap: pi's interactions preserve _context_ across state changes, and mine were recreating state. The other examples worth fixing in the same spirit:

| Interaction     | What pi does                                    | Status |
| --------------- | ----------------------------------------------- | ------ |
| Settings toggle | selection stays on the row                      | fixed  |
| Model picker    | search text survives a catalog refresh          | next   |
| Provider list   | Escape returns to the auth-method step, not out | fixed  |
| Session switch  | scroll position resets to the branch leaf       | ok     |

### Cleanup

`HandleProvidersKey` was half dead: the list stages became `TermSelector`s, so its `PickProvider` and model-list branches could never run. It is now only the key-entry handler, and `AdvanceKeyEntry` owns the key → account-id → activate sequence.

---

## 32. State preservation sweep

The settings jump was one instance of a pattern: **rebuilding a thing from scratch instead of updating it**. This pass found and fixed the rest.

### `TermSelector.SetItems` preserves context

The rule now lives in one place: replacing the items keeps the search text and restores the selection by label, falling back to the same index when the item genuinely disappeared. Every `Rebuild*` method reuses its selector instance instead of constructing a new one, which fixes:

- **Model picker losing your search** when `RefreshCatalogAsync` runs after a `/connect`.
- **Provider list jumping to the top** when you back out of a key prompt.
- **Settings list jumping** after a toggle (this was the original report).

`ClearQuery` exists for the opposite case — reopening a picker you had closed should start from the full list, but reopening one that is already open should not wipe what you typed.

### `ReplaceTranscript` resets everything derived from the transcript

Switching sessions only swapped `_messages`, so scroll position, tool cards, and the persistence cursor all carried over from the previous conversation. You could land in a new session scrolled halfway up, looking at another session's tool output.

Eight call sites now go through one method: session switch, fork, branch switch, compaction, `/clear`, `/new`, and both resume paths.

### `/sessions` became a real picker

It was the last bespoke list — arrow keys and a hand-rolled overlay, no search. pi's session selector is searchable, and now ours is too, through the same `TermSelector` as everything else. `n` creates a session and `d` deletes one, both only when the search box is empty so they cannot be swallowed by a query.

### `/clear` starts a new session

It used to blank the view while leaving the history in the tree — so the screen said "cleared" and the model still received the conversation. That is a trap. It now starts a fresh session, which is what the line-mode agent already did.

### Dead code removed

Three handlers could no longer run once the lists became selectors: `HandleSessionsKeyAsync`, `HandleToolsKey`, and the list branches of `HandleProvidersKey`. `RenderSessionsOverlay` and `RenderProviderList` went with them. The TUI is now: one chat view, one selector component, and a key-entry stage.

### What is left in this category

| Interaction | Current behaviour | pi |
| --- | --- | --- |
| `Ctrl+P` | opens a command palette | cycles scoped models |
| Slash dropdown | selection resets to the best match per keystroke | same |
| Model picker | query preserved across refresh | same |
| Scroll position | resets on session change | same |

`Ctrl+P` is the remaining divergence, and it needs `scoped-models` (a subset of models chosen for cycling) before it can match.

---

## 33. Wrap-up: what this is, and what it is not

### What shipped

Two packages and one reference client.

**`framework/Reptar.AI`** — the engine. 24 new files:

| Concern | Types |
| --- | --- |
| Loop | `AgentLoop`, `AgentSession`, `AgentRunRegistry`, `AgentEventJson` |
| Messages | `AgentMessages`, `AgentUsage` |
| Context | `ContextCompaction`, `ToolOutput` |
| Durability | `DurableAgent`, `SessionTreeStore` |
| SDK | `AgentSdk`, `AgentSettings`, `ToolSelection` |
| Codemode | `ScriptValue`, `ScriptParser`, `ScriptInterpreter`, `CodeMode` |
| Integrations | `McpClient`, `ClientIdentity`, `CacheWarmer`, `ModelAliases` |
| Resources | `SkillsDirectory` (diagnostics), `PromptTemplates`, `ProjectTrust`, `AgentResources` |
| Diagnostics | `AgentLog` |
| Models | `ThinkingLevels`, `LlmCatalog` (capabilities, output limits) |

**`framework/Reptar.Term`** — the terminal layer, 2,927 lines: buffered diffed rendering, raw-mode input parsing, a line editor, searchable selectors, layout geometry, ANSI markdown.

**`examples/agent-cli`** — a reference client: the agent TUI, a line-mode REPL, an RPC server and C# client, a web SPA, and a self-test.

**Docs** — `ai/agent-runtime` and `ai/term-package`, plus `plans/pi-parity-port.md` (this file).

### What is verified

- 906 unit tests, including contract tests for the loop's guarantees, compaction arithmetic, layout non-overlap, selector state preservation, and reflection-free settings.
- End-to-end against a live local model (Ollama, `llama3.1:8b`): tool calls, error recovery, file mutation, streaming, and the full event stream.
- RPC round trip from both a C# client and a raw JSONL script.
- Web API endpoints against a running server.

### What is not verified

1. **Anthropic prompt caching.** The three-breakpoint implementation is the most load-bearing unverified thing here. `cache_read_input_tokens > 0` on turn 2 is the one check that would confirm it, and Ollama cannot answer it.
2. **Provider attribution headers.** Names come from pi's source; whether OpenRouter credits the app is only visible in their dashboard.
3. **Thinking budgets against a real reasoning model.** The arithmetic is tested; acceptance is not.
4. **The TUI at scale.** Every layout round was fixed from a screenshot, never from watching it work.
5. **The ChatGPT subscription end to end.** The authorize URL, PKCE, callback, and token storage are tested; a token has never actually been used to make a request. The Responses client is likewise exercised only against recorded event shapes.

### What pi has that this does not

| pi | Status |
| --- | --- |
| Extensions (TypeScript modules in-process) | Script-tier port: `AgentExtensionHost` runs `.reptar` files registering tools, commands, and three hooks. AOT forbids loading TS or C# at runtime, so the language differs |
| OAuth / subscription sign-in | ChatGPT subscriptions work (`OAuthPkce` + `LlmOpenAiResponses`); no other provider has a flow |
| Session tree in the web client | Store and RPC support it; the SPA does not expose it |
| `context_edit` entries | Compaction rewrites the projection instead, which covers the common case |
| Provider breadth (~40 providers) | 9 registered, all OpenAI-wire-compatible |

### The honest assessment

The framework is a real agent engine: the loop is faithful to pi's scheduler, the durability is genuine commit-before-reveal, and the codemode interpreter is a working substitute for a JS engine under AOT. It runs, it recovers from tool errors, and it mutates files correctly.

The terminal UI works but is not as polished as pi's, and the gap is not architectural — it is the accumulated small decisions about spacing, colour, and motion that only come from using it daily.

The most valuable next step is not more code. It is a week of using it on real work, with `REPTAR_AGENT_LOG` on, and fixing what annoys you.
