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

        web.Use(async (HttpContext http, RequestDelegate _) =>
        {
            var request = await ToReptarRequest(http);
            var response = await app.HandleAsync(request);
            await WriteResponse(http, response);
        });

        await web.RunAsync();
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
