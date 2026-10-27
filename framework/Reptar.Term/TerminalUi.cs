#if !NETWASM
using System.Text;

namespace Reptar.Term;

/// <summary>
/// Zero-dependency terminal UI toolkit: an arrow-key selection prompt with
/// type-to-search, a validated text prompt with secret masking, a live status
/// spinner, and auto-measured bordered tables. Ideas borrowed from
/// Spectre.Console and the pi/opencode pickers, implemented over raw ANSI so
/// the Native AOT binary stays dependency-free.
///
/// Every interactive component degrades to plain numbered/line input when
/// stdin is redirected, so scripted and CI flows keep working.
/// </summary>
public static class TerminalUi
{
    // ------------------------------------------------------------------
    // Capability detection
    // ------------------------------------------------------------------

    public enum ColorDepth
    {
        Basic,      // 3-bit SGR colors
        Indexed256, // 8-bit (256-color palette)
        TrueColor   // 24-bit
    }

    public static ColorDepth Colors
    {
        get
        {
            if (!Ansi.Enabled)
                return ColorDepth.Basic;

            var colorterm = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
            if (colorterm.Contains("24bit", StringComparison.OrdinalIgnoreCase) ||
                colorterm.Contains("truecolor", StringComparison.OrdinalIgnoreCase))
                return ColorDepth.TrueColor;

            var term = Environment.GetEnvironmentVariable("TERM") ?? "";
            if (term.Contains("256color", StringComparison.OrdinalIgnoreCase))
                return ColorDepth.Indexed256;

            return ColorDepth.Basic;
        }
    }

    /// <summary>
    /// Colors text with an RGB value, downgrading to the nearest indexed or
    /// basic color when the terminal cannot show truecolor.
    /// </summary>
    public static string Rgb(int r, int g, int b, string text)
    {
        if (!Ansi.Enabled)
            return text;

        return Colors switch
        {
            ColorDepth.TrueColor => $"\x1b[38;2;{r};{g};{b}m{text}\x1b[0m",
            ColorDepth.Indexed256 => "\x1b[38;5;" + To256(r, g, b) + "m" + text + "\x1b[0m",
            _ => Ansi.Color(NearestBasic(r, g, b), text)
        };
    }

    // Reptar brand + common rich accents (nearest-basic fallbacks chosen by eye).
    public static string Purple(string text) => Rgb(167, 108, 242, text);
    public static string Orange(string text) => Rgb(251, 146, 60, text);
    public static string Teal(string text) => Rgb(45, 212, 191, text);
    public static string Rose(string text) => Rgb(251, 113, 133, text);
    public static string SkyBlue(string text) => Rgb(56, 189, 248, text);

    private static int To256(int r, int g, int b)
    {
        // 6x6x6 color cube starting at index 16
        var red = (int)Math.Round(r / 255.0 * 5);
        var green = (int)Math.Round(g / 255.0 * 5);
        var blue = (int)Math.Round(b / 255.0 * 5);
        return 16 + 36 * red + 6 * green + blue;
    }

    private static string NearestBasic(int r, int g, int b)
    {
        var palette = new (int R, int G, int B, string Code)[]
        {
            (0, 0, 0, Ansi.BrightBlack), (128, 0, 0, Ansi.Red), (0, 128, 0, Ansi.Green),
            (128, 128, 0, Ansi.Yellow), (0, 0, 128, Ansi.Blue), (128, 0, 128, Ansi.Magenta),
            (0, 128, 128, Ansi.Cyan), (192, 192, 192, Ansi.White)
        };

        var best = palette[0];
        var bestDistance = long.MaxValue;

        foreach (var entry in palette)
        {
            var distance = Square(entry.R - r) + Square(entry.G - g) + Square(entry.B - b);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }

        return best.Code;

        static long Square(int value) => (long)value * value;
    }

    // ------------------------------------------------------------------
    // Input facade: once the editor's reader thread owns stdin (after the
    // first prompt keypress), every key consumer must go through it — a
    // Console.ReadKey here would race the thread for bytes.
    // ------------------------------------------------------------------

