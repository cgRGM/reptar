using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Reptar;

public sealed class ReptarConfig
{
    public bool Debug { get; set; }
    public long MaxBodyBytes { get; set; } = 10 * 1024 * 1024;
    public IDbClient? Database { get; set; }
    public IStorage? Storage { get; set; }
    public IAiClient? AI { get; set; }
    public IRateLimiter? RateLimit { get; set; }
    public IKvClient? Cache { get; set; }
    public IQueueClient? Queues { get; set; }
    public IQueueObserver? QueueObserver { get; set; }
    public IDurableBridge? Durable { get; set; }
    public IHttpClient? Http { get; set; }
    public ICrypto? Crypto { get; set; }
    public IClock? Clock { get; set; }
    public IEnvironment? Environment { get; set; }
    public IJsonCodec? Json { get; set; }
    public IErrorMapper? Errors { get; set; }
    public IAppLogger? Log { get; set; }
    public IWebSocketTransport? WebSocket { get; set; }
    public AuthOptions? Auth { get; set; }
    public TelemetryOptions? Telemetry { get; set; }
}

public sealed class AuthOptions
{
    public int SessionDays { get; set; } = 30;
    public string CookieName { get; set; } = "reptar_session";
    public string? CookieDomain { get; set; }

    /// <summary>
    /// Adds the `Secure` attribute to session cookies. Defaults to true;
    /// set false only for plain-HTTP local development.
    /// </summary>
    public bool SecureCookies { get; set; } = true;
    public bool AllowAnonymous { get; set; } = true;
    public string[] BootstrapAdmins { get; set; } = [];
    public JwtOptions? Jwt { get; set; }

    // Shared by the native and Wasm auth slices.
    public int RateLimitMaxAttempts { get; set; } = 5;
    public int RateLimitWindowMinutes { get; set; } = 15;
    public bool EnableRateLimiting { get; set; } = true;

#if !NETWASM
    public List<IOAuthProvider> OAuthProviders { get; set; } = [];

    public AuthOptions WithGoogle(string clientId, string clientSecret)
    {
        OAuthProviders.Add(new GoogleOAuthProvider(clientId, clientSecret));
        return this;
    }

    public AuthOptions WithGitHub(string clientId, string clientSecret)
    {
        OAuthProviders.Add(new GitHubOAuthProvider(clientId, clientSecret));
        return this;
    }
#endif
}

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fraction of successful requests to log, 0..1. Errors and slow requests
    /// are always logged. Defaults to 1.0 (log every request).
    /// </summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Requests at or above this duration are marked slow and always logged.
    /// Set to 0 to disable slow-request marking.
    /// </summary>
    public int SlowRequestMs { get; set; } = 500;

    /// <summary>
    /// Queries at or above this duration count as slow queries.
    /// </summary>
    public int SlowQueryMs { get; set; } = 100;
}

public static class ReptarApi
{
    public static readonly ColApi Col = new();

    public static ReptarApp Create(ReptarConfig? config = null) =>
        ReptarApp.Create(config);

    public static Validator<T> Rule<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() => new();

    public static TableDef DefineTable(string name, object columns) =>
        TableDef.Create(name, columns);

    public static TableDef DefineTable(string name, params ColumnBuilder[] columns) =>
        TableDef.Define(name, columns);

    public static TableDef DefineTable(string name, params ColumnDef[] columns) =>
        TableDef.Define(name, columns);

    public static AuthOptions BuiltIn(AuthOptions? options = null) =>
        options ?? new AuthOptions();

    public static AuthOptions BetterAuth(AuthOptions? options = null) =>
        options ?? new AuthOptions();

    public static TelemetryOptions Axiom() => new();

    public static IDbClient D1() =>
        throw new PlatformNotSupportedException(
            "D1 is provided by the Workers shim. Use Db.Sqlite(...) for local development.");

    public static IR2Client R2() => new NoopR2Client();

#if !NETWASM
    public static LocalStorage LocalStorage(string rootDirectory = "uploads") =>
        new(rootDirectory);

    public static LocalStorage Disk(string rootDirectory = "uploads") =>
        new(rootDirectory);

    public static S3Storage S3(S3StorageOptions options) =>
        new(options);
#endif

    public static IAiClient WorkersAI() => new NoopAiClient();

    public static ReptarMiddleware RequestId() => Middlewares.RequestId();

    public static ReptarMiddleware Cors() => Middlewares.Cors();

    public static ReptarMiddleware Cors(params string[] allowedOrigins) =>
        Middlewares.Cors(allowedOrigins);

    public static ReptarMiddleware Cors(CorsOptions? options) =>
        Middlewares.Cors(options);

    public static ReptarMiddleware Telemetry(TelemetryOptions? options = null) =>
        Middlewares.Telemetry(options);

    public static ReptarMiddleware Timeout(TimeSpan timeout) =>
        Middlewares.Timeout(timeout);

    public static ReptarMiddleware RateLimit(
        int limit = 60,
        int windowSeconds = 60,
        string category = "api",
        Func<Ctx, string>? keySelector = null,
        bool trustProxyHeaders = false) =>
        Middlewares.RateLimit(limit, windowSeconds, category, keySelector, trustProxyHeaders);

    public static ReptarMiddleware RateLimit(RateLimitOptions options) =>
        Middlewares.RateLimit(options);
}

public static class Db
{
    public static IDbClient D1() => ReptarApi.D1();

#if !NETWASM
    public static IDbClient Sqlite(string path = "local.db") => new SqliteDbClient(path);
    public static IDbClient MySql(string connectionString) => new MySqlDbClient(connectionString);
#endif

#if !NETWASM
    public static IDbClient Postgres(string connectionString) => new PostgresDbClient(connectionString);
#endif
}
