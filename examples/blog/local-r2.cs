using System.IO;

public sealed class LocalR2Client : IR2Client
{
    private readonly string _root;

    public LocalR2Client(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutAsync(
        string key,
        byte[] body,
        string contentType)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, body);
        return $"/images/{key}";
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        var path = PathFor(key);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path)
            : null;
    }

    public Task<bool> ExistsAsync(string key) =>
        Task.FromResult(File.Exists(PathFor(key)));

    public Task DeleteAsync(string key)
    {
        var path = PathFor(key);

        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    private string PathFor(string key) =>
        Path.Combine(_root, Path.GetFileName(key));
}
