using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class DurableBridge : IDurableBridge
{
    [JSImport("durableFetch", "app.host")]
    private static extern Task<string> FetchJs(
        string namespaceName,
        string id,
        string path,
        string method,
        string body);

    public Task<string> FetchAsync(
        string namespaceName,
        string id,
        string path,
        string method = "GET",
        string body = "") =>
        FetchJs(namespaceName, id, path, method, body);
}
