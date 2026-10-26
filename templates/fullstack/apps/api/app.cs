#:include schema/schema.cs
#:include features/auth.cs
#:include features/items.cs
#:include features/responses.cs
#:include json-context.cs

global using Reptar;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;

using {{PROJECT_PASCAL}};
using {{PROJECT_PASCAL}}.Features;
using {{PROJECT_PASCAL}}.Schema;

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var database = !string.IsNullOrWhiteSpace(databaseUrl)
    ? Db.Postgres(databaseUrl)
    : Db.Sqlite("app.db");

var authOptions = new AuthOptions
{
    CookieName = "better-auth.session_token",
#if DEBUG
    // Local `dotnet run` serves plain HTTP. Release/AOT builds keep Secure.
    SecureCookies = false,
#endif
};

// Wire Google OAuth if credentials provided
var googleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
var googleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    authOptions.WithGoogle(googleClientId, googleClientSecret);
}

// Wire GitHub OAuth if credentials provided
var githubClientId = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");
var githubClientSecret = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET");
if (!string.IsNullOrEmpty(githubClientId) && !string.IsNullOrEmpty(githubClientSecret))
{
    authOptions.WithGitHub(githubClientId, githubClientSecret);
}

var app = ReptarApp.Create(new ReptarConfig
{
    Auth = authOptions,
    Database = database,
    // Source-generated JSON: no runtime reflection under Native AOT/Wasm.
    Json = new SourceGenJsonCodec(AppJsonContext.Default),
});

app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors(new CorsOptions
{
    AllowOrigins = ["http://localhost:5173", "http://localhost:3000"],
    AllowMethods = ["GET", "POST", "PUT", "DELETE", "OPTIONS"],
    AllowHeaders = ["Content-Type", "Authorization", "Cookie"],
    AllowCredentials = true,
}));

AuthFeature.Configure(app, authOptions);
ItemsFeature.Configure(app);

// Registers AppSchema.Items for EnsureCreatedAsync (the auth tables were
// already merged from the BetterAuthProtocol mount above).
app.RegisterSchema(AppSchema.Items);

// Liveness probe — the deploy stack health-gates on it.
app.Get("/health", ctx =>
    ctx.Json(new HealthResponse(true, DateTimeOffset.UtcNow.ToString("O"))))
    .Produces<HealthResponse>(200);

if (args.Contains("--codegen"))
{
    var outputIndex = Array.IndexOf(args, "--out");
    var output = outputIndex >= 0 && outputIndex + 1 < args.Length
        ? Path.GetFullPath(args[outputIndex + 1])
        : Path.GetFullPath("rpc");

    RpcGenerator.Generate(app, output);
    Console.WriteLine($"[Reptar] Generated type-safe client contracts at {output}");
    return;
}

// Creates every registered table (app schema + the auth tables merged from
// the BetterAuthProtocol mount) — idempotent, safe on every boot.
await app.EnsureCreatedAsync();
await app.RunAsync(args);
