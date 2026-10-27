#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>Which limit stopped the truncation.</summary>
public enum TruncationLimit
{
    None,
    Lines,
    Bytes
}

/// <summary>
/// Result of truncating tool output. Ported from pi's <c>tools/truncate.ts</c>: two independent
/// limits (lines and bytes), whichever is hit first wins, and partial lines are never returned.
/// </summary>
public sealed record TruncationResult(
    string Content,
    bool Truncated,
    TruncationLimit Limit,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes)
{
    public static readonly TruncationResult Empty =
        new("", false, TruncationLimit.None, 0, 0, 0, 0);
}

/// <summary>
/// Bounded tool output. A single <c>run_command</c> can emit megabytes; without a cap that
/// lands in the context window and the session dies. This caps the visible text, keeps the
/// full output on disk, and tells the model where to find it.
/// </summary>
public static class ToolOutput
{
    public const int DefaultMaxLines = 2_000;
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>Keeps the first N lines/bytes. Use for file reads.</summary>
    public static TruncationResult Head(string? content, int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(content))
            return TruncationResult.Empty;

        var (lines, totalBytes) = Measure(content);

        if (lines.Count <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult(
                Normalize(content), false, TruncationLimit.None, lines.Count, totalBytes, lines.Count, totalBytes);
        }

        var kept = new StringBuilder();
        var outputBytes = 0;
        var outputLines = 0;

        foreach (var line in lines)
        {
            if (outputLines >= maxLines)
                break;

            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (outputBytes + lineBytes > maxBytes)
                break;

            kept.AppendLine(line);
            outputBytes += lineBytes;
            outputLines++;
        }

        var limit = outputLines >= maxLines ? TruncationLimit.Lines : TruncationLimit.Bytes;

        return new TruncationResult(
            kept.ToString().TrimEnd('\n'),
            true,
            limit,
            lines.Count,
            totalBytes,
            outputLines,
            outputBytes);
    }

    /// <summary>
    /// Keeps the last N lines/bytes. Use for command output, where the tail is what matters.
    /// </summary>
    public static TruncationResult Tail(string? content, int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(content))
            return TruncationResult.Empty;

        var (lines, totalBytes) = Measure(content);

        if (lines.Count <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult(
                Normalize(content), false, TruncationLimit.None, lines.Count, totalBytes, lines.Count, totalBytes);
        }

        var kept = new List<string>(Math.Min(maxLines, lines.Count));
        var outputBytes = 0;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (kept.Count >= maxLines)
                break;

            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (outputBytes + lineBytes > maxBytes)
                break;

            kept.Add(lines[i]);
            outputBytes += lineBytes;
        }

        kept.Reverse();
        var limit = kept.Count >= maxLines ? TruncationLimit.Lines : TruncationLimit.Bytes;

        return new TruncationResult(
            string.Join('\n', kept),
            true,
            limit,
            lines.Count,
            totalBytes,
            kept.Count,
            outputBytes);
    }

    /// <summary>
    /// Writes the full output next to the workspace and returns its path, so the model can
    /// follow up with <c>read_file</c> instead of losing the data.
    /// </summary>
    public static string? SpillToFile(string content, string directory, string label)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var safeLabel = string.Join('_', label.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(directory, $"{safeLabel}-{Guid.NewGuid():N}"[..(safeLabel.Length + 9)] + ".log");
            File.WriteAllText(path, content);
            return path;
        }
        catch
        {
            // Spilling is best-effort; the truncated content is still usable.
            return null;
        }
    }

    /// <summary>
    /// Formats the note appended to truncated content. Names the limit hit and, when the full
    /// output was preserved, where to read it.
    /// </summary>
    public static string DescribeTruncation(TruncationResult result, string? fullOutputPath)
    {
        if (!result.Truncated)
            return "";

        var reason = result.Limit == TruncationLimit.Lines
            ? $"showing {result.OutputLines} of {result.TotalLines} lines"
            : $"showing {FormatSize(result.OutputBytes)} of {FormatSize(result.TotalBytes)}";

        var builder = new StringBuilder();
        builder.Append("[output truncated: ").Append(reason).Append(']');

        if (fullOutputPath is { Length: > 0 })
            builder.Append("\n[full output: ").Append(fullOutputPath).Append(']');

        return builder.ToString();
    }

    /// <summary>Human-readable byte count.</summary>
    public static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0}KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0}MB"
    };

    private static string Normalize(string content) =>
        content.Contains('\r') ? content.Replace("\r\n", "\n").Replace("\r", "") : content;

    private static (List<string> Lines, int TotalBytes) Measure(string content)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        var lines = content.Split('\n');

        // A trailing newline is not an extra line.
        var count = content.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var trimmed = new List<string>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
            trimmed.Add(lines[i].TrimEnd('\r'));

        return (trimmed, totalBytes);
    }
}
#endif
