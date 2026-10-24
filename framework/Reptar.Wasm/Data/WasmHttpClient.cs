using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Reptar;

public sealed class WasmHttpClient : IHttpClient
{
    [JSImport("httpFetch", "app.host")]
    private static extern Task<string> HttpFetch(
        string url,
        string method,
        string headersJson,
        string body);

    public async Task<HttpResponseData> FetchAsync(
        string url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        string body = "")
    {
        var headersJson = headers is null || headers.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(headers, ReptarJson.Options);

        var resJson = await HttpFetch(url, method, headersJson, body);
        using var doc = JsonDocument.Parse(resJson);

        var status = doc.RootElement.GetProperty("status").GetInt32();
        var respBody = doc.RootElement.TryGetProperty("body", out var b)
            ? b.GetString() ?? ""
            : "";

        var respHeaders = new Dictionary<string, string>();
        if (doc.RootElement.TryGetProperty("headers", out var h) &&
            h.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in h.EnumerateObject())
            {
                respHeaders[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        return new HttpResponseData(status, respHeaders, respBody);
    }
}
