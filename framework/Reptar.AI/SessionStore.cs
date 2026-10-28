#if !NETWASM
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Standard session/message TableDefs for agent conversation persistence.
/// The example app re-declares the same shape in examples/agent-cli/schema.cs;
/// these factories exist so any app can opt in with one line.
/// </summary>
public static class AgentSchema
{
    public static TableDef Sessions(string tableName = "agent_sessions") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("id").PrimaryKey(),
            ReptarApi.Col.Text("title").NotNull(),
            ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static TableDef Messages(string tableName = "agent_messages") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("id").PrimaryKey(),
            ReptarApi.Col.Text("session_id").NotNull().Indexed(),
            ReptarApi.Col.Text("role").NotNull(),
            ReptarApi.Col.Text("content").NotNull(),
            ReptarApi.Col.Text("tool_calls"),
            ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));
}

/// <summary>
/// A persisted conversation session.
/// Mapping is registered manually (zero reflection, Native AOT safe).
/// </summary>
public sealed record AgentSessionRow(string Id, string Title, string CreatedAt) : IReptarEntity
{
    static AgentSessionRow()
    {
        EntityMapper<AgentSessionRow>.DirectMap = Map;
        EntityMapper<AgentSessionRow>.Factory = _ => Map;
    }

    public static AgentSessionRow Map(IDataRecord record)
    {
        string id = "", title = "", createdAt = "";
        for (var i = 0; i < record.FieldCount; i++)
        {
            if (record.IsDBNull(i))
                continue;
            switch (Normalize(record.GetName(i)))
            {
                case "id": id = record.GetString(i); break;
                case "title": title = record.GetString(i); break;
                case "createdat" or "created_at": createdAt = record.GetValue(i).ToString() ?? ""; break;
            }
        }
        return new AgentSessionRow(id, title, createdAt);
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("Title", Title),
        ("CreatedAt", CreatedAt)
    ];

    private static string Normalize(string name) =>
        name.Replace("_", "").ToLowerInvariant();
}

