#if !NETWASM
namespace Reptar.AI;

/// <summary>One entry in the <c>/</c> dropdown.</summary>
public sealed record SlashCommand(string Name, string Hint, string Description);

/// <summary>
/// Matching rules for the <c>/</c> dropdown, kept pure so they can be tested without a terminal.
/// </summary>
/// <remarks>
/// Two rules that are easy to get wrong, and both were:
/// <list type="bullet">
/// <item>The list is open only while the input is a bare command name. Once there is a space the
/// user is typing arguments, so the list closes and Enter submits.</item>
/// <item>A query with no match must not close the list. It shows "no matches" instead, because
/// otherwise a typo makes the menu vanish and the user cannot tell whether the command does not
/// exist or the search is just too narrow.</item>
/// </list>
/// </remarks>
public static class SlashCommands
{
    /// <summary>True when the input is still a bare command name worth completing.</summary>
    public static bool IsCompleting(string input) =>
        input.StartsWith('/') && !input.Contains(' ');

    /// <summary>
    /// The command name being typed, without the leading slash. Empty for a bare <c>/</c>.
    /// </summary>
    public static string Query(string input) =>
        IsCompleting(input) ? input[1..] : "";

    /// <summary>
    /// Filters and ranks commands for the current input.
    /// </summary>
    /// <remarks>
    /// Matching is on the <b>name only</b>, which is what pi does. Searching descriptions too
    /// looks helpful and is actively harmful: a single letter matches almost every description
    /// ("s" matches <c>/model</c> because it says "Browse and <b>s</b>elect a model"), so the list
    /// never narrows and the command you are typing drops out of the visible rows.
    ///
    /// An empty result means no command name matches. Callers keep the list open and say so,
    /// because a menu that vanishes on a typo cannot be distinguished from a broken menu.
    /// </remarks>
    public static List<SlashCommand> Match(string input, IReadOnlyList<SlashCommand> commands)
    {
        if (!IsCompleting(input))
            return [];

        var query = Query(input);

        if (query.Length == 0)
            return [.. commands];

        return Fuzzy.Rank(query, commands, command => [command.Name]);
    }
}
#endif
