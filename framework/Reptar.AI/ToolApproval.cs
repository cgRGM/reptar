#if !NETWASM
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// G4 — A standing auto-approval rule for tool calls.
///
/// Matches in two ways (mirroring the Microsoft Agent Framework's ToolApproval
/// semantics, which are worth copying exactly):
///
///  - <b>Tool-level</b>: <see cref="ArgsJsonExact"/> is <c>null</c> → every
///    call to <see cref="ToolName"/> is auto-approved.
///  - <b>Tool + exact arguments</b>: <see cref="ArgsJsonExact"/> is non-null →
///    only calls whose arguments serialize identically are auto-approved.
///    A <b>non-null but empty</b> exact-args value ("{}") matches only calls
///    that supply no arguments — it can never silently widen into a
///    tool-level rule.
/// </summary>
public sealed record ToolApprovalRule
{
    public required string ToolName { get; init; }

    /// <summary>
    /// Normalized JSON of the exact arguments this rule approves, or null for a
    /// tool-wide rule. Normalization compacts whitespace so
    /// <c>{ "a": 1 }</c> and <c>{"a":1}</c> match.
    /// </summary>
    public string? ArgsJsonExact { get; init; }

    public static ToolApprovalRule ForTool(string toolName) => new() { ToolName = toolName };

    public static ToolApprovalRule ForCall(string toolName, string argsJson) => new()
    {
        ToolName = toolName,
        ArgsJsonExact = ToolApproval.NormalizeJson(argsJson),
    };
}

/// <summary>
/// Approval policy for agent tools. Tools not covered by an auto-approval rule
/// require a decision. The read-only / mutating grouping mirrors the MSFT
/// harness file-access taxonomy: reads are cheap to trust, writes are not.
/// </summary>
public sealed class ToolApproval
{
    private readonly Dictionary<string, List<ToolApprovalRule>> _rulesByTool;
    private readonly HashSet<string> _readOnlyTools;

