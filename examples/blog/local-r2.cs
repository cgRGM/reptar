using System;
using System.IO;
using System.Threading.Tasks;
using Reptar;

public sealed class LocalR2Client : IR2Client
{
    private readonly string _fullRoot;
    private readonly string _rootWithSeparator;

    public LocalR2Client(string root)
    {
        _fullRoot = Path.GetFullPath(root);
        _rootWithSeparator = _fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _fullRoot
            : _fullRoot + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_fullRoot);
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

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0'))
            throw new ArgumentException("Invalid storage key.", nameof(key));

        var normalized = key.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Storage key traversal is not allowed.", nameof(key));

        var candidate = Path.GetFullPath(Path.Combine(_fullRoot, normalized));
        if (!candidate.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
            throw new ArgumentException("Storage key escapes root directory.", nameof(key));

        return candidate;
    }
}
