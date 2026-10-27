using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the <c>/</c> dropdown matching. The rule that matters most: a query that
/// matches nothing must not close the list.
/// </summary>
public sealed class SlashCommandsTests
{
    private static readonly List<SlashCommand> Commands =
    [
        new("/model", "", "Browse and select a model"),
        new("/connect", "", "Add a provider key"),
        new("/settings", "", "Open the settings menu"),
        new("/sessions", "", "Switch to a saved session"),
        new("/tree", "", "Browse the session tree"),
        new("/tools", "", "List mounted tools"),
        new("/compact", "[focus]", "Summarize the context"),
        new("/help", "", "Show tips and shortcuts"),
        new("/exit", "", "Quit")
    ];

    private static List<string> Names(string input) =>
        [.. SlashCommands.Match(input, Commands).Select(command => command.Name)];

    // ------------------------------------------------------------------
    // Open / closed
    // ------------------------------------------------------------------

    [Fact]
    public void a_bare_slash_lists_everything()
    {
        Assert.Equal(Commands.Count, Names("/").Count);
    }

    [Fact]
    public void a_space_closes_the_list_because_arguments_are_being_typed()
    {
        Assert.Empty(Names("/model "));
        Assert.Empty(Names("/model anthropic/claude"));
        Assert.False(SlashCommands.IsCompleting("/model "));
    }

    [Fact]
    public void input_that_does_not_start_with_a_slash_is_not_a_command()
    {
        Assert.Empty(Names("model"));
        Assert.Empty(Names(""));
    }

    // ------------------------------------------------------------------
    // Filtering
    // ------------------------------------------------------------------

    [Fact]
    public void typing_narrows_the_list()
    {
        var names = Names("/s");

        Assert.Contains("/settings", names);
        Assert.Contains("/sessions", names);

        // Descriptions are not searched: /model's says "Browse and select a model", and matching
        // that would make every single letter match half the list.
        Assert.DoesNotContain("/model", names);
    }

    [Fact]
    public void a_single_letter_narrows_to_commands_whose_name_contains_it()
    {
        var names = Names("/t");

        Assert.Contains("/tree", names);
        Assert.Contains("/tools", names);
        Assert.DoesNotContain("/model", names);
        Assert.DoesNotContain("/help", names);
    }

    [Fact]
    public void a_full_command_name_matches_itself_first()
    {
        Assert.Equal("/model", Names("/model")[0]);
    }

    [Fact]
    public void the_description_is_not_searchable()
    {
        // pi matches names only. "summarize" appears only in /compact's description, so it must
        // not find it — otherwise every letter matches half the list through some description.
        Assert.Empty(Names("/summarize"));
    }

    [Fact]
    public void the_hint_is_not_searchable_either()
    {
        Assert.Empty(Names("/focus"));
    }

    // ------------------------------------------------------------------
    // The rule that was broken
    // ------------------------------------------------------------------

    [Fact]
    public void a_query_that_matches_nothing_returns_empty_but_is_still_completing()
    {
        // The caller uses IsCompleting to decide whether the list is open. An empty match must
        // not flip that, or the menu vanishes on a typo.
        Assert.Empty(Names("/zzzz"));
        Assert.True(SlashCommands.IsCompleting("/zzzz"));
    }

    [Fact]
    public void every_single_letter_still_matches_something()
    {
        // A menu that disappears on the first keystroke is the bug being pinned here.
        foreach (var letter in "abcdefghijklmnopqrstuvwxyz")
        {
            var input = "/" + letter;
            Assert.True(SlashCommands.IsCompleting(input));
        }
    }

    [Fact]
    public void incremental_typing_never_leaves_the_list_open_with_no_query()
    {
        const string target = "/sessions";

        for (var length = 1; length <= target.Length; length++)
        {
            var partial = target[..length];
            Assert.True(SlashCommands.IsCompleting(partial));
            Assert.Equal(partial[1..], SlashCommands.Query(partial));
        }
    }
}
