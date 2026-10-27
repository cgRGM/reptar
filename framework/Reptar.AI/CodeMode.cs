#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>Configuration for the <c>codemode</c> tool.</summary>
public sealed record CodeModeOptions
{
    /// <summary>Name the tool is registered under.</summary>
    public string ToolName { get; init; } = "codemode";

    /// <summary>Wall-clock budget for one script.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Statement budget, which is what actually stops a runaway loop.</summary>
    public int MaxSteps { get; init; } = 5_000;

    /// <summary>Cap on <c>text()</c> plus <c>log()</c> calls.</summary>
    public int MaxOutputItems { get; init; } = 100;

    /// <summary>Tools a script may call. Defaults to every tool the agent can call.</summary>
    public IReadOnlyList<string>? AllowTools { get; init; }

    /// <summary>Tools a script may not call.</summary>
    public IReadOnlyList<string>? DenyTools { get; init; }

    /// <summary>
    /// Keep <c>store()</c> values between runs of the same tool instance. On by default: a script
    /// that records a cursor is useless if the next script cannot read it.
    /// </summary>
    public bool PersistStore { get; init; } = true;
}

/// <summary>
/// The <c>codemode</c> tool: the model writes a script that calls other tools, and only the
/// script's output enters the conversation.
/// </summary>
/// <remarks>
/// Why this is worth having, in the words of the problem it solves: an agent asked to "find every
/// TODO in the repo and group them by file" otherwise makes dozens of tool calls, each one
/// dragging its full result into the context window. A script does the same work in one turn and
/// hands back six lines.
///
/// The language is a small C#-flavoured subset (see <see cref="ScriptInterpreter"/>), not
/// JavaScript: Reptar targets Native AOT and NetWasm, so there is no JS engine to embed and no
/// runtime compilation. The capability model is what matters and it is identical — tools are the
/// only way out of the sandbox.
///
/// Register it with <see cref="CodeMode.Register"/> and tell the model about it:
/// <code>
/// CodeMode.Register(app, agentTools);
/// var prompt = myPrompt + CodeMode.DescribeTools(agentTools);
/// </code>
/// </remarks>
public static class CodeMode
{
    /// <summary>
    /// Builds the <c>codemode</c> tool definition and registers it as an in-process Reptar route
    /// so <c>ExecuteAiToolAsync</c> can dispatch it like any other tool.
    /// </summary>
    /// <param name="app">App to register the route on.</param>
    /// <param name="agentTools">The tools scripts may call. Usually the agent's own tool list.</param>
    /// <param name="options">Limits and naming.</param>
    /// <returns>The tool definition to append to the agent's tool list.</returns>
    public static AiToolDefinition Register(
        ReptarApp app,
        IReadOnlyList<AiToolDefinition> agentTools,
        CodeModeOptions? options = null)
    {
        var config = options ?? new CodeModeOptions();
        var callable = SelectCallable(agentTools, config);

        // Scripts share one store for the lifetime of the registration, so `store("cursor", x)`
        // in one turn is readable by `load("cursor")` in the next.
        var store = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);

