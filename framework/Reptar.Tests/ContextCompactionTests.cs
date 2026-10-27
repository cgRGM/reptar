using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="ContextCompaction"/> and <see cref="ContextCompactor"/>:
/// token estimation, cut-point safety, threshold logic, and projection rewriting.
/// </summary>
public sealed class ContextCompactionTests
{
    private static ChatMessage User(string text) => new("user", text);

    private static ChatMessage Assistant(string text, AgentUsage? usage = null) =>
        new("assistant", text, Usage: usage);

    private static ChatMessage ToolResult(string toolCallId, string content) =>
        new("tool", content, Name: "read_file", ToolCallId: toolCallId);

    private static ChatMessage AssistantWithCall(string callId) =>
        new("assistant", "", ToolCalls: [new AiToolCall(callId, "read_file", """{"path":"a"}""")]);

    // ------------------------------------------------------------------
    // Token estimation
    // ------------------------------------------------------------------

    [Fact]
    public void estimate_context_prefers_provider_reported_usage_over_heuristics()
    {
        var messages = new List<ChatMessage>
        {
            User("hello"),
            Assistant("hi there", new AgentUsage(InputTokens: 120_000, OutputTokens: 500)),
            User("and now?"),
        };

        var estimate = ContextCompaction.EstimateContextTokens(messages);

        // 120_500 from usage, plus the trailing user message only.
        Assert.Equal(120_500 + ContextCompaction.EstimateTokens(messages[2]), estimate);
        Assert.True(estimate > 100_000);
    }

    [Fact]
    public void estimate_context_ignores_empty_usage_records()
    {
        var messages = new List<ChatMessage>
        {
            User("hello"),
            Assistant("", new AgentUsage(0, 0)),
        };

        var estimate = ContextCompaction.EstimateContextTokens(messages);
        Assert.Equal(
            ContextCompaction.EstimateTokens(messages[0]) + ContextCompaction.EstimateTokens(messages[1]),
            estimate);
    }

    [Fact]
    public void estimate_tokens_accounts_for_tool_call_arguments()
    {
        var withoutCalls = Assistant("x");
        var withCalls = AssistantWithCall("c1");

        Assert.True(ContextCompaction.EstimateTokens(withCalls) > ContextCompaction.EstimateTokens(withoutCalls));
    }

