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

    [JSImport("httpFetchBytes", "app.host")]
    private static extern Task<string> HttpFetchBytes(
        string url,
        string method,
        string headersJson,
        byte[]? body,
        string? contentType);

    public async Task<HttpResponseData> FetchAsync(
        string url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        string body = "")
    {
        headers = WithTraceParent(headers);
        var headersJson = headers is null || headers.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(headers, ReptarJson.Options);

        var resJson = await HttpFetch(url, method, headersJson, body);
        using var doc = JsonDocument.Parse(resJson);

        var status = doc.RootElement.GetProperty("status").GetInt32();
        var respBody = doc.RootElement.TryGetProperty("body", out var b)
            ? b.GetString() ?? ""
            : "";

        return new HttpResponseData(status, ReadHeaders(doc.RootElement), respBody);
    }

    public async Task<HttpBytesResponse> FetchBytesAsync(
        string url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        byte[]? body = null,
        string? contentType = null)
    {
        headers = WithTraceParent(headers);
        var headersJson = headers is null || headers.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(headers, ReptarJson.Options);

        var resJson = await HttpFetchBytes(url, method, headersJson, body, contentType);
        using var doc = JsonDocument.Parse(resJson);

        var status = doc.RootElement.GetProperty("status").GetInt32();
        var encoded = doc.RootElement.TryGetProperty("body", out var b)
            ? b.GetString() ?? ""
            : "";
        var bytes = encoded.Length == 0
            ? []
            : Convert.FromBase64String(encoded);

        return new HttpBytesResponse(status, ReadHeaders(doc.RootElement), bytes);
    }

    private static Dictionary<string, string>? WithTraceParent(
        Dictionary<string, string>? headers)
    {
        if (TraceContext.Current is not { } trace)
            return headers;

        var result = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        if (!result.ContainsKey("traceparent"))
            result["traceparent"] = trace.ToTraceParent();

        return result;
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var h) &&
            h.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in h.EnumerateObject())
                headers[property.Name] = property.Value.GetString() ?? "";
        }

        return headers;
    }
}
