using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the extension host: discovery, registration, dispatch, and the hook wiring.
/// </summary>
public sealed class AgentExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"reptar-ext-{Guid.NewGuid():n}");

    private string Write(string name, string source)
    {
        var directory = Path.Combine(_root, ".reptar", "extensions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, source);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_tool_a_command_and_a_hook_all_register()
    {
        Write("hello.reptar", """
            register_tool("word_count", "Counts words.", {type: "object"}, (args, ctx) => {
              return {content: "words: " + str(len(args.text.split(" ")))}
            })

            register_command("hello", "Greets", (args) => "hi")

            on("tool_call", (event) => { return {block: false} })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);

        Assert.Empty(host.Errors);
        Assert.Single(host.Tools);
        Assert.Equal("word_count", host.Tools[0].Name);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task a_broken_extension_does_not_stop_the_others()
    {
        Write("bad.reptar", "register_tool(");
        Write("good.reptar", """register_command("ok", "Fine", (args) => "ok")""");

        var host = await AgentExtensionHost.LoadAsync(_root);

        Assert.Single(host.Errors);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task an_unknown_event_is_reported_at_load_time()
    {
        Write("typo.reptar", """on("tool_calls", (event) => {})""");

        var host = await AgentExtensionHost.LoadAsync(_root);

        // Caught while loading rather than silently never firing during a run.
        Assert.Contains(host.Errors, error => error.Contains("unknown event"));
    }

    [Fact]
    public async Task a_missing_directory_is_not_an_error()
    {
        var host = await AgentExtensionHost.LoadAsync(Path.Combine(_root, "nope"));

        Assert.Empty(host.Extensions);
        Assert.Empty(host.Errors);
        Assert.True(host.IsEmpty);
    }

    [Fact]
    public async Task a_duplicate_tool_name_is_reported_rather_than_silently_overriding()
    {
        Write("one.reptar", """register_tool("same", "First", {type: "object"}, (args, ctx) => "a")""");
        Write("two.reptar", """register_tool("same", "Second", {type: "object"}, (args, ctx) => "b")""");

        var host = await AgentExtensionHost.LoadAsync(_root);

        Assert.Single(host.Tools);
        Assert.Contains(host.Errors, error => error.Contains("already registered"));
    }

    // ------------------------------------------------------------------
    // Dispatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_tool_handler_receives_the_parsed_arguments()
    {
        Write("echo.reptar", """
            register_tool("echo", "Echoes", {type: "object"}, (args, ctx) => {
              return {content: args.message}
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var result = await host.ExecuteToolAsync("echo", """{"message":"hello"}""");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("hello", result.Content);
    }

    [Fact]
    public async Task a_handler_returning_an_error_object_fails_without_throwing()
    {
        Write("fail.reptar", """
            register_tool("fail", "Fails", {type: "object"}, (args, ctx) => {
              return {error: "it did not work"}
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var result = await host.ExecuteToolAsync("fail", "{}");

        // A reported failure, not an exception: the model can read the message and recover.
        Assert.False(result!.Success);
        Assert.Equal("it did not work", result.ErrorMessage);
    }

    [Fact]
    public async Task an_unclaimed_tool_returns_null_so_the_host_can_fall_through()
    {
        Write("one.reptar", """register_command("x", "x", (args) => "x")""");

        var host = await AgentExtensionHost.LoadAsync(_root);

        Assert.Null(await host.ExecuteToolAsync("not_here", "{}"));
    }

    [Fact]
    public async Task a_command_handler_runs_and_returns_a_value()
    {
        Write("cmd.reptar", """
            register_command("greet", "Greets", (args) => "hello, " + str(args[0]))
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var value = await host.RunCommandAsync("greet", [ScriptValue.Of("world")]);

        Assert.Equal("hello, world", value!.ToDisplay());
    }

    [Fact]
    public async Task a_handler_can_call_a_script_builtin()
    {
        Write("builtin.reptar", """
            register_tool("shout", "Uppercases", {type: "object"}, (args, ctx) => {
              return {content: args.text.upper()}
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var result = await host.ExecuteToolAsync("shout", """{"text":"quiet"}""");

        // Proves the handler ran on the interpreter that defined it, with its globals intact.
        Assert.Equal("QUIET", result!.Content);
    }

    // ------------------------------------------------------------------
    // Hooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_tool_call_hook_can_block_and_give_a_reason()
    {
        Write("gate.reptar", """
            on("tool_call", (event) => {
              if (event.tool == "run_command") {
                return {block: true, reason: "shell is off limits"}
              }
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var config = host.Apply(new AgentLoopConfig());

        var blocked = await config.BeforeToolCall!(
            new AgentToolCallContext(1, new AiToolCall("c1", "run_command", "{}"), "{}"),
            CancellationToken.None);

        Assert.True(blocked.Block);
        Assert.Equal("shell is off limits", blocked.Reason);

        var allowed = await config.BeforeToolCall!(
            new AgentToolCallContext(1, new AiToolCall("c2", "read_file", "{}"), "{}"),
            CancellationToken.None);

        Assert.False(allowed.Block);
    }

    [Fact]
    public async Task a_hook_that_throws_blocks_rather_than_allowing()
    {
        // A gate that cannot decide must not silently permit the call.
        // Assigning to an undeclared name throws at runtime, which is what a real broken
        // handler looks like. Member access on a missing key returns null instead.
        Write("broken.reptar", """
            on("tool_call", (event) => {
              never_declared = 1
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var config = host.Apply(new AgentLoopConfig());

        var decision = await config.BeforeToolCall!(
            new AgentToolCallContext(1, new AiToolCall("c1", "run_command", "{}"), "{}"),
            CancellationToken.None);

        Assert.True(decision.Block);
    }

    [Fact]
    public async Task a_tool_result_hook_can_rewrite_the_content()
    {
        Write("redact.reptar", """
            on("tool_result", (event) => {
              if (event.tool == "read_file") {
                return {content: "[redacted]"}
              }
            })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var config = host.Apply(new AgentLoopConfig());

        var replaced = await config.AfterToolCall!(
            new AgentToolCallContext(1, new AiToolCall("c1", "read_file", "{}"), "{}"),
            AiToolResult.Ok(200, "secret"),
            false,
            CancellationToken.None);

        Assert.Equal("[redacted]", replaced!.Content);
    }

    [Fact]
    public async Task an_existing_host_hook_is_preserved_and_still_runs()
    {
        Write("observer.reptar", """
            on("turn_end", (event) => { log(event.stopReason) })
            """);

        var host = await AgentExtensionHost.LoadAsync(_root);
        var innerRan = false;

        var config = host.Apply(new AgentLoopConfig
        {
            BeforeToolCall = (_, _) =>
            {
                innerRan = true;
                return Task.FromResult(new AgentToolDecision(Block: true, Reason: "host says no"));
            }
        });

        var decision = await config.BeforeToolCall!(
            new AgentToolCallContext(1, new AiToolCall("c1", "read_file", "{}"), "{}"),
            CancellationToken.None);

        // Composition, not replacement: the host's own gate still decides when no extension objects.
        Assert.True(innerRan);
        Assert.True(decision.Block);
        Assert.Equal("host says no", decision.Reason);
    }

    [Fact]
    public async Task applying_an_empty_host_leaves_the_config_untouched()
    {
        var host = await AgentExtensionHost.LoadAsync(Path.Combine(_root, "nope"));
        var original = new AgentLoopConfig();
        var applied = host.Apply(original);

        Assert.Same(original, applied);
    }
}
