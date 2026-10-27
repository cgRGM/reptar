#if !NETWASM
using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// The definition of a background agent the parent can delegate to. Supply
/// one per named agent; resolution happens at start_task time so the module
/// never holds live clients for unused agents.
/// </summary>
public sealed record BackgroundAgentDefinition(
    string Name,
    string Instructions,
    Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AiToolDefinition>, CancellationToken, IAsyncEnumerable<LlmStreamEvent>> Stream,
    IReadOnlyList<AiToolDefinition> Tools,
    Func<string, string, Task<AiToolResult>> ExecuteTool,
    int MaxSteps = 6);

/// <summary>
/// G6 — Background agents module, mirroring the MSFT BackgroundAgentsProvider
/// tools: the parent agent starts tasks on named child agents, waits for the
/// first completion, and retrieves results — each child running in its own
/// in-memory session, concurrently, via Task.Run.
///
/// Production discipline (from the research): the host should clear
/// completed tasks to release sessions, and abandoned tasks are cancelled
/// only by disposing the module — treat that as a documented limitation for
/// the in-memory version.
/// </summary>
public sealed class BackgroundAgentsModule
{
    public const string StartToolName = "background_agents_start_task";
    public const string WaitForFirstToolName = "background_agents_wait_for_first_completion";
    public const string GetResultsToolName = "background_agents_get_task_results";
    public const string GetAllToolName = "background_agents_get_all_tasks";

    private const string InstructionsText = """
        ## Background Agents
        You can delegate work to background agents that run concurrently while you continue.

        - Use background_agents_start_task to start a task on a named background agent. It returns a task id immediately.
        - Start multiple tasks before waiting, so the agents work in parallel.
        - Use background_agents_wait_for_first_completion to wait until one of the tasks finishes (or the timeout expires; the others keep running, so you can wait again).
        - Use background_agents_get_task_results to retrieve the text output of completed tasks.
        - Use background_agents_get_all_tasks to review task ids, statuses, and agent names.
        """;

    private sealed class VolatileString
    {
        private string _value;

        public VolatileString(string value) => _value = value;

        public string Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }

    private readonly Func<string, BackgroundAgentDefinition?> _resolve;
    private readonly ConcurrentDictionary<string, BackgroundTaskRecord> _tasks = new(StringComparer.Ordinal);

    private sealed record BackgroundTaskRecord(
        string Id,
        string AgentName,
        string Task,
        VolatileString Status,
        VolatileString Result);

    private BackgroundAgentsModule(Func<string, BackgroundAgentDefinition?> resolve) =>
        _resolve = resolve;

    public static BackgroundAgentsModule Create(Func<string, BackgroundAgentDefinition?> resolve) =>
        new(resolve);

