using System.Net;

namespace Reptar.Realtime;

public sealed record CampfireMessage(
    long Id,
    long RoomId,
    long UserId,
    string UserName,
    string Body,
    string CreatedAt,
    string? AvatarUrl = null,
    string? AttachmentUrl = null
);

public sealed record CampfireTypingStatus(
    long RoomId,
    long UserId,
    string UserName,
    bool IsTyping
);

public sealed class CampfireRealtimeService
{
    private readonly ActionCableServer _server;

    public CampfireRealtimeService(ActionCableServer server)
    {
        _server = server;
    }

    public ActionCableServer Server => _server;

    public static string RoomStream(object roomId) => $"room_{roomId}";
    public static string UserStream(object userId) => $"user_{userId}";
    public static string SystemStream => "campfire_system";

    public async Task<int> BroadcastMessageCreatedAsync(CampfireMessage message)
    {
        var stream = RoomStream(message.RoomId);
        var messageHtml = RenderMessageHtml(message);

        // Broadcast Turbo Stream DOM update to web browsers
        var turboCount = await _server.BroadcastTurboStreamAsync(
            streamName: stream,
            action: "append",
            target: "messages",
            htmlContent: messageHtml
        );

        // Also broadcast JSON payload for native/API clients & benchmark verification suites
        await _server.BroadcastAsync(stream, new CampfireMessageEvent("message_created", message));

        return turboCount;
    }

    public async Task<int> BroadcastMessageUpdatedAsync(CampfireMessage message)
    {
        var stream = RoomStream(message.RoomId);
        var messageHtml = RenderMessageHtml(message);

        var turboCount = await _server.BroadcastTurboStreamAsync(
            streamName: stream,
            action: "replace",
            target: $"message_{message.Id}",
            htmlContent: messageHtml
        );

        await _server.BroadcastAsync(stream, new CampfireMessageEvent("message_updated", message));

        return turboCount;
    }

    public async Task<int> BroadcastMessageDeletedAsync(long roomId, long messageId)
    {
        var stream = RoomStream(roomId);

        var turboCount = await _server.BroadcastTurboStreamAsync(
            streamName: stream,
            action: "remove",
            target: $"message_{messageId}",
            htmlContent: ""
        );

        await _server.BroadcastAsync(stream, new CampfireDeletedEvent("message_deleted", roomId, messageId));

        return turboCount;
    }

    public async Task<int> BroadcastTypingAsync(CampfireTypingStatus typing)
    {
        var stream = RoomStream(typing.RoomId);

        var typingHtml = typing.IsTyping
            ? $"<div id=\"typing_indicator\" class=\"typing-active\"><span>{WebUtility.HtmlEncode(typing.UserName)} is typing...</span></div>"
            : "<div id=\"typing_indicator\" class=\"typing-idle\"></div>";

        var count = await _server.BroadcastTurboStreamAsync(
            streamName: stream,
            action: "update",
            target: "typing_indicator",
            htmlContent: typingHtml
        );

        await _server.BroadcastAsync(stream, new CampfireTypingEvent("typing", typing));
        return count;
    }

    public static string RenderMessageHtml(CampfireMessage message)
    {
        var safeBody = WebUtility.HtmlEncode(message.Body);
        var safeUserName = WebUtility.HtmlEncode(message.UserName);

        return $"""
        <article class="message" id="message_{message.Id}" data-user-id="{message.UserId}">
          <header class="message-header">
            <span class="author">{safeUserName}</span>
            <time datetime="{message.CreatedAt}">{message.CreatedAt}</time>
          </header>
          <div class="message-body">{safeBody}</div>
        </article>
        """;
    }
}

public sealed record CampfireMessageEvent(string Type, CampfireMessage Message);
public sealed record CampfireDeletedEvent(string Type, long RoomId, long MessageId);
public sealed record CampfireTypingEvent(string Type, CampfireTypingStatus Typing);
