#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema.cs
#:include verification.cs

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Reptar;
using Reptar.Hosting;
using static Reptar.ReptarApi;

var auth = new AuthOptions
{
    AllowAnonymous = false,
    SecureCookies = Environment.GetEnvironmentVariable("SWITCHYARD_INSECURE_LOCAL") != "1",
    BootstrapAdmins = (Environment.GetEnvironmentVariable("SWITCHYARD_ADMINS") ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
};
var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite(Environment.GetEnvironmentVariable("SWITCHYARD_DB") ?? "switchyard.db"),
    Auth = auth,
    Json = new SourceGenJsonCodec(SwitchyardJson.Default),
});
app.Use(RequestId());
app.Route("/api/auth", Auth.Routes());
app.RegisterSchema(SwitchyardSchema.All);
app.Get("/health", ctx => ctx.Json(new Health("ok", "sqlite-kestrel"))).Produces<Health>(200);

app.Get("/api/projects", async ctx =>
{
    var user = await ctx.UserAsync();
    if (user is null) return ctx.Unauthorized();
    var projects = await ctx.Db.From<ProjectRow>(SwitchyardSchema.Projects)
        .Where(SwitchyardSchema.Projects.Col<string>("owner_id").Eq(user.Id)).Limit(100).ToListAsync();
    return ctx.Json(projects);
}).Produces<List<ProjectRow>>(200);
app.Post<CreateProject>("/api/projects", async (ctx, input) =>
{
    var user = await ctx.UserAsync();
    if (user is null) return ctx.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80) return ctx.BadRequest("Name must be 1–80 characters.");
    var project = new ProjectRow(Ids.New("prj"), user.Id, input.Name.Trim());
    await ctx.Db.InsertAsync(SwitchyardSchema.Projects, project);
    return ctx.Json(project, 201);
}).Produces<ProjectRow>(201);
app.Get("/api/projects/:projectId/flags", async ctx =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    return ctx.Json(await ctx.Db.From<FlagRow>(SwitchyardSchema.Flags)
        .Where(SwitchyardSchema.Flags.Col<string>("project_id").Eq(ctx.Req.Param("projectId")!)).Limit(100).ToListAsync());
}).Produces<List<FlagRow>>(200);
app.Post<CreateFlag>("/api/projects/:projectId/flags", async (ctx, input) =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    if (!Access.ValidKey(input.Key) || !Access.ValidEnvironment(input.Environment)) return ctx.BadRequest("Use a lowercase flag key and development, staging, or production.");
    var row = new FlagRow(ctx.Req.Param("projectId")!, input.Environment, input.Key, 0, 0, "[]", 0, 0);
    var changed = await ctx.Db.ExecuteAsync("""
        INSERT INTO sy_flags(project_id, environment, key, enabled, rollout, targets, revision, version)
        VALUES(@project, @environment, @key, 0, 0, '[]', 0, 0) ON CONFLICT DO NOTHING
        """, ("project", row.ProjectId), ("environment", row.Environment), ("key", row.Key));
    return changed == 0 ? Access.Conflict(ctx) : ctx.Json(row, 201);
}).Produces<FlagRow>(201).Produces<Problem>(409);
app.Put<DraftInput>("/api/projects/:projectId/flags/:environment/:key/draft", async (ctx, input) =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    if (input.Rollout is < 0 or > 100 || input.ExpectedRevision < 0 || input.Targets is null || input.Targets.Length > 100 || input.Targets.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200))
        return ctx.BadRequest("Rollout must be 0–100; targets must contain at most 100 nonempty subject keys of at most 200 characters.");
    var changed = await ctx.Db.ExecuteAsync("""
        UPDATE sy_flags SET enabled=@enabled, rollout=@rollout, targets=@targets, revision=revision+1
        WHERE project_id=@project AND environment=@environment AND key=@key AND revision=@revision
        """, Access.Parameters(ctx).Concat(new (string, object?)[] {
            ("enabled", input.Enabled ? 1 : 0), ("rollout", input.Rollout),
            ("targets", JsonSerializer.Serialize(input.Targets, SwitchyardJson.Default.StringArray)), ("revision", input.ExpectedRevision) }).ToArray());
    if (changed == 0) return Access.Conflict(ctx);
    return ctx.Json((await Access.Flag(ctx))!);
}).Produces<FlagRow>(200).Produces<Problem>(409);
app.Post<PublishInput>("/api/projects/:projectId/flags/:environment/:key/publish", async (ctx, input) =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    // SQLite's real transaction pins reads and writes to the same connection.
    // This intentionally does not pretend D1's write-only buffer is equivalent.
    var published = await ctx.Db.TransactionAsync(async tx =>
    {
        var row = await tx.QuerySingleOrDefaultAsync<FlagRow>("SELECT * FROM sy_flags WHERE project_id=@project AND environment=@environment AND key=@key", Access.Parameters(ctx));
        if (row is null || row.Revision != input.ExpectedRevision || row.Version != input.ExpectedVersion) return null;
        var user = (await ctx.UserAsync())!;
        var version = new VersionRow(row.ProjectId, row.Environment, row.Key, row.Version + 1,
            row.Enabled, row.Rollout, row.Targets, user.Id, ctx.Clock.IsoNow());
        await tx.InsertAsync(SwitchyardSchema.Versions, version);
        await tx.ExecuteAsync("UPDATE sy_flags SET version=version+1 WHERE project_id=@project AND environment=@environment AND key=@key", Access.Parameters(ctx));
        await tx.InsertAsync(SwitchyardSchema.Audit, new AuditRow(Ids.New("audit"), row.ProjectId, row.Environment, row.Key, version.Version, user.Id, version.CreatedAt));
        return version;
    });
    return published is null ? Access.Conflict(ctx) : ctx.Json(published, 201);
}).Produces<VersionRow>(201).Produces<Problem>(409);
app.Get("/api/projects/:projectId/flags/:environment/:key/versions", async ctx =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    return ctx.Json(await ctx.Db.QueryAsync<VersionRow>("SELECT * FROM sy_versions WHERE project_id=@project AND environment=@environment AND key=@key ORDER BY version DESC LIMIT 100", Access.Parameters(ctx)));
}).Produces<List<VersionRow>>(200);
app.Get("/api/projects/:projectId/audit", async ctx =>
{
    var denied = await Access.Check(ctx);
    if (denied is not null) return denied;
    return ctx.Json(await ctx.Db.QueryAsync<AuditRow>("SELECT * FROM sy_audit WHERE project_id=@project ORDER BY created_at DESC, id DESC LIMIT 100", ("project", ctx.Req.Param("projectId"))));
}).Produces<List<AuditRow>>(200);
app.Get("/api/evaluate/:projectId/:environment/:key", ctx => Access.Evaluate(ctx, ctx.Req.QueryValue("subject")))
    .WithQuery<EvaluateInput>().Produces<Evaluation>().Produces(304);
