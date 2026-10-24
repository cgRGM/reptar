using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

public sealed record HostRequest(
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string Body,
    string RequestId);

public sealed record HostResponse(
    int Status,
    Dictionary<string, string> Headers,
    string Body,
    string[] SetCookies);

[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
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
internal partial class BlogJsonContext : JsonSerializerContext
{
}

public static partial class PageExports
{
    private static readonly ReptarApp App;

    static PageExports()
    {
        ReptarJson.Options.TypeInfoResolver = BlogJsonContext.Default;
        App = BuildApp();
    }

    [JSImport("respond", "app.host")]
    private static extern void Respond(string requestId, string json);

    [JSExport("handle")]
    public static void Handle(string requestJson)
    {
        _ = HandleAsync(requestJson);
    }

    private static async Task HandleAsync(string requestJson)
    {
        var request = JsonSerializer.Deserialize<HostRequest>(
            requestJson,
            ReptarJson.Options);

        if (request is null)
        {
            Respond(
                ExtractRequestId(requestJson),
                JsonSerializer.Serialize(
                    new HostResponse(400, [], "{\"error\":\"bad_request\"}", []),
                    ReptarJson.Options));
            return;
        }

        var response = await App.HandleAsync(new ReptarRequest
        {
            Method = request.Method,
            Path = request.Path,
            Query = request.Query.TrimStart('?'),
            Headers = request.Headers,
            Cookies = ParseCookies(request.Headers),
            Body = Encoding.UTF8.GetBytes(request.Body),
        });

        var body = response.Body is null
            ? ""
            : Encoding.UTF8.GetString(response.Body);

        var headers = new Dictionary<string, string>(response.Headers);

        if (response.ContentType is not null)
        {
            headers["content-type"] = response.ContentType;
        }

        Respond(
            request.RequestId,
            JsonSerializer.Serialize(
                new HostResponse(
                    response.Status,
                    headers,
                    body,
                    response.SetCookies.ToArray()),
                ReptarJson.Options));
    }

    private static string ExtractRequestId(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);

            return document.RootElement.TryGetProperty(
                "requestId",
                out var requestId)
                ? requestId.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
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

        app.Get("/platform/durable", async ctx =>
        {
            var body = await ctx.Durable.FetchAsync(
                "counter",
                "demo",
                "/",
                "GET");

            return ReptarResponse.Json(body);
        });

        app.Post("/platform/ai", async ctx =>
        {
            using var document = JsonDocument.Parse(ctx.Req.BodyText ?? "{}");
            var prompt = document.RootElement.TryGetProperty(
                "prompt",
                out var promptElement)
                ? promptElement.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(prompt))
                return ctx.BadRequest("Prompt is required.");

            var model = document.RootElement.TryGetProperty(
                "model",
                out var modelElement)
                ? modelElement.GetString() ?? "@cf/meta/llama-3.2-1b-instruct"
                : "@cf/meta/llama-3.2-1b-instruct";

            var result = await ctx.AI.GenerateAsync(
                new AiRequest(model, prompt));

            return ReptarResponse.Json(
                $"{{\"model\":{JsonSerializer.Serialize(result.Model, ReptarJson.Options)},\"text\":{JsonSerializer.Serialize(result.Text, ReptarJson.Options)},\"tokensIn\":{result.TokensIn},\"tokensOut\":{result.TokensOut}}}");
        });

        return app;
    }
}