    private static TermKey? ReadTermKey()
    {
        if (TerminalInput.Active)
            return TerminalInput.ReadKey();

        try
        {
            var info = Console.ReadKey(intercept: true);
            return new TermKey(
                info.Key,
                info.KeyChar,
                (info.Modifiers & ConsoleModifiers.Control) != 0);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Selection prompt (arrow keys + type-to-search)
    // ------------------------------------------------------------------

    /// <summary>
    /// Interactive single selection. Arrow keys navigate, typing filters, Enter confirms, Escape
    /// cancels. Falls back to a numbered menu when stdin is redirected (scripts, CI, pipes).
    /// </summary>
    /// <remarks>
    /// Delegates matching and selection to <see cref="TermSelector"/>, which is the one
    /// implementation of those rules. This method owns only the console framing: erase the
    /// previous frame, print the new one. Two copies of fuzzy matching and wrap-around selection
    /// is how the terminal and full-screen clients drift apart.
    /// </remarks>
    public static string? Select(
        string title,
        IReadOnlyList<TermSelectorItem> items,
        string? hint = null)
    {
        if (items.Count == 0)
            return null;

        if (IsPiped)
            return SelectNumbered(title, items, hint);

        var selector = new TermSelector(title, items)
        {
            Hint = hint ?? "type to filter · ↑↓ move · enter select · esc cancel"
        };

        var width = Math.Max(40, SafeWidth());
        var previousRows = 0;

        void Paint()
        {
            if (previousRows > 0)
                EraseSelectFrame(previousRows);

            var lines = selector.RenderLines(width, MaxPageSize);
            foreach (var line in lines)
                Console.WriteLine("  " + line);

            previousRows = lines.Count;
        }

        Paint();

        while (true)
        {
            var read = ReadTermKey();
            if (read is null)
            {
                EraseSelectFrame(previousRows);
                return null;
            }

            var key = read.Value;

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (selector.Selected is { } chosen)
                    {
                        EraseSelectFrame(previousRows);
                        return chosen.Value as string ?? chosen.Label;
                    }
                    break;

                case ConsoleKey.Escape:
                    EraseSelectFrame(previousRows);
                    return null;

                case ConsoleKey.UpArrow:
                    selector.Move(-1);
                    break;

                case ConsoleKey.DownArrow:
                    selector.Move(1);
                    break;

                case ConsoleKey.PageUp:
                    for (var i = 0; i < MaxPageSize; i++) selector.Move(-1);
                    break;

                case ConsoleKey.PageDown:
                    for (var i = 0; i < MaxPageSize; i++) selector.Move(1);
                    break;

                case ConsoleKey.Backspace:
                    selector.Backspace();
                    break;

                case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab:
                    break;

                default:
                    if (key.Ch is not '\0' && !char.IsControl(key.Ch))
                        selector.Type(key.Ch);
                    break;
            }

            Paint();
        }
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 100; }
    }

    private const int MaxPageSize = 8;

    private static bool IsPiped
    {
        get
        {
            try { return Console.IsInputRedirected; }
            catch { return true; }
        }
    }

    /// <summary>
    /// Removes the previously printed frame so the next one can take its place. Console-mode
    /// rendering moves the cursor up and clears each line, rather than diffing a cell buffer.
    /// </summary>
    private static void EraseSelectFrame(int rows)
    {
        if (rows <= 0)
            return;

        for (var i = 0; i < rows; i++)
        {
            if (Console.CursorTop > 0)
                Console.SetCursorPosition(0, Console.CursorTop - 1);
            Console.Write("\x1b[2K");
        }
    }

    private static string? SelectNumbered(
        string title,
        IReadOnlyList<TermSelectorItem> items,
        string? hint)
    {
        Console.WriteLine(Ansi.Color(Ansi.Bold + Ansi.Cyan, "  " + title));

        for (var i = 0; i < items.Count; i++)
        {
            var detail = items[i].Detail is { Length: > 0 }
                ? "  " + Ansi.Color(Ansi.Gray, items[i].Detail)
                : "";
            Console.WriteLine(Ansi.Color(Ansi.Cyan, $"    {i + 1,2}. ") + items[i].Label + detail);
        }

        if (hint is { Length: > 0 })
            Console.WriteLine(Ansi.Color(Ansi.Gray, "  " + hint));

        Console.Write(Ansi.Color(Ansi.Cyan, "  choice") +
            Ansi.Color(Ansi.Gray, " [number or name, 0 = cancel]: "));

        var answer = Console.In.ReadLine()?.Trim() ?? "";
        if (answer.Length == 0 || answer == "0")
            return null;

        if (int.TryParse(answer, out var index) && index >= 1 && index <= items.Count)
            return items[index - 1].Value as string ?? items[index - 1].Label;

        return items.FirstOrDefault(i =>
            string.Equals(i.Value as string, answer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Label, answer, StringComparison.OrdinalIgnoreCase))?.Value as string;
    }

