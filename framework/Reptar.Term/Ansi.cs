#if !NETWASM
using System.Text;

namespace Reptar.Term;

/// <summary>
/// Minimal ANSI escape helpers for the terminal UI. All calls are no-ops
/// when the stream is redirected or the terminal has no color support.
/// </summary>
public static class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string White = "\x1b[37m";
    public const string Gray = "\x1b[90m";
    public const string BrightBlack = "\x1b[90m";

    public static bool Enabled { get; private set; } = Detect();

    public static bool Detect()
    {
        try
        {
            if (Console.IsOutputRedirected)
                return false;
            var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
            if (!string.IsNullOrEmpty(noColor))
                return false;
            return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string Color(string code, string text) =>
        Enabled ? code + text + Reset : text;

    public static void Enable()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Non-interactive terminals may reject the encoding switch.
        }
        Enabled = Detect();
    }

    public static void Write(string text) => Console.Write(text);

    public static void WriteLine(string text = "")
    {
        if (Enabled)
            Console.WriteLine(text);
        else
            Console.WriteLine(Strip(text));
    }

    public static void WriteLine(string code, string text) => WriteLine(code + text + Reset);

    public static void Rule(char glyph = '─', int width = 64)
    {
        var count = Math.Min(width, TryGetWidth());
        WriteLine(Dim, new string(glyph, Math.Max(8, count)));
    }

    public static string Strip(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b')
            {
                while (i < text.Length && !char.IsLetter(text[i]))
                    i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static int TryGetWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 64 : Console.WindowWidth;
        }
        catch
        {
            return 64;
        }
    }
}

/// <summary>
/// Line-oriented streaming markdown renderer. Feed it complete lines as they
/// stream off the model; it colorizes headings, code fences, lists, and inline
/// spans without ever rewriting previous output (fx-style scrollback output).
/// </summary>
public sealed class AnsiMarkdown
{
    private bool _inCodeFence;
    private string _fenceLanguage = "";

    /// <summary>
    /// Renders one completed line of markdown. Code-fence state persists
    /// across calls so streamed blocks stay colored to the closing fence.
    /// </summary>
    public void WriteLine(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            if (_inCodeFence)
            {
                _inCodeFence = false;
                _fenceLanguage = "";
                Ansi.WriteLine(Ansi.Gray, "```");
                return;
            }

            _inCodeFence = true;
            _fenceLanguage = trimmed[3..].Trim();
            var label = _fenceLanguage.Length > 0 ? " " + _fenceLanguage : "";
            Ansi.WriteLine(Ansi.Gray, "```" + label);
            return;
        }

        if (_inCodeFence)
        {
            Ansi.WriteLine(Ansi.Gray, line);
            return;
        }

        if (trimmed.Length == 0)
        {
            Console.WriteLine();
            return;
        }

        var headingLevel = HeadingDepth(trimmed);
        if (headingLevel > 0)
        {
            Ansi.WriteLine(
                Ansi.Bold + (headingLevel <= 2 ? Ansi.Cyan : Ansi.White),
                new string('#', headingLevel) + " " + trimmed[(headingLevel + 1)..].TrimStart());
            return;
        }

        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
        {
            Ansi.WriteLine(
                Ansi.Color(Ansi.Magenta, trimmed[..2]) + RenderInline(trimmed[2..]));
            return;
        }

        var ordered = OrderedBullet(trimmed);
        if (ordered is { } bullet)
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Yellow, bullet) + RenderInline(trimmed[bullet.Length..]));
            return;
        }

        if (trimmed.StartsWith("> "))
        {
            Ansi.WriteLine(Ansi.Color(Ansi.Italic + Ansi.Gray, trimmed));
            return;
        }

        Ansi.WriteLine(RenderInline(line));
    }

    private static int HeadingDepth(string line)
    {
        if (line[0] != '#')
            return 0;

        var depth = 0;
        while (depth < line.Length && line[depth] == '#' && depth < 6)
            depth++;

        return depth < line.Length && line[depth] == ' ' ? depth : 0;
    }

    private static string? OrderedBullet(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits]))
            digits++;

        if (digits is > 0 and < 4 && digits + 1 < line.Length &&
            (line[digits] == '.' || line[digits] == ')') && line[digits + 1] == ' ')
            return line[..(digits + 2)];

        return null;
    }

    /// <summary>
    /// Colorizes `inline code`, **bold**, and *italic* spans on a single line.
    /// </summary>
    public static string RenderInline(string text)
    {
        if (!Ansi.Enabled)
            return text;

        var sb = new StringBuilder(text.Length + 32);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    sb.Append(Ansi.BrightBlack).Append(text[(i + 1)..close]).Append(Ansi.Reset);
                    i = close + 1;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    sb.Append(Ansi.Bold).Append(text[(i + 2)..close]).Append(Ansi.Reset);
                    i = close + 2;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }
}
#endif
