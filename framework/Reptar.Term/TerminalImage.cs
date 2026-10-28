#if !NETWASM
using System.Text;

namespace Reptar.Term;

/// <summary>Inline image protocol a terminal understands.</summary>
public enum ImageProtocol
{
    /// <summary>No inline image support; callers render a placeholder.</summary>
    None,

    /// <summary>Kitty graphics protocol (kitty, WezTerm, Ghostty).</summary>
    Kitty,

    /// <summary>iTerm2 inline images (iTerm2, WezTerm).</summary>
    Iterm2
}

/// <summary>
/// Renders images inline for terminals that support it, and reports clearly when they do not.
/// </summary>
/// <remarks>
/// Two protocols cover nearly every modern terminal. Kitty's is chunked and base64-encoded; iTerm2's
/// is a single OSC 1337 payload. Both take base64 directly, so no image decoding happens here —
/// the bytes are passed through.
///
/// There is deliberately no sixel path: it needs palette quantization, which is a decoder, and a
/// terminal without any of these should get a readable placeholder rather than mojibake.
/// </remarks>
public static class TerminalImage
{
    /// <summary>Largest payload chunk kitty accepts in one escape sequence.</summary>
    private const int KittyChunkSize = 4096;

    /// <summary>
    /// Detects the protocol from the environment. Checked in order of specificity, because a
    /// terminal that sets both should use the better one.
    /// </summary>
    public static ImageProtocol Detect()
    {
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";

        if (Environment.GetEnvironmentVariable("KITTY_WINDOW_ID") is { Length: > 0 } ||
            term.Contains("kitty", StringComparison.OrdinalIgnoreCase) ||
            termProgram.Contains("ghostty", StringComparison.OrdinalIgnoreCase) ||
            termProgram.Contains("WezTerm", StringComparison.OrdinalIgnoreCase))
        {
            return ImageProtocol.Kitty;
        }

        if (termProgram.Contains("iTerm", StringComparison.OrdinalIgnoreCase))
            return ImageProtocol.Iterm2;

        return ImageProtocol.None;
    }

    /// <summary>
    /// Builds the escape sequence for an inline image.
    /// </summary>
    /// <param name="base64Data">Base64-encoded image bytes.</param>
    /// <param name="protocol">Protocol to emit.</param>
    /// <param name="columns">Width in terminal cells.</param>
    /// <param name="rows">Height in terminal cells; 0 lets the terminal preserve the aspect ratio.</param>
    /// <returns>The sequence, or an empty string when there is nothing to draw.</returns>
    public static string Render(
        string base64Data,
        ImageProtocol protocol,
        int columns,
        int rows = 0)
    {
        if (base64Data.Length == 0 || protocol == ImageProtocol.None)
            return "";

        return protocol switch
        {
            ImageProtocol.Kitty => Kitty(base64Data, columns, rows),
            ImageProtocol.Iterm2 => Iterm2(base64Data, columns, rows),
            _ => ""
        };
    }

    /// <summary>
    /// A text placeholder naming the image and its size, for terminals with no image support.
    /// </summary>
    public static string Placeholder(string? label, int bytes) =>
        $"[image{(label is { Length: > 0 } ? " " + label : "")} · {bytes / 1024.0:0.#} KB]";

    /// <summary>Kitty graphics protocol, transmitted in chunks.</summary>
    private static string Kitty(string base64, int columns, int rows)
    {
        var builder = new StringBuilder(base64.Length + 128);

        for (var offset = 0; offset < base64.Length; offset += KittyChunkSize)
        {
            var length = Math.Min(KittyChunkSize, base64.Length - offset);
            var more = offset + length < base64.Length ? 1 : 0;

            builder.Append("\x1b_G");

            // Only the first chunk carries the geometry and the format.
            if (offset == 0)
            {
                builder.Append("a=T,f=100");
                builder.Append(",c=").Append(columns);
                if (rows > 0)
                    builder.Append(",r=").Append(rows);
            }

            builder.Append(",m=").Append(more);
            builder.Append(';').Append(base64, offset, length);
            builder.Append("\x1b\\");
        }

        return builder.ToString();
    }

    /// <summary>iTerm2 inline image protocol.</summary>
    private static string Iterm2(string base64, int columns, int rows)
    {
        var arguments = $"inline=1;width={columns}";

        if (rows > 0)
            arguments += $";height={rows}";

        return $"\x1b]1337;File={arguments};preserveAspectRatio=1:{base64}\a";
    }
}
#endif
