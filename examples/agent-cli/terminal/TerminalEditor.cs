#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>
/// A zero-dependency line editor for the agent prompt, modeled on pi's editor:
/// a "/"-prefix command dropdown with type-to-filter, "@file" suggestions from
/// the workspace, "!command" shell passthrough, Ctrl+P command palette, and
/// an optional status bar. Every widget erases exactly what it painted.
/// Falls back to plain ReadLine when stdin is piped.
/// </summary>
public sealed class TerminalEditor
{
    public sealed record Suggestion(string Insert, string Label, string? Detail = null);

    /// <summary>Supplies command suggestions for a slash prefix (without the leading '/').</summary>
    public Func<string, List<Suggestion>>? CommandSuggestions { get; set; }

    /// <summary>Supplies file suggestions for an "@" prefix (without the leading '@').</summary>
    public Func<string, List<Suggestion>>? FileSuggestions { get; set; }

    /// <summary>Renders the status bar line shown above the prompt. May return null.</summary>
    public Func<string?>? StatusBar { get; set; }

    /// <summary>
    /// When set, the status bar is painted through the pinned-footer screen
    /// (DECSTBM scroll region) instead of inline, so the bar stays put while
    /// chat output scrolls above it.
    /// </summary>
    public Action<string?>? PaintFooter { get; set; }

    /// <summary>In-memory input history (most recent last).</summary>
    public List<string> History { get; } = [];

    /// <summary>
    /// Optional file for persisted history (bash-HISTFILE style: one entry
    /// per line, last 500 kept). Lines starting with a space are never
    /// persisted — the standard privacy escape hatch.
    /// </summary>
    public string? HistoryPath { get; set; }

    /// <summary>
    /// The most recent clipboard image received via Ctrl+V (OSC 52), if the
    /// terminal supports clipboard reads and the clipboard held an image.
    /// Cleared after each submission.
    /// </summary>
    public AgentImage? ClipboardImage { get; private set; }

    // Raw bytes read during a paste window that weren't part of the OSC 52
    // response (simultaneous typing) — replayed through the key loop so no
    // input is ever lost. .NET's Console.ReadKey parses (and discards)
    // ESC-]-prefixed sequences internally, so the response is captured with
    // a direct stdin read while no ReadKey call is in flight.

