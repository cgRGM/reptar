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

---

# Part 2: The wider `dotnet/` tree — second pass (beyond Harness)

**Areas reviewed:** `Microsoft.Agents.AI.Abstractions` (agent/session/update models, structured output), `Hosting` (hosted agents, isolation keys), `A2A` + `Hosting.A2A` (Agent-to-Agent protocol), `AGUI` (→ moved to the official AG-UI C# SDK; only ASP.NET glue remains), `Workflows` (graph engine, checkpointing, human-in-the-loop ports, Magentic prompts), `Workflows.Generators` (Roslyn), `Workflows.Declarative` (YAML + expressions), `AgentHooks` (persistence gating), `AgentSkills` (SKILL.md conventions), `Evaluation` samples, `DevUI` samples, A2A samples.

Same filter as Part 1: **steal ideas translated to Reptar primitives; skip the .NET-isms.**

## G11. Structured output with typed validation — `Rule<T>` is the missing half of `RunAsync<T>`

MSFT's `AIAgent.RunAsync<T>(...)` requests a response of type `T` (JSON schema derived via source-gen serializers). Reptar already owns the *better* half of this — `Rule<T>` validators — and currently only uses them on inbound routes. The gem: use them on **outbound LLM output** with a validation-retry loop:

```csharp
public static async Task<T?> RunStructuredAsync<T>(this ILlmClient client, Validator<T> rule,
    string message, int maxAttempts = 3, CancellationToken ct = default)
{
    var attempt = message;
    for (var i = 0; i < maxAttempts; i++)
    {
        var raw = await client.CompleteAsync(attempt, ct);          // JSON-mode completion
        var parsed = TryParse<T>(raw);                               // zero-reflection: source-gen or JsonDocument mapping
        if (parsed is { } value && rule.Validate(value).Count == 0) return value;
        attempt = message + "\n\nYour previous reply was invalid JSON or failed validation: " +
                  issuesJson + "\nReply again with corrected JSON only.";
    }
    return null;
}
```

One helper turns any Reptar route handler into "LLM returns a validated C# record or we retry." This is the cheapest high-value gem in the whole tree for Reptar.

## G12. `AgentResponseUpdate` — the streaming-chunk taxonomy is worth copying field-for-field

Every update chunk carries `AgentId`, `MessageId`, `CreatedAt`, `FinishReason`, `ContinuationToken`, `RawRepresentation`, `AdditionalProperties`. Two gems over Reptar's current `AgentEvent` union:

1. **Stable `MessageId`/`CallId` on every streaming frame** — lets UIs key, merge, and reorder parts (Reptar's `ToolCallStarted/Finished` have `Id` already; text deltas don't).
2. **`ContinuationToken` on the update stream** — the A2A samples use it for *stream reconnection* and *polling for long-running task completion*. For the gateway: a `X-Reptar-Continuation` header + `?continuation=` replay would make browser reconnects survive network blips and Workers isolate eviction.

Also copy their discipline: buffered-response ⇄ update-stream are **bidirectionally convertible, and documented as lossy** — keep both shapes explicit.

## G13. A2A as a gateway provider type ("agents as upstreams")

The A2A sample set is the most gateway-relevant part of the tree: **agent cards** (capability/skill advertisement at a URL), task lifecycle (submitted → working → completed/failed), protocol selection (HTTP+JSON / JSON-RPC / gRPC), and continuation tokens for both polling and stream resume.

Reptar mapping — add two concepts to the `examples/ai-gateway` provider table:

- `"a2a/<url>"` model routing: fetch the **agent card** once, expose each advertised skill as a tool (`A2AAgent_AsFunctionTool` = whole agent as one tool; `A2AAgent_Skills` = one tool per skill — steal both shapes), translate chat-completions → A2A task submission, poll/subscribe until terminal state.
- Long-running task rows in SQLite/D1 (`a2a_tasks`: id, status, continuation, result) give the gateway free durable task tracking — the state store Reptar already ships is what MSFT builds entire hosting packages for.

And the inverse: Reptar agents *exposed* as A2A servers is a natural `app.MapA2A("/a2a", agent)` — but that's a bigger commitment; catalog it, don't build it yet.

## G14. Request ports — human-in-the-loop *as a first-class graph primitive* (the biggest workflow gem)

`Workflows` lets any executor register a **RequestPort**, emit a `RequestInfoEvent` mid-run, and **suspend** until the host posts an `ExternalResponse`. Combined with `CheckpointManager` (resumable runs) this means: a multi-step agent workflow can pause for days waiting on a human, then resume from durable state.

Reptar mapping, keeping it small:

- A workflow/agent run that needs input emits a `RequestInfo` event → serialized over SSE/data-stream to the client → client POSTs the answer to a `POST /agent/runs/{id}/continue` route → the run resumes from its checkpointed state.
- Checkpoint = a row in a `agent_run_checkpoints` `TableDef` (session id, step, state JSON, created_at) — written through the existing SQLite/D1 client. Reptar's durable session store already gives this for free; the *port/resume* semantics are the only new code.

This composes with G4 (approval) from Part 1 — approval is just a request port with two buttons.

## G15. The Magentic prompt pack — battle-tested multi-agent orchestration text

`MagenticDefaultPrompts` ships the Magentic-One "task ledger" research as copyable prompt templates:

- **Fact-sheet pre-survey** before planning: GIVEN OR VERIFIED FACTS / FACTS TO LOOK UP / FACTS TO DERIVE / EDUCATED GUESSES — with the instruction "at least add or update one educated guess, and explain your reasoning."
- **Replan-on-failure prompt**: "explain the root cause of the failure, then a new plan that avoids repeating the same mistakes."
- Plan + progress-ledger templates with named `{placeholders}` and a documented "overrides must keep `{schema}`" rule.

Same as the Harness todo/mode prompts: the *text* is the gem; port it into whatever multi-agent module Reptar grows (or a `PromptPack` static class users can compose into instructions).

## G16. `Workflows.Generators` validates Reptar's whole approach — and adds one idea

MSFT uses a Roslyn incremental generator so `[MessageHandler]` methods become **compile-time route tables** (zero runtime reflection — same philosophy as Reptar's `AsAiTool`/`ColumnRef` generators). The new idea worth taking: `[SendsMessage]/[YieldsOutput]` attributes generate **protocol metadata**, so message flow between executors is statically analyzable. A future Reptar workflow module could emit build errors like *"executor `Fetcher` yields `RawHtml`, but no executor handles it"* — compile-time graph validation is exactly "what we do," and it's a genuine differentiator vs every runtime-wired agent framework.

## G17. Declarative workflows — take three ideas, skip the platform

The YAML no-code platform itself (PowerFx interpreter, sensitivity-checked expressions, Foundry bindings) is too big and too .NET. But three details are excellent:

1. **Sensitivity metadata on expressions**: control-flow expressions must evaluate to *non-sensitive* values; diagnostics reference variable *names*, never values. Any Reptar feature that runs user-influenced expressions (filters, templates, workflow conditions) should adopt the same privacy rule.
2. **Capture decisions at checkpoint time**: "changes to workflow state while a request is pending do not change that invocation's behavior" — resume determinism. Reptar's resume (G14) should snapshot the decision alongside the checkpoint.
3. **File-based workflow definition** fits Reptar's philosophy perfectly: a `workflow.yaml` next to `app.cs`, validated by a source generator into route registrations — the same `app.cs` story applied to orchestration. (Design sketch only; not urgent.)

## G18. AgentHooks — persistence gating ("don't save the secret part")

`RunPersistenceGate` / `PersistenceGating` wrap run persistence with hooks — the practical use: mask or skip persisting tool results that contain secrets/PII before they land in session history. Reptar mapping: an optional `IPersistFilter` consulted by `AgentSessionStore` before writing message rows (redact, drop, or replace text). Ten lines of interface; large safety payoff once `WorkspaceTools` and web agents read real data.

## G19. Evals — run them through the gateway's mock provider and they're free

MSFT's `agent.EvaluateAsync(queries, evaluator)` → per-metric numeric scores, with variants for expected outputs, custom evals, multimodal. The Reptar version should not be a Foundry API call — it should be:

- an `evals` `TableDef` (run_id, query, response, metric, score, created_at),
- an eval runner route that replays a fixture list through the **ai-gateway**, deterministic against `mock-*` (CI-runnable, zero API cost), real providers opt-in,
- an LLM-judge as just another `ILlmClient` call with a scoring prompt (G15's discipline applies).

This is the natural extension of `examples/ai-gateway`: the gateway already proved the transport; evals turn it into a test harness for prompts and agent behavior.

## G20. Skills at runtime — load `SKILL.md` directories as agent tools

The repo itself (`.agents/skills/*/SKILL.md`) already follows this convention for *developer* agents. MSFT's `AgentSkills` makes the same shape a **runtime** capability: skill = metadata + resources + scripts; file-based / code-defined / class-based sources; scripts run through a `SubprocessScriptRunner`; skills can also arrive over MCP. Reptar mapping: `WorkspaceTools` gains a `SkillsDirectory(path)` that turns each skill into an `AiToolDefinition` (description from SKILL.md, scripts executed via the subprocess runner or, on NetWasm, through `IQueueClient`/DO isolation). Keep it file-based-first — that's the Reptar house style.

## G21. Isolation keys + DevUI (two hosting notes)

- `AgentIsolationKeyProvider` + `IsolationKeyScopedAgentSessionStore`: sessions scoped per tenant/user so hosted agents can't cross-read. Reptar analog: add `User.Id` (or an explicit isolation key column) to `agent_sessions` and filter every store read through it — mandatory before agents are ever exposed to multiple users.
- DevUI: an interactive web page for inspecting runs/events. Reptar already has a Studio console pattern (BaaS) — an "agent runs" page reading the `agent_runs` telemetry table (Part 1, G9) is the right-size version. No Aspire, no separate app.

## What to leave behind from this pass

| .NET-ism | Verdict |
| :-- | :-- |
| `Microsoft.Extensions.AI` `IChatClient` decorator onion (`AsIChatClient().AsAIAgent()`) | Reptar keeps `ILlmClient` + factory |
| DI `ServiceCollection` hosting (`AddA2AServer`, `HostedAgentBuilder`) | `app.Use...`/records/`new` |
| OpenTelemetry SDK in the agent core | `AgentEvent` stream + telemetry tables (already agreed in Part 1) |
| Workflows' `Reflection/` directory + delayed external-request sink plumbing | Request ports *concept* (G14) without the host plumbing |
| PowerFx interpreter for expressions | Not in scope; steal only its privacy discipline (G17) |
| Azure/Foundry, Cosmos/Valkey/Mem0, Purview adapters | Reptar already maps SQLite/D1/R2/KV/Storage in `ReptarConfig` |
| Aspire DevUI app model | Studio page (G21) |
| `[Experimental]`/XmlDoc ceremony, `Throw.*` helpers, `ClientRetryPolicy` | Optional |

## Updated adoption order (merged with Part 1)

1. **G11** structured output + `Rule<T>` retry — hours of work, immediately useful.
2. **G4 + G14** approval + request-port resume over the existing SSE/data-stream wire (gateway + session store already exist).
3. **G1/G2/G3** module contract + todo + plan/execute modules (prompt text from MSFT).
4. **G18** persistence gating, **G21** isolation keys — both required before any multi-user agent exposure.
5. **G19** evals through the gateway (mock provider = free CI).
6. **G12** continuation tokens on the wire; **G15** Magentic prompt pack; **G20** skills directory.
7. **G13/G16/G17** (A2A provider type, compile-time graph validation, declarative workflows) — design notes for when the engine stabilizes.
