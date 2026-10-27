#:project ../../../framework/Reptar/Reptar.csproj
#:project ../../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include probe.cs

using Reptar;
using Reptar.Hosting;
using System.Text;

var runId = Environment.GetEnvironmentVariable("REPTAR_PROBE_RUN_ID")
    ?? throw new InvalidOperationException("REPTAR_PROBE_RUN_ID is required for this test-only server.");
var app = ReptarApp.Create(new ReptarConfig
{
    Json = new SourceGenJsonCodec(ProbeJson.Default),
    MaxBodyBytes = 64,
});
app.Get("/health", ctx => ctx.Text(runId));
app.Get("/small", ctx => ctx.Text("Hello, World!"));
app.Get("/large", ctx => ctx.Text(new string('x', 4096)));
app.Get("/query/:key", ctx => ctx.Json(new QueryResult(
    ctx.Param("key") ?? "", ctx.Req.QueryValue("subject") ?? "", ctx.Req.QueryValue("enabled") ?? "")))
    .WithQuery<ProbeQuery>().Produces<QueryResult>();
foreach (var status in new[] { 204, 205, 304 })
{
    var code = status;
    app.Get($"/empty/{code}", ctx => ctx.Text("forbidden-body", code)).Produces(code);
}
app.Post("/buffer", ctx => ctx.Text(ctx.Req.BodyText ?? "")).WithMaxBody(16);
app.Post("/stream-body", async ctx =>
{
    using var body = ctx.OpenBody() ?? Stream.Null;
    using var bytes = new MemoryStream();
    await body.CopyToAsync(bytes, ctx.RequestAborted);
    return ctx.Text(Encoding.UTF8.GetString(bytes.ToArray()));
}).StreamBody(16);
app.Get("/stream/:mode", ctx => ctx.Stream(
    token => ProbeState.Stream(ctx.Param("mode") ?? "normal", token), "text/plain"));
app.Get("/sse", ctx => ctx.Sse(token => ProbeState.Events(token)));
app.Get("/stats", ctx => ctx.Json(ProbeState.Snapshot())).Produces<StreamStats>();
if (args.Contains("--codegen"))
{
    var index = Array.IndexOf(args, "--out");
    RpcGenerator.Generate(app, index >= 0 ? args[index + 1] : "rpc");
    return;
}
await app.RunAsync(args);