    // ------------------------------------------------------------------
    // Text prompt with validation (and optional secret masking)
    // ------------------------------------------------------------------

    /// <summary>
    /// Prompt for a line of input. The validator returns an error message for
    /// invalid input (which is re-asked, Spectre-style) or null to accept.
    /// Secret mode masks input with the given character.
    /// </summary>
    public static string? Ask(
        string prompt,
        Func<string, string?>? validate = null,
        bool secret = false,
        string? placeholder = null)
    {
        if (placeholder is { Length: > 0 })
            prompt += Ansi.Color(Ansi.Gray, $" [{placeholder}]");

        if (IsPiped)
        {
            Console.Write(Ansi.Color(Ansi.Cyan, "  ") + prompt + " ");
            return Console.In.ReadLine();
        }

        while (true)
        {
            Console.Write(Ansi.Color(Ansi.Cyan, "  ") + prompt + " ");

            var value = secret ? ReadMasked() : ReadLineInteractive();

            if (value is null)
                return null;

            var error = validate?.Invoke(value);
            if (error is null)
                return value;

            Console.WriteLine(Ansi.Color(Ansi.Red, "  ✗ " + error));
        }
    }

    /// <summary>
    /// Line input through the TerminalInput facade so it never races the
    /// reader thread that owns stdin after activation.
    /// </summary>
    private static string ReadLineInteractive()
    {
        var line = new System.Text.StringBuilder();
        while (true)
        {
            var read = ReadTermKey();
            if (read is null)
            {
                Console.WriteLine();
                return line.ToString();
            }

            var key = read.Value;

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return line.ToString();
            }

            if (key.PastedText is { Length: > 0 })
            {
                line.Append(key.PastedText);
                Console.Write(key.PastedText);
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (key.Ch is not '\0' && !char.IsControl(key.Ch))
            {
                line.Append(key.Ch);
                Console.Write(key.Ch);
            }
        }
    }

    private static string ReadMasked()
    {
        var secret = new StringBuilder();
        while (true)
        {
            var read = ReadTermKey();
            if (read is null)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            var key = read.Value;

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return "";
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (key.Ch is '\0' || char.IsControl(key.Ch))
                continue;

            secret.Append(key.Ch);
            Console.Write('*');
        }
    }

    // ------------------------------------------------------------------
    // Status spinner
    // ------------------------------------------------------------------

    private static readonly string[] SpinnerFrames = ["⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷"];

