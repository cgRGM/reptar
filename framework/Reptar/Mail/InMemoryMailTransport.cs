namespace Reptar;

/// <summary>
/// Records every send in memory — the automatic transport for
/// <c>app.CreateTestClient()</c>. Background sends registered through
/// <see cref="MailSender.SendInBackground"/> are captured as queued at
/// registration time, so assertions are deterministic without waiting for
/// the background drain.
/// </summary>
public sealed class InMemoryMailTransport : IMailClient
{
    /// <summary>One recorded send: the message, its result, and a capture timestamp.</summary>
    public sealed record SentMail(MailMessage Message, MailResult Result, string CreatedAt);

    private readonly object _gate = new();
    private readonly List<SentMail> _sent = [];
    private readonly List<MailMessage> _queued = [];

    public string Name => "memory";

    public Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var id = Ids.New("mail");

        lock (_gate)
        {
            _sent.Add(new SentMail(message, MailResult.Sent(id), DateTimeOffset.UtcNow.ToString("O")));
        }

        return Task.FromResult(MailResult.Sent(id));
    }

    internal void RecordQueued(MailMessage message)
    {
        lock (_gate)
        {
            _queued.Add(message);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sent.Clear();
            _queued.Clear();
        }
    }

    /// <summary>Snapshot of executed sends, oldest first.</summary>
    public IReadOnlyList<SentMail> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    /// <summary>Snapshot of messages registered for background sending, oldest first.</summary>
    public IReadOnlyList<MailMessage> Queued
    {
        get
        {
            lock (_gate)
            {
                return _queued.ToArray();
            }
        }
    }
}

/// <summary>
/// Ergonomic assertions over the <see cref="InMemoryMailTransport"/> installed
/// by <c>app.CreateTestClient()</c> (Swoosh <c>assert_email_sent</c> style).
/// When an explicit non-memory transport is configured, <see cref="Capturing"/>
/// is false and the lists stay empty instead of lying.
/// </summary>
public sealed class TestMailbox(InMemoryMailTransport? sink)
{
    /// <summary>False when an explicit non-memory transport was configured.</summary>
    public bool Capturing => sink is not null;

    /// <summary>Messages actually sent, oldest first.</summary>
    public IReadOnlyList<MailMessage> Sent =>
        sink?.Sent.Select(record => record.Message).ToArray() ?? [];

    /// <summary>Messages registered for background sending, oldest first.</summary>
    public IReadOnlyList<MailMessage> Queued => sink?.Queued ?? [];

    /// <summary>The most recent message sent to the address; throws when none.</summary>
    public MailMessage LastSentTo(string address)
    {
        var sent = Sent;

        for (var i = sent.Count - 1; i >= 0; i--)
        {
            if (sent[i].To.Contains(address, StringComparer.OrdinalIgnoreCase))
                return sent[i];
        }

        throw new InvalidOperationException(
            Capturing
                ? $"No mail was sent to '{address}'."
                : "The test client is not capturing mail: ReptarConfig.Mail is an explicit non-memory transport.");
    }

    /// <summary>First message matching the predicate, oldest first; null when none.</summary>
    public MailMessage? First(Func<MailMessage, bool> predicate) =>
        Sent.FirstOrDefault(predicate);

    /// <summary>
    /// First link (from newest to oldest mail) that starts with
    /// <paramref name="prefix"/> — the verification/magic-link helper.
    /// </summary>
    public string FirstLinkMatching(string prefix)
    {
        var sent = Sent;

        for (var i = sent.Count - 1; i >= 0; i--)
        {
            foreach (var link in MailScanners.ExtractLinks(sent[i].Html, sent[i].Text))
            {
                if (link.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return link;
            }
        }

        throw new InvalidOperationException($"No sent mail contains a link starting with '{prefix}'.");
    }

    /// <summary>First OTP code with the given digit count (newest mail first).</summary>
    public string FirstOtp(int digits = 6)
    {
        var sent = Sent;

        for (var i = sent.Count - 1; i >= 0; i--)
        {
            var codes = MailScanners.ExtractOtps(sent[i].Text, sent[i].Html, digits);

            if (codes.Count > 0)
                return codes[0];
        }

        throw new InvalidOperationException($"No sent mail contains a {digits}-digit code.");
    }

    public void Clear() => sink?.Clear();
}

/// <summary>
/// Manual link and OTP extraction — no Regex anywhere (the Binaryen optimizer
/// rejects it on the NetWasm path, and the scanner is shared with the
/// native-only dev UI).
/// </summary>
public static class MailScanners
{
    /// <summary>Distinct http(s) links found in the HTML, then the text, newest-relevant order preserved.</summary>
    public static List<string> ExtractLinks(string? html, string? text, int max = 25)
    {
        var links = new List<string>();

        if (max <= 0)
            return links;

        AppendLinks(html, links, max);

        if (links.Count < max)
            AppendLinks(text, links, max);

        return links;
    }

    /// <summary>Distinct numeric codes with exactly <paramref name="digits"/> digits (e.g. 6-digit OTPs).</summary>
    public static List<string> ExtractOtps(string? text, string? html, int digits = 6, int max = 5)
    {
        var codes = new List<string>();

        if (digits <= 0)
            return codes;

        AppendOtps(text, codes, digits, max);

        if (codes.Count < max)
            AppendOtps(html, codes, digits, max);

        return codes;
    }

    private static void AppendLinks(string? source, List<string> links, int max)
    {
        if (string.IsNullOrEmpty(source))
            return;

        var index = 0;

        while (links.Count < max && index < source.Length)
        {
            var start = source.IndexOf("http", index, StringComparison.Ordinal);

            if (start < 0)
                return;

            var after = start + 4;

            if (after >= source.Length)
                return;

            // "http://" or "https://" — anything else is not a URL start.
            if (source[after] == 's')
                after++;

            if (after + 2 >= source.Length || source[after] != ':' || source[after + 1] != '/' || source[after + 2] != '/')
            {
                index = start + 4;
                continue;
            }

            var end = after + 3;

            while (end < source.Length && !IsLinkBoundary(source[end]))
                end++;

            var link = source[start..end].TrimEnd('.', ',', ';', ':', '!', '?');

            if (link.Length > 0 && !links.Contains(link))
                links.Add(link);

            index = end;
        }
    }

    private static bool IsLinkBoundary(char c) =>
        char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '`' or ')' or ']';

    private static void AppendOtps(string? source, List<string> codes, int digits, int max)
    {
        if (string.IsNullOrEmpty(source))
            return;

        for (var i = 0; i <= source.Length - digits && codes.Count < max; i++)
        {
            if (!char.IsAsciiDigit(source[i]))
                continue;

            var run = 0;

            while (i + run < source.Length && char.IsAsciiDigit(source[i + run]))
                run++;

            // Exactly `digits` long — longer digit runs are not OTPs.
            if (run == digits)
            {
                var code = source.Substring(i, digits);

                if (!codes.Contains(code))
                    codes.Add(code);
            }

            i += run - 1;
        }
    }
}
