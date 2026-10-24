namespace Reptar;

public interface IDbClient
{
    Task<List<T>> QueryAsync<T>(string sql, object? param = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<T?> GetAsync<T>(TableDef table, object id);
    Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0);
    Task<int> InsertAsync(TableDef table, object values);
    Task<int> InsertAsync<T>(TableDef table, T values) where T : class;
    Task<int> UpdateAsync(TableDef table, object values, object id);
    Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class;
    Task<int> DeleteAsync(TableDef table, object id);
    Task<int> DeleteAsync<T>(TableDef table, object id);
    Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null);
    Task<bool> ExistsAsync(TableDef table, string where, object? param = null);
    Task EnsureCreatedAsync(params TableDef[] tables);
}

public interface IClock
{
    string IsoNow();
    string IsoIn(TimeSpan duration);
}

public sealed class SystemClock : IClock
{
    public string IsoNow() => DateTimeOffset.UtcNow.ToString("O");
    public string IsoIn(TimeSpan duration) => DateTimeOffset.UtcNow.Add(duration).ToString("O");
}

public interface ICrypto
{
    Task<string> HashPassword(string password);
    Task<bool> VerifyPassword(string password, string hash);
    Task<string> Sha256(string input);
    Task<string> HmacSha256(string key, string message);
    string RandomToken(int bytes = 32);
}

#if !NETWASM
public sealed class DevCrypto : ICrypto
{
    private const int Iterations = 210_000;

    public Task<string> HashPassword(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            32);

        return Task.FromResult(
            $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public Task<bool> VerifyPassword(string password, string hash)
    {
        var parts = hash.Split('$');

        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
            return Task.FromResult(false);

        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);

        var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            expected.Length);

        return Task.FromResult(
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected));
    }

    public Task<string> Sha256(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));

        return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
    }

    public Task<string> HmacSha256(string key, string message)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message));
        return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
    }

    public string RandomToken(int bytes = 32) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
#endif

public interface IR2Client
{
    Task<string> PutAsync(string key, byte[] body, string contentType);
    Task<byte[]?> GetAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task DeleteAsync(string key);
}

public sealed class NoopR2Client : IR2Client
{
    public Task<string> PutAsync(string key, byte[] body, string contentType) =>
        throw new PlatformNotSupportedException("R2 is provided by the Workers shim.");

    public Task<byte[]?> GetAsync(string key) =>
        throw new PlatformNotSupportedException("R2 is provided by the Workers shim.");

    public Task<bool> ExistsAsync(string key) =>
        throw new PlatformNotSupportedException("R2 is provided by the Workers shim.");

    public Task DeleteAsync(string key) =>
        throw new PlatformNotSupportedException("R2 is provided by the Workers shim.");
}

public sealed record AiRequest(string Model, string Prompt, int MaxTokens = 512);

public sealed record AiResult(string Model, string Text, int TokensIn, int TokensOut);

public interface IAiClient
{
    Task<AiResult> GenerateAsync(AiRequest request);
}

public sealed class NoopAiClient : IAiClient
{
    public Task<AiResult> GenerateAsync(AiRequest request) =>
        throw new PlatformNotSupportedException("Workers AI is provided by the Workers shim.");
}

public interface IRateLimiter
{
    Task<bool> LimitAsync(string category, string key);
}

public sealed class NoopRateLimiter : IRateLimiter
{
    public Task<bool> LimitAsync(string category, string key) => Task.FromResult(true);
}

public interface IKvClient
{
    Task<string?> GetAsync(string key);
    Task PutAsync(string key, string value, int? ttlSeconds = null);
    Task DeleteAsync(string key);
}

public sealed class NoopKvClient : IKvClient
{
    public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
    public Task PutAsync(string key, string value, int? ttlSeconds = null) => Task.CompletedTask;
    public Task DeleteAsync(string key) => Task.CompletedTask;
}

public interface IQueueClient
{
    Task SendAsync(string queueName, string messageJson);
}

public sealed class NoopQueueClient : IQueueClient
{
    public Task SendAsync(string queueName, string messageJson) => Task.CompletedTask;
}

public interface IDurableBridge
{
    Task<string> FetchAsync(string namespaceName, string id, string path, string method = "GET", string body = "");
}

public sealed class NoopDurableBridge : IDurableBridge
{
    public Task<string> FetchAsync(string namespaceName, string id, string path, string method = "GET", string body = "") =>
        throw new PlatformNotSupportedException("Durable Objects bridge is provided by the Workers shim.");
}

public sealed record HttpResponseData(int Status, Dictionary<string, string> Headers, string Body);

public interface IHttpClient
{
    Task<HttpResponseData> FetchAsync(string url, string method = "GET", Dictionary<string, string>? headers = null, string body = "");
}

#if !NETWASM
public sealed class DefaultHttpClient : IHttpClient
{
    public async Task<HttpResponseData> FetchAsync(string url, string method = "GET", Dictionary<string, string>? headers = null, string body = "")
    {
        using var client = new System.Net.Http.HttpClient();
        using var request = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(method), url);
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }
        if (!string.IsNullOrEmpty(body) && (method == "POST" || method == "PUT" || method == "PATCH"))
        {
            request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var respHeaders = new Dictionary<string, string>();
        foreach (var h in response.Headers)
        {
            respHeaders[h.Key] = string.Join(",", h.Value);
        }
        return new HttpResponseData((int)response.StatusCode, respHeaders, responseBody);
    }
}
#endif
