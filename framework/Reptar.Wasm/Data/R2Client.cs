using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class R2Client : IR2Client
{
    [JSImport("r2put", "app.host")]
    private static extern Task<string> Put(
        string key,
        byte[] body,
        string contentType);

    public Task<string> PutAsync(string key, byte[] body, string contentType) =>
        Put(key, body, contentType);

    public Task<byte[]?> GetAsync(string key) =>
        throw Unsupported("GetAsync");

    public Task<bool> ExistsAsync(string key) =>
        throw Unsupported("ExistsAsync");

    public Task DeleteAsync(string key) =>
        throw Unsupported("DeleteAsync");

    private static NotSupportedException Unsupported(string operation) =>
        new($"{operation} is handled by the Worker image route.");
}
