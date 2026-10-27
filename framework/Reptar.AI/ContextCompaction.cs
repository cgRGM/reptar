#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>
/// Settings for automatic context compaction. Ported from pi's
/// <c>DEFAULT_COMPACTION_SETTINGS</c>.
/// </summary>
public sealed record CompactionSettings
{
    /// <summary>Compaction is a no-op when false, including the manual entry point.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Headroom kept free for the next response. Compaction triggers when
    /// <c>contextTokens &gt; contextWindow - ReserveTokens</c>.
    /// </summary>
    public int ReserveTokens { get; init; } = 16_384;

    /// <summary>How much recent conversation to keep verbatim at the tail.</summary>
    public int KeepRecentTokens { get; init; } = 20_000;

    /// <summary>Upper bound on the summary response. Zero uses the model's own limit.</summary>
    public int SummaryMaxTokens { get; init; } = 4_096;

    public static readonly CompactionSettings Default = new();
    public static readonly CompactionSettings Disabled = new() { Enabled = false };
}

/// <summary>Why a compaction ran. Mirrors pi's <c>compaction_start.reason</c>.</summary>
public enum CompactionReason
{
    Manual,
    Threshold,
    Overflow
}

/// <summary>
/// Outcome of a compaction. <see cref="FirstKeptMessageIndex"/> is the index into the
/// transcript of the first message retained verbatim; everything before it is replaced by
/// <see cref="Summary"/>.
/// </summary>
public sealed record CompactionResult(
    string Summary,
    int FirstKeptMessageIndex,
    int TokensBefore,
    int EstimatedTokensAfter,
    AgentUsage? Usage,
    CompactionReason Reason);

/// <summary>
/// Context compaction for long sessions. Pure functions over the transcript, so the caller
/// owns storage and the model call.
/// </summary>
/// <remarks>
/// This is a port of pi's <c>core/compaction/compaction.ts</c>. The important properties:
/// <list type="bullet">
/// <item>Token counting prefers the last provider-reported <see cref="AgentUsage"/> and only
/// estimates the trailing messages, which is far more accurate than estimating everything.</item>
/// <item>Cut points never land on a tool result — a tool result must stay attached to the
/// assistant message that produced it.</item>
/// <item>A cut may split a turn; the summary then also covers the prefix of that turn so the
/// model does not see a tool call without its result.</item>
/// </list>
/// </remarks>
public static class ContextCompaction
{
    /// <summary>Conservative chars-per-token divisor; deliberately overestimates.</summary>
    private const int CharsPerToken = 4;

    /// <summary>Rough character cost of an image block.</summary>
    private const int EstimatedImageChars = 4_800;

    // ------------------------------------------------------------------
    // Token estimation
    // ------------------------------------------------------------------

    /// <summary>Estimates the token cost of one message.</summary>
    public static int EstimateTokens(ChatMessage message)
    {
        var chars = message.Content?.Length ?? 0;

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            foreach (var call in calls)
                chars += call.Name.Length + call.Arguments.Length;
        }

        if (message.Images is { Count: > 0 } images)
            chars += images.Count * EstimatedImageChars;

