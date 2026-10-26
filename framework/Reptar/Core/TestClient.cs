using System.Text;

namespace Reptar;

/// <summary>
/// In-memory HTTP test client that executes requests directly through the
/// Reptar routing and middleware pipeline without binding TCP ports or network sockets.
/// Inspired by FastAPI's TestClient and Hono's app.request().
/// </summary>
public sealed class ReptarTestClient(ReptarApp app)
{
    public Task<ReptarResponse> RequestAsync(ReptarRequest request) => app.HandleAsync(request);

    public Task<ReptarResponse> GetAsync(string path, Dictionary<string, string>? headers = null) =>
        RequestAsync(new ReptarRequest
        {
            Method = "GET",
            Path = path,
            Headers = headers ?? new(StringComparer.OrdinalIgnoreCase),
        });

    public Task<ReptarResponse> PostAsync(
        string path,
        byte[]? body = null,
        string contentType = "application/json",
        Dictionary<string, string>? headers = null)
    {
        var hdrs = headers ?? new(StringComparer.OrdinalIgnoreCase);
        if (!hdrs.ContainsKey("Content-Type"))
            hdrs["Content-Type"] = contentType;

        return RequestAsync(new ReptarRequest
        {
            Method = "POST",
            Path = path,
            Body = body,
            Headers = hdrs,
        });
    }

    public Task<ReptarResponse> PostJsonAsync(
        string path,
        string json,
        Dictionary<string, string>? headers = null) =>
        PostAsync(path, Encoding.UTF8.GetBytes(json), "application/json", headers);

    public Task<ReptarResponse> PutAsync(
        string path,
        byte[]? body = null,
        string contentType = "application/json",
        Dictionary<string, string>? headers = null)
    {
        var hdrs = headers ?? new(StringComparer.OrdinalIgnoreCase);
        if (!hdrs.ContainsKey("Content-Type"))
            hdrs["Content-Type"] = contentType;

        return RequestAsync(new ReptarRequest
        {
            Method = "PUT",
            Path = path,
            Body = body,
            Headers = hdrs,
        });
    }

    public Task<ReptarResponse> PutJsonAsync(
        string path,
        string json,
        Dictionary<string, string>? headers = null) =>
        PutAsync(path, Encoding.UTF8.GetBytes(json), "application/json", headers);

    public Task<ReptarResponse> DeleteAsync(string path, Dictionary<string, string>? headers = null) =>
        RequestAsync(new ReptarRequest
        {
            Method = "DELETE",
            Path = path,
            Headers = headers ?? new(StringComparer.OrdinalIgnoreCase),
        });
}
