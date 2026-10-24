using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class KvClient : IKvClient
{
    [JSImport("kvGet", "app.host")]
    private static extern Task<string?> GetJs(string key);

    [JSImport("kvPut", "app.host")]
    private static extern Task PutJs(string key, string value, int ttlSeconds);

    [JSImport("kvDelete", "app.host")]
    private static extern Task DeleteJs(string key);

    public Task<string?> GetAsync(string key) => GetJs(key);

    public Task PutAsync(string key, string value, int? ttlSeconds = null) =>
        PutJs(key, value, ttlSeconds ?? 0);

    public Task DeleteAsync(string key) => DeleteJs(key);
}