    public ToolApproval(
        IReadOnlyList<ToolApprovalRule> autoApproveRules,
        IReadOnlySet<string>? readOnlyTools = null)
    {
        _rulesByTool = autoApproveRules
            .GroupBy(rule => rule.ToolName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        _readOnlyTools = readOnlyTools is not null ? [.. readOnlyTools] : [];
    }

    public IReadOnlyCollection<ToolApprovalRule> Rules =>
        _rulesByTool.Values.SelectMany(rules => rules).ToList();

    /// <summary>
    /// Static approval source: allow any call the policy covers, deny
    /// everything else. Use for tests and trusted internal agents.
    /// </summary>
    public static ToolApproval ApproveAll(params string[] toolNames) =>
        new(toolNames.Select(ToolApprovalRule.ForTool).ToArray());

    /// <summary>
    /// Auto-approve the given read-only tools; every other tool (the mutating
    /// group) requires a decision. Mirrors the FileAccessProvider split
    /// (read / read_lines / ls / grep auto-approvable; write / delete / replace
    /// gated).
    /// </summary>
    public static ToolApproval ApproveReadOnly(params string[] readOnlyToolNames) =>
        new(readOnlyToolNames.Select(ToolApprovalRule.ForTool).ToArray(), readOnlyToolNames.ToHashSet());

    public bool IsReadOnly(string toolName) => _readOnlyTools.Contains(toolName);

    /// <summary>
    /// True when the call is covered by a standing rule: either a tool-level
    /// rule, or an exact-arguments rule whose normalized JSON matches.
    /// </summary>
    public bool IsAutoApproved(string toolName, string argsJson)
    {
        if (!_rulesByTool.TryGetValue(toolName, out var rules))
            return false;

        var normalized = NormalizeJson(argsJson);
        foreach (var rule in rules)
        {
            // null = tool-level: approve every invocation of the tool.
            if (rule.ArgsJsonExact is null)
                return true;

            // Non-null (including "{}") = argument-scoped: exact match only.
            if (string.Equals(rule.ArgsJsonExact, normalized, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when this call needs a human decision before execution.
    /// </summary>
    public bool NeedsDecision(string toolName, string argsJson) =>
        !IsAutoApproved(toolName, argsJson);

    /// <summary>
    /// Human-readable approval question for a gated call — the text surfaced
    /// through a request port or the wire approval event.
    /// </summary>
    public string DescribeCall(string toolName, string argsJson) =>
        $"Approve the '{toolName}' tool call with arguments {argsJson}? " +
        $"(Read-only: {IsReadOnly(toolName).ToString().ToLowerInvariant()})";

    internal static string? NormalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            // Canonical re-serialization (not GetRawText, which echoes the
            // source verbatim): compact output with key order preserved, so
            // whitespace differences never break exact-argument matching.
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }
}

/// <summary>
/// Executor decorators that enforce a <see cref="ToolApproval"/> policy.
/// Two flavors:
///
///  - <see cref="Wrap"/>: a synchronous decision callback (tests, trusted
///    internal policy engines, "always deny in CI").
///  - <see cref="WrapWithCheckpoint"/>: human-in-the-loop — a gated call
///    suspends the run through the G14 request-port machinery and resumes
///    minutes or days later; an approved answer executes the real tool and a
///    denied answer returns an explicit refusal the model can reason about.
/// </summary>
public static class ToolApprovalGate
{
    public const string ApprovalPortName = "approval";

    /// <summary>
    /// Wraps an executor with a synchronous approval decision. When the
    /// policy needs a decision and the callback declines (or throws), the
    /// model receives a structured 403 refusal instead of the tool result.
    /// </summary>
    public static Func<string, string, Task<AiToolResult>> Wrap(
        Func<string, string, Task<AiToolResult>> executor,
        ToolApproval approval,
        Func<string, string, Task<bool>> decide)
    {
        return async (name, argsJson) =>
        {
            if (approval.NeedsDecision(name, argsJson))
            {
                bool allowed;
                try
                {
                    allowed = await decide(name, argsJson).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return AiToolResult.Failure(403, "approval_denied",
                        $"Approval check failed: {ex.Message}");
                }

                if (!allowed)
                {
                    return AiToolResult.Failure(403, "approval_denied",
                        $"The user declined the '{name}' tool call.");
                }
            }

            return await executor(name, argsJson).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Wraps an executor with human-in-the-loop approval via request ports.
    /// A gated tool call persists an <c>approval</c> checkpoint (with the
    /// pending tool name and arguments) and suspends the run. The caller
    /// surfaces the checkpoint's question; the run resumes through
    /// <see cref="RequestPortAgent.ResumeAsync"/>, which executes the real
    /// tool on approval and returns a refusal on denial.
    /// </summary>
    public static Func<string, string, Task<AiToolResult>> WrapWithCheckpoint(
        Func<string, string, Task<AiToolResult>> executor,
        ToolApproval approval,
        RunCheckpointStore store,
        string sessionId,
        List<ChatMessage> history,
        Action<RequestPortEvent>? onPortEvent = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        return async (name, argsJson) =>
        {
            if (!approval.NeedsDecision(name, argsJson))
            {
                return await executor(name, argsJson).ConfigureAwait(false);
            }

            var options = jsonOptions ?? ReptarJson.Options;
            var callId = FindPendingToolCallId(history, name);

            var checkpoint = await store.CreateAsync(
                sessionId,
                ApprovalPortName,
                approval.DescribeCall(name, argsJson),
                JsonSerializer.Serialize(history, options),
                pendingToolCallId: callId,
                pendingToolName: name,
                pendingToolArgs: argsJson).ConfigureAwait(false);

            onPortEvent?.Invoke(new RequestPortEvent.RunSuspended(
                checkpoint.Id, ApprovalPortName, checkpoint.Question));

            throw new RequestPortSuspendedException(ApprovalPortName, checkpoint.Question);
        };
    }

    private static string FindPendingToolCallId(List<ChatMessage> history, string toolName)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index] is not { Role: "assistant", ToolCalls: { Count: > 0 } calls })
                continue;

            var call = calls.FirstOrDefault(candidate => candidate.Name == toolName);
            if (call is { } pending)
                return pending.Id;
        }

        return "call_pending";
    }
}
#endif
