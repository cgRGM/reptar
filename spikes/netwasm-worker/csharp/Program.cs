using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

public static partial class PageExports
{
    [JSImport("query", "app.host")]
    private static extern Task<string> Query(string sql, string paramsJson);

    [JSImport("r2put", "app.host")]
    private static extern Task<string> R2Put(string key, byte[] bytes, string contentType);

    [JSImport("resolve", "app.host")]
    private static extern void Resolve(string result);

    [JSExport("listPosts")]
    public static void ListPosts()
    {
        _ = RunList();
    }

    [JSExport("createPost")]
    public static void CreatePost(string title, string content, string imageKey)
    {
        _ = RunCreate(title, content, imageKey);
    }

    [JSExport("uploadImage")]
    public static void UploadImage(string key, string contentType, byte[] bytes)
    {
        _ = RunUpload(key, contentType, bytes);
    }

    private static async Task RunList()
    {
        var json = await Query(
            "select id, title, content, image_key as imageKey, created_at as createdAt from posts order by created_at desc",
            "[]");
        Resolve(json);
    }

    private static async Task RunCreate(string title, string content, string imageKey)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");

        var paramsJson = "[" + string.Join(",", new[]
        {
            J(id), J(title), J(content), J(imageKey), J(now),
        }) + "]";

        await Query(
            "insert into posts (id, title, content, image_key, created_at) values (?, ?, ?, ?, ?)",
            paramsJson);

        var json = "{\"id\":" + J(id) +
                   ",\"title\":" + J(title) +
                   ",\"content\":" + J(content) +
                   ",\"imageKey\":" + J(imageKey) +
                   ",\"createdAt\":" + J(now) + "}";
        Resolve(json);
    }

    private static async Task RunUpload(string key, string contentType, byte[] bytes)
    {
        var url = await R2Put(key, bytes, contentType);
        Resolve("{\"key\":" + J(key) + ",\"url\":" + J(url) + "}");
    }

    private static string J(string value) =>
        "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r") + "\"";
}
