using System.Text;
using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

public sealed class AgentEngineTests : IDisposable
{
    private readonly string _root;

    public AgentEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "line one\nline two\nline three\n");
        File.WriteAllText(Path.Combine(_root, "code.cs"), "namespace Demo;\n\nclass Widget { }\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }

    private ReptarApp CreateApp(Action<WorkspaceToolOptions>? configure = null)
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root, configure);
        return app;
    }

    // ------------------------------------------------------------------
    // Workspace tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task read_file_returns_numbered_content_and_respects_line_ranges()
    {
        var app = CreateApp();

        var result = await app.ExecuteAiToolAsync("read_file",
            """{"path": "hello.txt", "start_line": 2, "end_line": 3}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("line two", result.Content);
        Assert.Contains("line three", result.Content);
        Assert.Contains("\"start_line\":2", result.Content);
        Assert.DoesNotContain("line one", result.Content);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../../hello.txt")]
    public async Task read_file_rejects_paths_escaping_the_workspace_root(string path)
    {
        var app = CreateApp();

        var result = await app.ExecuteAiToolAsync("read_file", $$"""{"path": "{{path}}"}""");

        Assert.False(result.Success);
        Assert.Contains("escapes the workspace root", result.ToLlmResponse());
    }

    [Fact]
    public async Task write_file_creates_directories_and_returns_diff()
    {
        var app = CreateApp();

        var result = await app.ExecuteAiToolAsync("write_file",
            """{"path": "docs/deep/note.md", "content": "created by test"}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.True(File.Exists(Path.Combine(_root, "docs", "deep", "note.md")));
        Assert.Equal("created by test", File.ReadAllText(Path.Combine(_root, "docs", "deep", "note.md")));
        Assert.Contains("created by test", result.Content);
        Assert.Contains("docs/deep/note.md", result.Content);
    }

    [Fact]
    public async Task edit_file_replaces_first_occurrence_and_reports_missing_target()
    {
        var app = CreateApp();
        var file = Path.Combine(_root, "repeat.txt");
        File.WriteAllText(file, "alpha beta alpha\n");

        var edit = await app.ExecuteAiToolAsync("edit_file",
            """{"path": "repeat.txt", "target": "alpha", "replacement": "gamma"}""");
        Assert.True(edit.Success, edit.ToLlmResponse());
        Assert.Equal("gamma beta alpha\n", File.ReadAllText(file));

        var all = await app.ExecuteAiToolAsync("edit_file",
            """{"path": "repeat.txt", "target": "alpha", "replacement": "delta", "all": true}""");
        Assert.True(all.Success);
        Assert.Equal("gamma beta delta\n", File.ReadAllText(file));

        var missing = await app.ExecuteAiToolAsync("edit_file",
            """{"path": "repeat.txt", "target": "not-present", "replacement": "x"}""");
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task list_dir_ignores_bin_obj_and_dot_directories()
    {
        var app = CreateApp();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "");
        File.WriteAllText(Path.Combine(_root, "obj", "tmp.txt"), "");

        var result = await app.ExecuteAiToolAsync("list_dir", """{"path": ".", "depth": 2}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("hello.txt", result.Content);
        Assert.DoesNotContain("config", result.Content);
        Assert.DoesNotContain("tmp.txt", result.Content);
    }

    [Fact]
    public async Task run_command_executes_in_workspace_root_with_timeout()
    {
        var app = CreateApp();

        var result = await app.ExecuteAiToolAsync("run_command",
            """{"command": "echo agent-tools-work && pwd"}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("agent-tools-work", result.Content);
        Assert.Contains(_root, result.Content);
    }

    [Fact]
    public async Task run_command_accepts_snake_case_and_camel_case_timeout()
    {
        var app = CreateApp();

        var result = await app.ExecuteAiToolAsync("run_command",
            """{"command": "sleep 20", "wait_ms": 300}""");

        Assert.True(result.Success);
        Assert.Contains("\"timedOut\":true", result.Content);
    }

    [Fact]
    public async Task ResolveAiTool_caches_tool_definitions_after_first_call()
    {
        var app = CreateApp();

        var first = app.ResolveAiTool("read_file");
        var second = app.ResolveAiTool("read_file");
        var missing = app.ResolveAiTool("does_not_exist");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Null(missing);
    }

    // ------------------------------------------------------------------
    // Streaming agent harness
    // ------------------------------------------------------------------

    private static async IAsyncEnumerable<LlmStreamEvent> ScriptedStream(
        IReadOnlyList<ChatMessage> _,
        IReadOnlyList<AiToolDefinition> __,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ___)
    {
        await Task.Yield();
        // Turn 1: ask to read the file, then a tool call
        yield return new LlmStreamEvent.TextDelta("Let me read ");
        yield return new LlmStreamEvent.TextDelta("the file.");
        yield return new LlmStreamEvent.ToolCall("call_1", "read_file", """{"path": "hello.txt"}""");
        yield return new LlmStreamEvent.Completed("tool_calls");
    }

    private static async IAsyncEnumerable<LlmStreamEvent> FinalAnswerStream(
        IReadOnlyList<ChatMessage> _,
        IReadOnlyList<AiToolDefinition> __,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ___)
    {
        await Task.Yield();
        yield return new LlmStreamEvent.TextDelta("The file has three lines.");
        yield return new LlmStreamEvent.Completed("stop");
    }

    [Fact]
    public async Task StreamingAgentHarness_runs_tool_loop_until_final_answer()
    {
        var app = CreateApp();
        var history = new List<ChatMessage> { new("user", "how many lines does hello.txt have?") };

        var callCount = 0;
        IAsyncEnumerable<LlmStreamEvent> Stream(
            IReadOnlyList<ChatMessage> m,
            IReadOnlyList<AiToolDefinition> t,
            CancellationToken c)
        {
            callCount++;
            return callCount == 1 ? ScriptedStream(m, t, c) : FinalAnswerStream(m, t, c);
        }

        var textDeltas = new StringBuilder();
        var toolStarts = new List<string>();
        var toolEnds = new List<bool>();

        var summary = await StreamingAgentHarness.RunAsync(
            history,
            Stream,
            (name, args) => app.ExecuteAiToolAsync(name, args),
            app.GetAiTools(onlyExplicit: true),
            maxSteps: 6,
            model: "test",
            onEvent: evt =>
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta delta when delta.Step == 1:
                        textDeltas.Append(delta.Text);
                        break;
                    case AgentEvent.ToolCallStarted call:
                        toolStarts.Add(call.Name);
                        break;
                    case AgentEvent.ToolCallFinished call:
                        toolEnds.Add(call.Success);
                        break;
                }
            });

        Assert.Equal(AgentStopReason.Completed, summary.StopReason);
        Assert.Equal("The file has three lines.", summary.FinalContent);
        Assert.Equal(2, summary.StepsTaken);
        Assert.Equal(1, summary.ToolCallsExecuted);
        Assert.Equal(["read_file"], toolStarts);
        Assert.Equal([true], toolEnds);
        Assert.Equal("Let me read the file.", textDeltas.ToString());
        Assert.Equal(4, history.Count); // user, assistant(+tool call), tool, assistant
        Assert.Equal("tool", history[2].Role);
        Assert.Contains("line two", history[2].Content);
    }

    [Fact]
    public async Task StreamingAgentHarness_stops_at_max_steps()
    {
        var app = CreateApp();
        var history = new List<ChatMessage> { new("user", "loop forever") };

        var summary = await StreamingAgentHarness.RunAsync(
            history,
            ScriptedStream,
            (name, args) => app.ExecuteAiToolAsync(name, args),
            app.GetAiTools(onlyExplicit: true),
            maxSteps: 2,
            model: "test");

        Assert.Equal(AgentStopReason.MaxSteps, summary.StopReason);
        Assert.Equal(2, summary.StepsTaken);
        Assert.True(summary.ToolCallsExecuted >= 2);
    }

    // ------------------------------------------------------------------
    // Session store (SQLite, zero-reflection mapping)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AgentSessionStore_persists_and_restores_conversations()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"agent-sessions-{Guid.NewGuid():N}.db");
        try
        {
            var app = ReptarApp.Create(new ReptarConfig
            {
                Database = Db.Sqlite(dbPath)
            });
            var store = new AgentSessionStore(
                app.Db,
                AgentSchema.Sessions("agent_sessions"),
                AgentSchema.Messages("agent_messages"));

            await store.EnsureCreatedAsync();

            var session = await store.CreateSessionAsync("first chat");
            await store.AppendMessageAsync(session.Id, new ChatMessage("user", "hello agent"));
            await store.AppendMessageAsync(session.Id, new ChatMessage("assistant", "hi!", ToolCalls:
            [
                new AiToolCall("call_9", "read_file", """{"path":"x"}""")
            ]));

            var reloaded = await store.FindSessionAsync(session.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("first chat", reloaded!.Title);

            var messages = await store.LoadMessagesAsync(session.Id);
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Equal("hello agent", messages[0].Content);
            Assert.NotNull(messages[1].ToolCalls);
            Assert.Single(messages[1].ToolCalls!);
            Assert.Equal("read_file", messages[1].ToolCalls![0].Name);

            var sessions = await store.ListSessionsAsync();
            Assert.Contains(sessions, s => s.Id == session.Id);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(dbPath + suffix))
                        File.Delete(dbPath + suffix);
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------
    // Provider clients
    // ------------------------------------------------------------------

    [Fact]
    public void LlmConfig_resolves_providers_from_environment_precedence()
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");
            var config = LlmConfig.FromEnvironment();
            Assert.Equal(LlmProvider.OpenAi, config.Provider);
            Assert.Equal("https://api.openai.com/v1", config.BaseUrl);

            var explicitProvider = LlmConfig.FromEnvironment("anthropic", null);
            Assert.Equal(LlmProvider.Anthropic, explicitProvider.Provider);

            var noKeys = LlmConfig.FromEnvironment("echo", "custom-echo");
            Assert.Equal(LlmProvider.Echo, noKeys.Provider);
            Assert.Equal("custom-echo", noKeys.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        }
    }

    [Fact]
    public async Task EchoLlmClient_streams_a_deterministic_offline_reply()
    {
        var client = LlmClientFactory.Create(LlmConfig.Echo());

        var events = new List<LlmStreamEvent>();
        await foreach (var evt in client.StreamAsync([new("user", "hi")], []))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is LlmStreamEvent.TextDelta);
        Assert.Contains(events, e => e is LlmStreamEvent.Completed);
    }
}
