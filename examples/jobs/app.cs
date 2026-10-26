#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj

global using Reptar;
global using Reptar.Hosting;
global using static Reptar.ReptarApi;
using System.Text.Json.Nodes;

// Queue adapter selection: sync (inline, the native default), db
// (SQLite-durable), sqs, azure-storage, or pubsub. The app code never
// changes — only the REPTAR_QUEUE environment variable does.
var mode = Environment.GetEnvironmentVariable("REPTAR_QUEUE") ?? "sync";

var database = Db.Sqlite("jobs.db");

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
    Queues = mode switch
    {
        // Sync needs no explicit client: it is the native default and
        // resolves the app lazily at first send.
        "sync" => null,
        "db" => Queues.Db(database),
#if !NETWASM
        "sqs" => Queues.SqsFromEnvironment(),
        "azure-storage" or "azure" => Queues.AzureStorageFromEnvironment(),
        "pubsub" or "gcp" => Queues.PubSubFromEnvironment(),
#endif
        _ => null,
    },
});

app.Get("/stats", async ctx =>
{
    var processed = await ctx.Db.QuerySingleOrDefaultAsync<DbCount>(
        "select count(*) as count from bench_results");
    return ctx.JsonRaw(new JsonObject
    {
        ["processed"] = processed?.Count ?? 0,
        ["mode"] = mode,
    }.ToJsonString());
});

app.Post("/enqueue", async ctx =>
{
    var count = int.TryParse(ctx.Req.QueryValue("n"), out var n) ? n : 1;
    var delay = int.TryParse(ctx.Req.QueryValue("delay"), out var d) ? d : 0;

    for (var index = 0; index < count; index++)
    {
        // Source-generated JSON context: file-based apps disable
        // reflection-based serialization (the check:netwasm rule).
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new BenchJob($"job-{index}", DateTimeOffset.UtcNow.ToString("O")),
            JobsJsonContext.Default.BenchJob);

        await ctx.Queues.SendAsync("jobs", payload, new QueueSendOptions(DelaySeconds: delay));
    }

    return ctx.JsonRaw(new JsonObject
    {
        ["queued"] = count,
        ["mode"] = mode,
    }.ToJsonString());
});

app.OnQueue<BenchJob>("jobs", async (ctx, msg, job) =>
{
    await ctx.Db.ExecuteAsync(
        "insert or ignore into bench_results (id, job_id, processed_at) values (@id, @jobId, @at)",
        new { id = Ids.New("res"), jobId = job.Id, at = DateTimeOffset.UtcNow.ToString("O") });
});

app.OnScheduled("*/1 * * * *", async (ctx, evt) =>
{
    await ctx.Db.ExecuteAsync(
        "insert or replace into bench_results (id, job_id, processed_at) values ('tick', @jobId, @at)",
        new { jobId = $"cron-{evt.Cron}", at = evt.ScheduledTime });
});

await app.Db.ExecuteAsync(
    "create table if not exists bench_results (id text primary key, job_id text not null, processed_at text not null)");

if (app.Queues is DbQueueClient dbQueue)
{
    await dbQueue.EnsureCreatedAsync();
}

await app.RunAsync(args);

public sealed record BenchJob(string Id, string EnqueuedAt);

[System.Text.Json.Serialization.JsonSerializable(typeof(BenchJob))]
internal partial class JobsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
