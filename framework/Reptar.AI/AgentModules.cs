#if !NETWASM
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Reptar.AI;

/// <summary>
/// G1 — The harness module contract. A module is the single unit of agent
/// capability: an instruction fragment, a set of tools, and a dispatcher that
/// executes those tools. Modules compose; the harness is just a base loop plus
/// a list of these.
/// </summary>
public sealed record AgentModule
{
    public required string Name { get; init; }

    /// <summary>
    /// Instruction fragment appended to the system prompt when this module is
    /// active. Compose with
    /// <see cref="AgentModuleComposition.ComposeInstructions"/>.
    /// </summary>
    public string Instructions { get; init; } = "";

    /// <summary>Tool declarations surfaced to the model.</summary>
    public IReadOnlyList<AiToolDefinition> Tools { get; init; } = [];

    /// <summary>Executes one of this module's tools (name, argumentsJson).</summary>
    public Func<string, string, Task<AiToolResult>>? ExecuteAsync { get; init; }

    /// <summary>Reserved named state slices this module owns (G7).</summary>
    public string[] StateKeys { get; init; } = [];
}

/// <summary>
/// Composition helpers: fold a list of modules into the three things the
/// harness loop consumes — instructions, tool declarations, and one merged
/// dispatcher.
/// </summary>
public static class AgentModuleComposition
{
    public static string ComposeInstructions(
        string? baseInstructions,
        params IReadOnlyList<AgentModule> modules)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(baseInstructions))
        {
            builder.AppendLine(baseInstructions.TrimEnd());
        }

        foreach (var module in modules)
        {
            if (!string.IsNullOrWhiteSpace(module.Instructions))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.Append(module.Instructions.TrimEnd());
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<AiToolDefinition> ComposeTools(
        params IReadOnlyList<AgentModule> modules) =>
        modules.SelectMany(module => module.Tools).ToList();

    /// <summary>
    /// Merges module dispatchers into one executor. Unmatched names are passed
    /// through to the fallback executor (the app's own tools) when provided,
    /// or return a structured not-found failure.
    /// </summary>
    public static Func<string, string, Task<AiToolResult>> ComposeExecutor(
        Func<string, string, Task<AiToolResult>>? fallback,
        params IReadOnlyList<AgentModule> modules)
    {
        return async (name, argsJson) =>
        {
            foreach (var module in modules)
            {
                if (module.ExecuteAsync is { } execute &&
                    module.Tools.Any(tool => tool.Name == name))
                {
                    return await execute(name, argsJson).ConfigureAwait(false);
                }
            }

            if (fallback is not null)
            {
                return await fallback(name, argsJson).ConfigureAwait(false);
            }

            return AiToolResult.Failure(404, "unknown_tool", $"No module or app tool named '{name}'.");
        };
    }
}

/// <summary>
/// G2 — Todo module: plan tracking for long-running tasks, mirroring the
/// Microsoft Agent Framework's TodoProvider (tools and instruction text
/// adapted; the prompt discipline is the battle-tested part).
///
/// State is per-session, in-memory, and thread-safe. One instance per agent
/// session; create with <see cref="Create"/>.
/// </summary>
public sealed class TodoModule
{
    public const string AddToolName = "todos_add";
    public const string CompleteToolName = "todos_complete";
    public const string RemoveToolName = "todos_remove";
    public const string GetRemainingToolName = "todos_get_remaining";
    public const string GetAllToolName = "todos_get_all";

    private const string InstructionsText = """
        ## Todo Items

        You have access to a todo list for tracking work items.
        When a user asks you to perform a task, follow these steps to manage your work:
        1. Determine whether the ask requires multiple steps to complete (complex) or can be completed using a single step (simple).
        2. If complex, turn the task into manageable todo items and add them to the list.
        3. If simple, don't add a todo item, but rather just complete the task directly.

        ### General TODO Guidelines
        Ask questions from the user where clarification is needed to create effective todos.
        If the user provides feedback on your plan, adjust your todos accordingly by adding new items or removing irrelevant/old ones.
        During execution, use the todo list to keep track of what needs to be done, mark items as complete when finished, and remove any items that are no longer needed.
        When a user changes the topic, changes their mind or switches to a new request, ensure that you update the todo list accordingly by removing irrelevant/old items, clearing the list, or adding new ones as needed.

        Use these tools to manage your tasks:
        - Use todos_add to break down complex work into trackable items (supports adding one or many at once).
        - Use todos_complete to mark items as done when finished (supports one or many at once). Include a reason describing how the items were completed.
        - Use todos_get_remaining to check what work is still pending.
        - Use todos_get_all to review the full list including completed items.
        """;

