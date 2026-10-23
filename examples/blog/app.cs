#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema/posts.cs
#:include schema/index.cs
#:include features/posts.cs

global using Reptar;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;

var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite("blog.db"),
});

app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors());

app.Route("/auth", Auth.Routes());
app.RegisterSchema(Schema.All);
app.Route("/posts", Posts.Routes());

if (args.Contains("--codegen"))
{
    var outputIndex = Array.IndexOf(args, "--out");
    var output = outputIndex >= 0 && outputIndex + 1 < args.Length
        ? Path.GetFullPath(args[outputIndex + 1])
        : Path.GetFullPath("rpc");

    RpcGenerator.Generate(app, output);
    Console.WriteLine($"Generated {output}");
    return;
}

await app.EnsureCreatedAsync();
await app.RunAsync(args);
