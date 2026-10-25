using System.Diagnostics;

namespace Reptar;

public sealed class ReptarConfig
{
    public bool Debug { get; set; }
    public IDbClient? Database { get; set; }
    public IR2Client? Storage { get; set; }
    public IAiClient? AI { get; set; }
    public IRateLimiter? RateLimit { get; set; }
    public IKvClient? Cache { get; set; }
    public IQueueClient? Queues { get; set; }
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
    public bool AllowAnonymous { get; set; } = true;
    public string[] BootstrapAdmins { get; set; } = [];
    public JwtOptions? Jwt { get; set; }
#if !NETWASM
    public List<IOAuthProvider> OAuthProviders { get; set; } = [];
    public int RateLimitMaxAttempts { get; set; } = 5;
    public int RateLimitWindowMinutes { get; set; } = 15;
    public bool EnableRateLimiting { get; set; } = true;

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
}

public static class ReptarApi
{
    public static readonly ColApi Col = new();

    public static ReptarApp Create(ReptarConfig? config = null) =>
        ReptarApp.Create(config);

    public static Validator<T> Rule<T>() => new();

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

    public static IAiClient WorkersAI() => new NoopAiClient();

    public static ReptarMiddleware RequestId() => Middlewares.RequestId();

    public static ReptarMiddleware Cors() => Middlewares.Cors();

    public static ReptarMiddleware Cors(params string[] allowedOrigins) =>
        Middlewares.Cors(allowedOrigins);

    public static ReptarMiddleware Cors(CorsOptions? options) =>
        Middlewares.Cors(options);

    public static ReptarMiddleware Telemetry() => Middlewares.Telemetry();

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
#if !NETWASM
    public static IDbClient Sqlite(string path = "local.db") => new SqliteDbClient(path);
    public static IDbClient MySql(string connectionString) => new MySqlDbClient(connectionString);
#endif

    public static IDbClient Postgres(string connectionString) => new PostgresDbClient(connectionString);
}
