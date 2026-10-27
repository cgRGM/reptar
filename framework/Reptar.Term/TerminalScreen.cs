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

    /// <summary>
    /// SGR mouse reporting: press, release, and wheel. Motion (1002/1003) is deliberately left
    /// off — it floods the input queue with a report per cell the pointer crosses, and nothing
    /// here needs hover.
    /// </summary>
    private const string EnableMouse = "\x1b[?1000h\x1b[?1006h";

    private const string DisableMouse = "\x1b[?1006l\x1b[?1000l";

    private int _lastHeight;
    private bool _active;
    private bool _altScreen;
    private bool _mouseEnabled;

    public bool Active => _active;

    /// <summary>True while the alternate screen buffer is in use.</summary>
    public bool InAltScreen => _altScreen;

    /// <summary>
    /// Takes over the full screen: alternate buffer, hidden cursor, bracketed paste, and a clean
    /// slate. This is the mode a full-frame TUI wants, as opposed to <see cref="Enter"/>'s pinned
    /// footer over a scroll region.
    /// </summary>
    /// <remarks>
    /// Owning this here rather than in each application means the package can guarantee the
    /// terminal is restored: one <c>Dispose</c> covers every mode, and no app has to remember the
    /// escape sequences.
    /// </remarks>
    /// <param name="mouse">Enable click and wheel reporting.</param>
    public void EnterAltScreen(bool mouse = true)
    {
        if (!CanUseScreen())
            return;

        Console.Write("\x1b[?1049h");   // alternate buffer
        Console.Write("\x1b[?25l");     // hide cursor
        Console.Write(EnableBracketedPaste);
        if (mouse)
            Console.Write(EnableMouse);
        Console.Write("\x1b[H\x1b[2J");  // home + clear

        _mouseEnabled = mouse;
        _altScreen = true;
        _active = true;
    }

    /// <summary>
    /// Writes a frame to the alternate screen, wrapped so the terminal presents it atomically.
    /// </summary>
    public void WriteFrame(string frame)
    {
        if (!_altScreen)
        {
            Console.Write(frame);
            return;
        }

        Console.Write(Ansi.Frame(frame));
    }

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

        if (_altScreen)
        {
            _altScreen = false;
            try
            {
                if (_mouseEnabled)
                {
                    Console.Write(DisableMouse);
                    _mouseEnabled = false;
                }

                Console.Write("\x1b[?25h");       // show cursor
                Console.Write(DisableBracketedPaste);
                Console.Write("\x1b[?1049l");     // leave alternate buffer
            }
            catch
            {
                // Best-effort cleanup on exit.
            }

            return;
        }

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
