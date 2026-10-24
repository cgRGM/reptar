using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.Realtime;

public static class ActionCableProtocol
{
    public const string WelcomeType = "welcome";
    public const string PingType = "ping";
    public const string ConfirmSubscriptionType = "confirm_subscription";
    public const string RejectSubscriptionType = "reject_subscription";
    public const string DisconnectType = "disconnect";

    public const string CommandSubscribe = "subscribe";
    public const string CommandUnsubscribe = "unsubscribe";
    public const string CommandMessage = "message";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
#if !NETWASM
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
#endif
    };

    public static string EscapeString(string value)
    {
        if (value is null) return "\"\"";
        var sb = new System.Text.StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Welcome(string? sessionId = null) =>
        sessionId is null
            ? "{\"type\":\"welcome\"}"
            : $"{{\"type\":\"welcome\",\"sid\":\"{sessionId}\"}}";

    public static string Ping(long? timestamp = null) =>
        $"{{\"type\":\"ping\",\"message\":{timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}";

    public static string ConfirmSubscription(string identifier) =>
        $"{{\"type\":\"confirm_subscription\",\"identifier\":{EscapeString(identifier)}}}";

    public static string RejectSubscription(string identifier) =>
        $"{{\"type\":\"reject_subscription\",\"identifier\":{EscapeString(identifier)}}}";

    public static string Disconnect(string reason = "unauthorized", bool reconnect = false) =>
        $"{{\"type\":\"disconnect\",\"reason\":{EscapeString(reason)},\"reconnect\":{(reconnect ? "true" : "false")}}}";

    public static string BroadcastRaw(string identifier, string rawMessage) =>
        $"{{\"identifier\":{EscapeString(identifier)},\"message\":{rawMessage}}}";

    public static string Broadcast(string identifier, object message, IJsonCodec? json = null)
    {
        var serializedMessage = message is string s && (s.StartsWith("{") || s.StartsWith("<"))
            ? EscapeString(s)
            : (json?.Serialize(message) ?? (message is string str ? EscapeString(str) : EscapeString(message.ToString() ?? "")));

        return $"{{\"identifier\":{EscapeString(identifier)},\"message\":{serializedMessage}}}";
    }

    public static ActionCableCommand? ParseClientMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("command", out var cmdProp))
            {
                return null;
            }

            var command = cmdProp.GetString() ?? "";
            var identifier = root.TryGetProperty("identifier", out var idProp)
                ? idProp.GetString() ?? ""
                : "";

            var data = root.TryGetProperty("data", out var dataProp)
                ? dataProp.GetString()
                : null;

            string? channel = null;
            string? streamName = null;
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(identifier))
            {
                try
                {
                    using var idDoc = JsonDocument.Parse(identifier);
                    foreach (var prop in idDoc.RootElement.EnumerateObject())
                    {
                        if (prop.NameEquals("channel"))
                        {
                            channel = prop.Value.GetString();
                        }
                        else if (prop.NameEquals("signed_stream_name") || prop.NameEquals("stream_name"))
                        {
                            streamName = prop.Value.GetString();
                        }
                        else if (prop.NameEquals("room_id"))
                        {
                            parameters["room_id"] = prop.Value.ValueKind == JsonValueKind.Number
                                ? prop.Value.GetInt64()
                                : prop.Value.GetString();
                            streamName ??= $"room_{parameters["room_id"]}";
                        }
                        else
                        {
                            parameters[prop.Name] = prop.Value.GetString();
                        }
                    }
                }
                catch
                {
                    // Fall back to identifier as stream name if not valid JSON
                    streamName = identifier;
                }
            }

            streamName ??= !string.IsNullOrEmpty(channel) ? channel : identifier;

            return new ActionCableCommand(
                Command: command,
                Identifier: identifier,
                Data: data,
                Channel: channel,
                StreamName: streamName,
                Parameters: parameters
            );
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ActionCableCommand(
    string Command,
    string Identifier,
    string? Data,
    string? Channel,
    string StreamName,
    IReadOnlyDictionary<string, object?> Parameters
);
