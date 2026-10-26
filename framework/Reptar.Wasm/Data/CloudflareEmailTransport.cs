using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Reptar;

/// <summary>
/// Cloudflare Email Sending binding bridge. Serializes the structured
/// EmailMessageBuilder payload (from/to/subject/html/text/cc/bcc/replyTo/
/// attachments/headers) and sends it through the host's
/// <c>__REPTAR_SEND_EMAIL</c>, which calls <c>env[binding].send(payload)</c>.
/// Errors come back with the platform code (E_SENDER_NOT_VERIFIED,
/// E_RATE_LIMIT_EXCEEDED, ...).
/// </summary>
public sealed class CloudflareEmailTransport(string binding = "SEND_EMAIL", string? defaultFrom = null) : IMailClient
{
    public string Name => "cloudflare";

    [JSImport("sendEmail", "app.host")]
    private static extern Task<string> SendJs(string binding, string messageJson);

    public async Task<MailResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!MailMessages.TryPrepare(message, defaultFrom, out var from, out var failure))
            return failure;

        var payload = MailMessages.ToCloudflarePayload(message, from);
        var resultJson = await SendJs(
            binding,
            JsonSerializer.Serialize(payload, MailJsonContext.Default.CloudflareSendRequest));

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("messageId", out var messageId) &&
            messageId.GetString() is { Length: > 0 } id)
        {
            return MailResult.Sent(id);
        }

        if (root.TryGetProperty("code", out var code))
        {
            var text = root.TryGetProperty("message", out var reason)
                ? reason.GetString()
                : null;

            return MailResult.Failed($"{code.GetString()}: {text ?? "Cloudflare email send failed."}");
        }

        return MailResult.Failed("Unexpected response from the email host bridge.");
    }
}

public static partial class Mail
{
    /// <summary>
    /// Cloudflare Workers Email Sending binding (<c>env.SEND_EMAIL</c>).
    /// Requires a <c>send_email</c> binding in wrangler.jsonc and host wiring:
    /// <c>bindings: (env) =&gt; ({ email: { SEND_EMAIL: env.SEND_EMAIL } })</c>.
    /// Recipients can be restricted per binding with
    /// <c>allowed_destination_addresses</c>; see the Cloudflare send bindings docs.
    /// </summary>
    public static IMailClient Cloudflare(string binding = "SEND_EMAIL", string? defaultFrom = null) =>
        new CloudflareEmailTransport(binding, defaultFrom);
}
