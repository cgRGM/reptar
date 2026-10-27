#if !NETWASM
using System.Data;
using static Reptar.ReptarApi;

namespace Reptar.AI;

/// <summary>
/// Tables for the session tree. Deliberately separate from the flat message table so adding a
/// tree does not require altering a database that already exists.
/// </summary>
/// <remarks>
/// Ported from pi's session format: entries are append-only and each one names its parent, so a
/// branch is just an entry whose parent is not the current leaf. The leaf is stored separately
/// because it is the only mutable piece — everything else is immutable history.
/// </remarks>
public static class SessionTreeSchema
{
    /// <summary>Immutable transcript records forming a tree via <c>parent_id</c>.</summary>
    public static readonly TableDef Entries = DefineTable("agent_session_entries",
        Col.Text("id").PrimaryKey(),
        Col.Text("session_id").NotNull().Indexed(),
        Col.Text("parent_id"),
        Col.Int("seq").NotNull(),
        Col.Text("role").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("tool_calls"),
        Col.Text("name"),
        Col.Text("tool_call_id"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    /// <summary>One row per session: the active leaf, and nothing else that mutates.</summary>
    public static readonly TableDef State = DefineTable("agent_session_state",
        Col.Text("session_id").PrimaryKey(),
        Col.Text("leaf_id"),
        Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef[] All = [Entries!, State!];
}

/// <summary>One persisted entry in the session tree.</summary>
public sealed record SessionEntryRow(
    string Id,
    string SessionId,
    string? ParentId,
    long Seq,
    string Role,
    string Content,
    string? ToolCalls,
    string? Name,
    string? ToolCallId,
    string CreatedAt) : IReptarEntity
{
    static SessionEntryRow()
    {
        EntityMapper<SessionEntryRow>.DirectMap = Map;
        EntityMapper<SessionEntryRow>.Factory = _ => Map;
    }

    public static SessionEntryRow Map(IDataRecord record)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var i = 0; i < record.FieldCount; i++)
        {
            var key = record.GetName(i).Replace("_", "").ToLowerInvariant();
            values[key] = record.IsDBNull(i) ? null : record.GetValue(i);
        }

        string Text(string key) => values.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
        string? MaybeText(string key) =>
            values.TryGetValue(key, out var value) && value is not null ? value.ToString() : null;
        long Number(string key) =>
            values.TryGetValue(key, out var value) && value is not null && long.TryParse(value.ToString(), out var parsed)
                ? parsed
                : 0;

        return new SessionEntryRow(
            Text("id"),
            Text("sessionid"),
            MaybeText("parentid"),
            Number("seq"),
            Text("role"),
            Text("content"),
            MaybeText("toolcalls"),
            MaybeText("name"),
            MaybeText("toolcallid"),
            Text("createdat"));
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("SessionId", SessionId),
        ("ParentId", ParentId),
        ("Seq", Seq),
        ("Role", Role),
        ("Content", Content),
        ("ToolCalls", ToolCalls),
        ("Name", Name),
        ("ToolCallId", ToolCallId),
        ("CreatedAt", CreatedAt)
    ];

    /// <summary>Rehydrates the stored entry into the message the loop consumes.</summary>
    public ChatMessage ToChatMessage() => new(
        Role,
        Content,
        Name,
        ToolCallId,
        AgentMessageRow.DeserializeToolCalls(ToolCalls));
}

/// <summary>A node in the rendered tree.</summary>
public sealed record SessionTreeNode(
    SessionEntryRow Entry,
    IReadOnlyList<SessionTreeNode> Children)
{
    /// <summary>Total entries at or below this node.</summary>
    public int Size => 1 + Children.Sum(child => child.Size);
}

/// <summary>
/// Append-only session storage with in-place branching.
/// </summary>
/// <remarks>
/// The shape is pi's: every entry names its parent, and the active branch is the path from the
/// current leaf back to a root. That makes two things cheap that a flat message table cannot do
/// at all:
///
/// <list type="bullet">
/// <item><b>Fork.</b> Copy a path into a new session; the original is untouched.</item>
/// <item><b>Branch in place.</b> Move the leaf to an earlier entry and keep appending. Both
/// continuations stay in one session, so you can go back to either.</item>
/// </list>
///
/// The leaf is the only mutable state, which is why it lives in its own one-row-per-session
/// table rather than as a column on the session. Append, leaf switch, fork, and legacy import
/// each own a database transaction; do not wrap them in a nested SQLite transaction.
/// Atomic read/write isolation is required from the database client. SQLite has published
/// multi-process/crash coverage; buffered-write adapters alone do not establish that contract.
/// </remarks>
public sealed class SessionTreeStore
{
    private readonly IDbClient _db;

