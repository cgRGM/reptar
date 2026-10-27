#if !NETWASM
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Reptar;

/// <summary>
/// One message captured by <see cref="ConsoleTransport"/> for the
/// <c>/_mail</c> dev preview. Data, not display: the JSON endpoint serializes
/// these fields and extracts link/OTP hints server-side.
/// </summary>
public sealed record MailSnapshot(
    string Id,
    string From,
    string To,
    string Subject,
    string? Html,
    string? Text,
    string CreatedAt);

/// <summary>
/// Development transport: prints a compact preview to the console and keeps
/// the last N messages in a ring buffer for <c>GET /_mail</c> and
/// <c>GET /_mail/json</c>. Never sends real email — the local-Dockerless
/// counterpart of <c>wrangler dev</c>'s simulated send_email binding.
/// </summary>
public sealed class ConsoleTransport(int capacity = 50) : IMailClient
{
    private readonly MailSnapshot?[] _slots = new MailSnapshot?[Math.Max(1, capacity)];
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly object _gate = new();
    private int _head;

    public string Name => "console";

    public Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var id = Ids.New("mail");
        var snapshot = new MailSnapshot(
            id,
            message.From ?? "",
            string.Join(", ", message.To),
            message.Subject,
            message.Html,
            message.Text,
            DateTimeOffset.UtcNow.ToString("O"));

        lock (_gate)
        {
            _slots[_head] = snapshot;
            _head = (_head + 1) % _capacity;
        }

        Print(snapshot);

        return Task.FromResult(MailResult.Sent(id));
    }

    /// <summary>Snapshots newest first.</summary>
    public IReadOnlyList<MailSnapshot> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<MailSnapshot>(_capacity);

            for (var offset = 1; offset <= _capacity; offset++)
            {
                var slot = _slots[(_head - offset + _capacity) % _capacity];

                if (slot is not null)
                    result.Add(slot);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_slots);
            _head = 0;
        }
    }

    private static void Print(MailSnapshot snapshot)
    {
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────────────────────────────");
        Console.WriteLine($"│ ✉ {snapshot.Subject}");
        Console.WriteLine($"│ From: {snapshot.From}  →  {snapshot.To}");
        Console.WriteLine("│ Preview: GET /_mail  ·  GET /_mail/json  (development only)");
        Console.WriteLine("└──────────────────────────────────────────────────────────────");
    }
}

public static partial class Mail
{
    /// <summary>
    /// Development mail transport with the built-in <c>/_mail</c> preview.
    /// Register the preview routes with <c>app.UseMailPreview()</c>. Never
    /// deploy this to production: nothing is actually delivered.
    /// </summary>
    public static IMailClient Console() => new ConsoleTransport();
}

public static class MailPreviewExtensions
{
    /// <summary>
    /// Serves the self-contained mail preview next to Scalar at
    /// <c>/reference</c>: <c>GET /_mail</c> (HTML console),
    /// <c>GET /_mail/json</c> (agent/CI-friendly JSON with pre-extracted link
    /// and OTP hints), and <c>DELETE /_mail</c> (clear the buffer). Requires
    /// <c>Mail = Mail.Console()</c>.
    /// </summary>
    public static ReptarApp UseMailPreview(this ReptarApp app, string path = "/_mail")
    {
        if (app.Config.Mail is not ConsoleTransport console)
        {
            Console.WriteLine(
                "WARN UseMailPreview(): ReptarConfig.Mail is not Mail.Console(); preview routes were not registered.");
            return app;
        }

        app.Get(path, ctx => ctx.Html(RenderMailHtml(path, console)));
        app.Get(path + "/json", ctx => ctx.JsonRaw(RenderMailJson(console)));
        app.Delete(path, ctx =>
        {
            console.Clear();
            return ctx.Status(204);
        });

        Console.WriteLine($"Reptar mail preview: GET {path} · GET {path}/json · DELETE {path} (development only)");

        return app;
    }