/// <summary>
/// A persisted conversation message, including serialized tool calls.
/// </summary>
public sealed record AgentMessageRow(
    string Id,
    string SessionId,
    string Role,
    string Content,
    string? ToolCalls,
    string CreatedAt) : IReptarEntity
{
    static AgentMessageRow()
    {
        EntityMapper<AgentMessageRow>.DirectMap = Map;
        EntityMapper<AgentMessageRow>.Factory = _ => Map;
    }

    public static AgentMessageRow Map(IDataRecord record)
    {
        string id = "", sessionId = "", role = "", content = "", createdAt = "";
        string? toolCalls = null;

        for (var i = 0; i < record.FieldCount; i++)
        {
            if (record.IsDBNull(i))
                continue;
            switch (Normalize(record.GetName(i)))
            {
                case "id": id = record.GetString(i); break;
                case "sessionid": sessionId = record.GetString(i); break;
                case "role": role = record.GetString(i); break;
                case "content": content = record.GetString(i); break;
                case "toolcalls": toolCalls = record.GetString(i); break;
                case "createdat": createdAt = record.GetValue(i).ToString() ?? ""; break;
            }
        }

        return new AgentMessageRow(id, sessionId, role, content, toolCalls, createdAt);
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("SessionId", SessionId),
        ("Role", Role),
        ("Content", Content),
        ("ToolCalls", ToolCalls),
        ("CreatedAt", CreatedAt)
    ];

    private static string Normalize(string name) =>
        name.Replace("_", "").ToLowerInvariant();

    internal ChatMessage ToChatMessage()
    {
        IReadOnlyList<AiToolCall>? toolCalls = null;
        if (!string.IsNullOrEmpty(ToolCalls))
            toolCalls = DeserializeToolCalls(ToolCalls);

        return new ChatMessage(Role, Content, ToolCalls: toolCalls);
    }

    public static string? SerializeToolCalls(IReadOnlyList<AiToolCall>? calls)
    {
        if (calls is null || calls.Count == 0)
            return null;

        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["arguments"] = call.Arguments
            });
        }

        return array.ToJsonString();
    }

    public static IReadOnlyList<AiToolCall>? DeserializeToolCalls(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return null;

            var calls = new List<AiToolCall>();
            foreach (var node in array)
            {
                if (node is not JsonObject obj)
                    continue;

                calls.Add(new AiToolCall(
                    obj["id"]?.GetValue<string>() ?? "",
                    obj["name"]?.GetValue<string>() ?? "",
                    obj["arguments"]?.GetValue<string>() ?? "{}"));
            }

            return calls.Count > 0 ? calls : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// SQLite-backed session persistence for the agent loop. Works with any
/// Reptar IDbClient; all row mapping is reflection-free.
/// </summary>
public sealed class AgentSessionStore(IDbClient db, TableDef sessions, TableDef messages)
{
    static AgentSessionStore()
    {
        // Resuming can query before any row constructor has run in this process.
        EntityMapper<AgentSessionRow>.DirectMap = AgentSessionRow.Map;
        EntityMapper<AgentSessionRow>.Factory = _ => AgentSessionRow.Map;
        EntityMapper<AgentMessageRow>.DirectMap = AgentMessageRow.Map;
        EntityMapper<AgentMessageRow>.Factory = _ => AgentMessageRow.Map;
    }

    public TableDef Sessions => sessions;
    public TableDef Messages => messages;

    /// <summary>Raw client access for scoped stores and custom queries.</summary>
    public IDbClient Db => db;

    /// <summary>
    /// G18 — Optional filter applied to every message before it is persisted.
    /// Redacts or drops secrets/PII so session history stays safe to read,
    /// replay, and ship to model providers.
    /// </summary>
    public IMessagePersistFilter? PersistFilter { get; set; }

    public Task EnsureCreatedAsync() => db.EnsureCreatedAsync(sessions, messages);

    public async Task<AgentSessionRow> CreateSessionAsync(string title)
    {
        var row = new AgentSessionRow(
            Ids.New("sess"),
            title.Length > 120 ? title[..120] : title,
            DateTimeOffset.UtcNow.ToString("O"));

        await db.InsertAsync(Sessions, row);
        return row;
    }

    /// <summary>
    /// Creates a session with a caller-supplied id (used by
    /// <see cref="ScopedAgentSessionStore"/> to embed an isolation key in
    /// the session id).
    /// </summary>
    public async Task<AgentSessionRow> CreateSessionAsync(string title, string id)
    {
        var row = new AgentSessionRow(
            id,
            title.Length > 120 ? title[..120] : title,
            DateTimeOffset.UtcNow.ToString("O"));

        await db.InsertAsync(Sessions, row);
        return row;
    }

    public async Task RenameSessionAsync(string sessionId, string title)
    {
        await db.ExecuteAsync(
            $"update \"{Sessions.Name}\" set title = @title where id = @id",
            ("title", title.Length > 120 ? title[..120] : title),
            ("id", sessionId));
    }

    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        await db.ExecuteAsync(
            $"delete from \"{Messages.Name}\" where session_id = @id",
            ("id", sessionId));
        var affected = await db.ExecuteAsync(
            $"delete from \"{Sessions.Name}\" where id = @id",
            ("id", sessionId));
        return affected > 0;
    }

    public async Task<string> AppendMessageAsync(string sessionId, ChatMessage message)
    {
        if (PersistFilter is { } filter)
        {
            if (filter.BeforePersist(message, sessionId) is not { } filtered)
            {
                return ""; // dropped — never persisted
            }

            message = filtered;
        }

        var row = new AgentMessageRow(
            Ids.New("msg"),
            sessionId,
            message.Role,
            message.Content,
            AgentMessageRow.SerializeToolCalls(message.ToolCalls),
            DateTimeOffset.UtcNow.ToString("O"));

        await db.InsertAsync(Messages, row);
        return row.Id;
    }

    public async Task<List<ChatMessage>> LoadMessagesAsync(string sessionId)
    {
        var rows = await db.ListAsync<AgentMessageRow>(
            Messages,
            "where session_id = @sessionId order by rowid",
            ("sessionId", sessionId));

        return rows.Select(row => row.ToChatMessage()).ToList();
    }

    public async Task<List<AgentSessionRow>> ListSessionsAsync(int limit = 25)
    {
        return await db.ListAsync<AgentSessionRow>(
            Sessions,
            "order by rowid desc",
            limit: limit);
    }

    public async Task<AgentSessionRow?> FindSessionAsync(string sessionId)
    {
        var rows = await db.ListAsync<AgentSessionRow>(
            Sessions,
            "where id = @id",
            ("id", sessionId),
            limit: 1);

        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<AgentSessionRow?> LatestSessionAsync()
    {
        var rows = await db.ListAsync<AgentSessionRow>(Sessions, "order by rowid desc", limit: 1);
        return rows.Count > 0 ? rows[0] : null;
    }
}
#endif
