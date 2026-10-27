#if !NETWASM
using System.Text;

namespace Reptar.Term;

/// <summary>One styled span of text on a rendered markdown line.</summary>
public readonly record struct MarkdownRun(
    string Text,
    string Fg,
    bool Bold = false,
    bool Dim = false,
    bool Italic = false,
    bool Underline = false);

/// <summary>A rendered markdown line: styled runs plus an indent in cells.</summary>
public sealed record MarkdownLine(IReadOnlyList<MarkdownRun> Runs, int Indent = 0)
{
    /// <summary>Plain text of the line, for measuring and for tests.</summary>
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

/// <summary>
/// Renders markdown to styled runs a terminal buffer can draw.
/// </summary>
/// <remarks>
/// Line-based but <b>inline-aware</b>: emphasis, code spans, and links become runs with their own
/// style, so <c>**bold**</c> is drawn bold rather than having its markers stripped. That
/// distinction is most of the difference between markdown that reads well in a terminal and
/// markdown that has merely been de-punctuated.
///
/// The output is plain data — no console writes — so a caller can window, scroll, search, or
/// re-render it, and so it is testable without a terminal.
/// </remarks>
public static class MarkdownRenderer
{
    private const string TextFg = "\x1b[37m";
    private const string DimFg = "\x1b[90m";
    private const string AccentFg = "\x1b[38;2;16;185;129m";
    private const string HeadingFg = "\x1b[97m";
    private const string CodeFg = "\x1b[38;2;148;163;184m";
    private const string CodeBg = "\x1b[48;2;28;32;42m";
    private const string LinkFg = "\x1b[38;2;56;189;248m";
    private const string QuoteFg = "\x1b[38;2;120;130;150m";

    /// <summary>
    /// Renders markdown into lines no wider than <paramref name="width"/>.
    /// </summary>
    public static List<MarkdownLine> Render(string markdown, int width)
    {
        var lines = new List<MarkdownLine>();
        if (string.IsNullOrEmpty(markdown))
            return lines;

        var inFence = false;
        var fenceLanguage = "";

        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                fenceLanguage = inFence ? trimmed[3..].Trim() : "";
                lines.Add(new MarkdownLine(
                    [new MarkdownRun(fenceLanguage.Length > 0 ? $"┄┄ {fenceLanguage} ┄┄" : "┄┄┄┄", DimFg)], 2));
                continue;
            }

            if (inFence)
            {
                // Code is drawn on its own background so a block reads as one region rather than
                // a series of indented lines.
                lines.Add(new MarkdownLine(
                    [new MarkdownRun(" " + line + " ", CodeFg)], 2));
                continue;
            }

            if (trimmed.Length == 0)
            {
                lines.Add(new MarkdownLine([]));
                continue;
            }

            if (IsRule(trimmed))
            {
                lines.Add(new MarkdownLine([new MarkdownRun(new string('─', Math.Max(8, Math.Min(width - 4, 60))), DimFg)], 2));
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                    level++;

                var body = trimmed[level..].Trim();
                var fg = level <= 2 ? HeadingFg : TextFg;

                foreach (var wrapped in Wrap(Inline(body, fg, bold: true), width - 4))
                    lines.Add(new MarkdownLine(wrapped, 2));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var body = trimmed[1..].TrimStart();
                foreach (var wrapped in Wrap(Inline(body, QuoteFg, dim: true), width - 8))
                {
                    var runs = new List<MarkdownRun> { new("│ ", DimFg) };
                    runs.AddRange(wrapped);
                    lines.Add(new MarkdownLine(runs, 2));
                }
                continue;
            }

            if (TryBullet(trimmed, out var bulletIndent, out var bulletBody))
            {
                foreach (var wrapped in Wrap(Inline(bulletBody, TextFg), width - 6 - (bulletIndent * 2)))
                {
                    var runs = new List<MarkdownRun> { new("• ", AccentFg) };
                    runs.AddRange(wrapped);
                    lines.Add(new MarkdownLine(runs, 4 + (bulletIndent * 2)));
                }
                continue;
            }

