using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="TermSelector"/>: fuzzy search across label/detail/status, the
/// selection rules, and the row count the layout depends on.
/// </summary>
public sealed class TermSelectorTests
{
    private static TermSelector Build(params TermSelectorItem[] items) =>
        new("Select something:", items);

    private static TermSelector Providers() => Build(
        new TermSelectorItem("Amazon Bedrock", "us.anthropic.claude", "✓ stored", true),
        new TermSelectorItem("Anthropic", "claude-sonnet-4-5", "· not configured"),
        new TermSelectorItem("OpenAI", "gpt-4o-mini", "✓ stored"),
        new TermSelectorItem("OpenRouter", "openai/gpt-4o", "· not configured"));

    [Fact]
    public void with_no_query_every_item_is_visible_in_order()
    {
        var selector = Providers();

        Assert.Equal(4, selector.Visible.Count);
        Assert.Equal("Amazon Bedrock", selector.Selected!.Label);
    }

    [Fact]
    public void typing_filters_fuzzily_on_the_label()
    {
        var selector = Providers();
        foreach (var ch in "opena")
            selector.Type(ch);

        Assert.Contains(selector.Visible, item => item.Label == "OpenAI");
    }

    [Fact]
    public void the_status_is_searchable_so_you_can_find_configured_providers()
    {
        var selector = Providers();
        foreach (var ch in "stored")
            selector.Type(ch);

        Assert.Equal(2, selector.Visible.Count);
        Assert.All(selector.Visible, item => Assert.Equal("✓ stored", item.Status));
    }

    [Fact]
    public void the_detail_is_searchable_so_you_can_find_a_model_id()
    {
        var selector = Providers();
        foreach (var ch in "sonnet")
            selector.Type(ch);

        var only = Assert.Single(selector.Visible);
        Assert.Equal("Anthropic", only.Label);
    }

    [Fact]
    public void a_query_with_no_match_leaves_an_empty_list_and_no_selection()
    {
        var selector = Providers();
        foreach (var ch in "zzzz")
            selector.Type(ch);

        Assert.Empty(selector.Visible);
        Assert.Null(selector.Selected);
    }

    [Fact]
    public void backspace_widens_the_search_again()
    {
        var selector = Providers();
        foreach (var ch in "zzz")
            selector.Type(ch);
        Assert.Empty(selector.Visible);

        selector.Backspace();
        selector.Backspace();
        selector.Backspace();

        Assert.Equal(4, selector.Visible.Count);
        Assert.False(selector.IsSearching);
    }

    [Fact]
    public void moving_the_selection_wraps_at_both_ends()
    {
        var selector = Providers();

        selector.Move(-1);
        Assert.Equal("OpenRouter", selector.Selected!.Label);

        selector.Move(1);
        Assert.Equal("Amazon Bedrock", selector.Selected!.Label);
    }

    [Fact]
    public void control_characters_are_ignored_by_the_search()
    {
        var selector = Providers();
        selector.Type('\t');
        selector.Type('\0');

        Assert.False(selector.IsSearching);
    }

    [Fact]
    public void pasted_text_is_appended_and_newlines_collapse()
    {
        var selector = Providers();
        selector.Paste("open\nai");

        Assert.Equal("open ai", selector.Query);
    }

    [Fact]
    public void the_height_accounts_for_every_optional_row()
    {
        var bare = new TermSelector("", [new TermSelectorItem("a")], maxVisible: 5)
        {
            Hint = ""
        };

        // rule + search + 1 item + counter
        Assert.Equal(4, bare.Height);

        var rich = new TermSelector("Title", [new TermSelectorItem("a", Description: "d")], maxVisible: 5)
        {
            Notice = "notice",
            Status = "status",
            Hint = "hint"
        };

        // rule + notice + title + search + 1 item + counter + detail + status + hint
        Assert.Equal(9, rich.Height);
    }

    [Fact]
    public void the_visible_count_is_capped_by_max_visible()
    {
        var items = Enumerable.Range(0, 50)
            .Select(i => new TermSelectorItem($"item {i}"))
            .ToArray();

        var selector = new TermSelector("", items, maxVisible: 5);

        // Height caps the item rows even though 50 are visible to navigation.
        Assert.Equal(50, selector.Visible.Count);
        Assert.True(selector.Height < 15);
    }

    [Fact]
    public void replacing_the_items_keeps_the_query()
    {
        var selector = Providers();
        selector.Type('a');

        selector.SetItems([new TermSelectorItem("alpha"), new TermSelectorItem("beta")]);

        Assert.Equal("a", selector.Query);
        Assert.Contains(selector.Visible, item => item.Label == "alpha");
    }

    [Fact]
    public void the_current_choice_is_marked()
    {
        var selector = Providers();

        Assert.True(selector.Visible.Single(item => item.Label == "Amazon Bedrock").IsCurrent);
        Assert.False(selector.Visible.Single(item => item.Label == "Anthropic").IsCurrent);
    }
}
