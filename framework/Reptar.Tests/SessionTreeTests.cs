using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the session tree: linear append, in-place branching, the active-branch walk,
/// forking, and the flat-table import that keeps old sessions loadable.
/// </summary>
public sealed class SessionTreeTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public SessionTreeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-tree-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "tree.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ReptarApp CreateApp() => ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(_dbPath) });

    private static ChatMessage User(string text) => new(AgentRoles.User, text);
    private static ChatMessage Assistant(string text) => new(AgentRoles.Assistant, text);

    private async Task<SessionTreeStore> OpenAsync(ReptarApp app)
    {
        var store = new SessionTreeStore(app.Db);
        await store.EnsureCreatedAsync();
        return store;
    }

    // ------------------------------------------------------------------
    // Linear append
    // ------------------------------------------------------------------

    [Fact]
    public async Task appending_chains_entries_and_advances_the_leaf()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        var first = await store.AppendAsync("s1", User("hello"));
        var second = await store.AppendAsync("s1", Assistant("hi"));

        Assert.NotEqual(first, second);
        Assert.Equal(second, await store.LeafAsync("s1"));

        var branch = await store.LoadBranchAsync("s1");
        Assert.Equal(["hello", "hi"], branch.Select(message => message.Content));
    }

    [Fact]
    public async Task an_empty_session_has_no_leaf_and_no_branch()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        Assert.Null(await store.LeafAsync("nobody"));
        Assert.Empty(await store.LoadBranchAsync("nobody"));
    }

    [Fact]
    public async Task tool_call_metadata_survives_the_round_trip()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", new ChatMessage(
            AgentRoles.Assistant, "", ToolCalls: [new AiToolCall("c1", "read_file", """{"path":"a"}""")]));
        await store.AppendAsync("s1", new ChatMessage(
            AgentRoles.Tool, "contents", Name: "read_file", ToolCallId: "c1"));

        var branch = await store.LoadBranchAsync("s1");

        Assert.Single(branch[0].ToolCalls!);
        Assert.Equal("read_file", branch[1].Name);
        Assert.Equal("c1", branch[1].ToolCallId);
    }

    // ------------------------------------------------------------------
    // Branching
    // ------------------------------------------------------------------

    [Fact]
    public async Task moving_the_leaf_back_starts_a_new_branch_without_losing_the_old_one()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("q1"));
        var afterFirst = await store.AppendAsync("s1", Assistant("a1"));
        await store.AppendAsync("s1", User("q2"));
        await store.AppendAsync("s1", Assistant("a2"));

        // Go back to just after the first answer and try something else.
        await store.SetLeafAsync("s1", afterFirst);
        await store.AppendAsync("s1", User("q2-alt"));
        await store.AppendAsync("s1", Assistant("a2-alt"));

        var branch = await store.LoadBranchAsync("s1");

        Assert.Equal(["q1", "a1", "q2-alt", "a2-alt"], branch.Select(message => message.Content));

        // The abandoned continuation is still stored.
        var entries = await store.EntriesAsync("s1");
        Assert.Contains(entries, entry => entry.Content == "q2");
        Assert.Contains(entries, entry => entry.Content == "a2");
    }

    [Fact]
    public async Task switching_back_to_the_other_branch_restores_it()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("root"));
        var forkPoint = await store.LeafAsync("s1");

        await store.AppendAsync("s1", Assistant("branch A"));

        await store.SetLeafAsync("s1", forkPoint);
        await store.AppendAsync("s1", Assistant("branch B"));

        Assert.Equal(["root", "branch B"],
            (await store.LoadBranchAsync("s1")).Select(message => message.Content));

        var branchA = (await store.EntriesAsync("s1"))
            .Single(entry => entry.Content == "branch A");

        await store.SetLeafAsync("s1", branchA.Id);

        Assert.Equal(["root", "branch A"],
            (await store.LoadBranchAsync("s1")).Select(message => message.Content));
    }

    [Fact]
    public async Task the_tree_reports_both_children_of_the_fork_point()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("root"));
        var forkPoint = await store.LeafAsync("s1");
        await store.AppendAsync("s1", Assistant("A"));

        await store.SetLeafAsync("s1", forkPoint);
        await store.AppendAsync("s1", Assistant("B"));

        var tree = await store.TreeAsync("s1");

        var root = Assert.Single(tree);
        Assert.Equal("root", root.Entry.Content);
        Assert.Equal(2, root.Children.Count);
        Assert.Contains(root.Children, child => child.Entry.Content == "A");
        Assert.Contains(root.Children, child => child.Entry.Content == "B");
    }

    [Fact]
    public async Task the_described_tree_marks_the_leaf()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("root"));
        var forkPoint = await store.LeafAsync("s1");
        await store.AppendAsync("s1", Assistant("A"));
        await store.SetLeafAsync("s1", forkPoint);
        await store.AppendAsync("s1", Assistant("B"));

        var rendered = await store.DescribeTreeAsync("s1");

        Assert.Contains("Session tree", rendered);
        Assert.Contains("◀ leaf", rendered);
        Assert.Contains("user: root", rendered);
    }

    // ------------------------------------------------------------------
    // Forking
    // ------------------------------------------------------------------

    [Fact]
    public async Task forking_copies_the_branch_and_leaves_the_source_untouched()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("q1"));
        await store.AppendAsync("s1", Assistant("a1"));

        Assert.True(await store.ForkAsync("s1", "s2"));

        Assert.Equal(["q1", "a1"],
            (await store.LoadBranchAsync("s2")).Select(message => message.Content));
        Assert.Equal(2, (await store.LoadBranchAsync("s1")).Count);

        // The fork is independent.
        await store.AppendAsync("s2", User("only in the fork"));
        Assert.Equal(2, (await store.LoadBranchAsync("s1")).Count);
        Assert.Equal(3, (await store.LoadBranchAsync("s2")).Count);
    }

    [Fact]
    public async Task forking_from_an_earlier_entry_truncates_at_that_point()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        var first = await store.AppendAsync("s1", User("q1"));
        await store.AppendAsync("s1", Assistant("a1"));

        Assert.True(await store.ForkAsync("s1", "s2", fromEntryId: first));

        var branch = await store.LoadBranchAsync("s2");
        var only = Assert.Single(branch);
        Assert.Equal("q1", only.Content);
    }

    [Fact]
    public async Task forking_an_empty_session_is_refused()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        Assert.False(await store.ForkAsync("empty", "copy"));
    }

    [Fact]
    public async Task forking_from_a_missing_entry_is_refused()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);
        await store.AppendAsync("s1", User("q1"));

        Assert.False(await store.ForkAsync("s1", "s2", fromEntryId: "ent_nope"));
    }

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    [Fact]
    public async Task flat_messages_import_as_a_linear_chain()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        var flat = ReptarAgentSchema.Messages;
        await app.Db.EnsureCreatedAsync(ReptarAgentSchema.Sessions, flat);

        var legacy = new AgentSessionStore(app.Db, ReptarAgentSchema.Sessions, flat);
        await legacy.CreateSessionAsync("old", "s-legacy");
        await legacy.AppendMessageAsync("s-legacy", User("old q"));
        await legacy.AppendMessageAsync("s-legacy", Assistant("old a"));

        var imported = await store.ImportFlatAsync("s-legacy", flat);

        Assert.Equal(2, imported);
        Assert.Equal(["old q", "old a"],
            (await store.LoadBranchAsync("s-legacy")).Select(message => message.Content));
        Assert.NotNull(await store.LeafAsync("s-legacy"));
    }

    [Fact]
    public async Task importing_twice_is_a_no_op()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        var flat = ReptarAgentSchema.Messages;
        await app.Db.EnsureCreatedAsync(ReptarAgentSchema.Sessions, flat);
        var legacy = new AgentSessionStore(app.Db, ReptarAgentSchema.Sessions, flat);
        await legacy.CreateSessionAsync("old", "s-legacy");
        await legacy.AppendMessageAsync("s-legacy", User("q"));

        await store.ImportFlatAsync("s-legacy", flat);
        var second = await store.ImportFlatAsync("s-legacy", flat);

        Assert.Equal(0, second);
        Assert.Single(await store.EntriesAsync("s-legacy"));
    }

    // ------------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_session_without_a_leaf_falls_back_to_the_newest_entry()
    {
        var app = CreateApp();
        var store = await OpenAsync(app);

        await store.AppendAsync("s1", User("q1"));
        await store.AppendAsync("s1", Assistant("a1"));

        // Simulate a session written by a process that never recorded a leaf.
        await app.Db.ExecuteAsync(
            $"delete from \"{SessionTreeStore.State.Name}\" where session_id = @id", ("id", "s1"));

        Assert.Equal(["q1", "a1"],
            (await store.LoadBranchAsync("s1")).Select(message => message.Content));
    }

    [Fact]
    public void build_tree_tolerates_an_orphan_parent()
    {
        var entries = new List<SessionEntryRow>
        {
            new("e1", "s", null, 1, "user", "root", null, null, null, ""),
            new("e2", "s", "missing", 2, "assistant", "orphan", null, null, null, "")
        };

        var tree = SessionTreeStore.BuildTree(entries);

        // Both become roots rather than one disappearing.
        Assert.Equal(2, tree.Count);
    }
}
