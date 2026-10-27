#:project ../../../framework/Reptar/Reptar.csproj
#:project ../../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include lifecycle.cs

using Reptar;
using Reptar.Hosting;

var root = Environment.GetEnvironmentVariable("REPTAR_LIFECYCLE_ROOT")
    ?? throw new InvalidOperationException("A disposable fixture root is required.");
var mode = Environment.GetEnvironmentVariable("REPTAR_LIFECYCLE_MODE") ?? "drain";
var identity = Environment.GetEnvironmentVariable("REPTAR_PROBE_RUN_ID") ?? "missing";
var db = new SqliteDbClient(Path.Combine(root, "queue.db"));
var queue = new DbQueueClient(db, new DbQueueOptions(BaseBackoffSeconds: 1));
await queue.EnsureCreatedAsync();
await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS effects (id TEXT PRIMARY KEY, value TEXT NOT NULL)");
if (!File.Exists(Path.Combine(root, "seeded")))
{
    await queue.SendAsync("work", "{\"id\":\"fixture-work\"}");
    File.WriteAllText(Path.Combine(root, "seeded"), "yes");
}
var app = ReptarApp.Create(new ReptarConfig
{
    Database = db, Queues = queue, Json = new SourceGenJsonCodec(LifecycleJson.Default),
});
app.AddModule(new LifecycleModule(root));
app.Get("/health", ctx => ctx.Text(identity));
app.OnQueue<LifecycleJob>("work", async (ctx, message, job) =>
{
    if (job.Id != "fixture-work")
        throw new InvalidOperationException("Typed queue payload was not decoded by its source-generated codec.");
    if (mode == "retry" && message.Attempts == 1)
        throw new RetryIn(1);
    if (mode == "crash")
        await ctx.Db.ExecuteAsync("INSERT OR IGNORE INTO effects VALUES ('effect', 'committed-before-crash')");
    File.WriteAllText(Path.Combine(root, "handler-started"), "yes");
    try
    {
        if (mode == "cancel")
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted);
        else if (mode is "ignore" or "crash")
            await Task.Delay(Timeout.InfiniteTimeSpan);
        else if (mode == "drain")
        {
            while (!File.Exists(Path.Combine(root, "release")))
                await Task.Delay(10);
        }
        await ctx.Db.ExecuteAsync("INSERT OR IGNORE INTO effects VALUES ('effect', 'completed')");
        File.WriteAllText(Path.Combine(root, "handler-completed"), "yes");
    }
    finally
    {
        File.WriteAllText(Path.Combine(root, "handler-stopped"), "yes");
    }
});
if (mode == "cancel")
{
    app.OnScheduled("* * * * *", async (ctx, _) =>
    {
        File.WriteAllText(Path.Combine(root, "cron-started"), "yes");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted); }
        finally { File.WriteAllText(Path.Combine(root, "cron-stopped"), "yes"); }
    });
}
try
{
    await app.RunAsync();
}
catch (Exception error) when (mode == "ignore")
{
    // An expected deadline failure must be visible without generating a core dump.
    Console.Error.WriteLine($"Expected noncooperative shutdown failure: {error.GetType().Name}");
    Environment.ExitCode = 2;
}
