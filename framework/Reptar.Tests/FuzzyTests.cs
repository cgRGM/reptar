using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="Fuzzy"/>: the ranking rules that make a command dropdown feel
/// right rather than merely functional.
/// </summary>
public sealed class FuzzyTests
{
    private sealed record Item(string Name, string Description);

    private static readonly List<Item> Commands =
    [
        new("/model", "Browse and select a model"),
        new("/connect", "Connect provider credentials"),
        new("/compact", "Summarize the conversation into a checkpoint"),
        new("/clear", "Clear the chat"),
        new("/sessions", "Switch to a saved session"),
        new("/tools", "List mounted tools"),
        new("/providers", "Show provider connection status"),
        new("/help", "Show tips and shortcuts"),
        new("/exit", "Quit")
    ];

    private static List<string> Rank(string query) =>
        [.. Fuzzy.Rank(query, Commands, c => [c.Name, c.Description]).Select(c => c.Name)];

    // ------------------------------------------------------------------
    // Matching
    // ------------------------------------------------------------------

    [Fact]
    public void an_empty_query_matches_everything_in_order()
    {
        Assert.Equal([.. Commands.Select(c => c.Name)], Rank(""));
    }

    [Fact]
    public void matching_is_case_insensitive()
    {
        Assert.Equal(["/model"], Rank("MODEL"));
    }

    [Fact]
    public void all_query_characters_must_appear_in_order()
    {
        Assert.True(Fuzzy.Score("mdl", "/model").Matches);
        Assert.False(Fuzzy.Score("dlm", "/model").Matches);
    }

    [Fact]
    public void a_query_longer_than_the_text_cannot_match()
    {
        Assert.False(Fuzzy.Score("modelling", "/model").Matches);
    }

    [Fact]
    public void non_matching_items_are_dropped()
    {
        Assert.Empty(Rank("zzzz"));
    }

    // ------------------------------------------------------------------
    // Ranking
    // ------------------------------------------------------------------

    [Fact]
    public void a_prefix_match_ranks_first()
    {
        var ranked = Rank("mod");

        Assert.Equal("/model", ranked[0]);
        Assert.DoesNotContain("/exit", ranked);
    }

    [Fact]
    public void a_word_boundary_match_beats_a_mid_word_match()
    {
        // "s" is at the start of /sessions and /sessions' description; /tools has no s at a
        // boundary before a later position.
        var ranked = Rank("sess");

        Assert.Equal("/sessions", ranked[0]);
    }

    [Fact]
    public void consecutive_characters_beat_scattered_ones()
    {
        var consecutive = Fuzzy.Score("clear", "/clear");
        var scattered = Fuzzy.Score("clear", "/c-l-e-a-r");

        Assert.True(consecutive.Matches);
        Assert.True(scattered.Matches);
        Assert.True(consecutive.Score < scattered.Score);
    }

    [Fact]
    public void an_earlier_match_beats_a_later_one()
    {
        var early = Fuzzy.Score("x", "ax");
        var late = Fuzzy.Score("x", "aaaaaaaaax");

        Assert.True(early.Matches);
        Assert.True(late.Matches);
        Assert.True(early.Score < late.Score);
    }

    [Fact]
    public void the_description_is_searchable_so_typing_a_synonym_finds_the_command()
    {
        // "summarize" appears only in /compact's description.
        var ranked = Rank("summarize");

        Assert.Equal("/compact", ranked[0]);
    }

    [Fact]
    public void a_contiguous_occurrence_beats_a_greedy_scattered_alignment()
    {
        // pi's own example: "opus" in "openrouter openrouter/anthropic/claude-opus" would take
        // o, p, u from the first word under greedy alignment.
        var scattered = Fuzzy.Score("opus", "openrouter openrouter/claude-opus");
        var plain = Fuzzy.Score("opus", "opus");

        Assert.True(scattered.Matches);
        Assert.True(plain.Score < scattered.Score);
    }

    [Fact]
    public void ties_keep_their_original_order()
    {
        var items = new[] { "aa", "ab", "ac" };
        var ranked = Fuzzy.Rank("a", items, s => [s]);

        Assert.Equal(["aa", "ab", "ac"], ranked);
    }

    [Fact]
    public void multiple_fields_use_the_best_match()
    {
        var match = Fuzzy.ScoreAny("summarize", "/compact", "Summarize the conversation");

        Assert.True(match.Matches);
    }

    [Fact]
    public void a_query_matching_no_field_fails()
    {
        Assert.False(Fuzzy.ScoreAny("zzz", "/compact", "Summarize").Matches);
    }

    [Fact]
    public void null_fields_are_ignored()
    {
        var match = Fuzzy.ScoreAny("model", "/model", null);

        Assert.True(match.Matches);
    }
}