    private readonly string _sessionId;
    private readonly object _lock = new();
    private readonly List<TodoItemRecord> _items = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TodoModule> _sessions = new();

    public static TodoModule ForSession(string sessionId = "default") =>
        _sessions.GetOrAdd(sessionId, id => new TodoModule(id));

    private TodoModule(string sessionId) => _sessionId = sessionId;

    public static TodoModule Create(string sessionId = "default") => ForSession(sessionId);

    public AgentModule ToModule() => new()
    {
        Name = "todos",
        Instructions = InstructionsText,
        StateKeys = ["todos"],
        Tools =
        [
            Tool(AddToolName, "Add one or more todo items, each with a title and optional description.",
                """
                {
                  "type": "object",
                  "properties": {
                    "items": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "title": { "type": "string" },
                          "description": { "type": "string" }
                        },
                        "required": ["title"]
                      }
                    }
                  },
                  "required": ["items"]
                }
                """),
            Tool(CompleteToolName, "Mark one or more todo items complete by their IDs, with a reason.",
                """
                {
                  "type": "object",
                  "properties": {
                    "items": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "id": { "type": "string" },
                          "reason": { "type": "string" }
                        },
                        "required": ["id"]
                      }
                    }
                  },
                  "required": ["items"]
                }
                """),
            Tool(RemoveToolName, "Remove one or more todo items by their IDs.",
                """
                {
                  "type": "object",
                  "properties": {
                    "ids": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["ids"]
                }
                """),
            Tool(GetRemainingToolName, "Retrieve only incomplete todo items.",
                """
                { "type": "object", "properties": {} }
                """),
            Tool(GetAllToolName, "Retrieve all todo items, complete and incomplete.",
                """
                { "type": "object", "properties": {} }
                """),
        ],
        ExecuteAsync = ExecuteAsync,
    };

    public static implicit operator AgentModule(TodoModule module) => module.ToModule();

    public string SessionId => _sessionId;

    public IReadOnlyList<TodoItemRecord> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    private async Task<AiToolResult> ExecuteAsync(string name, string argsJson)
    {
        await Task.CompletedTask; // keep the uniform async contract

        try
        {
            switch (name)
            {
                case AddToolName:
                    return Add(argsJson);
                case CompleteToolName:
                    return Complete(argsJson);
                case RemoveToolName:
                    return Remove(argsJson);
                case GetRemainingToolName:
                    return List(completed: false);
                case GetAllToolName:
                    return List(completed: true);
                default:
                    return AiToolResult.Failure(404, "unknown_tool", $"TodoModule has no tool '{name}'.");
            }
        }
        catch (JsonException ex)
        {
            return AiToolResult.Failure(400, "invalid_arguments", $"Malformed arguments: {ex.Message}");
        }
        catch (TodoArgException ex)
        {
            return AiToolResult.Failure(400, "invalid_arguments", ex.Message);
        }
    }

