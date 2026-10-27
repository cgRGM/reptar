#!/usr/bin/env dotnet run
#:project ../../../framework/Reptar/Reptar.csproj
#:project ../../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include ../../../examples/realtime-rooms/schema.cs
#:include ../../../examples/realtime-rooms/hub.cs

using Reptar;
using System.Runtime.CompilerServices;
using System.Text.Json;

try
{
    if (RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("Publish this probe with Native AOT.");
    var root = args[0];
    if (args.Length > 1)
    {
        if (args[1] == "crash-producer")
            await CrashProducerAsync(root);
        else if (args[1] == "verify-loss")
            await VerifyLossAsync(root);
        else
            throw new ArgumentException("Unknown fault mode.");
        return;
    }
    await SaturationAsync(Path.Combine(root, "saturation.db"));
    await FailedWriteAsync(Path.Combine(root, "failure.db"));
    await ModuleDrainAsync(Path.Combine(root, "drain.db"));
    Console.WriteLine("PASS real SQLite saturation, failure continuation, stable ids and module drain");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task UntilAsync(Func<bool> predicate)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate())
        await Task.Delay(10, deadline.Token);
}

static async Task SaturationAsync(string path)
{
    var db = new SqliteDbClient(path);
    await db.EnsureCreatedAsync(Schema.All);
    var app = ReptarApp.Create(new ReptarConfig { Database = db });
    var persister = new RoomMessagePersister(db, workerCount: 1, queueCapacity: 1);
    app.AddModule(persister);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var transaction = db.TransactionAsync(async _ =>
    {
        entered.TrySetResult();
        await release.Task;
    });
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(persister.TryEnqueue("room", "sender", "{}", "first"), "First enqueue rejected.");
        await UntilAsync(() => persister.ActiveWrites == 1);
        Require(persister.TryEnqueue("room", "sender", "{}", "second"), "Pending enqueue rejected.");
        Require(!persister.TryEnqueue("room", "sender", "{}", "overflow"), "Saturation silently accepted excess work.");
        Require(persister.QueueDepth == 1 && persister.PersistedCount == 0 && persister.DroppedCount == 1,
            "Queue admission was confused with durable completion.");
        using (var stats = JsonDocument.Parse(new RoomHub(persister).StatsJson()))
        {
            Require(stats.RootElement.GetProperty("messagesPersistDropped").GetInt64() == 1,
                "Dropped persistence is not observable.");
            Require(stats.RootElement.GetProperty("persisterActiveWrites").GetInt64() == 1,
                "Active writes disappeared from operational stats.");
        }
        var stopped = persister.StopAsync(app, CancellationToken.None);
        Require(!stopped.IsCompleted, "Stop returned before accepted writes drained.");
        Require(!persister.TryEnqueue("room", "sender", "{}", "after-stop"), "Stopped persister accepted work.");
        release.TrySetResult();
        await transaction.WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Require(persister.PersistedCount == 2 && persister.FailedCount == 0 && persister.DroppedCount == 2,
            "Saturation/stop counters are inconsistent.");
        Require(persister.QueueDepth == 0 && persister.ActiveWrites == 0, "Stopped persister retained work.");
        var ids = await db.QueryMappedAsync("SELECT id FROM room_messages ORDER BY id", null, row => row["id"] as string ?? throw new InvalidOperationException("Missing id projection."));
        Require(ids.SequenceEqual(["first", "second"]), "Accepted identities were lost, duplicated or replaced.");
    }
    finally
    {
        release.TrySetResult();
        await transaction.WaitAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

static async Task FailedWriteAsync(string path)
{
    var db = new SqliteDbClient(path);
    await db.EnsureCreatedAsync(Schema.All);
    await db.ExecuteAsync("CREATE TRIGGER fail_room BEFORE INSERT ON room_messages WHEN NEW.id = 'failed' BEGIN SELECT RAISE(ABORT, 'injected-room-write-failure'); END");
    var app = ReptarApp.Create(new ReptarConfig { Database = db });
    var persister = new RoomMessagePersister(db, workerCount: 1, queueCapacity: 8);
    app.AddModule(persister);
    try
    {
        Require(persister.TryEnqueue("room", "sender", "{}", "failed"), "Failure checkpoint was not admitted.");
        Require(persister.TryEnqueue("room", "sender", "{}", "good"), "Continuation was not admitted.");
        await persister.StopAsync(app, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Require(persister.PersistedCount == 1 && persister.FailedCount == 1 && persister.DroppedCount == 0,
            "A failed write was acknowledged or killed the worker.");
        Require(persister.LastErrorJson.Contains("injected-room-write-failure", StringComparison.Ordinal),
            "The injected storage error disappeared.");
        Require(await db.QuerySingleOrDefaultAsync<string>("SELECT id FROM room_messages") == "good",
            "Worker did not continue after the injected failure.");
    }
    finally
    {
        await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

static async Task CrashProducerAsync(string root)
{
    using var db = new SqliteDbClient(Path.Combine(root, "crash.db"));
    await db.EnsureCreatedAsync(Schema.All);
    var persister = new RoomMessagePersister(db, workerCount: 1, queueCapacity: 1);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var heldTransaction = db.TransactionAsync(async _ =>
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite);
    });
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(persister.TryEnqueue("room", "sender", "{}", "admitted"), "Crash admission failed.");
    await UntilAsync(() => persister.ActiveWrites == 1);
    Require(persister.PersistedCount == 0, "Crash checkpoint unexpectedly committed.");
    File.WriteAllText(Path.Combine(root, "admitted.txt"), "ADMITTED_NOT_COMMITTED:admitted");
    await heldTransaction; // The parent owns the deliberate SIGKILL and deadline.
}

static async Task VerifyLossAsync(string root)
{
    var path = Path.Combine(root, "crash.db");
    Require(File.Exists(path), "Missing producer database would hide a failed checkpoint.");
    using var db = new SqliteDbClient(path);
    Require(await db.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM room_messages") == 0,
        "The uncommitted checkpoint unexpectedly survived.");
    Require(await db.QuerySingleOrDefaultAsync<string>("PRAGMA integrity_check") == "ok",
        "SQLite integrity failed after owned producer SIGKILL.");
    Console.WriteLine("PASS owned SIGKILL loses uncommitted admission without database corruption");
}

static async Task ModuleDrainAsync(string path)
{
    var db = new SqliteDbClient(path);
    await db.EnsureCreatedAsync(Schema.All);
    var app = ReptarApp.Create(new ReptarConfig { Database = db });
    var persister = new RoomMessagePersister(db, workerCount: 4, queueCapacity: 256);
    app.AddModule(persister);
    for (var index = 0; index < 128; index++)
        Require(persister.TryEnqueue("room", "sender", "{}", $"accepted-{index:D3}"), "Bounded drain fixture overflowed.");
    await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    Require(persister.PersistedCount == 128 && persister.FailedCount == 0,
        "Module/database shutdown lost admitted write-behind work.");
    Require(persister.QueueDepth == 0 && persister.ActiveWrites == 0, "Module shutdown left work active.");
    using var reopened = new SqliteDbClient(path);
    Require(await reopened.QuerySingleOrDefaultAsync<long>("SELECT COUNT(*) FROM room_messages") == 128,
        "Drained acknowledgements did not survive reopening SQLite.");
    Require(await reopened.QuerySingleOrDefaultAsync<string>("PRAGMA integrity_check") == "ok",
        "SQLite integrity failed after drain.");
}
