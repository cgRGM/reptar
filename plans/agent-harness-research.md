# Research: Microsoft Agent Framework "Harness" → What Fits Reptar

**Source:** [microsoft/agent-framework · dotnet/samples/02-agents/Harness](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/Harness) and `dotnet/src/Microsoft.Agents.AI/Harness/` (read 2026-10-25).
**Context:** input for the in-flight `framework/Reptar/AI/` agent engine (StreamingAgentHarness, AgentSessionStore, WorkspaceTools, TerminalAgent already exist).
**Filter rule (per the repo owner):** steal the *ideas*, translated to what Reptar does — zero reflection, Native AOT, NetWasm, `TableDef`/`IReptarEntity`, static classes + records, env-var config — not the .NET-isms (DI builders, OTel, Microsoft.Extensions.AI abstraction onions).

---

## What the MSFT harness actually is

Not a framework — a **composition of five `AIContextProvider`s**. Each provider contributes exactly three things: tools, an instruction fragment, and a named slice of session state. `AsHarnessAgent(...)` stacks Todo + AgentMode + FileMemory + FileAccess + ToolApproval + WebSearch on top of a chat client, and `LoopAgent` re-invokes the whole thing until a `LoopEvaluator` says stop. The sample console renders agent events (tool calls, planning, modes) as observers.

That's the whole idea: **agent = base loop + composable (instructions, tools, state) modules.**

---

## Gems worth stealing (Reptar-shaped)

### G1. The module contract: instructions + tools + state key in one unit

MSFT: `abstract class AIContextProvider { Instructions, Tools, StateKeys }`.
Reptar already has the ingredients scattered (`WorkspaceTools` is static, session store is separate, instructions live in callers). One small interface unifies them without DI:

```csharp
public sealed record AgentModule(
    string Name,
    string Instructions,                 // composed into the system prompt
    IReadOnlyList<AiToolDefinition> Tools, // zero new machinery: AiToolDefinition exists
    params string[] StateKeys);            // named slices in the session row

// usage — no builder, no container
var modules = new List<AgentModule>
{
    TodoModule.Create(),          // G2
    PlanExecuteModule.Create(),   // G3
    WorkspaceTools.Module(root),  // exists today; gains approval from G4
};
var systemPrompt = baseInstructions + string.Concat(m => m.Instructions for m in modules);
```

This is a *design rule*, not code volume: every capability ships as one of these, never as scattered wiring.

### G2. `TodoModule` — copy the tools, and especially the prompt

MSFT's `todos_add / todos_complete / todos_remove / todos_get_remaining / todos_get_all` tools with state in the session bag. Reptar: tools already dispatch via `ExecuteAiToolAsync`; state is one `TEXT` column holding the module's JSON slice (or an `agent_todos` `TableDef` if it needs querying).

The real gem is the **battle-tested instruction text** — the discipline rules: "determine whether the ask is complex or simple; if simple, don't add todos, just do it", "update the list when the user changes topics", "include a completion reason". Copy that wording nearly verbatim; prompt-engineering polish is expensive and MSFT has already paid for it.

### G3. `PlanExecuteModule` — mode_set / mode_get tools + mode-conditional instructions

Plan mode: ask clarifying questions **one at a time**, present options instead of asking the user to retype, write the plan to a memory file **so it survives compaction**, ask approval, then `mode_set("execute")`. Execute mode: work autonomously, note judgment calls, don't stall on ambiguity.

Reptar mapping: mode is a column on `agent_sessions`; the harness interpolates `You are currently operating in {mode} mode` + the mode's workflow into the system prompt each turn. Ten lines of code, turns the ReAct loop into something that feels like Claude Code's plan mode. The per-mode instruction templates in `AgentModeProvider.cs` are again directly reusable prompt text.

### G4. Tool approval — the security model is the best part

MSFT's rules, distilled:

1. Dangerous tools are **default-deny** (each exposed as "approval required" and the host decides).
2. Standing auto-approval rules match **tool-level** (`Arguments == null` → all calls) or **tool + exact arguments** (non-null dict). Crucially: *a non-null empty dict matches only zero-arg calls* — it can never silently widen into a tool-level rule. That null/non-null discipline is the part worth copying exactly.
3. Tools come in **read-only vs mutating groups** (`read/read_lines/ls/grep` vs `write/delete/replace/replace_lines`) — approve by group.

Reptar version without their `ApprovalRequiredAIFunction` wrapper classes:

```csharp
public sealed record ToolApprovalRule(string ToolName, string? ArgsJsonExact); // null = tool-wide

public sealed class ToolApproval(
    IReadOnlyList<ToolApprovalRule> autoApprove)   // consulted by ExecuteAiToolAsync
{
    public bool NeedsPrompt(string tool, string argsJson) =>
        !autoApprove.Any(r => r.ToolName == tool &&
            (r.ArgsJsonExact is null || r.ArgsJsonExact == argsJson));
}
```

On the wire this slots straight into the gateway's Vercel data-stream support: `9:` tool-call frame out → UI renders approve/deny → `a:` tool-result frame back. MSFT's harness is console-first; Reptar can be **approval-over-the-wire first**, which is strictly more useful.

### G5. `LoopEvaluator` — one-method stop/continue + hard `MaxIterations` cap