    // ------------------------------------------------------------------
    // Threshold
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(190_000, 200_000, true)]   // above window - reserve
    [InlineData(180_000, 200_000, false)]  // comfortably inside
    [InlineData(200_000, 200_000, true)]   // at the limit
    public void should_compact_uses_window_minus_reserve(int contextTokens, int contextWindow, bool expected)
    {
        var settings = new CompactionSettings { ReserveTokens = 16_384 };
        Assert.Equal(expected, ContextCompaction.ShouldCompact(contextTokens, contextWindow, settings));
    }

    [Fact]
    public void should_compact_is_false_when_disabled_or_window_unknown()
    {
        Assert.False(ContextCompaction.ShouldCompact(999_999, 200_000, CompactionSettings.Disabled));
        Assert.False(ContextCompaction.ShouldCompact(999_999, 0, CompactionSettings.Default));
    }

    // ------------------------------------------------------------------
    // Cut points
    // ------------------------------------------------------------------

    [Fact]
    public void cut_point_never_lands_on_a_tool_result()
    {
        var messages = new List<ChatMessage>();
        for (var turn = 0; turn < 8; turn++)
        {
            messages.Add(User($"question {turn} " + new string('x', 400)));
            messages.Add(AssistantWithCall($"call_{turn}"));
            messages.Add(ToolResult($"call_{turn}", new string('y', 400)));
            messages.Add(Assistant($"answer {turn}"));
        }

        // Walk every budget so the cut lands in many different places.
        foreach (var budget in new[] { 1, 50, 200, 500, 1_000, 5_000 })
        {
            var (firstKept, _, _) = ContextCompaction.FindCutPoint(messages, budget);
            Assert.InRange(firstKept, 0, messages.Count - 1);
            Assert.NotEqual("tool", messages[firstKept].Role);
        }
    }

    [Fact]
    public void cut_point_keeps_the_recent_tail_when_the_budget_is_small()
    {
        var messages = new List<ChatMessage>();
        for (var turn = 0; turn < 10; turn++)
            messages.Add(User($"message {turn} " + new string('a', 2_000)));

        var (firstKept, _, _) = ContextCompaction.FindCutPoint(messages, keepRecentTokens: 1_000);

        Assert.True(firstKept > 0, "a small budget should drop the early messages");
        Assert.True(
            ContextCompaction.EstimateContextTokens(messages.Skip(firstKept).ToList()) < 10_000,
            "the retained tail should be small");
    }

    [Fact]
    public void cut_point_reports_a_split_turn_with_its_starting_user_message()
    {
        // One huge user message followed by many assistant turns: the budget forces a cut
        // inside that single user span, so the turn is split.
        var messages = new List<ChatMessage> { User(new string('z', 40_000)) };
        for (var i = 0; i < 6; i++)
            messages.Add(Assistant($"step {i} " + new string('b', 400)));

        var (firstKept, turnStart, isSplit) = ContextCompaction.FindCutPoint(messages, 500);

        Assert.True(firstKept > 0);
        if (isSplit)
        {
            Assert.Equal(0, turnStart);
            Assert.Equal("user", messages[turnStart].Role);
        }
    }

    // ------------------------------------------------------------------
    // Projection rewriting
    // ------------------------------------------------------------------

    [Fact]
    public void apply_replaces_the_prefix_with_a_summary_and_keeps_the_system_prompt()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "you are a coding agent"),
            User("old question"),
            Assistant("old answer"),
            User("recent question"),
            Assistant("recent answer"),
        };

        var result = new CompactionResult(
            "## Goal\nShip it.",
            FirstKeptMessageIndex: 3,
            TokensBefore: 1_000,
            EstimatedTokensAfter: 100,
            Usage: null,
            CompactionReason.Manual);

        var projection = ContextCompaction.Apply(messages, result);

        Assert.Equal("system", projection[0].Role);
        Assert.Equal("you are a coding agent", projection[0].Content);
        Assert.Contains("compacted into the following summary", projection[1].Content);
        Assert.Contains("Ship it.", projection[1].Content);
        Assert.Equal("recent question", projection[2].Content);
        Assert.Equal("recent answer", projection[3].Content);
        Assert.DoesNotContain(projection, m => m.Content == "old question");
    }

    // ------------------------------------------------------------------
    // End-to-end
    // ------------------------------------------------------------------

    [Fact]
    public async Task compactor_summarizes_and_returns_a_shorter_projection()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 12; i++)
        {
            history.Add(User($"task {i} " + new string('q', 900)));
            history.Add(Assistant($"done {i} " + new string('a', 900)));
        }

        var client = new ScriptedEventLlmClient(
            [new LlmStreamEvent.TextDelta("## Goal\nFinish the port."), new LlmStreamEvent.Completed("stop")]);

        var outcome = await ContextCompactor.CompactAsync(
            history,
            client,
            new CompactionSettings { KeepRecentTokens = 1_000 },
            CompactionReason.Threshold);

        Assert.NotNull(outcome);
        var (result, projection) = outcome!.Value;

        Assert.Contains("Finish the port.", result.Summary);
        Assert.True(result.FirstKeptMessageIndex > 0);
        Assert.True(projection.Count < history.Count, "the projection must be shorter than the history");
        Assert.Contains(projection, m => m.Content.Contains("compacted into the following summary"));
    }

    [Fact]
    public async Task compactor_refuses_a_truncated_summary()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 12; i++)
        {
            history.Add(User($"task {i} " + new string('q', 900)));
            history.Add(Assistant($"done {i} " + new string('a', 900)));
        }

        var client = new ScriptedEventLlmClient(
            [new LlmStreamEvent.TextDelta("## Goal\npartial"), new LlmStreamEvent.Completed("length")]);

        var outcome = await ContextCompactor.CompactAsync(
            history,
            client,
            new CompactionSettings { KeepRecentTokens = 1_000 },
            CompactionReason.Overflow);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task compactor_is_a_no_op_when_disabled()
    {
        var client = new ScriptedEventLlmClient(
            [new LlmStreamEvent.TextDelta("ignored"), new LlmStreamEvent.Completed("stop")]);

        var outcome = await ContextCompactor.CompactAsync(
            [User("a"), Assistant("b")],
            client,
            CompactionSettings.Disabled,
            CompactionReason.Manual);

        Assert.Null(outcome);
    }

    [Fact]
    public void summary_prompt_switches_to_iterative_mode_when_a_previous_summary_exists()
    {
        var first = ContextCompaction.BuildSummaryPrompt([User("hi")], [], null, null);
        Assert.Contains("Create a structured context checkpoint", first);
        Assert.DoesNotContain("<previous-summary>", first);

        var update = ContextCompaction.BuildSummaryPrompt([User("hi")], [], "## Goal\nOld goal.", null);
        Assert.Contains("<previous-summary>", update);
        Assert.Contains("Old goal.", update);
        Assert.Contains("PRESERVE all existing information", update);
    }

    [Fact]
    public void summary_prompt_includes_split_turn_prefix_and_custom_instructions()
    {
        var prompt = ContextCompaction.BuildSummaryPrompt(
            [User("summarize me")],
            [Assistant("in-flight work")],
            previousSummary: null,
            customInstructions: "focus on database migrations");

        Assert.Contains("<current-turn-prefix>", prompt);
        Assert.Contains("in-flight work", prompt);
        Assert.Contains("focus on database migrations", prompt);
    }
}
