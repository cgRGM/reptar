using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

// NetWasm entrypoint for the Reptar Rooms hub. Exports the HTTP handler and
// the WebSocket trio (open/message/close) bridged by the Worker host in
// packages/host — same hub and routes as the local file-based app.

public sealed record HostWebSocketOpen(
    Dictionary<string, string> Headers,
    string Path,
    string Query,
    string SessionId);

public sealed record HostWebSocketMessage(
    string Message,
    string SessionId);

public sealed record HostWebSocketClose(
    string SessionId);

[JsonSerializable(typeof(HostWebSocketOpen))]
[JsonSerializable(typeof(HostWebSocketMessage))]
[JsonSerializable(typeof(HostWebSocketClose))]
[JsonSerializable(typeof(RoomMessageRecord))]
[JsonSerializable(typeof(List<RoomMessageRecord>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RoomsJsonContext : JsonSerializerContext
{
}

public static partial class PageExports
{
    private static readonly ReptarApp App;

    static PageExports()
    {
        ReptarJson.RegisterContext(RoomsJsonContext.Default);
        ReptarJson.UseRegisteredContexts();
        App = BuildApp();
    }

    [JSImport("respond", "app.host")]
    private static extern void Respond(string requestId, string json);

    [JSExport("handle")]
    public static Task Handle(string requestJson) =>
        WorkerEntrypoint.HandleAsync(App, requestJson, Respond);

    [JSExport("handleWebSocketOpen")]
    public static void HandleWebSocketOpen(string openJson)
    {
        var open = JsonSerializer.Deserialize<HostWebSocketOpen>(
            openJson,
            ReptarJson.Options);

        if (open is null)
        {
            return;
        }

        _ = HandleWebSocketOpenAsync(open);
    }

    private static async Task HandleWebSocketOpenAsync(HostWebSocketOpen open)
    {
        try
        {
            await App.HandleWebSocketOpenAsync(
                open.SessionId,
                new ReptarRequest
                {
                    Method = "GET",
                    Path = open.Path,
                    Query = open.Query.TrimStart('?'),
                    Headers = open.Headers,
                    Cookies = ParseCookies(open.Headers),
                    Body = [],
                });
        }
        finally
        {
            WsMessageDone(open.SessionId);
        }
    }

    [JSImport("wsMessageDone", "app.host")]
    private static extern void WsMessageDone(string sessionId);

    [JSExport("supportsWsMessageDone")]
    public static bool SupportsWsMessageDone() => true;

    [JSExport("handleWebSocketMessage")]
    public static void HandleWebSocketMessage(string messageJson)
    {
        var message = JsonSerializer.Deserialize<HostWebSocketMessage>(
            messageJson,
            ReptarJson.Options);

        if (message is null)
        {
            return;
        }

        _ = HandleWebSocketMessageAsync(message);
    }

    private static async Task HandleWebSocketMessageAsync(HostWebSocketMessage message)
    {
        try
        {
            await App.HandleWebSocketMessageAsync(message.SessionId, message.Message);
        }
        finally
        {
            // Release the held-open event so the host knows this pipeline —
            // including any D1 binding I/O — has completed.
            WsMessageDone(message.SessionId);
        }
    }

    [JSExport("handleWebSocketClose")]
    public static void HandleWebSocketClose(string closeJson)
    {
        var close = JsonSerializer.Deserialize<HostWebSocketClose>(
            closeJson,
            ReptarJson.Options);

        if (close is null)
        {
            return;
        }

        _ = HandleWebSocketCloseAsync(close.SessionId);
    }

    private static async Task HandleWebSocketCloseAsync(string sessionId)
    {
        try
        {
            await App.HandleWebSocketCloseAsync(sessionId);
        }
        finally
        {
            WsMessageDone(sessionId);
        }
    }

    private static Dictionary<string, string> ParseCookies(
        Dictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        if (!headers.TryGetValue("cookie", out var header))
        {
            return cookies;
        }

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (name.Length > 0)
            {
                cookies[name] = value;
            }
        }

        return cookies;
    }

    private static ReptarApp BuildApp()
    {
        var json = new DefaultJsonCodec();
        var db = new D1DbClient(json);

        var app = ReptarApp.Create(new ReptarConfig
        {
            Database = db,
            Json = json,
            WebSocket = new WasmWebSocketTransport(),
        });

        app.RegisterSchema(Schema.All);
        app.Use(ReptarApi.RequestId());
        app.Use(ReptarApi.Telemetry());

        // Same write-behind persister as the local host; under NetWasm the
        // concurrent writers coalesce into D1 batch statements over the
        // binding instead of SQLite group-commit transactions.
        var persister = new RoomMessagePersister(db, workerCount: 4, queueCapacity: 50_000);
        var hub = new RoomHub(persister);
        RoomsRoutes.Configure(app, hub);

        // Cold-start diagnostics for the NetWasm runtime surface (randomness,
        // time, string interpolation of exceptions).
        app.Get("/diag/ids", ctx =>
        {
            try
            {
                var id = Ids.New("diag");
                return ctx.JsonRaw($"{{\"id\":{RoomProtocol.Escape(id)}}}");
            }
            catch (System.Exception exception)
            {
                return ctx.JsonRaw($"{{\"error\":{RoomProtocol.Escape(exception.GetType().FullName ?? "?")}}}");
            }
        });

        return app;
    }
}
