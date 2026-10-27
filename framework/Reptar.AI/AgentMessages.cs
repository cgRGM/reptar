#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// Message roles the agent understands. Ported from pi's <c>AgentMessage</c> union: the
/// transcript stores richer roles than any provider accepts, and
/// <see cref="AgentMessages.ConvertToLlm"/> flattens them at the request boundary.
/// </summary>
public static class AgentRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";

    /// <summary>
    /// A shell command the user ran directly (<c>!command</c>). Reaches the model as a user
    /// message describing what was run and what came back, but is not a tool result — the model
    /// did not decide to run it.
    /// </summary>
    public const string BashExecution = "bashExecution";

    /// <summary>Host-injected context (a skill body, a retrieved document). Flattened to user.</summary>
    public const string Custom = "custom";

    /// <summary>Summary of a branch the conversation returned from. Flattened to user.</summary>
    public const string BranchSummary = "branchSummary";

    /// <summary>A compaction checkpoint. Flattened to user, wrapped in summary tags.</summary>
    public const string CompactionSummary = "compactionSummary";

    /// <summary>Roles the provider accepts as-is.</summary>
    public static bool IsProviderRole(string role) =>
        role is System or User or Assistant or Tool;

    /// <summary>Roles that carry conversation context and must survive compaction.</summary>
    public static bool IsConversational(string role) =>
        role is User or Assistant or Tool or BashExecution or Custom or BranchSummary or CompactionSummary;
}

/// <summary>
/// Converts the stored transcript into the message list a provider accepts. This is the C# port
/// of pi's <c>core/messages.ts</c> <c>convertToLlm</c>.
/// </summary>
/// <remarks>
/// Two reasons this exists rather than storing provider-shaped messages directly:
/// <list type="bullet">
/// <item><c>!command</c> output belongs in the transcript but is not a tool result. Storing it
/// as <see cref="AgentRoles.BashExecution"/> keeps the distinction; flattening it to a user
/// message at the boundary is what lets the model reason about what you ran.</item>
/// <item>A compaction checkpoint is not something the user said. Wrapping it in summary tags
/// stops the model from answering it as a request.</item>
/// </list>
/// </remarks>
public static class AgentMessages
{
    public const string CompactionSummaryPrefix =
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    public const string CompactionSummarySuffix = "\n</summary>";

    public const string BranchSummaryPrefix =
        "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";

    public const string BranchSummarySuffix = "\n</summary>";

    /// <summary>
    /// Builds a <see cref="AgentRoles.BashExecution"/> message for a direct shell command.
    /// </summary>
    /// <param name="command">The command line as the user typed it.</param>
    /// <param name="stdout">Captured stdout.</param>
    /// <param name="stderr">Captured stderr.</param>
    /// <param name="exitCode">Process exit code, or null when it never ran.</param>
    /// <param name="cancelled">True when the command timed out or was interrupted.</param>
    /// <param name="fullOutputPath">Where the untruncated output was spilled, if anywhere.</param>
    /// <param name="excludeFromContext">
    /// True for the <c>!!</c> prefix: keep the message in the transcript for display but omit it
    /// from model context.
    /// </param>
    public static ChatMessage BashExecution(
        string command,
        string stdout,
        string stderr,
        int? exitCode,
        bool cancelled = false,
        string? fullOutputPath = null,
        bool excludeFromContext = false)
    {
        var message = new ChatMessage(AgentRoles.BashExecution, command);
        return message with
        {
            Bash = new BashExecutionDetails(
                command, stdout, stderr, exitCode, cancelled, fullOutputPath, excludeFromContext)
        };
    }

    /// <summary>
    /// Renders a bash-execution message as the user-role text the model sees. Mirrors pi's
    /// <c>bashExecutionToText</c>.
    /// </summary>
    public static string BashExecutionToText(ChatMessage message)
    {
        var bash = message.Bash;
        var command = bash?.Command ?? message.Content;

        var text = $"Ran `{command}`\n";
        var output = bash is null
            ? ""
            : string.Concat(
                bash.Stdout,
                bash.Stdout.Length > 0 && bash.Stderr.Length > 0 ? "\n" : "",
                bash.Stderr);

        text += output.Length > 0 ? $"```\n{output}\n```" : "(no output)";

        if (bash is { Cancelled: true })
            text += "\n\n(command cancelled)";
        else if (bash?.ExitCode is { } code && code != 0)
            text += $"\n\nCommand exited with code {code}";

        if (bash?.FullOutputPath is { Length: > 0 } path)
            text += $"\n\n[Output truncated. Full output: {path}]";

        return text;
    }

    /// <summary>
    /// Flattens the transcript into provider-shaped messages. Messages that must not reach the
    /// model (excluded bash executions, UI-only notes) are dropped rather than emptied.
    /// </summary>
    /// <param name="messages">The stored transcript.</param>
    /// <param name="includeSystem">
    /// Keep <see cref="AgentRoles.System"/> messages. Pass false when the caller supplies its own
    /// system prompt.
    /// </param>
    public static List<ChatMessage> ConvertToLlm(
        IReadOnlyList<ChatMessage> messages,
        bool includeSystem = true)
    {
        var result = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case AgentRoles.BashExecution:
                    if (message.Bash?.ExcludeFromContext is true)
                        continue;
                    result.Add(new ChatMessage(AgentRoles.User, BashExecutionToText(message), Usage: message.Usage));
                    break;

                case AgentRoles.Custom:
                    result.Add(new ChatMessage(AgentRoles.User, message.Content, Usage: message.Usage));
                    break;

                case AgentRoles.BranchSummary:
                    result.Add(new ChatMessage(
                        AgentRoles.User,
                        BranchSummaryPrefix + message.Content + BranchSummarySuffix,
                        Usage: message.Usage));
                    break;

                case AgentRoles.CompactionSummary:
                    result.Add(new ChatMessage(
                        AgentRoles.User,
                        CompactionSummaryPrefix + message.Content + CompactionSummarySuffix,
                        Usage: message.Usage));
                    break;

                case AgentRoles.System:
                    if (includeSystem)
                        result.Add(message);
                    break;

                case AgentRoles.User:
                case AgentRoles.Assistant:
                case AgentRoles.Tool:
                    result.Add(message);
                    break;

                default:
                    // Unknown role: treat as host-injected context rather than dropping silently,
                    // because losing a message changes model behaviour invisibly.
                    result.Add(new ChatMessage(AgentRoles.User, message.Content, Usage: message.Usage));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Ensures a tool result is immediately preceded by the assistant message that issued its
    /// call. Providers reject an orphaned tool result, and compaction or a projection rewrite can
    /// strand one.
    /// </summary>
    public static List<ChatMessage> DropOrphanedToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var issued = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            if (message.Role == AgentRoles.Assistant && message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                    issued.Add(call.Id);
            }

            if (message.Role == AgentRoles.Tool)
            {
                if (message.ToolCallId is null || !issued.Contains(message.ToolCallId))
                    continue;
            }

            result.Add(message);
        }

        return result;
    }
}

/// <summary>Detail carried by a <see cref="AgentRoles.BashExecution"/> message.</summary>
public sealed record BashExecutionDetails(
    string Command,
    string Stdout,
    string Stderr,
    int? ExitCode,
    bool Cancelled,
    string? FullOutputPath,
    bool ExcludeFromContext);
#endif
