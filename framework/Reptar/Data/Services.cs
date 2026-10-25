namespace Reptar;

public interface IDbClient
{
    Task<List<T>> QueryAsync<T>(string sql, object? param = null);
    Task<List<T>> QueryAsync<T>(string sql, (string Name, object? Value) p1) =>
        QueryAsync<T>(sql, (object)p1);
    Task<List<T>> QueryAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2) =>
        QueryAsync<T>(sql, (object)new[] { p1, p2 });
    Task<List<T>> QueryAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3) =>
        QueryAsync<T>(sql, (object)new[] { p1, p2, p3 });
    Task<List<T>> QueryAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3, (string Name, object? Value) p4) =>
        QueryAsync<T>(sql, (object)new[] { p1, p2, p3, p4 });
    Task<List<T>> QueryAsync<T>(string sql, params (string Name, object? Value)[] parameters) =>
        QueryAsync<T>(sql, (object)parameters);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, (string Name, object? Value) p1) =>
        QuerySingleOrDefaultAsync<T>(sql, (object)p1);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2) =>
        QuerySingleOrDefaultAsync<T>(sql, (object)new[] { p1, p2 });
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3) =>
        QuerySingleOrDefaultAsync<T>(sql, (object)new[] { p1, p2, p3 });
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3, (string Name, object? Value) p4) =>
        QuerySingleOrDefaultAsync<T>(sql, (object)new[] { p1, p2, p3, p4 });
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, params (string Name, object? Value)[] parameters) =>
        QuerySingleOrDefaultAsync<T>(sql, (object)parameters);

    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1) =>
        ExecuteAsync(sql, (object)p1);
    Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2) =>
        ExecuteAsync(sql, (object)new[] { p1, p2 });
    Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3) =>
        ExecuteAsync(sql, (object)new[] { p1, p2, p3 });
    Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3, (string Name, object? Value) p4) =>
        ExecuteAsync(sql, (object)new[] { p1, p2, p3, p4 });
    Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters) =>
        ExecuteAsync(sql, (object)parameters);
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

    Task<List<T>> QueryMappedAsync<T>(
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map) =>
        QueryAsync<T>(sql, param);

    Task EnsureCreatedAsync(params TableDef[] tables);

    async Task<int> CountAsync(
        TableDef table,
        string? where = null,
        object? param = null)
    {
        var clause = string.IsNullOrWhiteSpace(where)
            ? ""
            : where.TrimStart().StartsWith("where", StringComparison.OrdinalIgnoreCase)
                ? " " + where.Trim()
                : " where " + where.Trim();

        var row = await QuerySingleOrDefaultAsync<DbCount>(
            $"select count(*) as count from \"{table.Name}\"{clause}",
            param);

        return row?.Count ?? 0;
    }

    async Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        var changes = 0;

        foreach (var statement in statements)
        {
            changes += await ExecuteAsync(statement.Sql, statement.Param);
        }

        return changes;
    }
}

[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record DbCount(int Count);

public sealed record DbStatement(string Sql, object? Param = null);

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

public interface IWebSocketTransport
{
    void Send(string sessionId, string message);
    void Close(string sessionId, int code, string reason);
}

public sealed class NoopWebSocketTransport : IWebSocketTransport
{
    public void Send(string sessionId, string message) =>
        throw new PlatformNotSupportedException(
            "WebSockets are provided by the Workers shim.");

    public void Close(string sessionId, int code, string reason) { }
}

public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class ConsoleAppLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine(message);
    public void Warn(string message) => Console.WriteLine($"WARN {message}");
    public void Error(string message) => Console.Error.WriteLine(message);
}

public interface IErrorMapper
{
    ReptarResponse Error(Ctx ctx, int status, string code, string message);
    ReptarResponse Validation(Ctx ctx, IReadOnlyList<ValidationIssue> issues);
}

public sealed class DefaultErrorMapper : IErrorMapper
{
    public ReptarResponse Error(
        Ctx ctx,
        int status,
        string code,
        string message) =>
        ReptarResponse.Json(
            ctx.JsonCodec.Serialize(new ErrorBody(code, message, ctx.RequestId)),
            status);

    public ReptarResponse Validation(
        Ctx ctx,
        IReadOnlyList<ValidationIssue> issues) =>
        ReptarResponse.Json(
            ctx.JsonCodec.Serialize(new ValidationError(
                "validation_error",
                "Validation failed.",
                ctx.RequestId,
                issues.ToArray())),
            400);
}

public interface IEnvironment
{
    string? Get(string name);
}

