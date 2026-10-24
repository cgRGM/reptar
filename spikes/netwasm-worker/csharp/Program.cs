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
            Auth = BuiltIn(),
            Database = new D1DbClient(json),
            Storage = new R2Client(),
            Json = json,
        });

        app.Use(ReptarApi.RequestId());
        Blog.Configure(app);

        return app;
    }
}