        return (chars + CharsPerToken - 1) / CharsPerToken;
    }

    /// <summary>
    /// Estimates the live context size. When the transcript contains a provider-reported
    /// usage, that value is authoritative for everything up to it and only the trailing
    /// messages are estimated.
    /// </summary>
    public static int EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != "assistant" || message.Usage is not { IsEmpty: false } usage)
                continue;

            var trailing = 0;
            for (var j = i + 1; j < messages.Count; j++)
                trailing += EstimateTokens(messages[j]);

            return usage.TotalTokens + trailing;
        }

        var total = 0;
        foreach (var message in messages)
            total += EstimateTokens(message);
        return total;
    }

    /// <summary>True when the context is close enough to the window limit to compact.</summary>
    public static bool ShouldCompact(int contextTokens, int contextWindow, CompactionSettings settings) =>
        settings.Enabled && contextWindow > 0 && contextTokens > contextWindow - settings.ReserveTokens;

    // ------------------------------------------------------------------
    // Cut point selection
    // ------------------------------------------------------------------

    /// <summary>
    /// A message that can start a retained tail. Tool results are excluded because a tool
    /// result detached from its tool call is rejected by every provider.
    /// </summary>
    private static bool IsCutPoint(ChatMessage message) => message.Role is
        "user" or "assistant" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

    /// <summary>A message that begins a new turn (user-like, never assistant or tool).</summary>
    private static bool IsTurnStart(ChatMessage message) => message.Role is
        "user" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

    /// <summary>
    /// Chooses where to cut the transcript so roughly <see cref="CompactionSettings.KeepRecentTokens"/>
    /// of recent conversation survives verbatim.
    /// </summary>
    /// <param name="messages">The full transcript.</param>
    /// <param name="keepRecentTokens">Tail budget.</param>
    /// <param name="startIndex">First index eligible for summarization (0, or after a prior compaction).</param>
    /// <returns>
    /// The index of the first message to keep, and — when the cut splits a turn — the index of
    /// the user message that started it.
    /// </returns>
    public static (int FirstKeptIndex, int TurnStartIndex, bool IsSplitTurn) FindCutPoint(
        IReadOnlyList<ChatMessage> messages,
        int keepRecentTokens,
        int startIndex = 0)
    {
        if (messages.Count == 0)
            return (0, -1, false);

        var cutPoints = new List<int>();
        for (var i = startIndex; i < messages.Count; i++)
        {
            if (IsCutPoint(messages[i]))
                cutPoints.Add(i);
        }

        if (cutPoints.Count == 0)
            return (startIndex, -1, false);

        var accumulated = 0;
        var cutIndex = cutPoints[0];

        for (var i = messages.Count - 1; i >= startIndex; i--)
        {
            var tokens = EstimateTokens(messages[i]);
            if (tokens == 0)
                continue;

            accumulated += tokens;

            if (accumulated < keepRecentTokens)
                continue;

            // Prefer the nearest valid cut point at or after this message. If trailing tool
            // results alone blow the budget, keep their preceding assistant tool call rather
            // than collapsing to the first message.
            var candidate = -1;
            foreach (var point in cutPoints)
            {
                if (point >= i)
                {
                    candidate = point;
                    break;
                }
            }
            cutIndex = candidate >= 0 ? candidate : cutPoints[^1];
            break;
        }

        var startsTurn = IsTurnStart(messages[cutIndex]);
        var turnStart = startsTurn ? -1 : FindTurnStart(messages, cutIndex, startIndex);

        return (cutIndex, turnStart, !startsTurn && turnStart != -1);
    }

    /// <summary>Finds the user-like message that started the turn containing <paramref name="index"/>.</summary>
    public static int FindTurnStart(IReadOnlyList<ChatMessage> messages, int index, int startIndex)
    {
        for (var i = index; i >= startIndex; i--)
        {
            if (IsTurnStart(messages[i]))
                return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // Summarization prompts (verbatim from pi)
    // ------------------------------------------------------------------

    /// <summary>System prompt for the summarization call.</summary>
    public const string SummarizationSystemPrompt =
        "You are a precise technical summarizer. You produce structured context checkpoints "
        + "that let another assistant continue work without re-reading the conversation. "
        + "Never invent details. Preserve exact file paths, identifiers, and error messages.";

    /// <summary>First-pass summarization instruction.</summary>
    public const string SummarizationPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this EXACT format:

        ## Goal
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    /// <summary>Iterative summarization instruction, used when a previous summary exists.</summary>
    public const string UpdateSummarizationPrompt = """
        The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

        Update the existing structured summary with new information. RULES:
        - PRESERVE all existing information from the previous summary
        - ADD new progress, decisions, and context from the new messages
        - UPDATE the Progress section: move items from "In Progress" to "Done" when completed
        - UPDATE "Next Steps" based on what was accomplished
        - PRESERVE exact file paths, function names, and error messages
        - If something is no longer relevant, you may remove it

        Use the EXACT same section layout as the existing summary: Goal, Constraints & Preferences,
        Progress (Done / In Progress / Blocked), Key Decisions, Next Steps, Critical Context.
        """;

    // ------------------------------------------------------------------
    // Summary assembly
    // ------------------------------------------------------------------

    /// <summary>
    /// Serializes the messages being summarized into the prompt body. Tool calls and results
    /// are flattened so the summarizer sees what actually ran.
    /// </summary>
    public static string SerializeForSummary(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "user":
                    builder.AppendLine("### User");
                    builder.AppendLine(message.Content);
                    break;

                case "assistant":
                    builder.AppendLine("### Assistant");
                    if (message.Content.Length > 0)
                        builder.AppendLine(message.Content);

                    if (message.ToolCalls is { Count: > 0 } calls)
                    {
                        foreach (var call in calls)
                            builder.AppendLine($"- called `{call.Name}` with {Clip(call.Arguments, 600)}");
                    }
                    break;

                case "tool":
                    builder.AppendLine($"### Tool result ({message.Name ?? "tool"})");
                    builder.AppendLine(Clip(message.Content, 2_000));
                    break;

                default:
                    builder.AppendLine($"### {message.Role}");
                    builder.AppendLine(Clip(message.Content, 2_000));
                    break;
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the user-role prompt for the summarization call.
    /// </summary>
    /// <param name="messagesToSummarize">Messages that will be replaced by the summary.</param>
    /// <param name="splitTurnPrefix">
    /// When the cut splits a turn, the leading portion of that turn. It is summarized into the
    /// same checkpoint so the retained tail starts on a turn boundary.
    /// </param>
    /// <param name="previousSummary">Prior checkpoint, for iterative updates.</param>
    /// <param name="customInstructions">User-supplied focus, from <c>/compact &lt;instructions&gt;</c>.</param>
    public static string BuildSummaryPrompt(
        IReadOnlyList<ChatMessage> messagesToSummarize,
        IReadOnlyList<ChatMessage> splitTurnPrefix,
        string? previousSummary,
        string? customInstructions)
    {
        var builder = new StringBuilder();

        if (previousSummary is { Length: > 0 })
        {
            builder.AppendLine("<previous-summary>");
            builder.AppendLine(previousSummary);
            builder.AppendLine("</previous-summary>");
            builder.AppendLine();
        }

        if (messagesToSummarize.Count > 0)
        {
            builder.AppendLine("<conversation>");
            builder.AppendLine(SerializeForSummary(messagesToSummarize));
            builder.AppendLine("</conversation>");
            builder.AppendLine();
        }

        if (splitTurnPrefix.Count > 0)
        {
            builder.AppendLine("<current-turn-prefix>");
            builder.AppendLine(SerializeForSummary(splitTurnPrefix));
            builder.AppendLine("</current-turn-prefix>");
            builder.AppendLine();
            builder.AppendLine(
                "The current-turn-prefix is the beginning of a turn whose remainder is kept verbatim. "
                + "Summarize its intent and results so the retained remainder still makes sense.");
            builder.AppendLine();
        }

        builder.AppendLine(previousSummary is { Length: > 0 } ? UpdateSummarizationPrompt : SummarizationPrompt);

        if (customInstructions is { Length: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine($"Additional focus requested by the user: {customInstructions}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Wraps a summary so the model recognizes it as a checkpoint rather than a user request.
    /// </summary>
    public static string FormatSummaryMessage(string summary) =>
        "The conversation history before this point was compacted into the following summary:\n\n"
        + "<summary>\n"
        + summary.Trim()
        + "\n</summary>";

    /// <summary>
    /// Applies a compaction to a transcript: the summary replaces everything before the kept
    /// tail. Returns the new message list; the caller keeps the original for export.
    /// </summary>
    public static List<ChatMessage> Apply(
        IReadOnlyList<ChatMessage> messages,
        CompactionResult result)
    {
        var kept = messages
            .Skip(Math.Clamp(result.FirstKeptMessageIndex, 0, messages.Count))
            .ToList();

        var compacted = new List<ChatMessage>(kept.Count + 2);

        // Preserve a leading system prompt; it is not conversation history.
        foreach (var message in messages)
        {
            if (message.Role != "system")
                break;
            compacted.Add(message);
        }

        compacted.Add(new ChatMessage("user", FormatSummaryMessage(result.Summary)));
        compacted.AddRange(kept);

        return compacted;
    }

    /// <summary>
    /// Plans a compaction without calling a model: computes the cut point, token counts, and
    /// the prompt to send. Call this, run the summary, then call <see cref="Apply"/>.
    /// </summary>
    public static CompactionPlan Plan(
        IReadOnlyList<ChatMessage> messages,
        CompactionSettings settings,
        int startIndex = 0,
        string? previousSummary = null,
        string? customInstructions = null)
    {
        var (firstKeptIndex, turnStartIndex, isSplitTurn) = FindCutPoint(
            messages, settings.KeepRecentTokens, startIndex);

        var summarizeEnd = isSplitTurn ? turnStartIndex : firstKeptIndex;

        var messagesToSummarize = new List<ChatMessage>();
        for (var i = startIndex; i < Math.Max(startIndex, summarizeEnd); i++)
            messagesToSummarize.Add(messages[i]);

        var splitTurnPrefix = new List<ChatMessage>();
        if (isSplitTurn && turnStartIndex >= 0)
        {
            for (var i = turnStartIndex; i < firstKeptIndex; i++)
                splitTurnPrefix.Add(messages[i]);
        }

        var tokensBefore = EstimateContextTokens(messages);

        return new CompactionPlan(
            messagesToSummarize,
            splitTurnPrefix,
            firstKeptIndex,
            tokensBefore,
            BuildSummaryPrompt(messagesToSummarize, splitTurnPrefix, previousSummary, customInstructions),
            isSplitTurn);
    }

    private static string Clip(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";
}

/// <summary>
/// The inputs and cut point for one compaction, before the summary exists.
/// </summary>
public sealed record CompactionPlan(
    IReadOnlyList<ChatMessage> MessagesToSummarize,
    IReadOnlyList<ChatMessage> SplitTurnPrefix,
    int FirstKeptMessageIndex,
    int TokensBefore,
    string Prompt,
    bool IsSplitTurn)
{
    /// <summary>True when there is nothing to compact.</summary>
    public bool IsEmpty => MessagesToSummarize.Count == 0 && SplitTurnPrefix.Count == 0;
}

/// <summary>
/// Runs a compaction end to end: plans the cut, asks the model for the checkpoint summary,
/// and returns the result plus the shortened projection.
/// </summary>
public static class ContextCompactor
{
    /// <summary>
    /// Summarizes and applies a compaction. Returns null when there is nothing to compact or
    /// the model produced no usable summary (an aborted or truncated summary must never become
    /// a checkpoint).
    /// </summary>
    /// <param name="history">Full transcript. Never mutated.</param>
    /// <param name="client">Provider used for the summarization call.</param>
    /// <param name="settings">Compaction thresholds.</param>
    /// <param name="reason">Why this compaction ran.</param>
    /// <param name="startIndex">
    /// Where summarization may begin. Pass the previous compaction's
    /// <see cref="CompactionResult.FirstKeptMessageIndex"/> on repeated compactions so messages
    /// that survived the earlier pass are summarized too.
    /// </param>
    /// <param name="previousSummary">Prior checkpoint, for iterative updates.</param>
    /// <param name="customInstructions">Focus from <c>/compact &lt;instructions&gt;</c>.</param>
    public static async Task<(CompactionResult Result, List<ChatMessage> Projection)?> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        ILlmClient client,
        CompactionSettings settings,
        CompactionReason reason,
        int startIndex = 0,
        string? previousSummary = null,
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
            return null;

        var plan = ContextCompaction.Plan(
            history, settings, startIndex, previousSummary, customInstructions);

        if (plan.IsEmpty)
            return null;

        var summary = await SummarizeAsync(plan.Prompt, client, settings, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summary))
            return null;

        var result = new CompactionResult(
            summary.Trim(),
            plan.FirstKeptMessageIndex,
            plan.TokensBefore,
            ContextCompaction.EstimateContextTokens(
                ContextCompaction.Apply(history, new CompactionResult(
                    summary, plan.FirstKeptMessageIndex, plan.TokensBefore, 0, null, reason))),
            null,
            reason);

        return (result, ContextCompaction.Apply(history, result));
    }

    /// <summary>
    /// Issues the summarization request. Tools are deliberately not offered: a summarizer that
    /// can call tools can mutate the workspace.
    /// </summary>
    public static async Task<string> SummarizeAsync(
        string prompt,
        ILlmClient client,
        CompactionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new("system", ContextCompaction.SummarizationSystemPrompt),
            new("user", prompt)
        };

        var builder = new StringBuilder();

        await foreach (var evt in client
            .StreamAsync(messages, [], cancellationToken)
            .ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmStreamEvent.TextDelta delta:
                    builder.Append(delta.Text);
                    break;

                case LlmStreamEvent.Completed done:
                    // A length stop means the summary is partial and must not be persisted.
                    if (done.FinishReason is "length" or "max_tokens")
                        return "";
                    break;

                case LlmStreamEvent.Failed:
                    return "";
            }
        }

        return builder.ToString();
    }
}
#endif
