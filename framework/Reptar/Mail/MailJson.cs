using System.Text.Json.Serialization;

namespace Reptar;

// Provider wire DTOs. Property names are explicit per provider contract:
// Resend/Cloudflare use camelCase (with a few snake_case exceptions like
// reply_to), Postmark and MailPit use PascalCase. All serialization goes
// through MailJsonContext (source-generated, AOT/NetWasm-safe) so no
// reflection metadata is ever required for mail.

/// <summary>Resend <c>POST /emails</c> request payload.</summary>
public sealed record ResendSendRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string[] To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("cc")] string[]? Cc,
    [property: JsonPropertyName("bcc")] string[]? Bcc,
    [property: JsonPropertyName("reply_to")] string[]? ReplyTo,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("attachments")] ResendAttachment[]? Attachments,
    // NetWasm's System.Text.Json source generator has no metadata factory for
    // IReadOnlyDictionary; the wire DTO uses the concrete dictionary type.
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers);

public sealed record ResendAttachment(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("content_type")] string? ContentType,
    [property: JsonPropertyName("content_id")] string? ContentId);

public sealed record ResendSendResponse([property: JsonPropertyName("id")] string? Id);

/// <summary>Postmark <c>POST /email</c> request payload (PascalCase).</summary>
public sealed record PostmarkSendRequest(
    string From,
    string To,
    string Subject,
    string? HtmlBody,
    string? TextBody,
    string? Cc,
    string? Bcc,
    string? ReplyTo,
    PostmarkHeader[]? Headers,
    Dictionary<string, string>? Metadata,
    PostmarkAttachment[]? Attachments,
    string? MessageStream);

public sealed record PostmarkHeader(string Name, string Value);

public sealed record PostmarkAttachment(
    string Name,
    string Content,
    string? ContentType,
    string? ContentID);

/// <summary>Postmark response: ErrorCode 0 means accepted.</summary>
public sealed record PostmarkSendResponse(
    string? To,
    string? SubmittedAt,
    string? MessageID,
    int ErrorCode,
    string? Message);

/// <summary>MailPit <c>POST /api/v1/send</c> request payload.</summary>
public sealed record MailPitAddress(string Email, string? Name);

public sealed record MailPitSendRequest(
    MailPitAddress From,
    MailPitAddress[] To,
    MailPitAddress[]? Cc,
    string[]? Bcc,
    MailPitAddress[]? ReplyTo,
    string Subject,
    string? Text,
    [property: JsonPropertyName("HTML")] string? Html,
    MailPitAttachment[]? Attachments);

public sealed record MailPitAttachment(
    [property: JsonPropertyName("Content")] string Content,
    [property: JsonPropertyName("Filename")] string Filename);

/// <summary>
/// Cloudflare Email Sending structured payload — the EmailMessageBuilder
/// shape accepted by <c>env[binding].send(...)</c>. Also the neutral JSON
/// payload for the generic HTTP transport.
/// </summary>
public sealed record CloudflareSendRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string[] To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("cc")] string[]? Cc,
    [property: JsonPropertyName("bcc")] string[]? Bcc,
    [property: JsonPropertyName("replyTo")] string? ReplyTo,
    [property: JsonPropertyName("attachments")] CloudflareEmailAttachment[]? Attachments,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers);

public sealed record CloudflareEmailAttachment(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("contentId")] string? ContentId);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResendSendRequest))]
[JsonSerializable(typeof(ResendSendResponse))]
[JsonSerializable(typeof(PostmarkSendRequest))]
[JsonSerializable(typeof(PostmarkSendResponse))]
[JsonSerializable(typeof(MailPitSendRequest))]
[JsonSerializable(typeof(CloudflareSendRequest))]
public sealed partial class MailJsonContext : JsonSerializerContext;
