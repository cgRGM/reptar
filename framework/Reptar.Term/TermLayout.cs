#if !NETWASM
namespace Reptar.Term;

/// <summary>
/// Vertical geometry for the bottom of the screen.
/// </summary>
/// <remarks>
/// This exists because the layout kept being wrong in ways only a screenshot revealed: a list
/// drawn over the footer, an input covering the list, a selector painted across the transcript.
/// The rules are simple but they interact, and eyeballing them is not a reliable check.
///
/// The screen, bottom-up:
/// <code>
///   height-1   footer: stats
///   height-2   footer: working directory
///   height-3   composer, or the selector's last row
///   ...        the dropdown band, when open
///   ...        working indicator
///   ...        transcript
/// </code>
/// Exactly one of a selector and a composer is visible, and the dropdown only exists with a
/// composer, so the two never fight for the same rows.
/// </remarks>
public readonly record struct TermLayout(int Height, int DropdownRows = 0, int SelectorRows = 0)
{
    /// <summary>Rows the dropdown band needs, including its rule and counter.</summary>
    public int DropdownRows { get; } = Math.Max(0, DropdownRows);

    /// <summary>Rows an open selector needs.</summary>
    public int SelectorRows { get; } = Math.Max(0, SelectorRows);

    /// <summary>True when a selector owns the composer's slot.</summary>
    public bool SelectorOpen => SelectorRows > 0;

    /// <summary>Row of the token/cost stats line. Always the last row.</summary>
    public int FooterStats => Height - 1;

    /// <summary>Row of the working-directory line.</summary>
    public int FooterLocation => Height - 2;

    /// <summary>
    /// First row above the footer. A selector's block ends here; the dropdown band ends here.
    /// </summary>
    public int ContentBottom => FooterLocation - 1;

    /// <summary>Row of the composer. Zero dropdown rows means it sits directly above the footer.</summary>
    public int Composer => SelectorOpen ? -1 : ContentBottom - DropdownRows;

    /// <summary>Row of the working indicator, one above the composer.</summary>
    public int Working => Composer < 0 ? -1 : Composer - 1;

    /// <summary>First row of the dropdown band, directly under the composer.</summary>
    public int DropdownTop => Composer < 0 ? -1 : Composer + 1;

    /// <summary>Last row a selector may draw into.</summary>
    public int SelectorBottom => ContentBottom;

    /// <summary>First row a selector draws into.</summary>
    public int SelectorTop => Math.Max(2, ContentBottom - SelectorRows + 1);

    /// <summary>
    /// Last row the transcript may use. It stops above whatever owns the bottom of the screen.
    /// </summary>
    public int TranscriptBottom => SelectorOpen
        ? Math.Max(2, SelectorTop - 1)
        : Math.Max(2, Working - 1);

    /// <summary>
    /// True when every band fits on the screen. False means the terminal is too short and
    /// something has to be dropped rather than drawn over something else.
    /// </summary>
    public bool Fits => SelectorOpen
        ? SelectorRows <= ContentBottom - 2
        : DropdownRows <= ContentBottom - 4;
}
#endif
