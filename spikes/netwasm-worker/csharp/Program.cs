using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

[JsonSerializable(typeof(Posts.Post))]
[JsonSerializable(typeof(List<Posts.Post>))]
[JsonSerializable(typeof(Posts.CreatePost))]
[JsonSerializable(typeof(Posts.UpdatePost))]
[JsonSerializable(typeof(Blog.UploadResult))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SessionUser))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(Auth.UserRow))]
[JsonSerializable(typeof(List<Auth.UserRow>))]
[JsonSerializable(typeof(Auth.CountRow))]
[JsonSerializable(typeof(List<Auth.CountRow>))]
[JsonSerializable(typeof(List<SessionUser>))]
[JsonSerializable(typeof(Auth.SignUpInput))]
[JsonSerializable(typeof(Auth.SignInInput))]
[JsonSerializable(typeof(DbCount))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class BlogJsonContext : JsonSerializerContext
{
}

public static partial class PageExports
{
    private static readonly ReptarApp App;

    static PageExports()
    {
        ReptarJson.RegisterContext(BlogJsonContext.Default);
        ReptarJson.UseRegisteredContexts();
        App = BuildApp();
    }

    [JSImport("respond", "app.host")]
    private static extern void Respond(string requestId, string json);

    [JSImport("respondQueue", "app.host")]
    private static extern void RespondQueue(string batchId, string json);

    [JSImport("respondScheduled", "app.host")]
    private static extern void RespondScheduled(string scheduleId, string json);

    [JSExport("handle")]
    public static Task Handle(string requestJson) =>
        WorkerEntrypoint.HandleAsync(App, requestJson, Respond);

    [JSExport("handleQueue")]
    public static Task HandleQueue(string batchJson) =>
        WorkerEntrypoint.HandleQueueAsync(App, batchJson, RespondQueue);

    [JSExport("handleScheduled")]
    public static Task HandleScheduled(string scheduledJson) =>
        WorkerEntrypoint.HandleScheduledAsync(App, scheduledJson, RespondScheduled);

    private static ReptarApp BuildApp()
    {
        var json = new DefaultJsonCodec();

        var app = ReptarApp.Create(new ReptarConfig
        {
            AI = new AiClient(),
            Auth = BuiltIn(),
            Cache = new KvClient(),
            Database = new D1DbClient(json),
            Durable = new DurableBridge(),
            Http = new WasmHttpClient(),
            Json = json,
            Queues = new QueueClient(),
            Storage = new R2Client(),
        });

        app.Use(ReptarApi.RequestId());
        app.Use(ReptarApi.Telemetry());
        Blog.Configure(app);

        app.Get("/platform/env", ctx => Task.FromResult(ctx.Json(
            new Dictionary<string, string>
            {
                ["appName"] = ctx.Environment.Get("APP_NAME") ?? "",
            })));

        app.Get("/platform/kv", async ctx =>
        {
            var value = await ctx.Cache.GetAsync("demo");
            var jsonValue = value is null
                ? "null"
                : JsonSerializer.Serialize(value, ReptarJson.Options);

            return ReptarResponse.Json($"{{\"value\":{jsonValue}}}");
        });

        app.Put("/platform/kv", async ctx =>
        {
            using var document = JsonDocument.Parse(ctx.Req.BodyText ?? "{}");

            if (!document.RootElement.TryGetProperty("value", out var value))
                return ctx.BadRequest("Value is required.");

            int? ttl = document.RootElement.TryGetProperty(
                "ttlSeconds",
                out var ttlElement) &&
                ttlElement.TryGetInt32(out var ttlValue)
                ? ttlValue
                : null;

            await ctx.Cache.PutAsync(
                "demo",
                value.GetString() ?? "",
                ttl);

            return ctx.Status(204);
        });

        app.Post("/platform/queue", async ctx =>
        {
            var body = ctx.Req.BodyText ?? "{}";
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("message", out var message) ||
                string.IsNullOrWhiteSpace(message.GetString()))
            {
                return ctx.BadRequest("Message is required.");
            }

            await ctx.Queues.SendAsync("events", body);
            return ReptarResponse.Json("{\"sent\":true}", 202);
        });

        app.OnQueue("events", async (ctx, msg) =>
        {
            try
            {
                // Trace the payload, and mirror it onto the "demo" key so
                // the round trip is observable through GET /platform/kv:
                // POST /platform/queue → Queues → this consumer → KV.
                await ctx.Cache.PutAsync($"queue:{msg.Id}", msg.Body, 300);
                await ctx.Cache.PutAsync("demo", msg.Body, 300);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[queue] consumer failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        });

        return app;
    }
}
