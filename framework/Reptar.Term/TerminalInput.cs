#if !NETWASM
using Reptar.AI;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Reptar.Term;

/// <summary>
/// One key event parsed from the terminal's byte stream: a control key, a
/// printable character, a bracketed paste, or an OSC 52 clipboard image.
/// </summary>
/// <summary>One mouse event decoded from an SGR sequence.</summary>
public readonly record struct TermMouseEvent(
    TermMouseKind Kind,
    int X,
    int Y,
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false)
{
    /// <summary>Wheel direction, or 0 for a click.</summary>
    public int Wheel => Kind switch
    {
        TermMouseKind.WheelUp => -1,
        TermMouseKind.WheelDown => 1,
        _ => 0
    };
}

public enum TermMouseKind
{
    Press,
    Release,
    Move,
    WheelUp,
    WheelDown
}

public readonly record struct TermKey(
    ConsoleKey Key = ConsoleKey.None,
    char Ch = '\0',
    bool Ctrl = false,
    string? PastedText = null,
    AgentImage? ClipboardImage = null,
    bool Shift = false,
    TermMouseEvent? Mouse = null)
{
    public bool IsKey => PastedText is null && ClipboardImage is null;
}

/// <summary>
/// The single owner of terminal input. .NET's Console.ReadKey parses (and
/// silently discards) ESC-]-prefixed sequences, and mixing a second reader
/// on the same fd races for bytes. So after activation, one dedicated thread
/// reads stdin and this parser turns the byte stream into <see cref="TermKey"/>
/// events: arrows, Home/End/PageUp/Down, Delete, Ctrl combos, Enter, ESC,
/// bracketed paste, UTF-8 text, and OSC 52 clipboard responses.
///
/// Activate after at least one Console.ReadKey call — .NET has already put
/// the terminal into raw mode at that point, which this layer relies on.
/// </summary>
public static class TerminalInput
{
    private static readonly object Sync = new();
    private static readonly Queue<byte> Bytes = new();
    private static BlockingCollection<byte[]>? _chunks;
    private static Thread? _thread;
    private static bool _eof;
    private static byte[]? _originalMode;

    public static bool Active => _thread is not null;

