#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.AI.Gateway/Reptar.AI.Gateway.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj

global using Reptar;
global using Reptar.AI.Gateway;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;

// ─────────────────────────────────────────────────────────────────────────────
// Reptar AI Gateway example.
//
// The reusable gateway (provider routing, failover, OpenAI-compatible SSE /
// Vercel AI Data Stream framing, prompt cache, token telemetry) lives in
// framework/Reptar.AI.Gateway. This app is the composition: database, CORS,
// telemetry, and the gateway routes.
// ─────────────────────────────────────────────────────────────────────────────

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var database = !string.IsNullOrWhiteSpace(databaseUrl)
    ? Db.Postgres(databaseUrl)
    : Db.Sqlite("gateway.db");

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
});

app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors("http://localhost:4321", "http://localhost:5173", "http://localhost:3000"));

AiGateway.Configure(app);
app.RegisterSchema(GatewaySchema.All);

await app.EnsureCreatedAsync();
await app.RunAsync(args);
