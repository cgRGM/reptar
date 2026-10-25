#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema/realworld.cs
#:include features/realworld.cs

global using Reptar;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;

var databasePath = Environment.GetEnvironmentVariable("REALWORLD_DB")
    ?? "realworld.db";
var databaseUrl = Environment.GetEnvironmentVariable("REALWORLD_DATABASE_URL")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

IDbClient database = !string.IsNullOrWhiteSpace(databaseUrl) &&
    (databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
     databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    ? Db.Postgres(databaseUrl)
    : Db.Sqlite(databasePath);

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
});

app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors());
app.Use(CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30, options =>
{
    options.ShouldCache = ctx =>
        ctx.Req.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    // Article writes can change the tag list as well as the article lists.
    options.OnInvalidate = (_, path) =>
    {
        if (path.StartsWith("/api/articles", StringComparison.OrdinalIgnoreCase))
            CacheControl.Invalidate("/api/tags");

        if (path.StartsWith("/api/user", StringComparison.OrdinalIgnoreCase))
            CacheControl.Invalidate("/api/profiles");
    };
}));

RealWorld.Configure(app);

await app.EnsureCreatedAsync();
await RealWorld.EnsureIndexesAsync(app);
await app.RunAsync(args);