// Keep the body-based route for callers that prefer POST. Both routes now
// declare complete inputs; handlers still validate subject values explicitly.
app.Post<EvaluateInput>("/api/evaluate/:projectId/:environment/:key", (ctx, input) => Access.Evaluate(ctx, input.Subject))
    .Produces<Evaluation>(200);

if (args.Contains("--schema")) { Console.WriteLine(app.ExportSchemaJson()); return; }
if (args.Contains("--codegen"))
{
    var index = Array.IndexOf(args, "--out");
    RpcGenerator.Generate(app, index >= 0 ? args[index + 1] : "rpc");
    return;
}
await app.EnsureCreatedAsync();
if (args.Contains("--self-test"))
{
    await SwitchyardVerification.Run(app.Config.Database!);
    return;
}
await app.RunAsync(args);

public static partial class Access
{
    public static async Task<ReptarResponse> Evaluate(Ctx ctx, string? subject)
    {
    var denied = await Check(ctx);
    if (denied is not null) return denied;
    if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200) return ctx.BadRequest("Subject must be 1–200 characters.");
    // One joined statement gives a coherent active-version snapshot; no stale process cache.
    var version = await ctx.Db.QuerySingleOrDefaultAsync<VersionRow>("""
        SELECT v.* FROM sy_versions v JOIN sy_flags f
        ON f.project_id=v.project_id AND f.environment=v.environment AND f.key=v.key AND f.version=v.version
        WHERE f.project_id=@project AND f.environment=@environment AND f.key=@key
        """, Access.Parameters(ctx));
    if (version is null) return ctx.NotFound("No published version.");
    var bucket = Rollouts.Bucket(version.ProjectId, version.Environment, version.Key, subject);
    var targeted = (JsonSerializer.Deserialize(version.Targets, SwitchyardJson.Default.StringArray) ?? []).Contains(subject, StringComparer.Ordinal);
    var enabled = version.Enabled == 1 && (targeted || bucket < version.Rollout);
    var reason = version.Enabled == 0 ? "disabled" : targeted ? "targeted" : "percentage";
    var tag = $"\"v{version.Version}-b{bucket}-e{(enabled ? 1 : 0)}-{reason}\"";
    ctx.SetHeader("ETag", tag);
    ctx.SetHeader("Cache-Control", "private, no-cache");
    ctx.SetHeader("Vary", "Cookie, Authorization");
    if (ctx.Req.Method == "GET" && ctx.Req.Header("If-None-Match")?.Split(',').Any(x => x.Trim() == tag || x.Trim() == "*" || x.Trim() == "W/" + tag) == true)
        return ctx.Text("", 304);
    return ctx.Json(new Evaluation(enabled, bucket, version.Version, reason));
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    public static bool ValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);
    public static bool ValidEnvironment(string? environment) => environment is "development" or "staging" or "production";
    public static ReptarResponse Conflict(Ctx ctx) => ctx.Json(new Problem("conflict", "Resource changed or already exists. Reload before retrying."), 409);
    public static (string Name, object? Value)[] Parameters(Ctx ctx) => [
        ("project", ctx.Req.Param("projectId")), ("environment", ctx.Req.Param("environment")), ("key", ctx.Req.Param("key"))];
    public static Task<FlagRow?> Flag(Ctx ctx) => ctx.Db.QuerySingleOrDefaultAsync<FlagRow>(
        "SELECT * FROM sy_flags WHERE project_id=@project AND environment=@environment AND key=@key", Parameters(ctx));
    public static async Task<ReptarResponse?> Check(Ctx ctx)
    {
        var user = await ctx.UserAsync();
        if (user is null) return ctx.Unauthorized();
        var project = await ctx.Db.GetAsync<ProjectRow>(SwitchyardSchema.Projects, ctx.Req.Param("projectId")!);
        return project is null || (project.OwnerId != user.Id && user.Role != "admin") ? ctx.NotFound() : null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectRow))]
[JsonSerializable(typeof(List<ProjectRow>))]
[JsonSerializable(typeof(FlagRow))]
[JsonSerializable(typeof(List<FlagRow>))]
[JsonSerializable(typeof(VersionRow))]
[JsonSerializable(typeof(List<VersionRow>))]
[JsonSerializable(typeof(AuditRow))]
[JsonSerializable(typeof(List<AuditRow>))]
[JsonSerializable(typeof(CreateProject))]
[JsonSerializable(typeof(CreateFlag))]
[JsonSerializable(typeof(DraftInput))]
[JsonSerializable(typeof(PublishInput))]
[JsonSerializable(typeof(Evaluation))]
[JsonSerializable(typeof(EvaluateInput))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(string[]))]
internal partial class SwitchyardJson : JsonSerializerContext;