    /// <summary>
    /// Takes ownership of the terminal: raw mode is applied directly via
    /// termios (ICANON and ECHO off, ISIG kept so Ctrl+C still interrupts),
    /// then a single reader thread owns stdin. .NET's Console.ReadKey keeps
    /// an internal CSI parser that silently swallows arrow keys and OSC
    /// sequences once invoked — never call it after this.
    /// </summary>
    public static void Activate()
    {
        lock (Sync)
        {
            if (_thread is not null)
                return;

            if (!TrySetRawMode(out _originalMode))
                return; // not a terminal: fall back to Console paths

            _chunks = new BlockingCollection<byte[]>(boundedCapacity: 64);
            _thread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "reptar-terminal-input"
            };
            _thread.Start();
        }
    }

    /// <summary>Restores the terminal mode captured by Activate.</summary>
    public static void Deactivate()
    {
        lock (Sync)
        {
            if (_originalMode is { } mode)
            {
                RestoreMode(mode);
                _originalMode = null;
            }
        }
    }

    // ---- termios via P/Invoke (Native AOT safe, cross-platform) ----

    private static bool TrySetRawMode(out byte[]? original)
    {
        original = null;

        try
        {
            if (OperatingSystem.IsWindows())
                return false;

            var buffer = new byte[128];
            if (tcgetattr(0, buffer) != 0)
                return false;

            original = (byte[])buffer.Clone();

            var raw = (byte[])buffer.Clone();
            cfmakeraw(raw);

            return tcsetattr(0, 0 /* TCSANOW */, raw) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RestoreMode(byte[] mode)
    {
        try
        {
            _ = tcsetattr(0, 0, mode);
        }
        catch
        {
            // Best-effort restore.
        }
    }

    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = false)]
    private static extern int tcgetattr(int fileDescriptor, [Out] byte[] termios);

    [DllImport("libc", EntryPoint = "cfmakeraw", SetLastError = false)]
    private static extern void cfmakeraw([In, Out] byte[] termios);

    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = false)]
    private static extern int tcsetattr(int fileDescriptor, int optionalActions, [In] byte[] termios);

    [DllImport("libc", EntryPoint = "read", SetLastError = false)]
    private static extern nint posix_read(int fd, [Out] byte[] buffer, nuint count);

    private static void ReadLoop()
    {
        try
        {
            var buffer = new byte[64 * 1024];

            while (true)
            {
                var count = (int)posix_read(0, buffer, (nuint)buffer.Length);
                if (count <= 0)
                {
                    _eof = true;
                    _chunks!.CompleteAdding();
                    return;
                }

                var chunk = new byte[count];
                Array.Copy(buffer, chunk, count);

                if (Environment.GetEnvironmentVariable("REPTAR_INPUT_DEBUG") is "1")
                {
                    try
                    {
                        File.AppendAllText("/tmp/reptar-input-debug.log",
                            $"chunk[{count}]: {Convert.ToHexString(chunk)}\n");
                    }
                    catch
                    {
                    }
                }

                if (!_chunks!.TryAdd(chunk))
                {
                    // Queue full (nobody is reading): block until it drains
                    // rather than dropping input.
                    _chunks.Add(chunk);
                }
            }
        }
        catch
        {
            _chunks?.CompleteAdding();
        }
    }

    /// <summary>
    /// Blocks for the next key event. Returns null on timeout or EOF.
    /// timeoutMs &lt; 0 waits forever.
    /// </summary>
    public static TermKey? ReadKey(int timeoutMs = -1)
    {
        var first = NextByte(timeoutMs);
        if (first is null)
            return null;

        var b0 = first.Value;
        return ParseKey(b0);
    }

    private static TermKey? ParseKey(byte b0)
    {
        switch (b0)
        {
            case 0x1b:
                return ParseEscape();

            case 0x0D or 0x0A:
                return new TermKey(ConsoleKey.Enter);

            case 0x09:
                return new TermKey(ConsoleKey.Tab);

            case 0x7F or 0x08:
                return new TermKey(ConsoleKey.Backspace);

            case 0x10:
                return new TermKey(ConsoleKey.P, Ctrl: true);

            case 0x16:
                return new TermKey(ConsoleKey.V, Ctrl: true);

            case 0x04:
                return new TermKey(ConsoleKey.D, Ctrl: true);

            case >= 0x01 and <= 0x1A:
                // Other control combos: report as Ctrl+letter, harmless default.
                return new TermKey((ConsoleKey)(char.ToUpperInvariant((char)(b0 + 0x40))), Ctrl: true);

            case >= 0x20:
                return ParseUtf8(b0);

            default:
                return new TermKey();
        }
    }

    private static TermKey? ParseUtf8(byte lead)
    {
        if (lead < 0x80)
            return new TermKey(Ch: (char)lead);

        var length = lead switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1
        };

        var sequence = new byte[length];
        sequence[0] = lead;

        for (var i = 1; i < length; i++)
        {
            var next = NextByte(10);
            if (next is null)
                return new TermKey(Ch: (char)lead); // truncated: best effort

            sequence[i] = next.Value;
        }

        var text = System.Text.Encoding.UTF8.GetString(sequence);
        return new TermKey(Ch: text.Length > 0 ? text[0] : '\0');
    }

    private static TermKey? ParseEscape()
    {
        var next = NextByte(25);
        if (next is null)
            return new TermKey(ConsoleKey.Escape); // lone ESC keypress

        switch (next.Value)
        {
            case (byte)'[':
                return ParseCsi();

            case (byte)']':
                return ParseOsc();

            case (byte)'O':
                // SS3 arrows (rare): ESC O A/B/C/D/H/F
                var ss3 = NextByte(25);
                return ss3 is null
                    ? new TermKey(ConsoleKey.Escape)
                    : ss3.Value switch
                    {
                        (byte)'A' => new TermKey(ConsoleKey.UpArrow),
                        (byte)'B' => new TermKey(ConsoleKey.DownArrow),
                        (byte)'C' => new TermKey(ConsoleKey.RightArrow),
                        (byte)'D' => new TermKey(ConsoleKey.LeftArrow),
                        (byte)'H' => new TermKey(ConsoleKey.Home),
                        (byte)'F' => new TermKey(ConsoleKey.End),
                        _ => new TermKey()
                    };

            default:
                // Alt+key: drop the ESC, deliver the plain key.
                return ParseKey(next.Value);
        }
    }

    private static TermKey? ParseCsi()
    {
        var parameters = new System.Text.StringBuilder();

        while (true)
        {
            var next = NextByte(50);
            if (next is null)
                return new TermKey(ConsoleKey.Escape);

            var b = next.Value;

            if (b >= 0x40 && b <= 0x7E)
            {
                return b switch
                {
                    (byte)'A' => new TermKey(ConsoleKey.UpArrow),
                    (byte)'B' => new TermKey(ConsoleKey.DownArrow),
                    (byte)'C' => new TermKey(ConsoleKey.RightArrow),
                    (byte)'D' => new TermKey(ConsoleKey.LeftArrow),
                    (byte)'H' => new TermKey(ConsoleKey.Home),
                    (byte)'F' => new TermKey(ConsoleKey.End),
                    (byte)'~' => ParseTilde(parameters.ToString()),
                    // CSI Z is Shift+Tab (back-tab). pi binds it to the thinking-level cycle.
                    (byte)'Z' => new TermKey(ConsoleKey.Tab, Shift: true),
                    (byte)'M' or (byte)'m' => ParseSgrMouse(b, parameters.ToString()),
                    _ => new TermKey() // unknown CSI: consumed, ignored
                };
            }

            parameters.Append((char)b);
        }
    }

    /// <summary>
    /// Parses an SGR mouse report: <c>CSI &lt; button ; x ; y M</c> for press/move and
    /// <c>m</c> for release.
    /// </summary>
    /// <remarks>
    /// SGR (mode 1006) rather than the legacy X10 encoding, because X10 cannot express
    /// coordinates past column 223 and gives no way to distinguish a release from a press.
    /// </remarks>
    private static TermKey ParseSgrMouse(byte terminator, string parameters)
    {
        var parts = parameters.TrimStart('<').Split(';');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out var button) ||
            !int.TryParse(parts[1], out var x) ||
            !int.TryParse(parts[2], out var y))
        {
            return new TermKey();
        }

        var ctrl = (button & 16) != 0;
        var shift = (button & 4) != 0;
        var alt = (button & 8) != 0;
        var motion = (button & 32) != 0;
        var baseButton = button & 0b1100_0011;

        var kind = baseButton switch
        {
            64 => TermMouseKind.WheelUp,
            65 => TermMouseKind.WheelDown,
            _ when motion => TermMouseKind.Move,
            _ when terminator == (byte)'m' => TermMouseKind.Release,
            _ => TermMouseKind.Press
        };

        return new TermKey(Mouse: new TermMouseEvent(kind, x, y, ctrl, alt, shift));
    }

    private static TermKey? ParseTilde(string parameters)
    {
        var code = parameters.Split(';')[0];

        if (code == "200")
            return CollectBracketedPaste();

        return code switch
        {
            "1" or "7" => new TermKey(ConsoleKey.Home),
            "2" => new TermKey(ConsoleKey.None),        // Insert: ignore
            "3" => new TermKey(ConsoleKey.Delete),
            "4" or "8" => new TermKey(ConsoleKey.End),
            "5" => new TermKey(ConsoleKey.PageUp),
            "6" => new TermKey(ConsoleKey.PageDown),
            _ => new TermKey()
        };
    }

    private static TermKey? CollectBracketedPaste()
    {
        var bytes = new List<byte>(256);

        while (true)
        {
            var next = NextByte(-1);
            if (next is null)
                return null; // EOF mid-paste

            var b = next.Value;

            if (b == 0x1b)
            {
                // Expect [201~
                if (IsSequence("201~"))
                    break;

                bytes.Add(b);
                continue;
            }

            if (b is 0x0D or 0x0A)
                continue; // newlines inside pastes become spaces

            bytes.Add(b);
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Replace('\r', ' ').Replace('\n', ' ');

        // Huge pastes are truncated: an agent prompt is not a file drop.
        if (text.Length > 8_000)
            text = text[..8_000] + "…";

        return new TermKey(PastedText: text);
    }

    private static TermKey? ParseOsc()
    {
        var payload = new System.Text.StringBuilder();

        while (true)
        {
            var next = NextByte(150);
            if (next is null)
                return new TermKey(); // unterminated OSC: ignore

            var b = next.Value;

            if (b == 0x07)
                break;

            if (b == 0x1b)
            {
                var terminator = NextByte(25);
                if (terminator is (byte)'\\')
                    break;

                if (terminator is null)
                    return new TermKey();
            }

            payload.Append((char)b);
        }

        var value = payload.ToString();

        if (!value.StartsWith("52;c;", StringComparison.Ordinal))
            return new TermKey(); // some other OSC: ignore

        var base64 = value[5..];
        if (base64.Length == 0 || base64 == "?")
            return new TermKey();

        try
        {
            var decoded = Convert.FromBase64String(base64);
            var mime = TerminalEditor.SniffImageMime(decoded);

            if (mime is null)
                return new TermKey(); // text clipboard: paste works normally

            return new TermKey(ClipboardImage: new AgentImage(mime, base64));
        }
        catch (FormatException)
        {
            return new TermKey();
        }
    }

    private static bool IsSequence(string expected)
    {
        foreach (var ch in expected)
        {
            var next = NextByte(50);
            if (next is null || next.Value != ch)
                return false;
        }

        return true;
    }

    private static byte? NextByte(int timeoutMs)
    {
        lock (Sync)
        {
            if (Bytes.Count > 0)
                return Bytes.Dequeue();

            if (_eof || !_chunks!.TryTake(out var chunk, timeoutMs < 0 ? -1 : timeoutMs))
                return null;

            for (var i = 1; i < chunk.Length; i++)
                Bytes.Enqueue(chunk[i]);

            return chunk.Length == 0 ? null : chunk[0];
        }
    }
}
#endif
