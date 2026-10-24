using Reptar.Realtime;

namespace Reptar;

public static class RealtimeExtensions
{
    public static ActionCableServer UseActionCable(
        this ReptarApp app,
        string path = "/cable",
        Action<ActionCableServer>? configure = null)
    {
        var server = new ActionCableServer(app.Config.Json);
        configure?.Invoke(server);
        app.OnWebSocket(path, server.CreateHandlers());
        return server;
    }

    public static CampfireRealtimeService UseCampfire(
        this ReptarApp app,
        string path = "/cable",
        Action<CampfireRealtimeService>? configure = null)
    {
        var cableServer = app.UseActionCable(path);
        var campfire = new CampfireRealtimeService(cableServer);
        configure?.Invoke(campfire);
        return campfire;
    }
}
