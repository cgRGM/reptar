using System.Text.Json;
using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class WasmWebSocketTransport : IWebSocketTransport
{
    [JSImport("wsCommand", "app.host")]
    private static extern void Command(string sessionId, string json);

    public void Send(string sessionId, string message) =>
        Command(
            sessionId,
            $"{{\"type\":\"send\",\"message\":{JsonSerializer.Serialize(message, ReptarJson.Options)}}}");

    public void Close(string sessionId, int code, string reason) =>
        Command(
            sessionId,
            $"{{\"type\":\"close\",\"code\":{code},\"reason\":{JsonSerializer.Serialize(reason, ReptarJson.Options)}}}");
}
