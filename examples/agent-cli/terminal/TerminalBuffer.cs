#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>
/// A single cell in the 2D terminal screen buffer.
/// </summary>
public readonly record struct TerminalCell(
    char Char = ' ',
    string Fg = "",
    string Bg = "",
    bool Bold = false,
    bool Dim = false,
    bool Italic = false,
    bool Underline = false)
{
    public static readonly TerminalCell Empty = new(' ');

    public bool HasStyle => Bold || Dim || Italic || Underline || !string.IsNullOrEmpty(Fg) || !string.IsNullOrEmpty(Bg);

    public bool SameStyle(in TerminalCell other) =>
        Fg == other.Fg && Bg == other.Bg && Bold == other.Bold && Dim == other.Dim && Italic == other.Italic && Underline == other.Underline;
}

/// <summary>
/// 2D virtual screen buffer with double-buffering and minimal ANSI diffing.
/// Eliminates cursor jumping, flickering, and terminal tearing across Ghostty,
/// iTerm2, Alacritty, VS Code, and standard xterm emulators.
/// </summary>
public sealed class TerminalBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    private TerminalCell[] _cells;

    public TerminalBuffer(int width, int height)
    {
        Width = Math.Max(20, width);
        Height = Math.Max(8, height);
        _cells = new TerminalCell[Width * Height];
        Clear();
    }

    public void Resize(int width, int height)
    {
        var newWidth = Math.Max(20, width);
        var newHeight = Math.Max(8, height);
        if (newWidth == Width && newHeight == Height)
            return;

        Width = newWidth;
        Height = newHeight;
        _cells = new TerminalCell[Width * Height];
        Clear();
    }

    public void Clear()
    {
        Array.Fill(_cells, TerminalCell.Empty);
    }

    public void SetCell(int x, int y, in TerminalCell cell)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return;
        _cells[y * Width + x] = cell;
    }

    public TerminalCell GetCell(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return TerminalCell.Empty;
        return _cells[y * Width + x];
    }

    public void Print(
        int x, int y,
        string text,
        string fg = "",
        string bg = "",
        bool bold = false,
        bool dim = false,
        bool italic = false,
        bool underline = false,
        int maxWidth = -1)
    {
        if (y < 0 || y >= Height || x >= Width)
            return;

        var available = maxWidth > 0 ? Math.Min(maxWidth, Width - x) : Width - x;
        if (available <= 0)
            return;

        var col = x;
        for (var i = 0; i < text.Length && col < x + available; i++)
        {
            var ch = text[i];
            if (ch == '\t')
            {
                var tabStop = ((col + 4) / 4) * 4;
                while (col < tabStop && col < x + available)
                {
                    SetCell(col++, y, new TerminalCell(' ', fg, bg, bold, dim, italic, underline));
                }
                continue;
            }

            if (char.IsControl(ch))
                continue;

            SetCell(col++, y, new TerminalCell(ch, fg, bg, bold, dim, italic, underline));
        }
    }

    public void PrintCentered(
        int y,
        string text,
        string fg = "",
        string bg = "",
        bool bold = false,
        bool dim = false)
    {
        var len = Math.Min(text.Length, Width);
        var x = Math.Max(0, (Width - len) / 2);
        Print(x, y, text, fg, bg, bold, dim, maxWidth: Width - x);
    }

    public void FillRect(int x, int y, int w, int h, char ch = ' ', string fg = "", string bg = "")
    {
        var cell = new TerminalCell(ch, fg, bg);
        for (var row = y; row < y + h && row < Height; row++)
        {
            if (row < 0) continue;
            for (var col = x; col < x + w && col < Width; col++)
            {
                if (col < 0) continue;
                SetCell(col, row, cell);
            }
        }
    }

    /// <summary>
    /// Applies a faint shadow effect by dimming existing cells in the given rectangle.
    /// Used for floating modal dialogs and dropdown menus.
    /// </summary>
    public void DrawShadow(int x, int y, int w, int h)
    {
        for (var row = y; row < y + h && row < Height; row++)
        {
            if (row < 0) continue;
            for (var col = x; col < x + w && col < Width; col++)
            {
                if (col < 0) continue;
                var current = GetCell(col, row);
                SetCell(col, row, current with { Dim = true, Fg = "\x1b[90m" });
            }
        }
    }

    /// <summary>
    /// Draws a border box with rounded corners and optional title.
    /// </summary>
    public void DrawBox(
        int x, int y, int w, int h,
        string? title = null,
        string borderFg = "",
        string titleFg = "",
        bool rounded = true)
    {
        if (w < 2 || h < 2)
            return;

        char tl = rounded ? '╭' : '┌';
        char tr = rounded ? '╮' : '┐';
        char bl = rounded ? '╰' : '└';
        char br = rounded ? '╯' : '┘';
        char horiz = '─';
        char vert = '│';

        // Top line
        SetCell(x, y, new TerminalCell(tl, borderFg));
        for (var col = x + 1; col < x + w - 1 && col < Width; col++)
            SetCell(col, y, new TerminalCell(horiz, borderFg));
        SetCell(x + w - 1, y, new TerminalCell(tr, borderFg));

        // Sides
        for (var row = y + 1; row < y + h - 1 && row < Height; row++)
        {
            SetCell(x, row, new TerminalCell(vert, borderFg));
            SetCell(x + w - 1, row, new TerminalCell(vert, borderFg));
        }

        // Bottom line
        SetCell(x, y + h - 1, new TerminalCell(bl, borderFg));
        for (var col = x + 1; col < x + w - 1 && col < Width; col++)
            SetCell(col, y + h - 1, new TerminalCell(horiz, borderFg));
        SetCell(x + w - 1, y + h - 1, new TerminalCell(br, borderFg));

        // Title
        if (!string.IsNullOrEmpty(title) && w > 4)
        {
            var maxTitleLen = w - 4;
            var displayTitle = title.Length > maxTitleLen ? title[..(maxTitleLen - 1)] + "…" : title;
            Print(x + 2, y, $" {displayTitle} ", fg: string.IsNullOrEmpty(titleFg) ? borderFg : titleFg, bold: true);
        }
    }

    /// <summary>
    /// Diffs the current buffer against a previous buffer and returns the minimal
    /// ANSI string needed to update the terminal. If previous is null, emits a full redraw.
    /// </summary>
    public string RenderDiff(TerminalBuffer? previous, int cursorX = -1, int cursorY = -1)
    {
        var sb = new StringBuilder(Width * Height * 2);

        // Hide cursor during repaint to avoid flicker
        sb.Append("\x1b[?25l");

        var curRow = -1;
        var curCol = -1;
        var activeFg = "";
        var activeBg = "";
        var activeBold = false;
        var activeDim = false;
        var activeItalic = false;
        var activeUnderline = false;

        void ApplyStyle(in TerminalCell cell)
        {
            if (activeBold != cell.Bold || activeDim != cell.Dim || activeItalic != cell.Italic || activeUnderline != cell.Underline ||
                activeFg != cell.Fg || activeBg != cell.Bg)
            {
                sb.Append("\x1b[0m"); // Reset
                if (cell.Bold) sb.Append("\x1b[1m");
                if (cell.Dim) sb.Append("\x1b[2m");
                if (cell.Italic) sb.Append("\x1b[3m");
                if (cell.Underline) sb.Append("\x1b[4m");
                if (!string.IsNullOrEmpty(cell.Fg)) sb.Append(cell.Fg);
                if (!string.IsNullOrEmpty(cell.Bg)) sb.Append(cell.Bg);

                activeBold = cell.Bold;
                activeDim = cell.Dim;
                activeItalic = cell.Italic;
                activeUnderline = cell.Underline;
                activeFg = cell.Fg;
                activeBg = cell.Bg;
            }
        }

        var fullRedraw = previous is null || previous.Width != Width || previous.Height != Height;

        for (var row = 0; row < Height; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                var cur = GetCell(col, row);

                if (!fullRedraw && previous is not null)
                {
                    var prev = previous.GetCell(col, row);
                    if (cur == prev)
                        continue;
                }

                // Move cursor if not adjacent
                if (curRow != row || curCol != col)
                {
                    sb.Append($"\x1b[{row + 1};{col + 1}H");
                    curRow = row;
                    curCol = col;
                }

                ApplyStyle(cur);
                sb.Append(cur.Char);
                curCol++;
            }
        }

        // Reset style at the end
        sb.Append("\x1b[0m");

        // Restore cursor position if requested
        if (cursorX >= 0 && cursorY >= 0 && cursorX < Width && cursorY < Height)
        {
            sb.Append($"\x1b[{cursorY + 1};{cursorX + 1}H\x1b[?25h");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates a clone of this buffer for double buffering.
    /// </summary>
    public TerminalBuffer Clone()
    {
        var copy = new TerminalBuffer(Width, Height);
        Array.Copy(_cells, copy._cells, _cells.Length);
        return copy;
    }
}
#endif