    private AiToolResult Add(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        var items = document.RootElement.TryGetProperty("items", out var itemsElement)
            ? itemsElement
            : throw new TodoArgException("todos_add requires an 'items' array.");

        var added = new List<TodoItemRecord>();
        lock (_lock)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleElement) ||
                    titleElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(titleElement.GetString()))
                {
                    throw new TodoArgException("Every todos_add item requires a non-empty 'title'.");
                }

                string? description = item.TryGetProperty("description", out var descriptionElement) &&
                                      descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;

                var record = new TodoItemRecord(
                    Ids.New("todo"),
                    titleElement.GetString()!,
                    description,
                    IsComplete: false,
                    CompletionReason: null);
                _items.Add(record);
                added.Add(record);
            }
        }

        return AiToolResult.Ok(200, Serialize(added));
    }

    private AiToolResult Complete(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        if (!document.RootElement.TryGetProperty("items", out var itemsElement))
            throw new TodoArgException("todos_complete requires an 'items' array.");

        var completed = new List<string>();
        lock (_lock)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement))
                    throw new TodoArgException("Every todos_complete item requires an 'id'.");

                var id = idElement.GetString() ?? "";
                string? reason = item.TryGetProperty("reason", out var reasonElement) &&
                                 reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;

                var target = _items.FirstOrDefault(candidate => candidate.Id == id);
                if (target is null)
                    throw new TodoArgException($"No todo item with id '{id}'.");

                var index = _items.IndexOf(target);
                _items[index] = target with { IsComplete = true, CompletionReason = reason };
                completed.Add(id);
            }
        }

        return AiToolResult.Ok(200, Serialize(completed.Select(id => new { id })));
    }

    private AiToolResult Remove(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        if (!document.RootElement.TryGetProperty("ids", out var idsElement))
            throw new TodoArgException("todos_remove requires an 'ids' array.");

        var removed = new List<string>();
        lock (_lock)
        {
            foreach (var idElement in idsElement.EnumerateArray())
            {
                var id = idElement.GetString() ?? "";
                if (_items.RemoveAll(candidate => candidate.Id == id) > 0)
                {
                    removed.Add(id);
                }
            }
        }

        return AiToolResult.Ok(200, Serialize(removed.Select(id => new { id })));
    }

    private AiToolResult List(bool completed)
    {
        List<TodoItemRecord> snapshot;
        lock (_lock)
        {
            snapshot = completed ? [.. _items] : _items.Where(item => !item.IsComplete).ToList();
        }

        return AiToolResult.Ok(200, Serialize(snapshot));
    }

    private static string Serialize<TValue>(TValue value) =>
        JsonSerializer.Serialize(value, ReptarJson.Options);

    private static AiToolDefinition Tool(string name, string description, string parametersJson) => new()
    {
        Name = name,
        Description = description,
        Parameters = ParseParameters(parametersJson),
        Method = "post",
        Path = $"/agent/module/{name}"
    };

    private static Dictionary<string, object?> ParseParameters(string parametersJson)
    {
        using var document = JsonDocument.Parse(parametersJson);
        return ToObject(document.RootElement) as Dictionary<string, object?> ?? [];
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (element.TryGetDouble(out var d) ? d : (object)element.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private sealed class TodoArgException(string message) : Exception(message);
}

/// <summary>
/// A todo item as tracked by <see cref="TodoModule"/>.
/// </summary>
public sealed record TodoItemRecord(
    string Id,
    string Title,
    string? Description,
    bool IsComplete,
    string? CompletionReason);

/// <summary>
/// G3 — Plan/execute mode module, mirroring the MSFT AgentModeProvider:
/// a two-mode workflow where plan mode is interactive (clarifying questions,
/// options, user approval) and execute mode is autonomous. The instruction
/// fragment is interpolated with the current mode on every turn.
/// </summary>
public sealed class PlanExecuteModule
{
    public const string ModeSetToolName = "mode_set";
    public const string ModeGetToolName = "mode_get";

    public const string PlanMode = "plan";
    public const string ExecuteMode = "execute";

    private const string ModeGetInstructions = "Use the mode_get tool to check your current operating mode.";
    private const string ModeSetInstructions = "Use the mode_set tool to switch between modes as your work progresses. Only use mode_set if the user explicitly instructs or allows you to change modes.";

    private const string PlanModeWorkflow = """
        ### Plan Mode Workflow
        Use this mode when analyzing requirements, breaking down tasks, and creating plans. This is the interactive mode.
        1. Analyze the request with the purpose of building a plan.
        2. Create todo items for the plan steps.
        3. If needed, use read-only tools for exploratory checks that help you build the plan and determine what clarifying questions to ask.
        4. Ask clarifying questions where needed:
           1. Ask each question one at a time.
           2. When you have specific options in mind, present them so the user can choose instead of retyping an entire response.
           3. Do not proceed until you have received all needed clarifications.
        5. Present the plan to the user and ask for approval to switch to execute mode.
        6. When approval is granted, switch to execute mode with mode_set.
        """;

    private const string ExecuteModeWorkflow = """
        ### Execute Mode Workflow
        Work autonomously using your best judgment:
        1. If you don't have a plan or tasks yet, analyze the request and create them (skip this if you came from plan mode).
        2. Do not ask the user questions or wait for feedback; the goal is a complete, useful result when they return.
        3. If you encounter ambiguity or an unexpected situation, choose the most reasonable option, note your choice, and keep going.
        4. Mark tasks as completed as you finish them.
        5. Continue working and calling tools until you have the result for the user.
        """;

    private readonly ConcurrentDictionary<string, string> _modes = new(StringComparer.Ordinal);

    private PlanExecuteModule()
    {
    }

    public static PlanExecuteModule Create() => new();

    /// <summary>
    /// The module. Instructions carry the `{current_mode}` placeholder — use
    /// <see cref="CurrentInstructions(string, string?)"/> per turn, or compose
    /// once and require the agent to call mode_get.
    /// </summary>
    public AgentModule Module(string sessionId = "default") => new()
    {
        Name = "mode",
        Instructions = ModeTemplate(sessionId),
        StateKeys = ["mode"],
        Tools = [ModeSetTool(), ModeGetTool()],
        ExecuteAsync = (name, argsJson) => ExecuteAsync(sessionId, name, argsJson),
    };

    public string CurrentMode(string sessionId) =>
        _modes.GetValueOrDefault(sessionId, PlanMode);

    /// <summary>
    /// Instructions with the session's current mode interpolated — recompose
    /// the system prompt from this at the start of each turn.
    /// </summary>
    public string CurrentInstructions(string sessionId, string? baseInstructions = null)
    {
        var mode = CurrentMode(sessionId);
        var instructions = AgentModuleComposition.ComposeInstructions(baseInstructions, Module(sessionId))
            .Replace("{current_mode}", mode, StringComparison.Ordinal)
            .Replace(
                "{current_mode_workflow}",
                mode == ExecuteMode ? ExecuteModeWorkflow : PlanModeWorkflow,
                StringComparison.Ordinal);
        return instructions;
    }

    private static string ModeTemplate(string sessionId) =>
        $$"""
        ## Agent Mode (session {{sessionId}})

        {{ModeGetInstructions}}
        {{ModeSetInstructions}}
        You are currently operating in the {current_mode} mode.

        {current_mode_workflow}
        """;

    private AiToolDefinition ModeSetTool() => new()
    {
        Name = ModeSetToolName,
        Description = "Switch the agent's operating mode.",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["mode"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new List<string> { PlanMode, ExecuteMode },
                    ["description"] = "The mode to switch to: 'plan' (interactive) or 'execute' (autonomous)."
                }
            },
            ["required"] = new List<string> { "mode" }
        },
        Method = "post",
        Path = "/agent/module/mode_set"
    };

    private AiToolDefinition ModeGetTool() => new()
    {
        Name = ModeGetToolName,
        Description = "Retrieve the agent's current operating mode.",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>()
        },
        Method = "post",
        Path = "/agent/module/mode_get"
    };

    private async Task<AiToolResult> ExecuteAsync(string sessionId, string name, string argsJson)
    {
        await Task.CompletedTask;

        switch (name)
        {
            case ModeSetToolName:
            {
                using var document = JsonDocument.Parse(argsJson);
                if (!document.RootElement.TryGetProperty("mode", out var modeElement) ||
                    modeElement.ValueKind != JsonValueKind.String)
                {
                    return AiToolResult.Failure(400, "invalid_arguments", "mode_set requires a 'mode' string.");
                }

                var mode = modeElement.GetString() ?? "";
                if (mode is not (PlanMode or ExecuteMode))
                {
                    return AiToolResult.Failure(400, "invalid_arguments",
                        $"Unknown mode '{mode}'. Valid modes: '{PlanMode}', '{ExecuteMode}'.");
                }

                _modes[sessionId] = mode;
                return AiToolResult.Ok(200, JsonSerializer.Serialize(new { mode }, ReptarJson.Options));
            }

            case ModeGetToolName:
            {
                var mode = CurrentMode(sessionId);
                return AiToolResult.Ok(200, JsonSerializer.Serialize(new { mode }, ReptarJson.Options));
            }

            default:
                return AiToolResult.Failure(404, "unknown_tool", $"PlanExecuteModule has no tool '{name}'.");
        }
    }
}
#endif