    /// <summary>
    /// Runs work under an animated spinner. The message can be updated mid-run.
    /// When output is piped, prints the initial message statically instead.
    /// Never overlaps with streaming chat output — use between turns only.
    /// </summary>
    public static async Task<T> SpinAsync<T>(
        string message,
        Func<Func<string, Task>, Task<T>> work)
    {
        if (IsPiped || !Ansi.Enabled)
        {
            Console.WriteLine(Ansi.Color(Ansi.Gray, "  " + message + "…"));
            return await work(updated =>
            {
                Console.WriteLine(Ansi.Color(Ansi.Gray, "  " + updated));
                return Task.CompletedTask;
            });
        }

        var current = message;
        var start = DateTime.UtcNow;
        using var cts = new CancellationTokenSource();

        var animation = Task.Run(async () =>
        {
            var frame = 0;
            while (!cts.IsCancellationRequested)
            {
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                Console.Write("\r\x1b[2K" +
                    Purple(SpinnerFrames[frame]) +
                    " " + current +
                    Ansi.Color(Ansi.Gray, $" ({elapsed:0.0}s)"));
                frame = (frame + 1) % SpinnerFrames.Length;
                try
                {
                    await Task.Delay(80, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        try
        {
            return await work(async updated =>
            {
                current = updated;
            });
        }
        finally
        {
            cts.Cancel();
            try
            {
                await animation;
            }
            catch (OperationCanceledException)
            {
                // Expected: the spinner stops by cancelling its own delay loop.
            }
            Console.Write("\r\x1b[2K");
        }
    }

    // ------------------------------------------------------------------
    // Bordered table
    // ------------------------------------------------------------------

    public sealed record Column(string Header, int MaxWidth = 48, string? ColorCode = null);

    /// <summary>
    /// Renders an auto-measured box-drawing table. Column widths come from
    /// content (capped), so long model names truncate cleanly instead of
    /// breaking alignment.
    /// </summary>
    public static void Table(
        IReadOnlyList<Column> columns,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int? widthOverride = null)
    {
        var widths = columns
            .Select((column, index) => Math.Min(
                column.MaxWidth,
                rows
                    .Select(row => VisibleLength(row[index]))
                    .Append(VisibleLength(column.Header))
                    .DefaultIfEmpty(3)
                    .Max()))
            .ToArray();

        var terminalWidth = widthOverride ?? TryConsoleWidth();

        // Shrink the widest columns if the table would overflow the terminal.
        // Total visible width = sum(width + 2 padding) + (columns + 1) borders.
        int TotalWidth() => widths.Sum() + widths.Length * 3 + 1;
        while (TotalWidth() > terminalWidth && widths.Any(w => w > 8))
        {
            var widest = Array.IndexOf(widths, widths.Max());
            widths[widest] = Math.Max(8, widths[widest] - Math.Min(widths[widest] - 8, TotalWidth() - terminalWidth));
        }

        var rule = widths.Select(w => new string('─', w + 2)).ToArray();

        Console.WriteLine(Border("┌", rule, "┬", "┐"));

        Console.Write("│");
        for (var i = 0; i < columns.Count; i++)
        {
            var header = Fit(columns[i].Header, widths[i]);
            Console.Write(" " + Ansi.Color(Ansi.Bold, Pad(header, widths[i])) + " │");
        }
        Console.WriteLine();

        Console.WriteLine(Border("├", rule, "┼", "┤"));

        foreach (var row in rows)
        {
            Console.Write("│");
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = Fit(row[i], widths[i]);
                var color = columns[i].ColorCode;
                var rendered = color is not null ? Ansi.Color(color, cell) : cell;
                Console.Write(" " + Pad(rendered, cell, widths[i]) + " │");
            }
            Console.WriteLine();
        }

        Console.WriteLine(Border("└", rule, "┴", "┘"));

        static string Border(string left, string[] rule, string middle, string right) =>
            left + string.Join(middle, rule) + right;
    }

    private static string Pad(string rendered, string text, int width)
    {
        var visible = VisibleLength(text);
        return visible >= width ? rendered : rendered + new string(' ', width - visible);
    }

    private static string Pad(string text, int width) => Pad(text, text, width);

    private static string Fit(string text, int width) =>
        VisibleLength(text) <= width ? text : text[..(width - 1)] + "…";

    private static int VisibleLength(string text) => Ansi.Strip(text).Length;

    private static int TryConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 100 : Math.Max(60, Console.WindowWidth);
        }
        catch
        {
            return 100;
        }
    }

    // ------------------------------------------------------------------
    // Exception rendering
    // ------------------------------------------------------------------

    /// <summary>
    /// Pretty multi-line exception rendering (Spectre-style): red type +
    /// message, dim stack frames with shortened paths, nested causes indented.
    /// </summary>
    public static void RenderException(Exception exception, string? header = null)
    {
        if (header is { Length: > 0 })
            Console.WriteLine(Ansi.Color(Ansi.Bold + Ansi.Red, "  " + header));

        RenderOne(exception, indent: "  ", depth: 0);

        static void RenderOne(Exception ex, string indent, int depth)
        {
            Console.WriteLine(
                Ansi.Color(Ansi.Red, indent + (depth > 0 ? "caused by " : "") + ex.GetType().Name) +
                Ansi.Color(Ansi.Bold, ": ") + ex.Message);

            if (ex.StackTrace is { Length: > 0 } stack)
            {
                var shown = 0;
                foreach (var rawLine in stack.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (!line.StartsWith("at ", StringComparison.Ordinal))
                        continue;

                    Console.WriteLine(Ansi.Color(Ansi.Gray, indent + "   " + ShortenFrame(line)));
                    if (++shown >= 6)
                    {
                        Console.WriteLine(Ansi.Color(Ansi.Gray, indent + "   …"));
                        break;
                    }
                }
            }

            if (ex.InnerException is { } inner)
                RenderOne(inner, indent + "  ", depth + 1);
        }

        static string ShortenFrame(string frame)
        {
            // "at Foo() in /long/path/file.cs:line 42" → "at Foo() file.cs:42"
            var match = System.Text.RegularExpressions.Regex.Match(frame, @"^(.*?)(?:\s+in\s+)?(.+[/\\])([^/\\]+\.cs):line (\d+)$");
            return match.Success
                ? $"{match.Groups[1].Value} {match.Groups[3].Value}:{match.Groups[4].Value}"
                : frame;
        }
    }
}
#endif
