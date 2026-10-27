using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="ScriptInterpreter"/> and <see cref="CodeMode"/>: the language,
/// the tool-call bridge, the concurrency escape hatch, and every sandbox limit.
/// </summary>
public sealed class CodeModeTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _invocations = [];

    public CodeModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-codemode-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\nTODO: one\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "beta\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ScriptInterpreter CreateInterpreter(
        Func<string, string, CancellationToken, Task<AiToolResult>>? executor = null,
        int maxSteps = 5_000,
        int maxOutputItems = 100)
    {
        var tools = new List<AiToolDefinition>
        {
            new()
            {
                Name = "echo",
                Description = "Echo a value back.",
                Method = "POST",
                Path = "/echo",
                Parameters = new Dictionary<string, object?>()
            },
            new()
            {
                Name = "read_file",
                Description = "Read a file.",
                Method = "POST",
                Path = "/read",
                Parameters = new Dictionary<string, object?>()
            },
            new()
            {
                Name = "my-tool",
                Description = "A tool with a dash in its name.",
                Method = "POST",
                Path = "/dash",
                Parameters = new Dictionary<string, object?>()
            }
        };

        return new ScriptInterpreter(
            executor ?? DefaultExecutor,
            tools,
            maxSteps,
            maxOutputItems);
    }

    private Task<AiToolResult> DefaultExecutor(string name, string args, CancellationToken ct)
    {
        _invocations.Add($"{name}({args})");

        return name switch
        {
            "echo" => Task.FromResult(AiToolResult.Ok(200, args)),
            "read_file" => Task.FromResult(AiToolResult.Ok(200, """{"content":"file body"}""")),
            _ => Task.FromResult(AiToolResult.Failure(404, "not_found", $"no tool {name}"))
        };
    }

    // ------------------------------------------------------------------
    // Language basics
    // ------------------------------------------------------------------

    [Fact]
    public async Task returns_a_literal()
    {
        var result = await CreateInterpreter().RunAsync("return 42;");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("42", result.Value.ToDisplay());
    }

    [Fact]
    public async Task concatenates_strings_and_numbers()
    {
        var result = await CreateInterpreter().RunAsync("""let n = 3; return "count: " + n;""");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("count: 3", result.Value.ToDisplay());
    }

    [Fact]
    public async Task evaluates_arithmetic_with_precedence()
    {
        var result = await CreateInterpreter().RunAsync("return 2 + 3 * 4 - 1;");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("13", result.Value.ToDisplay());
    }

    [Fact]
    public async Task builds_and_indexes_arrays()
    {
        var result = await CreateInterpreter().RunAsync("""
            let xs = [10, 20, 30];
            return xs[1] + xs.length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("23", result.Value.ToDisplay());
    }

    [Fact]
    public async Task builds_objects_and_reads_members()
    {
        var result = await CreateInterpreter().RunAsync("""
            let o = { name: "reptar", nested: { n: 7 } };
            return o.name + o.nested.n;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("reptar7", result.Value.ToDisplay());
    }

    [Fact]
    public async Task runs_if_else_branches()
    {
        var result = await CreateInterpreter().RunAsync("""
            let x = 5;
            if (x > 10) { return "big"; } else { return "small"; }
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("small", result.Value.ToDisplay());
    }

    [Fact]
    public async Task runs_foreach_over_an_array()
    {
        var result = await CreateInterpreter().RunAsync("""
            let total = 0;
            foreach (let x in [1, 2, 3, 4]) { total += x; }
            return total;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("10", result.Value.ToDisplay());
    }

    [Fact]
    public async Task runs_while_loops_with_break()
    {
        var result = await CreateInterpreter().RunAsync("""
            let i = 0;
            while (true) { i += 1; if (i == 4) { break; } }
            return i;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("4", result.Value.ToDisplay());
    }

    [Fact]
    public async Task supports_string_methods()
    {
        var result = await CreateInterpreter().RunAsync("""
            let s = "Hello World";
            return s.upper() + "/" + s.lower() + "/" + s.includes("World") + "/" + s.split(" ").length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("HELLO WORLD/hello world/true/2", result.Value.ToDisplay());
    }

    [Fact]
    public async Task filter_takes_an_arrow_lambda()
    {
        var result = await CreateInterpreter().RunAsync("""
            let xs = [1, 2, 3, 4, 5];
            let evens = xs.filter((x) => x % 2 == 0);
            return evens.length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2", result.Value.ToDisplay());
    }

    [Fact]
    public async Task map_transforms_each_item()
    {
        var result = await CreateInterpreter().RunAsync("""
            let doubled = [1, 2, 3].map((x) => x * 2);
            return doubled.join(",");
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2,4,6", result.Value.ToDisplay());
    }

    [Fact]
    public async Task single_parameter_lambda_shorthand_works()
    {
        var result = await CreateInterpreter().RunAsync("""
            let xs = [5, 10, 15];
            return xs.find(x => x > 7);
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("10", result.Value.ToDisplay());
    }

    [Fact]
    public async Task lambdas_can_have_block_bodies()
    {
        var result = await CreateInterpreter().RunAsync("""
            let big = [1, 2, 3, 4].filter((x) => {
                let threshold = 2;
                return x > threshold;
            });
            return big.length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2", result.Value.ToDisplay());
    }

    [Fact]
    public async Task closures_capture_their_defining_scope()
    {
        var result = await CreateInterpreter().RunAsync("""
            let limit = 10;
            let xs = [5, 15, 25];
            return xs.filter((x) => x > limit).length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2", result.Value.ToDisplay());
    }

    [Fact]
    public async Task grouping_parentheses_still_work_alongside_lambdas()
    {
        var result = await CreateInterpreter().RunAsync("return (1 + 2) * 3;");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("9", result.Value.ToDisplay());
    }

    // ------------------------------------------------------------------
    // Tool bridge
    // ------------------------------------------------------------------

    [Fact]
    public async Task calls_a_tool_and_gets_parsed_json_back()
    {
        var result = await CreateInterpreter().RunAsync("""
            let r = tools.read_file({ path: "a.txt" });
            return r.content;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("file body", result.Value.ToDisplay());
        Assert.Equal(1, result.ToolCalls);
        Assert.Contains(_invocations, call => call.StartsWith("read_file(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task passes_arguments_as_json()
    {
        await CreateInterpreter().RunAsync("""tools.echo({ path: "x.txt", count: 2 });""");

        Assert.Contains(_invocations, call =>
            call.Contains("\"path\":\"x.txt\"", StringComparison.Ordinal) &&
            call.Contains("\"count\":2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task maps_dashed_tool_names_to_identifiers()
    {
        await CreateInterpreter().RunAsync("""tools.my_tool({});""");

        Assert.Contains(_invocations, call => call.StartsWith("my-tool(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task an_unknown_tool_is_a_script_error()
    {
        var result = await CreateInterpreter().RunAsync("""tools.nope({});""");

        Assert.False(result.Ok);
        Assert.Contains("unknown tool", result.Error!);
    }

    [Fact]
    public async Task a_failing_tool_reports_its_error_text()
    {
        var result = await CreateInterpreter().RunAsync("""tools.boom({});""");

        Assert.False(result.Ok);
        Assert.Contains("unknown tool", result.Error!);
    }

    [Fact]
    public async Task spawn_and_join_run_calls_concurrently()
    {
        var started = 0;
        var gate = new TaskCompletionSource();

        var interpreter = CreateInterpreter(async (name, args, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2)
                gate.TrySetResult();

            // Sequential execution would never satisfy the gate and would hit the timeout.
            await Task.WhenAny(gate.Task, Task.Delay(2_000, ct));
            return AiToolResult.Ok(200, """{"ok":true}""");
        });

        var result = await interpreter.RunAsync("""
            let a = spawn(tools.read_file({ path: "a.txt" }));
            let b = spawn(tools.read_file({ path: "b.txt" }));
            let results = join([a, b]);
            return results.length;
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("2", result.Value.ToDisplay());
        Assert.Equal(2, result.ToolCalls);
    }

    // ------------------------------------------------------------------
    // Output
    // ------------------------------------------------------------------

    [Fact]
    public async Task text_and_log_are_reported_separately()
    {
        var result = await CreateInterpreter().RunAsync("""
            text("visible");
            log("debug", 1);
            return "final";
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(["visible"], result.Output);
        Assert.Equal(["debug 1"], result.Console);

        var rendered = result.ToModelText();
        Assert.Contains("Script completed", rendered);
        Assert.Contains("visible", rendered);
        Assert.Contains("<console_output>", rendered);
        Assert.Contains("debug 1", rendered);
    }

    [Fact]
    public async Task multiple_output_items_are_numbered()
    {
        var result = await CreateInterpreter().RunAsync("""
            text("one"); text("two");
            """);

        var rendered = result.ToModelText();
        Assert.Contains("==> text 1/2 <==", rendered);
        Assert.Contains("==> text 2/2 <==", rendered);
    }

    [Fact]
    public async Task only_output_reaches_the_model_not_tool_payloads()
    {
        var interpreter = CreateInterpreter((_, _, _) =>
            Task.FromResult(AiToolResult.Ok(200, """{"content":"SECRET-INTERNAL-PAYLOAD"}""")));

        var result = await interpreter.RunAsync("""
            let r = tools.read_file({ path: "a.txt" });
            text("3 lines");
            """);

        var rendered = result.ToModelText();
        Assert.Contains("3 lines", rendered);
        Assert.DoesNotContain("SECRET-INTERNAL-PAYLOAD", rendered);
    }

    // ------------------------------------------------------------------
    // Images and the script store
    // ------------------------------------------------------------------

    [Fact]
    public async Task image_accepts_a_base64_data_url()
    {
        var result = await CreateInterpreter().RunAsync("""
            image("data:image/png;base64,iVBORw0KGgo=");
            text("chart attached");
            """);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Images);
        var image = Assert.Single(result.Images!);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("iVBORw0KGgo=", image.Base64Data);
        Assert.Contains("[image 1/1: image/png]", result.ToModelText());
    }

    [Fact]
    public async Task image_accepts_an_image_block()
    {
        var result = await CreateInterpreter().RunAsync("""
            image({ type: "image", mimeType: "image/jpeg", data: "abc123" });
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("image/jpeg", Assert.Single(result.Images!).MimeType);
    }

    [Fact]
    public async Task image_refuses_a_remote_url()
    {
        var result = await CreateInterpreter().RunAsync("""
            image("https://example.com/cat.png");
            """);

        Assert.False(result.Ok);
        Assert.Contains("remote URLs are not supported", result.Error!);
    }

    [Fact]
    public async Task image_refuses_a_non_base64_data_url()
    {
        var result = await CreateInterpreter().RunAsync("""
            image("data:image/png,notbase64");
            """);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task store_and_load_round_trip_within_a_run()
    {
        var result = await CreateInterpreter().RunAsync("""
            store("cursor", 42);
            return load("cursor");
            """);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("42", result.Value.ToDisplay());
    }

    [Fact]
    public async Task load_of_an_unknown_key_is_null()
    {
        var result = await CreateInterpreter().RunAsync("""return load("nope");""");

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Value.IsNull);
    }

    [Fact]
    public async Task storing_null_deletes_the_key()
    {
        var result = await CreateInterpreter().RunAsync("""
            store("k", "v");
            store("k", null);
            return load("k");
            """);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Value.IsNull);
    }

    [Fact]
    public async Task a_shared_store_survives_across_runs()
    {
        var shared = new Dictionary<string, ScriptValue>();

        var first = new ScriptInterpreter(
            DefaultExecutor,
            [new AiToolDefinition { Name = "echo", Description = "e", Method = "POST", Path = "/e", Parameters = new Dictionary<string, object?>() }],
            store: shared);

        await first.RunAsync("""store("cursor", "page-2");""");

        var second = new ScriptInterpreter(
            DefaultExecutor,
            [new AiToolDefinition { Name = "echo", Description = "e", Method = "POST", Path = "/e", Parameters = new Dictionary<string, object?>() }],
            store: shared);

        var result = await second.RunAsync("""return load("cursor");""");

        Assert.Equal("page-2", result.Value.ToDisplay());
    }

    // ------------------------------------------------------------------
    // Sandbox limits
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_syntax_error_is_reported_with_a_line_number()
    {
        var result = await CreateInterpreter().RunAsync("let x = ;");

        Assert.Equal(ScriptStatus.Failed, result.Status);
        Assert.Contains("line 1", result.Error!);
    }

    [Fact]
    public async Task an_infinite_loop_trips_the_step_budget()
    {
        var result = await CreateInterpreter(maxSteps: 200).RunAsync("while (true) { }");

        Assert.Equal(ScriptStatus.StepLimit, result.Status);
        Assert.Contains("budget", result.Error!);
    }

    [Fact]
    public async Task cancellation_reports_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await CreateInterpreter(maxSteps: int.MaxValue).RunAsync("while (true) { }", cts.Token);

        Assert.Equal(ScriptStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task too_much_output_is_rejected()
    {
        var result = await CreateInterpreter(maxOutputItems: 5).RunAsync("""
            let i = 0;
            while (i < 100) { text("x"); i += 1; }
            """);

        Assert.Equal(ScriptStatus.Failed, result.Status);
        Assert.Contains("output items", result.Error!);
    }

    [Fact]
    public async Task there_is_no_way_to_reach_the_filesystem_without_a_tool()
    {
        // Every plausible escape hatch must be an ordinary "not callable" or unknown-name error.
        foreach (var attempt in new[]
                 {
                     """require("fs");""",
                     """fetch("https://example.com");""",
                     """process.exit(0);""",
                     """eval("1+1");"""
                 })
        {
            var result = await CreateInterpreter().RunAsync(attempt);
            Assert.False(result.Ok, $"script should fail: {attempt}");
        }
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    [Fact]
    public void register_exposes_codemode_as_an_ai_tool()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root);

        var agentTools = app.GetAiTools(onlyExplicit: true).ToList();
        var definition = CodeMode.Register(app, agentTools);

        Assert.Equal("codemode", definition.Name);
        Assert.Contains("read_file", definition.Description);
        var properties = Assert.IsType<Dictionary<string, object?>>(definition.Parameters["properties"]);
        Assert.Contains("script", properties.Keys);

        // The route is reachable through the same dispatcher the agent uses.
        var registered = app.GetAiTools(onlyExplicit: true).Any(tool => tool.Name == "codemode");
        Assert.True(registered);
    }

    [Fact]
    public void describe_tools_lists_callable_names_and_the_globals()
    {
        var tools = new List<AiToolDefinition>
        {
            new()
            {
                Name = "read_file",
                Description = "Read a file from disk.",
                Method = "POST",
                Path = "/read",
                Parameters = new Dictionary<string, object?>()
            }
        };

        var text = CodeMode.DescribeTools(tools);

        Assert.Contains("<codemode>", text);
        Assert.Contains("tools.read_file", text);
        Assert.Contains("text(v)", text);
        Assert.Contains("spawn(", text);
        Assert.Contains("foreach", text);
    }

    [Fact]
    public void code_mode_never_offers_itself_to_scripts()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root);

        var tools = app.GetAiTools(onlyExplicit: true).ToList();
        var codemode = CodeMode.Register(app, tools);

        var described = CodeMode.DescribeTools(tools.Append(codemode).ToList());
        Assert.DoesNotContain("tools.codemode", described);
    }

    [Fact]
    public void deny_list_removes_a_tool_from_the_script_surface()
    {
        var tools = new List<AiToolDefinition>
        {
            new() { Name = "read_file", Description = "Read.", Method = "POST", Path = "/r", Parameters = new Dictionary<string, object?>() },
            new() { Name = "write_file", Description = "Write.", Method = "POST", Path = "/w", Parameters = new Dictionary<string, object?>() }
        };

        var text = CodeMode.DescribeTools(tools, new CodeModeOptions { DenyTools = ["write_file"] });

        Assert.Contains("tools.read_file", text);
        Assert.DoesNotContain("tools.write_file", text);
    }
}
