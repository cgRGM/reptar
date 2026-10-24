using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class R2Client : IR2Client
{
    [JSImport("r2put", "app.host")]
    private static extern Task<string> Put(
        string key,
        byte[] body,
        string contentType);

    [JSImport("r2get", "app.host")]
    private static extern Task<byte[]?> Get(string key);

    [JSImport("r2exists", "app.host")]
    private static extern Task<bool> Exists(string key);

    [JSImport("r2delete", "app.host")]
    private static extern Task Delete(string key);

    public Task<string> PutAsync(string key, byte[] body, string contentType) =>
        Put(key, body, contentType);

    public Task<byte[]?> GetAsync(string key) => Get(key);

    public Task<bool> ExistsAsync(string key) => Exists(key);

    public Task DeleteAsync(string key) => Delete(key);
}