```csharp
public abstract record LoopEvaluation
{
    public static LoopEvaluation Stop() ...
    public static LoopEvaluation Continue(string feedback) ...
}
public interface LoopEvaluator        // evaluated after each agent run
{
    ValueTask<LoopEvaluation> EvaluateAsync(AgentRunSummary last, AgentState state, CancellationToken ct);
}
```

Ship the zero-cost evaluators first: **CompletionMarker** (stop when a fixed string appears in the response — no LLM calls), **TodoCompletion** (stop when the todo slice is empty — composes with G2, and MSFT scopes it to execute-mode only), **Delegate** (host predicate). `AIJudge` (second model judges) and the compaction LLM are gems but cost extra calls — keep both **opt-in** like MSFT does. The `MaxIterations` safety cap is non-negotiable and belongs in the harness loop itself, not in options.

### G6. Background agents as five tools, with the release discipline

`start_task / wait_for_first_completion / get_task_results / continue_task / clear_completed_task` — each task in **its own session**, timeout leaves the task running (call again later), and — the production gem — hosts **must release sessions on teardown so abandoned tasks stop burning tokens**. Reptar's `AgentSessionStore` already gives per-session isolation; the tasks registry is one `agent_tasks` `TableDef`. On NetWasm this is exactly where `IQueueClient`/`IDurableBridge` slot in instead of threads.

### G7. Per-module named state slices (and Reptar's is already better)

MSFT keeps each provider's state under its own key in an in-memory `AgentSessionStateBag`. Reptar's sessions already persist to SQLite via `IReptarEntity` — **strictly better**: state survives process restarts, works on Workers with D1. Keep the naming discipline (`todos`, `mode`, `memory`) so modules stay blind to each other. Replace MSFT's `ConditionalWeakTable<AgentSession, SemaphoreSlim>` per-session locks with a plain `ConcurrentDictionary<string, SemaphoreSlim>` keyed by session id (evictable, NetWasm-safe, no GC-coupling).

### G8. Event-stream → observers/formatters (web-first, not console-first)

Reptar's `AgentEvent` records (`TextDelta`, `ToolCallStarted/Finished`, `RunFinished`…) are already isomorphic to MSFT's rendering observer inputs. The gem: a one-method `IAgentObserver` + per-tool output formatters, so `TerminalAgent`'s ANSI rendering and a future web/SSE rendering are just two observer sets over the same events. My `examples/ai-gateway` data-stream encoder is effectively a third — same event union, different wire.

### G9. Runs produce inspectable trace artifacts

MSFT's `FileSpanExporter` dumps every span to a plain text file. Reptar equivalent with zero OTel: an `agent_runs` `TableDef` (or JSONL append) written fire-and-forget like the gateway's telemetry, keyed by session — model, steps, tool calls, elapsed, token usage. `AgentRunSummary` already carries everything needed.

### G10. Security-notes-as-first-class convention

Every MSFT provider that extends the trust boundary ships with an opt-in flag + a written security note (background agents exfiltration risk, AI-judge judge-client choice, remote skills, compaction LLM). Adopt the convention: `WorkspaceTools` (shell!), `BackgroundAgents`, `AIJudge` each get an explicit opt-in + a security paragraph in docs/llms.txt.

---

## What to leave behind (the ".NET does" list)

| MSFT-ism | Why not for Reptar |
| :-- | :-- |
| `Microsoft.Extensions.AI` `IChatClient` + `.AsIChatClient().AsHarnessAgent()` builder onion | Reptar has `ILlmClient` + `LlmClientFactory` — keep the direct factory; the decorator chain is where AOT/trim pain and indirection live |
| DI container + `*Options` class per type | Records + static factories + `new` |
| OpenTelemetry SDK dependency | Reflection-annotated, heavy for AOT/Wasm; `AgentEvent` stream + `agent_runs` rows cover the 90% need |
| `ApprovalRequiredAIFunction` wrapper class | Flag + rule predicate (G4) |
| Azure/Foundry clients, `DefaultAzureCredential` | Env-var API keys (already the Reptar way) |
| `ConditionalWeakTable` session locks | Plain concurrent dictionary per session id (G7) |
| 1M-token context assumptions, per-call `ChatOptions` soup | Reptar's `LlmConfig` stays a record with a catalog |
| XmlDoc-everything + `[Experimental]` ceremony | Optional |

## What Reptar already has that MSFT's harness does not

- **Durable sessions out of the box** (SQLite/D1-backed `AgentSessionStore` vs in-memory state bag).
- **Route-tree-native tools** — any `.AsAiTool(...)` route becomes a harness tool with validation already attached (`Rule<T>` in the route), no separate tool registry.
- **A real wire protocol story** (SSE + Vercel data stream) — MSFT's harness renders to a console; Reptar's can render to any `useChat()` client, including approvals (G4).
- **The provider-translation layer I built for the gateway** (OpenAI/Anthropic/Gemini/Ollama payload translation + failover) — `LlmClientFactory` already covers the client side; the two efforts should share the translation/parsing code rather than duplicate it.

## Suggested order if the agent-engine team adopts this

1. G4 (approval) — security-relevant, composes with existing `ExecuteAiToolAsync`, unlocks over-the-wire approval via the gateway's `a:` frames.
2. G1 (module contract) + G2/G3 (todo + plan/execute modules) — biggest behavior gain, mostly prompt text + small tools.
3. G5 (loop evaluators + MaxIterations cap).
4. G7 (named state slices over the existing session store).
5. G6/G8/G9 as the harness grows; G10 immediately (docs convention).
