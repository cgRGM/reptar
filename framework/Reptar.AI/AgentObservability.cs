#if !NETWASM
using System.Data;
using System.Text;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// G9 — Standard run-telemetry TableDef: one row per agent run, so every run
/// (terminal or web) leaves an inspectable trace artifact for the Studio
/// console and CI reporting.
/// </summary>
public static class AgentRunSchema
{
    public static TableDef Runs(string tableName = "agent_runs") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("id").PrimaryKey(),
            ReptarApi.Col.Text("session_id").NotNull().Indexed(),
            ReptarApi.Col.Text("model").NotNull(),
            ReptarApi.Col.Int("steps").NotNull(),
            ReptarApi.Col.Int("tool_calls").NotNull(),
            ReptarApi.Col.Real("elapsed_ms"),
            ReptarApi.Col.Text("stop_reason").NotNull(),
            ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));
}

/// <summary>
/// A persisted agent-run trace row. Manual mapping (zero reflection, AOT safe).
/// </summary>
public sealed record AgentRunRow(
    string Id,
    string SessionId,
    string Model,
    int Steps,
    int ToolCalls,
    double ElapsedMs,
    string StopReason,
    string CreatedAt) : IReptarEntity
{
    static AgentRunRow()
    {
        EntityMapper<AgentRunRow>.DirectMap = Map;
        EntityMapper<AgentRunRow>.Factory = _ => Map;
    }

    public static AgentRunRow Map(IDataRecord record)
    {
        string id = "", sessionId = "", model = "", stopReason = "completed", createdAt = "";
        int steps = 0, toolCalls = 0;
        double elapsedMs = 0;

        for (var i = 0; i < record.FieldCount; i++)
        {
            if (record.IsDBNull(i))
                continue;
            switch (Normalize(record.GetName(i)))
            {
                case "id": id = record.GetString(i); break;
                case "sessionid" or "session_id": sessionId = record.GetString(i); break;
                case "model": model = record.GetString(i); break;
                case "steps": steps = record.GetInt32(i); break;
                case "toolcalls" or "tool_calls": toolCalls = record.GetInt32(i); break;
                case "elapsedms" or "elapsed_ms": elapsedMs = record.GetDouble(i); break;
                case "stopreason" or "stop_reason": stopReason = record.GetString(i); break;
                case "createdat" or "created_at": createdAt = record.GetValue(i).ToString() ?? ""; break;
            }
        }

        return new AgentRunRow(id, sessionId, model, steps, toolCalls, elapsedMs, stopReason, createdAt);
    }

    public (string Name, object? Value)[] ToDbParameters() =>
    [
        ("Id", Id),
        ("SessionId", SessionId),
        ("Model", Model),
        ("Steps", Steps),
        ("ToolCalls", ToolCalls),
        ("ElapsedMs", ElapsedMs),
        ("StopReason", StopReason),
        ("CreatedAt", CreatedAt)
    ];

    private static string Normalize(string name) =>
        name.Replace("_", "").ToLowerInvariant();
}

/// <summary>
/// Durable run telemetry over any <see cref="IDbClient"/>.
/// </summary>
public sealed class AgentRunStore(IDbClient db, TableDef runs)
{
    public TableDef Runs => runs;

    public Task EnsureCreatedAsync() => db.EnsureCreatedAsync(runs);

    public Task RecordAsync(string sessionId, AgentRunSummary summary) =>
        db.InsertAsync(runs, new AgentRunRow(
            Ids.New("run"),
            sessionId,
            summary.Model,
            summary.StepsTaken,
            summary.ToolCallsExecuted,
            Math.Round(summary.ElapsedMs, 3),
            summary.StopReason.ToString().ToLowerInvariant(),
            DateTimeOffset.UtcNow.ToString("O")));

    public Task<List<AgentRunRow>> RecentAsync(int limit = 25) =>
        db.ListAsync<AgentRunRow>(runs, "order by rowid desc", limit: limit);

    public Task<List<AgentRunRow>> ListSessionAsync(string sessionId) =>
        db.ListAsync<AgentRunRow>(
            runs, "where session_id = @sessionId order by rowid desc", ("sessionId", sessionId));
}

/// <summary>
/// G8 — Composable observers over the <see cref="AgentEvent"/> stream: the
/// same events render to a terminal (their ANSI code), a web client (JSONL
/// over SSE), or a trace buffer, without the harness knowing about any of
/// them.
/// </summary>
public static class AgentObservers
{
    /// <summary>Composes observer callbacks into one, invoked in order.</summary>
    public static Action<AgentEvent> Compose(params Action<AgentEvent>[] observers) =>
        @event =>
        {
            foreach (var observer in observers)
            {
                observer(@event);
            }
        };

    /// <summary>
    /// An in-memory trace collector: keeps every event of a run plus the
    /// derived tool-call timings — the shape the Studio/DevUI page renders.
    /// </summary>
    public sealed class TraceObserver
    {
        private readonly object _lock = new();
        private readonly List<AgentEvent> _events = [];

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (_lock)
                {
                    return [.. _events];
                }
            }
        }

        public List<(string Name, bool Success, double ElapsedMs)> ToolTimings
        {
            get
            {
                var finished = Events.OfType<AgentEvent.ToolExecutionEnd>();
                return finished.Select(finishedEvent =>
                    (finishedEvent.ToolName, !finishedEvent.IsError, finishedEvent.DurationMs)).ToList();
            }
        }

        public void Observe(AgentEvent @event)
        {
            lock (_lock)
            {
                _events.Add(@event);
            }
        }

        public Action<AgentEvent> ToObserver() => Observe;
    }

    /// <summary>
    /// Serializes every event to a JSON line (JSONL) on the given writer —
    /// the wire format for web frontends and log shippers. Flushes after
    /// every event so partial lines never survive a crash.
    /// </summary>
    public static Action<AgentEvent> JsonLines(TextWriter writer, JsonSerializerOptions? options = null)
    {
        var jsonOptions = options ?? ReptarJson.Options;
        return @event =>
        {
            writer.WriteLine(JsonSerializer.Serialize(@event, @event.GetType(), jsonOptions));
            writer.Flush();
        };
    }

    /// <summary>A trace observer that also records the finished run summary.</summary>
    public static TraceObserver Trace(out Action<AgentEvent> observer)
    {
        var trace = new TraceObserver();
        observer = trace.Observe;
        return trace;
    }
}
#endif