    public static BackgroundAgentsModule Create(params BackgroundAgentDefinition[] agents) =>
        new(name => agents.FirstOrDefault(agent =>
            string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)));

    public AgentModule ToModule(string sessionId = "default") => new()
    {
        Name = "background-agents",
        Instructions = InstructionsText,
        StateKeys = ["background-agents"],
        Tools =
        [
            Tool(StartToolName, "Start a background task on a named agent with text input. Returns the task id immediately.",
                """
                {
                  "type": "object",
                  "properties": {
                    "agent": { "type": "string", "description": "Name of the background agent" },
                    "task": { "type": "string", "description": "The task text to send to the agent" }
                  },
                  "required": ["agent", "task"]
                }
                """),
            Tool(WaitForFirstToolName, "Wait until one of the given task ids completes or the timeout (ms) expires. A timeout leaves the tasks running, so you can call this again.",
                """
                {
                  "type": "object",
                  "properties": {
                    "ids": { "type": "array", "items": { "type": "string" } },
                    "timeout_ms": { "type": "integer", "description": "Max time to wait, default 30000" }
                  },
                  "required": ["ids"]
                }
                """),
            Tool(GetResultsToolName, "Retrieve the text output of one or more completed background tasks.",
                """
                {
                  "type": "object",
                  "properties": {
                    "ids": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["ids"]
                }
                """),
            Tool(GetAllToolName, "List all background tasks with ids, statuses, tasks, and agent names.",
                """
                { "type": "object", "properties": {} }
                """),
        ],
        ExecuteAsync = ExecuteToolAsync,
    };

    /// <summary>
    /// Names of all background tasks started through this module (test/inspection hook).
    /// </summary>
    public IReadOnlyDictionary<string, (string Agent, string Status)> Tasks
    {
        get
        {
            var snapshot = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var (id, task) in _tasks)
            {
                snapshot[id] = (task.AgentName, task.Status.Value);
            }

            return snapshot;
        }
    }

    public void ClearCompleted(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status.Value is "completed" or "failed")
        {
            _tasks.TryRemove(taskId, out _);
        }
    }

    private async Task<AiToolResult> ExecuteToolAsync(string name, string argsJson)
    {
        try
        {
            switch (name)
            {
                case StartToolName:
                    return await StartTaskAsync(argsJson);
                case WaitForFirstToolName:
                    return await WaitForFirstAsync(argsJson);
                case GetResultsToolName:
                    return GetResults(argsJson);
                case GetAllToolName:
                    return GetAll();
                default:
                    return AiToolResult.Failure(404, "unknown_tool", $"BackgroundAgentsModule has no tool '{name}'.");
            }
        }
        catch (JsonException ex)
        {
            return AiToolResult.Failure(400, "invalid_arguments", $"Malformed arguments: {ex.Message}");
        }
    }

    private async Task<AiToolResult> StartTaskAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        if (!document.RootElement.TryGetProperty("agent", out var agentElement) ||
            agentElement.GetString() is not { Length: > 0 } agentName ||
            _resolve(agentName) is not { } agent)
        {
            return AiToolResult.Failure(404, "unknown_agent", "No background agent with that name.");
        }

        if (!document.RootElement.TryGetProperty("task", out var taskElement) ||
            taskElement.GetString() is not { Length: > 0 } taskText)
        {
            return AiToolResult.Failure(400, "invalid_arguments", "start_task requires a non-empty 'task'.");
        }

        var id = Ids.New("bgtask");
        var record = new BackgroundTaskRecord(
            id, agent.Name, taskText, new VolatileString("running"), new VolatileString(""));
        _tasks[id] = record;

        // Each background task runs in its own session/history, concurrently.
        _ = Task.Run(async () =>
        {
            try
            {
                var history = new List<ChatMessage>
                {
                    new("system", agent.Instructions),
                    new("user", taskText),
                };

                var summary = await AgentLoop.RunAsync(
                    history,
                    new AgentLoopConfig
                    {
                        Model = $"background:{agent.Name}",
                        MaxSteps = agent.MaxSteps,
                        ToolExecutor = AgentToolExecutors.FromNameArgs(agent.ExecuteTool)
                    },
                    agent.Stream,
                    agent.Tools,
                    onEvent: null,
                    cancellationToken: default).ConfigureAwait(false);

                record.Result.Value = summary.StopReason == AgentStopReason.Completed
                    ? summary.FinalContent
                    : $"Task ended with {summary.StopReason}: {summary.FinalContent}";
                record.Status.Value = summary.StopReason == AgentStopReason.Completed
                    ? "completed"
                    : "failed";
            }
            catch (Exception ex)
            {
                record.Result.Value = ex.Message;
                record.Status.Value = "failed";
            }
        });

        return AiToolResult.Ok(200, new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id,
            ["agent"] = agent.Name,
            ["status"] = "running"
        }.ToJsonString());
    }

    private async Task<AiToolResult> WaitForFirstAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        if (!document.RootElement.TryGetProperty("ids", out var idsElement))
        {
            return AiToolResult.Failure(400, "invalid_arguments", "wait_for_first_completion requires 'ids'.");
        }

        var ids = idsElement.EnumerateArray().Select(element => element.GetString() ?? "").ToList();
        var timeoutMs = document.RootElement.TryGetProperty("timeout_ms", out var timeoutElement) &&
                        timeoutElement.ValueKind == JsonValueKind.Number
            ? timeoutElement.GetInt32()
            : 30_000;

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            var finished = ids.FirstOrDefault(id =>
                _tasks.TryGetValue(id, out var task) &&
                task.Status.Value is "completed" or "failed");

            if (finished is { Length: > 0 })
            {
                var task = _tasks[finished];
                return AiToolResult.Ok(200, new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = finished,
                    ["status"] = task.Status.Value
                }.ToJsonString());
            }

            await Task.Delay(100);
        }

        var stillRunning = new System.Text.Json.Nodes.JsonArray();
        foreach (var id in ids)
        {
            if (_tasks.TryGetValue(id, out var task) && task.Status.Value == "running")
                stillRunning.Add(id);
        }

        return AiToolResult.Ok(200, new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = "timeout",
            ["ids_still_running"] = stillRunning
        }.ToJsonString());
    }

    private AiToolResult GetResults(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        if (!document.RootElement.TryGetProperty("ids", out var idsElement))
        {
            return AiToolResult.Failure(400, "invalid_arguments", "get_task_results requires 'ids'.");
        }

        var results = new System.Text.Json.Nodes.JsonArray();
        foreach (var idElement in idsElement.EnumerateArray())
        {
            var id = idElement.GetString() ?? "";

            if (!_tasks.TryGetValue(id, out var task))
            {
                results.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["status"] = "unknown"
                });
                continue;
            }

            results.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id,
                ["status"] = task.Status.Value,
                ["result"] = task.Result.Value
            });
        }

        return AiToolResult.Ok(200, results.ToJsonString());
    }

    private AiToolResult GetAll()
    {
        var all = new System.Text.Json.Nodes.JsonArray();
        foreach (var task in _tasks.Values.OrderBy(task => task.Id, StringComparer.Ordinal))
        {
            all.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = task.Id,
                ["agent"] = task.AgentName,
                ["status"] = task.Status.Value,
                ["task"] = task.Task
            });
        }

        return AiToolResult.Ok(200, all.ToJsonString());
    }

    private static AiToolDefinition Tool(string name, string description, string parametersJson) => new()
    {
        Name = name,
        Description = description,
        Parameters = ParseParameters(parametersJson),
        Method = "post",
        Path = $"/agent/module/{name}"
    };

    /// <summary>
    /// Parses a parameter schema without reflection. A tool schema is plain JSON, so it is walked
    /// into the dictionary shape <see cref="AiToolDefinition"/> wants.
    /// </summary>
    private static Dictionary<string, object?> ParseParameters(string parametersJson)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(parametersJson) is System.Text.Json.Nodes.JsonObject obj
                ? ToPlain(obj)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, object?> ToPlain(System.Text.Json.Nodes.JsonObject obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in obj)
            result[key] = ToPlainValue(value);

        return result;
    }

    private static object? ToPlainValue(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonObject obj => ToPlain(obj),
        System.Text.Json.Nodes.JsonArray array => array.Select(ToPlainValue).ToList(),
        System.Text.Json.Nodes.JsonValue value => value.TryGetValue<string>(out var text) ? text
            : value.TryGetValue<bool>(out var flag) ? flag
            : value.TryGetValue<double>(out var number) ? number
            : null,
        _ => null
    };
}