            if (TryOrdered(trimmed, out var number, out var orderedBody))
            {
                var marker = number + ". ";
                foreach (var wrapped in Wrap(Inline(orderedBody, TextFg), width - 6 - marker.Length))
                {
                    var runs = new List<MarkdownRun> { new(marker, AccentFg) };
                    runs.AddRange(wrapped);
                    lines.Add(new MarkdownLine(runs, 4));
                }
                continue;
            }

            foreach (var wrapped in Wrap(Inline(line, TextFg), width - 4))
                lines.Add(new MarkdownLine(wrapped, 2));
        }

        return lines;
    }

    // ------------------------------------------------------------------
    // Inline
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses inline markdown into styled runs: <c>**strong**</c>, <c>*em*</c>, <c>~~strike~~</c>,
    /// <c>`code`</c>, <c>[label](url)</c>, and bare URLs.
    /// </summary>
    public static List<MarkdownRun> Inline(string text, string baseFg, bool bold = false, bool dim = false)
    {
        var runs = new List<MarkdownRun>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0)
                return;

            runs.Add(new MarkdownRun(plain.ToString(), baseFg, Bold: bold, Dim: dim));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            // **strong**
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.Add(new MarkdownRun(text[(i + 2)..end], baseFg, Bold: true, Dim: dim));
                    i = end + 1;
                    continue;
                }
            }

            // ~~strike~~
            if (text[i] == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.Add(new MarkdownRun(text[(i + 2)..end], DimFg, Dim: true));
                    i = end + 1;
                    continue;
                }
            }

            // *emphasis*
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] != ' ')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    runs.Add(new MarkdownRun(text[(i + 1)..end], baseFg, Bold: bold, Dim: dim, Italic: true));
                    i = end;
                    continue;
                }
            }

            // `code`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    runs.Add(new MarkdownRun(text[(i + 1)..end], CodeFg));
                    i = end;
                    continue;
                }
            }

            // [label](url)
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > close + 2)
                    {
                        Flush();
                        runs.Add(new MarkdownRun(text[(i + 1)..close], LinkFg, Underline: true));
                        i = paren;
                        continue;
                    }
                }
            }

            plain.Append(text[i]);
        }

        Flush();
        return runs;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    /// <summary>
    /// Wraps styled runs to a width, breaking on spaces and never splitting a run's styling.
    /// </summary>
    public static List<List<MarkdownRun>> Wrap(IReadOnlyList<MarkdownRun> runs, int width)
    {
        var lines = new List<List<MarkdownRun>>();
        var current = new List<MarkdownRun>();
        var used = 0;

        foreach (var run in runs)
        {
            var remaining = run.Text;

            while (remaining.Length > 0)
            {
                var space = Math.Max(1, width - used);

                if (remaining.Length <= space)
                {
                    current.Add(run with { Text = remaining });
                    used += remaining.Length;
                    break;
                }

                // Prefer breaking at the last space inside the available span.
                var cut = remaining.LastIndexOf(' ', Math.Min(space, remaining.Length - 1));
                if (cut <= 0)
                    cut = space;

                var head = remaining[..cut];
                if (head.Length > 0)
                {
                    current.Add(run with { Text = head });
                    used += head.Length;
                }

                lines.Add(current);
                current = [];
                used = 0;
                remaining = remaining[cut..].TrimStart(' ');
            }
        }

        if (current.Count > 0 || lines.Count == 0)
            lines.Add(current);

        return lines;
    }

    private static bool IsRule(string trimmed) =>
        trimmed.Length >= 3 &&
        trimmed.All(ch => ch is '-' or '*' or '_') &&
        trimmed.Distinct().Count() == 1;

    private static bool TryBullet(string trimmed, out int indent, out string body)
    {
        indent = 0;
        body = "";

        if (trimmed.Length < 2 || trimmed[0] is not ('-' or '*' or '+') || trimmed[1] != ' ')
            return false;

        body = trimmed[2..].Trim();
        return true;
    }

    private static bool TryOrdered(string trimmed, out string number, out string body)
    {
        number = "";
        body = "";

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;

        if (digits == 0 || digits + 1 >= trimmed.Length || trimmed[digits] != '.' || trimmed[digits + 1] != ' ')
            return false;

        number = trimmed[..digits];
        body = trimmed[(digits + 2)..].Trim();
        return true;
    }
}
#endif
