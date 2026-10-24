#:include schema/schema.cs
#:include features/auth.cs
#:include features/items.cs

global using Reptar;
global using Reptar.Auth;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;

using ReptarApp.Features;
using ReptarApp.Schema;

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var database = !string.IsNullOrWhiteSpace(databaseUrl)
    ? Db.Postgres(databaseUrl)
    : Db.Sqlite("app.db");

var authOptions = new AuthOptions
{
    CookieName = "better-auth.session_token",
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

await app.EnsureCreatedAsync();
await app.RunAsync(args);