    static SessionTreeStore()
    {
        // A restart can read entries before constructing the first new row.
        EntityMapper<SessionEntryRow>.DirectMap = SessionEntryRow.Map;
        EntityMapper<SessionEntryRow>.Factory = _ => SessionEntryRow.Map;
        // These scalar projection shapes need explicit mappers under Native AOT.
        EntityMapper<LeafRow>.DirectMap = record => new LeafRow(record.IsDBNull(0) ? null : record.GetString(0));
        EntityMapper<LeafRow>.Factory = _ => EntityMapper<LeafRow>.DirectMap!;
        EntityMapper<SeqRow>.DirectMap = record => new SeqRow(record.GetInt64(0));
        EntityMapper<SeqRow>.Factory = _ => EntityMapper<SeqRow>.DirectMap!;
    }

    public SessionTreeStore(IDbClient db)
    {
        _db = db;
    }

    /// <summary>Entry table.</summary>
    public static TableDef Entries => SessionTreeSchema.Entries;

    /// <summary>Leaf table.</summary>
    public static TableDef State => SessionTreeSchema.State;

    public Task EnsureCreatedAsync() => _db.EnsureCreatedAsync(SessionTreeSchema.All);

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends a message as a child of the current leaf and advances the leaf. This is the only
    /// write path: everything else is a leaf move.
    /// </summary>
    /// <returns>The new entry id, or "" when a persist filter dropped the message.</returns>
    public async Task<string> AppendAsync(
        string sessionId,
        ChatMessage message,
        IMessagePersistFilter? filter = null)
    {
        if (filter is { } active)
        {
            if (active.BeforePersist(message, sessionId) is not { } filtered)
                return "";

            message = filtered;
        }

        return await _db.TransactionAsync(db =>
            new SessionTreeStore(db).AppendCoreAsync(sessionId, message)).ConfigureAwait(false);
    }

