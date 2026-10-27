#if !NETWASM
namespace Reptar.Term;

/// <summary>
/// Pinned-footer support over a DECSTBM scroll region: the terminal scrolls
/// only inside the region while the bottom line(s) stay reserved for the
/// status bar. This is how minimal raw-ANSI TUIs pin content without a
/// full-screen alternate buffer, so chat output keeps flowing into
/// scrollback while the bar stays put.
///
/// The footer paints at an absolute position (CUP ignores scroll margins),
/// so it is redrawn cheaply after every prompt, turn, or token delta.
/// Piped/non-TTY mode is a no-op.
/// </summary>
public sealed class TerminalScreen : IDisposable
{
    private const string EnableBracketedPaste = "\x1b[?2004h";
    private const string DisableBracketedPaste = "\x1b[?2004l";

    private int _lastHeight;
    private bool _active;

    public bool Active => _active;

    /// <summary>
    /// Reserves the bottom line for the footer and enables bracketed paste.
    /// </summary>
    public void Enter()
    {
        if (!CanUseScreen())
            return;

        ApplyRegion();
        Console.Write(EnableBracketedPaste);
        _active = true;
    }

    /// <summary>
    /// Paints the footer into the reserved bottom line and returns the cursor
    /// to wherever it was. Re-applies the region if the terminal was resized.
    /// </summary>
    public void PaintFooter(string? footer)
    {
        if (!_active)
        {
            // No pinned region: print the bar inline (previous behavior).
            if (footer is { Length: > 0 })
                Console.WriteLine(Ansi.Color(Ansi.Gray, footer));
            return;
        }

        var height = SafeHeight();
        if (height != _lastHeight)
        {
            ApplyRegion();
        }

        // DECSC/DECRC restore the cursor reliably across terminals.
        Console.Write("\x1b7");
        Console.Write($"\x1b[{height};1H");
        Console.Write("\x1b[2K");

        if (footer is { Length: > 0 })
        {
            var width = SafeWidth();
            var visible = Ansi.Strip(footer);
            if (visible.Length > width - 2)
                footer = Ansi.Strip(footer)[..(width - 5)] + "…";
            Console.Write(footer);
        }

        Console.Write("\x1b8");
    }

    /// <summary>
    /// Clears the footer, disables bracketed paste, and resets the scroll
    /// region so the terminal is left exactly as we found it.
    /// </summary>
    public void Dispose()
    {
        if (!_active)
            return;

        _active = false;
        try
        {
            var height = SafeHeight();
            Console.Write("\x1b7");
            Console.Write($"\x1b[{height};1H");
            Console.Write("\x1b[2K");
            Console.Write("\x1b8");
            Console.Write("\x1b[r");
            Console.Write(DisableBracketedPaste);
        }
        catch
        {
            // Best-effort cleanup on exit.
        }
    }

    private void ApplyRegion()
    {
        var height = SafeHeight();
        _lastHeight = height;

        if (height < 8)
        {
            _active = false;
            return;
        }

        // Reserve the final line for the footer. The cursor must be inside
        // the region before DECSTBM takes effect.
        Console.Write("\x1b[H");
        Console.Write($"\x1b[1;{height - 1}r");
        Console.Write("\x1b[H");
    }

    private static bool CanUseScreen()
    {
        try
        {
            return !Console.IsInputRedirected && !Console.IsOutputRedirected && Ansi.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static int SafeHeight()
    {
        try
        {
            return Math.Max(8, Console.WindowHeight);
        }
        catch
        {
            return 40;
        }
    }

    private static int SafeWidth()
    {
        try
        {
            return Math.Max(20, Console.WindowWidth);
        }
        catch
        {
            return 100;
        }
    }
}
#endif