    /// <summary>
    /// Sniffs image magic bytes and returns the MIME type, or null when the
    /// payload is not a recognized image format.
    /// </summary>
    public static string? SniffImageMime(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";

        if (data.Length >= 3 &&
            data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.Length >= 6 &&
            data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
            return "image/gif";

        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return "image/webp";

        return null;
    }

    /// <summary>
    /// Loads persisted history. Call once at startup; also trims the file
    /// back to the cap.
    /// </summary>
    public void LoadHistory()
    {
        if (HistoryPath is null || !File.Exists(HistoryPath))
            return;

        try
        {
            var lines = File.ReadAllLines(HistoryPath)
                .Where(line => line.Length > 0)
                .ToList();

            if (lines.Count > MaxHistory)
                lines = lines[^MaxHistory..];

            History.Clear();
            History.AddRange(lines);

            if (lines.Count < MaxHistory)
                return;

            var directory = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(HistoryPath, lines);
        }
        catch (IOException)
        {
            // Unreadable history file: start with in-memory only.
        }
    }

    private const int MaxHistory = 500;

    public enum DropdownMode
    {
        None,
        Commands,
        Files,
        Palette
    }

    /// <summary>
    /// Reads a line interactively. Returns null on EOF/Ctrl+D.
    /// The returned line still contains any "/" "@" "!" sigils; the caller
    /// interprets them.
    /// </summary>
    public string? ReadLine(string prompt = "▸ ")
    {
        if (IsPiped)
        {
            var bar = StatusBar?.Invoke();
            if (bar is { Length: > 0 })
                Console.WriteLine(Ansi.Color(Ansi.Gray, "  " + bar));
            Console.Write(Ansi.Color(Ansi.Cyan, prompt));
            var piped = Console.In.ReadLine();
            if (piped is { Length: > 0 })
                AddHistory(piped);
            return piped;
        }

        var buffer = new StringBuilder();
        var cursor = 0;
        var historyIndex = History.Count;
        var mode = DropdownMode.None;
        var items = new List<Suggestion>();
        var highlight = -1;
        var paletteFilter = new StringBuilder();

        var lastRows = 0;

        void Paint()
        {
            Erase(lastRows);

            var rows = 0;

            var bar = StatusBar?.Invoke();
            if (PaintFooter is { } pinned)
            {
                // The pinned footer lives outside our erasable frame.
                pinned(bar);
            }
            else if (bar is { Length: > 0 })
            {
                Console.WriteLine(Ansi.Color(Ansi.Gray, "  " + bar));
                rows++;
            }

            Console.Write(Ansi.Color(Ansi.Cyan, prompt));
            WriteBuffer(buffer, cursor);
            Console.WriteLine();
            rows++;

            if (items.Count > 0)
            {
                var page = Math.Min(items.Count, 8);
                var window = Math.Max(0, Math.Min(highlight < 0 ? 0 : highlight - page / 2, items.Count - page));

                for (var i = window; i < window + page; i++)
                {
                    var selected = i == highlight;
                    var pointer = selected ? Ansi.Color(Ansi.Cyan, "❯ ") : "  ";
                    var label = selected ? Ansi.Color(Ansi.Bold, items[i].Label) : items[i].Label;
                    var detail = items[i].Detail is { Length: > 0 } d
                        ? "  " + Ansi.Color(Ansi.Gray, d!)
                        : "";
                    Console.WriteLine("  " + pointer + label + detail);
                    rows++;
                }

                if (items.Count > page)
                {
                    Console.WriteLine(Ansi.Color(Ansi.Gray,
                        $"  ↑ {window}–{window + page - 1} of {items.Count} ↓"));
                    rows++;
                }

                Console.WriteLine(Ansi.Color(Ansi.Gray,
                    "  ↑↓ select · type to filter · tab/enter accept · esc dismiss"));
                rows++;
            }

            Console.Write("\x1b[1F"); // park cursor back on the input line
            lastRows = rows;
        }

        void Refresh()
        {
            var text = buffer.ToString();
            mode = DetectMode(text, cursor, paletteFilter.ToString(), mode == DropdownMode.Palette);
            items = mode switch
            {
                DropdownMode.Commands => CommandSuggestions?.Invoke(SlashPrefix(text)) ?? [],
                DropdownMode.Palette => CommandSuggestions?.Invoke(paletteFilter.ToString()) ?? [],
                DropdownMode.Files => FileSuggestions?.Invoke(FilePrefix(text, cursor)) ?? [],
                _ => []
            };

            if (items.Count == 0)
            {
                highlight = -1;
            }
            else
            {
                // Enter/Tab with an open dropdown accepts the top suggestion
                // (first-match behavior shared by pi and Claude Code).
                if (highlight < 0)
                    highlight = 0;
                if (highlight >= items.Count)
                    highlight = items.Count - 1;
            }

            Paint();
        }

        Paint();

        TermKey? ReadNext()
        {
            if (!TerminalInput.Active)
            {
                // First interactive read: take over the terminal. .NET's
                // ReadKey keeps an internal CSI parser that swallows arrow
                // keys and OSC sequences, so this layer owns stdin itself.
                TerminalInput.Activate();
            }

            return TerminalInput.ReadKey();
        }

        while (true)
        {
            var key = ReadNext();
            if (key is null)
            {
                Console.WriteLine();
                return null;
            }

            var termKey = key.Value;

            switch (termKey.Key)
            {
                case ConsoleKey.Enter:
                    // File suggestions accept into the buffer (pi/Claude Code
                    // behavior); a second Enter submits the completed line.
                    if (mode == DropdownMode.Files && highlight >= 0 && items.Count > 0)
                    {
                        AcceptSuggestion(items[highlight], ref buffer, ref cursor, ref paletteFilter, mode);
                        highlight = 0;
                        Refresh();
                        break;
                    }

                    Erase(lastRows);
                    var line = mode is DropdownMode.Commands or DropdownMode.Palette && highlight >= 0
                        ? items[highlight].Insert
                        : buffer.ToString();
                    AddHistory(line);
                    Console.WriteLine(Ansi.Color(Ansi.Cyan, prompt) + line);
                    return line;

                case ConsoleKey.Tab:
                    if (items.Count > 0)
                    {
                        AcceptSuggestion(items[Math.Max(highlight, 0)], ref buffer, ref cursor,
                            ref paletteFilter, mode);
                        Refresh();
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (items.Count > 0)
                    {
                        highlight = highlight <= 0 ? items.Count - 1 : highlight - 1;
                        Paint();
                    }
                    else if (History.Count > 0)
                    {
                        historyIndex = Math.Max(0, historyIndex - 1);
                        SetBuffer(History[Math.Min(historyIndex, History.Count - 1)], ref buffer, ref cursor);
                        Refresh();
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (items.Count > 0)
                    {
                        highlight = highlight >= items.Count - 1 ? 0 : highlight + 1;
                        Paint();
                    }
                    else if (History.Count > 0 && historyIndex < History.Count)
                    {
                        historyIndex = Math.Min(History.Count, historyIndex + 1);
                        var restored = historyIndex == History.Count ? "" : History[historyIndex];
                        SetBuffer(restored, ref buffer, ref cursor);
                        Refresh();
                    }
                    break;

                case ConsoleKey.D when termKey.Ctrl:
                    // Ctrl+D on an empty buffer is EOF; otherwise ignored.
                    if (buffer.Length == 0)
                    {
                        Erase(lastRows);
                        Console.WriteLine();
                        return null;
                    }
                    break;

                case ConsoleKey.Escape:
                    if (items.Count > 0 || mode == DropdownMode.Palette)
                    {
                        mode = DropdownMode.None;
                        items = [];
                        highlight = -1;
                        if (mode == DropdownMode.None && buffer.Length == 0)
                            paletteFilter.Clear();
                        Paint();
                    }
                    else if (buffer.Length > 0)
                    {
                        buffer.Clear();
                        cursor = 0;
                        Paint();
                    }
                    break;

                case ConsoleKey.P when termKey.Ctrl:
                    // Ctrl+P: command palette from anywhere
                    mode = mode == DropdownMode.Palette ? DropdownMode.None : DropdownMode.Palette;
                    paletteFilter.Clear();
                    highlight = -1;
                    Refresh();
                    break;

                case ConsoleKey.V when termKey.Ctrl:
                    // Ctrl+V: ask the terminal for its clipboard (OSC 52 read).
                    // The response arrives on stdin and the TerminalInput parser
                    // turns it into a ClipboardImage key event.
                    Console.Write("\x1b]52;c;?\x07");
                    Console.Out.Flush();
                    break;

                case ConsoleKey.Backspace:
                    if (mode == DropdownMode.Palette && paletteFilter.Length > 0)
                    {
                        paletteFilter.Length--;
                        Refresh();
                    }
                    else if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        Refresh();
                    }
                    else
                    {
                        Paint();
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        Refresh();
                    }
                    else
                    {
                        Paint();
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (mode != DropdownMode.Palette)
                    {
                        cursor = Math.Max(0, cursor - 1);
                        Paint();
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (mode != DropdownMode.Palette)
                    {
                        cursor = Math.Min(buffer.Length, cursor + 1);
                        Paint();
                    }
                    break;

                case ConsoleKey.Home:
                    if (mode != DropdownMode.Palette)
                    {
                        cursor = 0;
                        Paint();
                    }
                    break;

                case ConsoleKey.End:
                    if (mode != DropdownMode.Palette)
                    {
                        cursor = buffer.Length;
                        Paint();
                    }
                    break;

                default:
                    if (termKey.PastedText is { Length: > 0 })
                    {
                        buffer.Insert(cursor, termKey.PastedText);
                        cursor += termKey.PastedText.Length;
                        Refresh();
                        break;
                    }

                    if (termKey.ClipboardImage is not null)
                    {
                        ClipboardImage = termKey.ClipboardImage;
                        buffer.Insert(cursor, "[clipboard image] ");
                        cursor += "[clipboard image] ".Length;
                        Refresh();
                        break;
                    }

                    if (termKey.Ch is '\0' || char.IsControl(termKey.Ch))
                        break;

                    if (termKey.Ctrl)
                        break; // unhandled control combos

                    if (mode == DropdownMode.Palette)
                    {
                        paletteFilter.Append(char.ToLowerInvariant(termKey.Ch));
                        Refresh();
                    }
                    else
                    {
                        buffer.Insert(cursor, termKey.Ch);
                        cursor++;
                        Refresh();
                    }
                    break;
            }
        }
    }

    public void ClearClipboardImage() => ClipboardImage = null;

    public void AddHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Exact repeats move to the most-recent position instead of piling up.
        History.Remove(line);
        History.Add(line);
        if (History.Count > MaxHistory)
            History.RemoveAt(0);

        if (HistoryPath is null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Rewrite from memory: leading-space entries stay off disk (the
            // privacy escape hatch) while still being recallable this session.
            File.WriteAllLines(HistoryPath,
                History.Where(entry => !entry.StartsWith(' ')));
        }
        catch (IOException)
        {
            // Read-only home: history just doesn't persist.
        }
    }

    private static void SetBuffer(string text, ref StringBuilder buffer, ref int cursor)
    {
        buffer.Clear();
        buffer.Append(text);
        cursor = buffer.Length;
    }

    public static void AcceptSuggestion(
        Suggestion item,
        ref StringBuilder buffer,
        ref int cursor,
        ref StringBuilder paletteFilter,
        DropdownMode mode)
    {
        switch (mode)
        {
            case DropdownMode.Palette:
                SetBuffer(item.Insert, ref buffer, ref cursor);
                paletteFilter.Clear();
                break;

            case DropdownMode.Commands:
                SetBuffer(item.Insert, ref buffer, ref cursor);
                break;

            case DropdownMode.Files:
                // Replace the token that starts with the most recent '@',
                // keeping the '@' so the mention survives into the message
                // (the agent resolves @path into attached file content).
                var at = LastAtPosition(buffer, cursor);
                if (at >= 0)
                {
                    buffer.Remove(at, cursor - at);
                    cursor = at;
                    buffer.Insert(cursor, "@" + item.Insert + " ");
                    cursor += item.Insert.Length + 2;
                }
                break;
        }
    }

    /// <summary>
    /// Waits briefly for more bytes after ESC — terminal writes can trail the
    /// ESC byte by a few milliseconds, and treating that as a lone keypress
    /// would leak sequence fragments into the buffer.
    /// </summary>
    public static int LastAtPosition(StringBuilder buffer, int cursor)
    {
        for (var i = cursor - 1; i >= 0; i--)
        {
            var ch = buffer[i];
            if (ch == '@')
                return i;
            if (char.IsWhiteSpace(ch))
                break;
        }
        return -1;
    }

    private static void WriteBuffer(StringBuilder buffer, int cursor)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            if (i == cursor)
                Console.Write("\x1b[7m" + buffer[i] + "\x1b[0m");
            else
                Console.Write(buffer[i]);
        }

        if (cursor >= buffer.Length)
            Console.Write("\x1b[7m \x1b[0m");
    }

    public static DropdownMode DetectMode(string text, int cursor, string palette, bool paletteActive)
    {
        if (paletteActive)
            return DropdownMode.Palette;
        if (text.StartsWith('/'))
            return DropdownMode.Commands;
        if (LastTokenStartsWith(text, cursor, '@'))
            return DropdownMode.Files;
        return DropdownMode.None;
    }

    private static bool LastTokenStartsWith(string text, int cursor, char sigil)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        for (var i = cursor - 1; i >= 0; i--)
        {
            if (text[i] == sigil)
                return true;
            if (char.IsWhiteSpace(text[i]))
                return false;
        }
        return false;
    }

    public static string SlashPrefix(string text) => text.StartsWith('/') ? text[1..] : text;

    public static string FilePrefix(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);

        var start = -1;
        for (var i = cursor - 1; i >= 0; i--)
        {
            if (text[i] == '@')
            {
                start = i;
                break;
            }
            if (char.IsWhiteSpace(text[i]))
                break;
        }
        return start < 0 ? "" : text[(start + 1)..cursor];
    }

    private static void Erase(int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            Console.Write("\x1b[1F\x1b[2K");
        }
        Console.Write("\r");
    }

    private static bool IsPiped
    {
        get
        {
            try
            {
                return Console.IsInputRedirected;
            }
            catch
            {
                return true;
            }
        }
    }
}
#endif
