using Microsoft.Data.Sqlite;
using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

public sealed class SessionTreeFaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reptar-tree-faults-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "tree.db");

    public SessionTreeFaultTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static ChatMessage Message(string content) => new(AgentRoles.User, content);

    private static Task FailLeafWritesAsync(IDbClient db) => db.ExecuteAsync("""
        CREATE TRIGGER fail_leaf_write BEFORE INSERT ON agent_session_state
        BEGIN SELECT RAISE(ABORT, 'injected leaf persistence failure'); END;
        """);

    [Fact]
    public async Task Failed_append_rolls_back_the_entry_and_preserves_the_acknowledged_leaf()
    {
        using var db = new SqliteDbClient(DbPath);
        var store = new SessionTreeStore(db);
        await store.EnsureCreatedAsync();
        var stable = await store.AppendAsync("session", Message("acknowledged"));
        await FailLeafWritesAsync(db);
        await Assert.ThrowsAsync<SqliteException>(() => store.AppendAsync("session", Message("must roll back")));
        Assert.Equal(stable, await store.LeafAsync("session"));
        Assert.Equal("acknowledged", Assert.Single(await store.EntriesAsync("session")).Content);
        await db.ExecuteAsync("DROP TRIGGER fail_leaf_write");
        await store.AppendAsync("session", Message("after recovery"));
        Assert.Equal(new[] { "acknowledged", "after recovery" },
            (await store.LoadBranchAsync("session")).Select(message => message.Content));
    }

    [Fact]
    public async Task Failed_leaf_switch_does_not_erase_the_previous_leaf()
    {
        using var db = new SqliteDbClient(DbPath);
        var store = new SessionTreeStore(db);
        await store.EnsureCreatedAsync();
        var first = await store.AppendAsync("session", Message("first"));
        var second = await store.AppendAsync("session", Message("second"));
        await FailLeafWritesAsync(db);
        await Assert.ThrowsAsync<SqliteException>(() => store.SetLeafAsync("session", first));
        Assert.Equal(second, await store.LeafAsync("session"));
        Assert.Equal(2, (await store.LoadBranchAsync("session")).Count);
    }

    [Fact]
    public async Task Failed_fork_leaves_no_partial_target_and_does_not_change_the_source()
    {
        using var db = new SqliteDbClient(DbPath);
        var store = new SessionTreeStore(db);
        await store.EnsureCreatedAsync();
        await store.AppendAsync("source", Message("first"));
        var leaf = await store.AppendAsync("source", Message("second"));
        await FailLeafWritesAsync(db);
        await Assert.ThrowsAsync<SqliteException>(() => store.ForkAsync("source", "target"));
        Assert.Empty(await store.EntriesAsync("target"));
        Assert.Null(await store.LeafAsync("target"));
        Assert.Equal(leaf, await store.LeafAsync("source"));
        Assert.Equal(2, (await store.LoadBranchAsync("source")).Count);
    }

    [Fact]
    public async Task Failed_legacy_import_is_retryable_after_reopening_the_database()
    {
        using (var db = new SqliteDbClient(DbPath))
        {
            var store = new SessionTreeStore(db);
            await store.EnsureCreatedAsync();
            await db.EnsureCreatedAsync(ReptarAgentSchema.All);
            var legacy = new AgentSessionStore(db, ReptarAgentSchema.Sessions, ReptarAgentSchema.Messages);
            await legacy.CreateSessionAsync("legacy", "legacy");
            await legacy.AppendMessageAsync("legacy", Message("old first"));
            await legacy.AppendMessageAsync("legacy", Message("old second"));
            await FailLeafWritesAsync(db);
            await Assert.ThrowsAsync<SqliteException>(() => store.ImportFlatAsync("legacy", ReptarAgentSchema.Messages));
            Assert.Empty(await store.EntriesAsync("legacy"));
            await db.ExecuteAsync("DROP TRIGGER fail_leaf_write");
        }
        using var reopened = new SqliteDbClient(DbPath);
        var recovered = new SessionTreeStore(reopened);
        Assert.Equal(2, await recovered.ImportFlatAsync("legacy", ReptarAgentSchema.Messages));
        Assert.Equal(0, await recovered.ImportFlatAsync("legacy", ReptarAgentSchema.Messages));
        Assert.Equal(new[] { "old first", "old second" },
            (await recovered.LoadBranchAsync("legacy")).Select(message => message.Content));
    }

    [Fact]
    public async Task A_fork_cannot_overwrite_an_existing_target_or_copy_into_itself()
    {
        using var db = new SqliteDbClient(DbPath);
        var store = new SessionTreeStore(db);
        await store.EnsureCreatedAsync();
        var source = await store.AppendAsync("source", Message("source"));
        var target = await store.AppendAsync("target", Message("existing target"));
        Assert.False(await store.ForkAsync("source", "target"));
        Assert.False(await store.ForkAsync("source", "source"));
        Assert.Equal(source, await store.LeafAsync("source"));
        Assert.Equal(target, await store.LeafAsync("target"));
        Assert.Single(await store.EntriesAsync("source"));
        Assert.Single(await store.EntriesAsync("target"));
    }

    [Fact]
    public async Task A_foreign_or_unknown_leaf_is_rejected_without_mutating_history()
    {
        using var db = new SqliteDbClient(DbPath);
        var store = new SessionTreeStore(db);
        await store.EnsureCreatedAsync();
        var own = await store.AppendAsync("own", Message("own"));
        var foreign = await store.AppendAsync("foreign", Message("foreign"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetLeafAsync("own", foreign));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetLeafAsync("own", "ent_missing"));
        Assert.Equal(own, await store.LeafAsync("own"));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(43)]
    [InlineData(101)]
    public async Task Independent_clients_serialize_appends_into_one_complete_branch(int seed)
    {
        using var firstDb = new SqliteDbClient(DbPath);
        using var secondDb = new SqliteDbClient(DbPath);
        var stores = new[] { new SessionTreeStore(firstDb), new SessionTreeStore(secondDb) };
        await stores[0].EnsureCreatedAsync();
        var random = new Random(seed);
        var delays = Enumerable.Range(0, 32).Select(_ => random.Next(0, 4)).ToArray();
        var writes = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await Task.Delay(delays[index]);
            return await stores[index % 2].AppendAsync("shared", Message($"seed-{seed}-message-{index}"));
        }));
        var ids = await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(32, ids.Distinct().Count());
        var entries = await stores[0].EntriesAsync("shared");
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (long)value), entries.Select(entry => entry.Seq));
        Assert.Null(entries[0].ParentId);
        for (var index = 1; index < entries.Count; index++)
            Assert.Equal(entries[index - 1].Id, entries[index].ParentId);
        Assert.Equal(entries[^1].Id, await stores[0].LeafAsync("shared"));
        Assert.Equal(32, (await stores[1].LoadBranchAsync("shared")).Count);
    }
}
