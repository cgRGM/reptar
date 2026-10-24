using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
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
        ReptarJson.Options.TypeInfoResolver = RealWorldJsonContext.Default;
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
                    new HostResponse(400, [], "e30=", []),
                    ReptarJson.Options));
            return;
        }

        ReptarResponse response;

        try
        {
            response = await App.HandleAsync(new ReptarRequest
            {
                Method = request.Method,
                Path = request.Path,
                Query = request.Query.TrimStart('?'),
                Headers = request.Headers,
                Cookies = ParseCookies(request.Headers),
                Body = string.IsNullOrEmpty(request.Body)
                    ? []
                    : Convert.FromBase64String(request.Body),
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            response = ReptarResponse.Json(
                "{\"code\":\"internal_error\",\"message\":\"Something went wrong.\"}",
                500);
        }

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
                    response.Body is null
                        ? ""
                        : Convert.ToBase64String(response.Body),
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
            return cookies;

        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
                continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (name.Length > 0)
                cookies[name] = value;
        }

        return cookies;
    }

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
        RealWorld.Configure(app);

        return app;
    }
}