    private static string RenderMailJson(ConsoleTransport console)
    {
        var snapshots = console.Snapshot();
        var buffer = new ArrayBufferWriter<byte>(4096);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", snapshots.Count);
            writer.WriteStartArray("messages");

            foreach (var snapshot in snapshots)
            {
                writer.WriteStartObject();
                writer.WriteString("id", snapshot.Id);
                writer.WriteString("from", snapshot.From);
                writer.WriteString("to", snapshot.To);
                writer.WriteString("subject", snapshot.Subject);

                if (snapshot.Html is not null)
                    writer.WriteString("html", snapshot.Html);

                if (snapshot.Text is not null)
                    writer.WriteString("text", snapshot.Text);

                writer.WriteString("createdAt", snapshot.CreatedAt);
                WriteStringArray(writer, "links", MailScanners.ExtractLinks(snapshot.Html, snapshot.Text));
                WriteStringArray(writer, "otps", MailScanners.ExtractOtps(snapshot.Text, snapshot.Html));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteStringArray(Utf8JsonWriter writer, string name, List<string> values)
        {
            writer.WriteStartArray(name);

            foreach (var value in values)
                writer.WriteStringValue(value);

            writer.WriteEndArray();
        }
    }

    private static string RenderMailHtml(string jsonPath, ConsoleTransport console)
    {
        var snapshots = console.Snapshot();
        var rows = new StringBuilder();

        if (snapshots.Count == 0)
        {
            rows.Append(
                """
                <p class="empty">No mail captured yet. Trigger a signup, password reset, or any
                mail-sending route, then reload this page.</p>
                """);
        }

        foreach (var snapshot in snapshots)
        {
            var links = MailScanners.ExtractLinks(snapshot.Html, snapshot.Text);
            var otps = MailScanners.ExtractOtps(snapshot.Text, snapshot.Html);

            rows.Append($"""
                <article>
                  <header>
                    <h3>{Escape(snapshot.Subject)}</h3>
                    <p class="meta">{Escape(snapshot.From)} → {Escape(snapshot.To)}</p>
                    <p class="meta">{Escape(snapshot.CreatedAt)}</p>
                  </header>
                """);

            if (links.Count > 0)
            {
                rows.Append("<ul class='hints'>");

                foreach (var link in links)
                    rows.Append($"<li><a href='{Escape(link)}'>{Escape(link)}</a></li>");

                rows.Append("</ul>");
            }

            if (otps.Count > 0)
                rows.Append($"<p class='otp'>Code: <code>{Escape(otps[0])}</code></p>");

            if (snapshot.Text is not null)
                rows.Append($"<details><summary>text</summary><pre>{Escape(snapshot.Text)}</pre></details>");

            if (snapshot.Html is not null)
                rows.Append($"<details><summary>html</summary><pre>{Escape(snapshot.Html)}</pre></details>");

            rows.Append("</article>");
        }

        return $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <title>Reptar Mail</title>
                <style>
                  :root { color-scheme: dark; }
                  body { margin: 0; padding: 2rem; background: #0a0a0f; color: #e6e6ef;
                         font: 14px/1.5 ui-sans-serif, system-ui, sans-serif; }
                  h1 { font-size: 1.25rem; margin: 0 0 .25rem; }
                  a { color: #7dd3fc; }
                  .meta, .empty { color: #8b8ba3; }
                  .meta { margin: .1rem 0; font-size: .8rem; }
                  article { border: 1px solid #26263a; border-radius: 8px; padding: 1rem;
                            margin: 1rem 0; background: #12121c; }
                  h3 { margin: 0 0 .25rem; font-size: 1rem; }
                  ul.hints { margin: .5rem 0; padding-left: 1.25rem; word-break: break-all; }
                  .otp code { background: #1e1e2e; padding: .15rem .4rem; border-radius: 4px;
                              font-size: 1.05rem; letter-spacing: .15rem; }
                  details { margin-top: .5rem; }
                  summary { cursor: pointer; color: #8b8ba3; }
                  pre { white-space: pre-wrap; word-break: break-all; background: #08080d;
                        border: 1px solid #26263a; border-radius: 6px; padding: .75rem; }
                </style>
              </head>
              <body>
                <h1>✉ Reptar Mail</h1>
                <p class="meta">{{snapshots.Count}} captured · <a href="{{jsonPath}}">{{jsonPath}} (JSON for agents)</a> · nothing is actually delivered</p>
                {{rows}}
              </body>
            </html>
            """;
    }

    private static string Escape(string? text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
#endif
