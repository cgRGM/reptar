using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RealWorldJsonContext : JsonSerializerContext
{
}

public static partial class PageExports
{
    private static readonly ReptarApp App;

    static PageExports()
    {
        ReptarJson.RegisterContext(RealWorldJsonContext.Default);
        ReptarJson.UseRegisteredContexts();
        App = BuildApp();
    }

    [JSImport("respond", "app.host")]
    private static extern void Respond(string requestId, string json);

    [JSExport("handle")]
    public static Task Handle(string requestJson) =>
        WorkerEntrypoint.HandleAsync(App, requestJson, Respond);

    private static ReptarApp BuildApp()
    {
        var json = new RealWorldJsonCodec();

        var app = ReptarApp.Create(new ReptarConfig
        {
            Auth = BuiltIn(),
            Database = new D1DbClient(json),
            Json = json,
        });

        app.Use(ReptarApi.RequestId());
        app.Use(ReptarApi.Telemetry());
        RealWorld.Configure(app);

        return app;
    }
}