        app.Post($"/workspace/{config.ToolName}", async ctx =>
        {
            var node = WorkspaceTools.ReadJsonNode(ctx);
            var script = WorkspaceTools.Str(node?["script"] ?? node?["code"]);

            if (string.IsNullOrWhiteSpace(script))
                return ctx.BadRequest("Body field 'script' is required.");

            using var timeout = new CancellationTokenSource(config.Timeout);

            var interpreter = new ScriptInterpreter(
                (name, args, ct) => app.ExecuteAiToolAsync(name, args),
                callable,
                config.MaxSteps,
                config.MaxOutputItems,
                config.PersistStore ? store : null);

            var result = await interpreter
                .RunAsync(script, timeout.Token)
                .ConfigureAwait(false);

            return ctx.JsonRaw(new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = result.Ok,
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["output"] = result.ToModelText(),
                ["toolCalls"] = result.ToolCalls,
                ["steps"] = result.Steps,
                ["elapsedMs"] = Math.Round(result.ElapsedMs, 1),
                ["error"] = result.Error,
                ["images"] = result.Images is { Count: > 0 } images
                    ? new System.Text.Json.Nodes.JsonArray(
                        [.. images.Select(image => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "image",
                            ["mimeType"] = image.MimeType,
                            ["data"] = image.Base64Data
                        })])
                    : null
            }.ToJsonString());
        })
        .WithName(config.ToolName)
        .WithSummary("Run a script that calls other tools")
        .AsAiTool(config.ToolName, BuildDescription(callable, config));

        return new AiToolDefinition
        {
            Name = config.ToolName,
            Description = BuildDescription(callable, config),
            Method = "POST",
            Path = $"/workspace/{config.ToolName}",
            Parameters = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["script"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] =
                            "Script source. Use `tools.<name>({ ...args })` to call tools, "
                            + "`text(value)` to add output, and `return value` for the final result."
                    }
                },
                ["required"] = new List<object?> { "script" }
            }
        };
    }

    /// <summary>
    /// Renders the tool list a script can reach, for the system prompt. Mirrors pi's practice of
    /// declaring callable tools so the model does not have to guess names.
    /// </summary>
    public static string DescribeTools(IReadOnlyList<AiToolDefinition> agentTools, CodeModeOptions? options = null)
    {
        var callable = SelectCallable(agentTools, options ?? new CodeModeOptions());

        var builder = new StringBuilder();
        builder.AppendLine("<codemode>");
        builder.AppendLine(
            "You can write a script that calls tools instead of calling them one at a time. "
            + "Only the script's output reaches you, so filter large results inside the script.");
        builder.AppendLine();
        builder.AppendLine("Available inside a script:");
        builder.AppendLine();

        foreach (var tool in callable)
        {
            builder.Append("- tools.").Append(ScriptInterpreter.Identifier(tool.Name));
            builder.Append("({ ... })  — ").AppendLine(FirstSentence(tool.Description));
        }

        builder.AppendLine();
        builder.AppendLine("Globals: text(v), image(dataUrl), log(...), len(v), str(v), num(v),");
        builder.AppendLine("         json.parse(s), json.stringify(v), spawn(tools.x({...})), join(h),");
        builder.AppendLine("         store(key, value), load(key), ALL_TOOLS");
        builder.AppendLine("Control flow: let, if/else, while, for, foreach (x in list), return, break, continue");
        builder.AppendLine();
        builder.AppendLine("Example:");
        builder.AppendLine("  let entries = tools.list_dir({ path: \".\", depth: 3 });");
        builder.AppendLine("  foreach (let e in entries.entries) {");
        builder.AppendLine("      if (e.type == \"file\" && e.path.endsWith(\".cs\")) {");
        builder.AppendLine("          let src = tools.read_file({ path: e.path });");
        builder.AppendLine("          if (src.content.includes(\"TODO\")) { text(e.path); }");
        builder.AppendLine("      }");
        builder.AppendLine("  }");
        builder.AppendLine("</codemode>");

        return builder.ToString();
    }

    private static IReadOnlyList<AiToolDefinition> SelectCallable(
        IReadOnlyList<AiToolDefinition> tools,
        CodeModeOptions options)
    {
        var callable = tools
            .Where(tool => !string.Equals(tool.Name, options.ToolName, StringComparison.Ordinal))
            .ToList();

        if (options.AllowTools is { Count: > 0 } allow)
        {
            callable = callable
                .Where(tool => allow.Contains(tool.Name, StringComparer.Ordinal))
                .ToList();
        }

        if (options.DenyTools is { Count: > 0 } deny)
        {
            callable = callable
                .Where(tool => !deny.Contains(tool.Name, StringComparer.Ordinal))
                .ToList();
        }

        return callable;
    }

    private static string BuildDescription(IReadOnlyList<AiToolDefinition> callable, CodeModeOptions options)
    {
        var names = string.Join(", ", callable.Select(tool => ScriptInterpreter.Identifier(tool.Name)));

        return
            "Run a script that calls other tools, in one turn. Use this instead of many tool calls when you "
            + "need to loop, filter, or combine results: only text(), log(), and the return value reach you, "
            + "so a script can read a huge file and hand back three lines. "
            + $"Callable tools: {names}. "
            + "Syntax: let/if/else/while/for/foreach/return. "
            + "Call a tool with tools.<name>({ ...args }); it returns parsed JSON. "
            + "Add output with text(value) or return value. "
            + "Run calls concurrently with spawn(tools.x({...})) then join([a, b]). "
            + $"Budget: {options.MaxSteps} steps, {options.Timeout.TotalSeconds:0}s.";
    }

    private static string FirstSentence(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end > 0 ? description[..(end + 1)] : description;
        return sentence.Length <= 120 ? sentence : sentence[..120] + "…";
    }
}
#endif