    private async Task<string> AppendCoreAsync(string sessionId, ChatMessage message)
    {
        var parentId = await LeafAsync(sessionId).ConfigureAwait(false);
        var next = await NextSequenceAsync(sessionId).ConfigureAwait(false);

        var row = new SessionEntryRow(
            Ids.New("ent"),
            sessionId,
            parentId,
            next,
            message.Role,
            message.Content,
            AgentMessageRow.SerializeToolCalls(message.ToolCalls),
            message.Name,
            message.ToolCallId,
            DateTimeOffset.UtcNow.ToString("O"));

        await _db.ExecuteAsync(
            $"insert into \"{Entries.Name}\" " +
            "(id, session_id, parent_id, seq, role, content, tool_calls, name, tool_call_id, created_at) " +
            "values (@id, @sessionId, @parentId, @seq, @role, @content, @toolCalls, @name, @toolCallId, @createdAt)",
            ("id", row.Id),
            ("sessionId", row.SessionId),
            ("parentId", row.ParentId),
            ("seq", row.Seq),
            ("role", row.Role),
            ("content", row.Content),
            ("toolCalls", row.ToolCalls),
            ("name", row.Name),
            ("toolCallId", row.ToolCallId),
            ("createdAt", row.CreatedAt)).ConfigureAwait(false);

        await WriteLeafAsync(sessionId, row.Id).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>Atomically moves the leaf to an entry in this session, or null to start a root.</summary>
    /// <exception cref="ArgumentException">The entry is absent or belongs to another session.</exception>
    public Task SetLeafAsync(string sessionId, string? entryId) =>
        _db.TransactionAsync(async db =>
        {
            var store = new SessionTreeStore(db);
            if (entryId is not null)
            {
                var found = await db.QueryAsync<LeafRow>(
                    $"select id from \"{Entries.Name}\" where id = @entryId and session_id = @sessionId",
                    ("entryId", entryId), ("sessionId", sessionId)).ConfigureAwait(false);
                if (found.Count == 0)
                    throw new ArgumentException("Leaf must identify an entry in the same session.", nameof(entryId));
            }
            await store.WriteLeafAsync(sessionId, entryId).ConfigureAwait(false);
        });

    private async Task WriteLeafAsync(string sessionId, string? entryId)
    {
        await _db.ExecuteAsync(
            $"delete from \"{State.Name}\" where session_id = @sessionId",
            ("sessionId", sessionId)).ConfigureAwait(false);

        await _db.ExecuteAsync(
            $"insert into \"{State.Name}\" (session_id, leaf_id, updated_at) " +
            "values (@sessionId, @leafId, @updatedAt)",
            ("sessionId", sessionId),
            ("leafId", entryId),
            ("updatedAt", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
    }

    /// <summary>The current leaf, or null for an empty session.</summary>
    public async Task<string?> LeafAsync(string sessionId)
    {
        var rows = await _db.QueryAsync<LeafRow>(
            $"select leaf_id from \"{State.Name}\" where session_id = @sessionId",
            ("sessionId", sessionId)).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0].LeafId : null;
    }

    private sealed record LeafRow(string? LeafId);

    private async Task<long> NextSequenceAsync(string sessionId)
    {
        var rows = await _db.QueryAsync<SeqRow>(
            $"select coalesce(max(seq), 0) + 1 as value from \"{Entries.Name}\" where session_id = @sessionId",
            ("sessionId", sessionId)).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0].Value : 1;
    }

    private sealed record SeqRow(long Value);

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>
    /// The active branch as the model sees it: leaf back to root, returned oldest-first.
    /// </summary>
    public async Task<List<ChatMessage>> LoadBranchAsync(string sessionId)
    {
        var path = await BranchEntriesAsync(sessionId).ConfigureAwait(false);
        return [.. path.Select(entry => entry.ToChatMessage())];
    }

    /// <summary>The active branch as entries, oldest first.</summary>
    public async Task<List<SessionEntryRow>> BranchEntriesAsync(string sessionId)
    {
        var all = await EntriesAsync(sessionId).ConfigureAwait(false);
        if (all.Count == 0)
            return [];

        var byId = all.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var leafId = await LeafAsync(sessionId).ConfigureAwait(false);

        // A session with no recorded leaf (created before the tree existed, or written by another
        // process) falls back to the newest entry.
        var current = leafId is not null && byId.ContainsKey(leafId)
            ? leafId
            : all[^1].Id;

        var path = new List<SessionEntryRow>();
        var guard = 0;

        while (current is not null && byId.TryGetValue(current, out var entry) && guard++ < all.Count + 1)
        {
            path.Add(entry);
            current = entry.ParentId;
        }

        path.Reverse();
        return path;
    }

    /// <summary>Every entry in the session, in append order.</summary>
    public Task<List<SessionEntryRow>> EntriesAsync(string sessionId) =>
        _db.QueryAsync<SessionEntryRow>(
            $"select * from \"{Entries.Name}\" where session_id = @sessionId order by seq",
            ("sessionId", sessionId));

    /// <summary>
    /// The session as a tree, roots first. More than one root is possible when a branch was
    /// started from nothing.
    /// </summary>
    public async Task<List<SessionTreeNode>> TreeAsync(string sessionId)
    {
        var entries = await EntriesAsync(sessionId).ConfigureAwait(false);
        return BuildTree(entries);
    }

    /// <summary>Builds the tree from a flat entry list. Pure, so it is easy to test.</summary>
    public static List<SessionTreeNode> BuildTree(IReadOnlyList<SessionEntryRow> entries)
    {
        var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var children = new Dictionary<string, List<SessionEntryRow>>(StringComparer.Ordinal);
        var roots = new List<SessionEntryRow>();

        foreach (var entry in entries)
        {
            if (entry.ParentId is { Length: > 0 } parent && byId.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = [];
                list.Add(entry);
            }
            else
            {
                roots.Add(entry);
            }
        }

        SessionTreeNode Build(SessionEntryRow entry, int depth)
        {
            // Depth guard: a corrupt parent chain must not recurse forever.
            if (depth > 512)
                return new SessionTreeNode(entry, []);

            var kids = children.TryGetValue(entry.Id, out var list)
                ? list.Select(child => Build(child, depth + 1)).ToList()
                : [];

            return new SessionTreeNode(entry, kids);
        }

        return [.. roots.Select(root => Build(root, 0))];
    }

    // ------------------------------------------------------------------
    // Forking
    // ------------------------------------------------------------------

    /// <summary>
    /// Copies the branch ending at <paramref name="fromEntryId"/> into a new session.
    /// </summary>
    /// <param name="sessionId">Session to fork.</param>
    /// <param name="newSessionId">Id for the fork.</param>
    /// <param name="fromEntryId">
    /// Entry to cut at. Null uses the current leaf, which clones the whole active branch.
    /// </param>
    /// <returns>True when the fork was created atomically; false for an absent source or nonempty target.</returns>
    public Task<bool> ForkAsync(
        string sessionId,
        string newSessionId,
        string? fromEntryId = null) =>
        _db.TransactionAsync(db => new SessionTreeStore(db).ForkCoreAsync(sessionId, newSessionId, fromEntryId));

    private async Task<bool> ForkCoreAsync(string sessionId, string newSessionId, string? fromEntryId)
    {
        if (sessionId == newSessionId || (await EntriesAsync(newSessionId).ConfigureAwait(false)).Count > 0)
            return false;

        var entries = await EntriesAsync(sessionId).ConfigureAwait(false);
        if (entries.Count == 0)
            return false;

        var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var leafId = fromEntryId ?? await LeafAsync(sessionId).ConfigureAwait(false) ?? entries[^1].Id;

        if (!byId.ContainsKey(leafId))
            return false;

        // Walk the source path so the fork contains exactly that history.
        var path = new List<SessionEntryRow>();
        var current = leafId;
        var guard = 0;

        while (current is not null && byId.TryGetValue(current, out var entry) && guard++ < entries.Count + 1)
        {
            path.Add(entry);
            current = entry.ParentId;
        }

        path.Reverse();

        var parent = (string?)null;
        long seq = 1;

        foreach (var source in path)
        {
            var id = Ids.New("ent");

            await _db.ExecuteAsync(
                $"insert into \"{Entries.Name}\" " +
                "(id, session_id, parent_id, seq, role, content, tool_calls, name, tool_call_id, created_at) " +
                "values (@id, @sessionId, @parentId, @seq, @role, @content, @toolCalls, @name, @toolCallId, @createdAt)",
                ("id", id),
                ("sessionId", newSessionId),
                ("parentId", parent),
                ("seq", seq++),
                ("role", source.Role),
                ("content", source.Content),
                ("toolCalls", source.ToolCalls),
                ("name", source.Name),
                ("toolCallId", source.ToolCallId),
                ("createdAt", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);

            parent = id;
        }

        await WriteLeafAsync(newSessionId, parent).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    /// <summary>
    /// Imports rows from the flat message table into the tree as a linear chain.
    /// </summary>
    /// <remarks>
    /// Sessions written before the tree existed are a plain ordered list. Importing them keeps
    /// every existing conversation loadable and gives them a leaf, after which they can be
    /// branched like any other.
    /// </remarks>
    /// <returns>How many entries were imported.</returns>
    public Task<int> ImportFlatAsync(string sessionId, TableDef flatMessages) =>
        _db.TransactionAsync(db => new SessionTreeStore(db).ImportFlatCoreAsync(sessionId, flatMessages));

    private async Task<int> ImportFlatCoreAsync(string sessionId, TableDef flatMessages)
    {
        var existing = await EntriesAsync(sessionId).ConfigureAwait(false);
        if (existing.Count > 0)
            return 0;

        var rows = await _db.QueryAsync<AgentMessageRow>(
            $"select * from \"{flatMessages.Name}\" where session_id = @sessionId order by rowid",
            ("sessionId", sessionId)).ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        var parent = (string?)null;
        long seq = 1;

        foreach (var row in rows)
        {
            var id = Ids.New("ent");

            await _db.ExecuteAsync(
                $"insert into \"{Entries.Name}\" " +
                "(id, session_id, parent_id, seq, role, content, tool_calls, name, tool_call_id, created_at) " +
                "values (@id, @sessionId, @parentId, @seq, @role, @content, @toolCalls, @name, @toolCallId, @createdAt)",
                ("id", id),
                ("sessionId", sessionId),
                ("parentId", parent),
                ("seq", seq++),
                ("role", row.Role),
                ("content", row.Content),
                ("toolCalls", row.ToolCalls),
                ("name", (string?)null),
                ("toolCallId", (string?)null),
                ("createdAt", row.CreatedAt)).ConfigureAwait(false);

            parent = id;
        }

        await WriteLeafAsync(sessionId, parent).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>
    /// Renders the tree for a <c>/tree</c> view: one line per entry with the branch structure.
    /// </summary>
    /// <param name="sessionId">Session to render.</param>
    /// <param name="maxRows">Cap, so a long session does not produce an unreadable wall.</param>
    public async Task<string> DescribeTreeAsync(string sessionId, int maxRows = 40)
    {
        var tree = await TreeAsync(sessionId).ConfigureAwait(false);
        var leafId = await LeafAsync(sessionId).ConfigureAwait(false);

        if (tree.Count == 0)
            return "This session has no entries yet.";

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("**Session tree**");
        builder.AppendLine();
        builder.AppendLine("```");

        var rows = 0;

        void Walk(SessionTreeNode node, string prefix, bool last, int depth)
        {
            if (rows++ >= maxRows)
                return;

            var glyph = depth == 0 ? "●" : last ? "└─" : "├─";
            var marker = node.Entry.Id == leafId ? " ◀ leaf" : "";
            var label = Summarize(node.Entry);

            builder.Append(prefix).Append(glyph).Append(' ').Append(label).AppendLine(marker);

            var childPrefix = depth == 0 ? "" : prefix + (last ? "   " : "│  ");

            for (var i = 0; i < node.Children.Count; i++)
                Walk(node.Children[i], childPrefix, i == node.Children.Count - 1, depth + 1);
        }

        foreach (var root in tree)
            Walk(root, "", true, 0);

        if (rows >= maxRows)
            builder.AppendLine($"… truncated at {maxRows} entries");

        builder.AppendLine("```");
        builder.AppendLine();
        builder.Append("`/fork <n>` branches from an entry · `esc` closes");

        return builder.ToString();
    }

    private static string Summarize(SessionEntryRow entry)
    {
        var text = entry.Content.ReplaceLineEndings(" ").Trim();

        if (text.Length == 0 && entry.ToolCalls is { Length: > 0 })
            text = "(tool call)";

        if (text.Length > 72)
            text = text[..72] + "…";

        var label = entry.Role switch
        {
            AgentRoles.User => "user",
            AgentRoles.Assistant => "assistant",
            AgentRoles.Tool => entry.Name ?? "tool",
            _ => entry.Role
        };

        return $"{label}: {text}";
    }
}
#endif