#if !NETWASM
public sealed class SystemEnvironment : IEnvironment
{
    public string? Get(string name) =>
        Environment.GetEnvironmentVariable(name);
}
#endif

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
    // Match the workerd WebCrypto cap so local and deployed hashes verify.
    private const int Iterations = 100_000;

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

        if (!int.TryParse(parts[1], out var iterations))
            return Task.FromResult(false);

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);

        Span<byte> actual = stackalloc byte[expected.Length];
        Span<byte> pwdBytes = stackalloc byte[System.Text.Encoding.UTF8.GetByteCount(password)];
        System.Text.Encoding.UTF8.GetBytes(password, pwdBytes);

        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            pwdBytes,
            salt,
            actual,
            iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256);

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
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var hash = System.Security.Cryptography.HMACSHA256.HashData(keyBytes, msgBytes);
        return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
    }

    public string RandomToken(int bytes = 32) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
#endif

public interface IR2Client : IStorage;

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

public readonly record struct RateLimitResult(
    bool Allowed,
    int Limit,
    int Remaining,
    long ResetSeconds,
    int RetryAfterSeconds);

public interface IRateLimiter
{
    Task<bool> LimitAsync(string category, string key);
    Task<bool> LimitAsync(string category, string key, int limit, TimeSpan window);
    Task<RateLimitResult> CheckAsync(string category, string key, int limit, TimeSpan window) =>
        Task.FromResult(new RateLimitResult(true, limit, limit, 0, 0));
}

public sealed class NoopRateLimiter : IRateLimiter
{
    public Task<bool> LimitAsync(string category, string key) => Task.FromResult(true);
    public Task<bool> LimitAsync(string category, string key, int limit, TimeSpan window) => Task.FromResult(true);
    public Task<RateLimitResult> CheckAsync(string category, string key, int limit, TimeSpan window) =>
        Task.FromResult(new RateLimitResult(true, limit, limit, 0, 0));
}

public sealed class MemoryRateLimiter : IRateLimiter
{
    private sealed class WindowBucket
    {
        public long CurrentWindowStart;
        public int CurrentCount;
        public int PreviousCount;
        public readonly object Lock = new();
    }

    private readonly int _defaultLimit;
    private readonly TimeSpan _defaultWindow;
    private readonly Dictionary<string, (int Limit, TimeSpan Window)> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WindowBucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private long _lastCleanupSeconds;

    public MemoryRateLimiter(int defaultLimit = 60, TimeSpan? defaultWindow = null)
    {
        _defaultLimit = defaultLimit;
        _defaultWindow = defaultWindow ?? TimeSpan.FromMinutes(1);
    }

    public MemoryRateLimiter Configure(string category, int limit, TimeSpan window)
    {
        lock (_sync)
        {
            _rules[category] = (limit, window);
        }
        return this;
    }

    public Task<bool> LimitAsync(string category, string key)
    {
        (int Limit, TimeSpan Window) rule;
        lock (_sync)
        {
            if (!_rules.TryGetValue(category, out rule))
                rule = (_defaultLimit, _defaultWindow);
        }
        return LimitAsync(category, key, rule.Limit, rule.Window);
    }

    public async Task<bool> LimitAsync(string category, string key, int limit, TimeSpan window)
    {
        var result = await CheckAsync(category, key, limit, window);
        return result.Allowed;
    }

    public Task<RateLimitResult> CheckAsync(string category, string key, int limit, TimeSpan window)
    {
        var windowSec = Math.Max(1, (long)window.TotalSeconds);
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var windowStart = (nowSec / windowSec) * windowSec;
        var resetSec = windowStart + windowSec;
        var elapsedInWindow = nowSec - windowStart;
        var previousWeight = Math.Max(0.0, (double)(windowSec - elapsedInWindow) / windowSec);

        var bucketKey = $"{category}:{key}";
        var bucket = _buckets.GetOrAdd(bucketKey, _ => new WindowBucket { CurrentWindowStart = windowStart });

        if (nowSec - Volatile.Read(ref _lastCleanupSeconds) > 300)
        {
            CleanupStaleBuckets(nowSec);
            Volatile.Write(ref _lastCleanupSeconds, nowSec);
        }

        lock (bucket.Lock)
        {
            if (windowStart > bucket.CurrentWindowStart)
            {
                if (windowStart == bucket.CurrentWindowStart + windowSec)
                {
                    bucket.PreviousCount = bucket.CurrentCount;
                }
                else
                {
                    bucket.PreviousCount = 0;
                }
                bucket.CurrentCount = 0;
                bucket.CurrentWindowStart = windowStart;
            }

            var estimated = (int)(bucket.PreviousCount * previousWeight) + bucket.CurrentCount;
            if (estimated >= limit)
            {
                var retryAfter = Math.Max(1, (int)(resetSec - nowSec));
                return Task.FromResult(new RateLimitResult(false, limit, 0, resetSec, retryAfter));
            }

            bucket.CurrentCount++;
            var remaining = Math.Max(0, limit - (estimated + 1));
            return Task.FromResult(new RateLimitResult(true, limit, remaining, resetSec, 0));
        }
    }

    private void CleanupStaleBuckets(long nowSec)
    {
        foreach (var (k, b) in _buckets)
        {
            if (nowSec - b.CurrentWindowStart > 600)
            {
                _buckets.TryRemove(k, out _);
            }
        }
    }
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