/// <summary>
/// G7 — Named per-module JSON state slices, keyed by (session, module key):
/// each module keeps its own state without the others knowing, and the slices
/// persist to SQLite/D1 so they survive restarts (the MSFT session state bag,
/// but durable by default).
/// </summary>
public static class AgentSchemaState
{
    public static TableDef ModuleState(string tableName = "agent_module_state") =>
        ReptarApi.DefineTable(tableName,
            ReptarApi.Col.Text("session_id"),
            ReptarApi.Col.Text("key"),
            ReptarApi.Col.Text("value_json").NotNull(),
            ReptarApi.Col.Text("updated_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"))
        .PrimaryKey("session_id", "key");
}

/// <summary>
/// Durable module state slices over any <see cref="IDbClient"/>. AOT note:
/// callers serialize their state themselves (or pass source-gen options);
/// the slice itself stores opaque JSON text.
/// </summary>
public sealed class ModuleStateStore(IDbClient db, TableDef state)
{
    public TableDef State => state;

    public Task EnsureCreatedAsync() => db.EnsureCreatedAsync(state);

    /// <summary>Writes a slice; also usable for in-memory module state (todos, modes) that should survive restarts.</summary>
    public Task SetAsync(string sessionId, string key, string valueJson) =>
        db.ExecuteAsync(
            $"insert into \"{state.Name}\" (session_id, key, value_json, updated_at) values (@sessionId, @key, @valueJson, @now) " +
            "on conflict(session_id, key) do update set value_json = @valueJson, updated_at = @now",
            ("sessionId", sessionId),
            ("key", key),
            ("valueJson", valueJson),
            ("now", DateTimeOffset.UtcNow.ToString("O")));

    public async Task<string?> GetAsync(string sessionId, string key)
    {
        var rows = await db.ListAsync<KeyValueRow>(
            state,
            "where session_id = @sessionId and key = @key",
            new (string Name, object? Value)[] { ("sessionId", sessionId), ("key", key) },
            limit: 1);
        return rows.Count > 0 ? rows[0].ValueJson : null;
    }

    public sealed record KeyValueRow(string SessionId, string Key, string ValueJson) : IReptarEntity
    {
        static KeyValueRow()
        {
            EntityMapper<KeyValueRow>.DirectMap = Map;
            EntityMapper<KeyValueRow>.Factory = _ => Map;
        }

        public static KeyValueRow Map(IDataRecord record)
        {
            string sessionId = "", key = "", valueJson = "";

            for (var i = 0; i < record.FieldCount; i++)
            {
                if (record.IsDBNull(i))
                    continue;
                switch (record.GetName(i).Replace("_", "").ToLowerInvariant())
                {
                    case "sessionid": sessionId = record.GetString(i); break;
                    case "key": key = record.GetString(i); break;
                    case "valuejson": valueJson = record.GetString(i); break;
                }
            }

            return new KeyValueRow(sessionId, key, valueJson);
        }

        public (string Name, object? Value)[] ToDbParameters() =>
        [
            ("SessionId", SessionId),
            ("Key", Key),
            ("ValueJson", ValueJson)
        ];
    }
}
#endif
