using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Reptar.Hosting;

public static class KestrelHost
{
    public static async Task RunAsync(this ReptarApp app, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort)
            ? configuredPort
            : 8787;

        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

        var web = builder.Build();

        var kestrelTransport = new KestrelWebSocketTransport();
        app.SetWebSocketTransport(kestrelTransport);

        web.UseWebSockets();

        web.Use(async (HttpContext http, RequestDelegate _) =>
        {
            var path = http.Request.Path.Value ?? "/";
            if (http.WebSockets.IsWebSocketRequest && app.HasWebSocketHandler(path))
            {
                string? subProtocol = null;
                if (http.WebSockets.WebSocketRequestedProtocols.Contains("actioncable-v1-json"))
                {
                    subProtocol = "actioncable-v1-json";
                }

                using var socket = await http.WebSockets.AcceptWebSocketAsync(subProtocol);
                var sessionId = Guid.NewGuid().ToString("N");
                kestrelTransport.Register(sessionId, socket);

                var wsRequest = await ToReptarRequest(http);
                await app.HandleWebSocketOpenAsync(sessionId, wsRequest);

                var buffer = new byte[8192];
                try
                {
                    while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        using var ms = new MemoryStream();
                        System.Net.WebSockets.WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), http.RequestAborted);
                            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            break;
                        }

                        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                        await app.HandleWebSocketMessageAsync(sessionId, text);
                    }
                }
                catch
                {
                    // Client disconnected or socket aborted
                }
                finally
                {
                    await app.HandleWebSocketCloseAsync(sessionId);
                    kestrelTransport.Unregister(sessionId);
                }

                return;
            }

            var request = await ToReptarRequest(http);
            var response = await app.HandleAsync(request);
            await WriteResponse(http, response);
        });

        await web.RunAsync();
    }

    internal sealed class KestrelWebSocketTransport : IWebSocketTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _sockets = new();

        public void Register(string sessionId, System.Net.WebSockets.WebSocket socket) =>
            _sockets[sessionId] = socket;

        public void Unregister(string sessionId) =>
            _sockets.TryRemove(sessionId, out _);

        public void Send(string sessionId, string message)
        {
            if (_sockets.TryGetValue(sessionId, out var socket) && socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                _ = socket.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public void Close(string sessionId, int code, string reason)
        {
            if (_sockets.TryRemove(sessionId, out var socket) && socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                _ = socket.CloseAsync((System.Net.WebSockets.WebSocketCloseStatus)code, reason, CancellationToken.None);
            }
        }
    }

    private static async Task<ReptarRequest> ToReptarRequest(HttpContext http)
    {
        byte[]? body = null;

        if (http.Request.ContentLength is > 0)
        {
            using var memory = new MemoryStream();
            await http.Request.Body.CopyToAsync(memory);
            body = memory.ToArray();
        }

        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (http.Request.Headers.TryGetValue("Cookie", out var cookieHeader))
        {
            foreach (var segment in cookieHeader.ToString().Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = segment.IndexOf('=');

                if (separator > 0)
                    cookies[segment[..separator]] = segment[(separator + 1)..];
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in http.Request.Headers)
            headers[key] = value.ToString();

        return new ReptarRequest
        {
            Method = http.Request.Method,
            Path = http.Request.Path.Value ?? "/",
            Query = http.Request.QueryString.HasValue
                ? http.Request.QueryString.Value!.TrimStart('?')
                : "",
            Headers = headers,
            Cookies = cookies,
            Body = body,
        };
    }

    private static async Task WriteResponse(HttpContext http, ReptarResponse response)
    {
        http.Response.StatusCode = response.Status;

        foreach (var (key, value) in response.Headers)
            http.Response.Headers[key] = value;

        if (response.ContentType is not null)
            http.Response.ContentType = response.ContentType;

        foreach (var cookie in response.SetCookies)
            http.Response.Headers.Append("Set-Cookie", cookie);

        if (response.BodyStream is not null)
        {
            await foreach (var chunk in response.BodyStream(http.RequestAborted))
            {
                await http.Response.Body.WriteAsync(chunk, http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }

            return;
        }

        if (response.Body is { Length: > 0 } body)
            await http.Response.Body.WriteAsync(body);
    }
}
