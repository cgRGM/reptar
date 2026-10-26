using System.Text;

namespace Reptar;

/// <summary>
/// In-memory HTTP test client that executes requests directly through the
/// Reptar routing and middleware pipeline without binding TCP ports or network sockets.
/// Inspired by FastAPI's TestClient and Hono's app.request().
/// </summary>
public sealed class ReptarTestClient(ReptarApp app)
{
    /// <summary>
    /// Mail assertions over an <see cref="InMemoryMailTransport"/>. When no
    /// mail transport is configured, the client installs one automatically;
    /// an explicit non-memory transport is respected, and <see cref="TestMailbox.Capturing"/>
    /// reports false so tests never assert against a lie.
    /// </summary>
    public TestMailbox Mail { get; } = CreateTestMailbox(app);

    /// <summary>
    /// Database metrics captured from the most recent request (query count,
    /// total ms, slowest SQL, and per-template execution counts for N+1
    /// assertions). Null until the first request executes.
    /// </summary>
    public DbMetrics? LastDbMetrics { get; private set; }

    public async Task<ReptarResponse> RequestAsync(ReptarRequest request)
    {
        // DbMetrics scopes are AsyncLocal inside the request pipeline; hook
        // Begin() through an AsyncLocal slot so parallel test clients never
        // capture each other's metrics.
        var previousObserver = DbMetrics.ScopeObserverSlot.Value;
        DbMetrics.ScopeObserverSlot.Value = CaptureMetrics;
        try
        {
            var result = await app.HandleWithBackgroundAsync(request);

            // Test requests are deterministic: wait for ctx.WaitUntil work so
            // assertions can inspect its effects without polling.
            await result.Background;

            return result.Response;
        }
        finally
        {
            DbMetrics.ScopeObserverSlot.Value = previousObserver;
        }
    }

    private void CaptureMetrics(DbMetrics metrics) => LastDbMetrics = metrics;

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

    private static TestMailbox CreateTestMailbox(ReptarApp app)
    {
        switch (app.Config.Mail)
        {
            case InMemoryMailTransport sink:
                return new TestMailbox(sink);
            case null or NoopMailClient:
                var fresh = new InMemoryMailTransport();
                app.SetMailTransport(fresh);
                return new TestMailbox(fresh);
            default:
                // Explicit production transport: respect it, capture nothing.
                return new TestMailbox(null);
        }
    }
}
