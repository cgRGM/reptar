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

    // ------------------------------------------------------------------
    // Provider credentials (/connect flow)
    // ------------------------------------------------------------------

    [Fact]
    public void LlmAuthFile_persists_upserts_and_clears()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"reptar-auth-{Guid.NewGuid():N}", "auth.json");
        try
        {
            var auth = new LlmAuthFile(authPath);
            Assert.Empty(auth.Credentials);

            auth.Set(LlmProviders.OpenAi, new ProviderCredential("sk-test", null, "gpt-4o"));
            auth.Set(LlmProviders.Ollama, new ProviderCredential(null, "http://localhost:11434", "llama3.2"));

            Assert.Equal(2, auth.Credentials.Count);
            Assert.Equal(LlmProviders.Ollama, auth.DefaultProvider);

            // Upsert keeps one entry and updates the default model.
            auth.Set(LlmProviders.OpenAi, new ProviderCredential("sk-test-2", null, "gpt-4o-mini"));
            var openAi = auth.Get(LlmProviders.OpenAi);
            Assert.NotNull(openAi);
            Assert.Equal("sk-test-2", openAi!.ApiKey);
            Assert.Equal(2, auth.Credentials.Count);

            // Reloading from disk round-trips everything.
            var reloaded = new LlmAuthFile(authPath);
            Assert.Equal(2, reloaded.Credentials.Count);
            Assert.Equal("http://localhost:11434", reloaded.Get(LlmProviders.Ollama)!.BaseUrl);
            Assert.Equal(LlmProviders.OpenAi, reloaded.DefaultProvider);

            // Clearing the default provider moves the default to the remainder.
            Assert.True(reloaded.Clear(LlmProviders.OpenAi));
            Assert.Equal(LlmProviders.Ollama, reloaded.DefaultProvider);
            Assert.False(reloaded.Clear(LlmProviders.OpenAi));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(authPath)!, true); }
            catch { }
        }
    }

    [Fact]
    public void LlmConfig_resolves_saved_auth_when_no_env_keys_are_set()
    {
        var (authPath, originals) = IsolateAuthAndProviderEnv();
        try
        {
            var auth = new LlmAuthFile(authPath);
            auth.Set(LlmProviders.Anthropic,
                new ProviderCredential("sk-ant-saved", null, "claude-haiku-4-5"));

            var config = LlmConfig.FromEnvironment();
            Assert.Equal(LlmProvider.Anthropic, config.Provider);
            Assert.Equal("sk-ant-saved", config.ApiKey);
            Assert.Equal("claude-haiku-4-5", config.Model);
            Assert.Equal(LlmProviders.Anthropic, config.ProviderKey);
        }
        finally
        {
            RestoreEnv(originals);
        }
    }

    [Fact]
    public void LlmConfig_env_keys_win_over_saved_auth()
    {
        var (authPath, originals) = IsolateAuthAndProviderEnv();
        try
        {
            var auth = new LlmAuthFile(authPath);
            auth.Set(LlmProviders.Anthropic, new ProviderCredential("sk-ant-saved", null, null));

            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-env-wins");
            var config = LlmConfig.FromEnvironment();
            Assert.Equal(LlmProvider.OpenAi, config.Provider);
            Assert.Equal("sk-env-wins", config.ApiKey);
        }
        finally
        {
            RestoreEnv(originals);
        }
    }

    [Fact]
    public void LlmConfig_resolves_custom_saved_providers()
    {
        var (authPath, originals) = IsolateAuthAndProviderEnv();
        try
        {
            var auth = new LlmAuthFile(authPath);
            auth.Set("mocai", new ProviderCredential("sk-mock", "http://localhost:5999/v1", "mock-sonnet"));

            var config = LlmConfig.FromEnvironment("mocai");
            Assert.Equal(LlmProvider.OpenAi, config.Provider);
            Assert.Equal("mocai", config.ProviderKey);
            Assert.Equal("mock-sonnet", config.Model);
            Assert.Equal("http://localhost:5999/v1", config.BaseUrl);

            // Unknown, unregistered provider ids fail fast with the supported list.
            var error = Assert.Throws<ArgumentException>(() => LlmConfig.FromEnvironment("nope-123"));
            Assert.Contains("openai, anthropic, gemini", error.Message);
        }
        finally
        {
            RestoreEnv(originals);
        }
    }

    [Fact]
    public async Task StreamingAgentHarness_dedupes_streamed_and_complete_tool_call_events()
    {
        var app = CreateApp();
        var history = new List<ChatMessage> { new("user", "list the workspace") };

        // OpenAI-compatible streams emit argument deltas first and then a
        // complete call with the same id. The harness must merge them or the
        // tool executes twice (regression: caught against a live mock server).
        var executions = 0;

        IAsyncEnumerable<LlmStreamEvent> Stream(
            IReadOnlyList<ChatMessage> _,
            IReadOnlyList<AiToolDefinition> __,
            CancellationToken ___)
        {
            return StreamOnce();
        }

        async IAsyncEnumerable<LlmStreamEvent> StreamOnce()
        {
            await Task.Yield();
            yield return new LlmStreamEvent.ToolCallDelta(0, "call_1", "list_dir", "{\"path\"");
            yield return new LlmStreamEvent.ToolCallDelta(0, null, null, ": \".\"}");
            yield return new LlmStreamEvent.ToolCall("call_1", "list_dir", "{\"path\":\".\"}");
            yield return new LlmStreamEvent.Completed("tool_calls");
        }

        IAsyncEnumerable<LlmStreamEvent> StreamFinal(
            IReadOnlyList<ChatMessage> _,
            IReadOnlyList<AiToolDefinition> __,
            CancellationToken ___)
        {
            return StreamDone();
        }

        async IAsyncEnumerable<LlmStreamEvent> StreamDone()
        {
            await Task.Yield();
            yield return new LlmStreamEvent.TextDelta("listed.");
            yield return new LlmStreamEvent.Completed("stop");
        }

        var callCount = 0;

        var summary = await StreamingAgentHarness.RunAsync(
            history,
            (m, t, c) => ++callCount == 1 ? Stream(m, t, c) : StreamFinal(m, t, c),
            (name, args) =>
            {
                executions++;
                return app.ExecuteAiToolAsync(name, args);
            },
            app.GetAiTools(onlyExplicit: true),
            maxSteps: 4,
            model: "test");

        Assert.Equal(1, executions);
        Assert.Equal(1, summary.ToolCallsExecuted);
        Assert.Equal("listed.", summary.FinalContent);

        // One assistant tool-call message and one tool result message.
        Assert.Equal(4, history.Count);
        Assert.Single(history[1].ToolCalls!);
        Assert.Equal("tool", history[2].Role);
    }

    [Fact]
    public void LlmCatalog_parsers_read_every_provider_shape()
    {
        Assert.Equal(
            ["mock-haiku", "mock-sonnet"],
            LlmCatalog.ParseOpenAiModels(
                """{"data":[{"id":"mock-haiku"},{"id":"mock-sonnet"}]}"""));

        Assert.Equal(
            ["claude-a", "claude-b"],
            LlmCatalog.ParseAnthropicModels(
                """{"data":[{"id":"claude-a"},{"id":"claude-b"}]}"""));

        Assert.Equal(
            ["gemini-2.5-flash", "gemini-2.5-pro"],
            LlmCatalog.ParseGeminiModels(
                """{"models":[{"name":"models/gemini-2.5-flash"},{"name":"models/gemini-2.5-pro"}]}"""));

        Assert.Equal(
            ["llama3.2:latest", "qwen3:8b"],
            LlmCatalog.ParseOllamaModels(
                """{"models":[{"name":"llama3.2:latest"},{"name":"qwen3:8b"}]}"""));

        // Malformed payloads yield empty lists, never exceptions.
        Assert.Empty(LlmCatalog.ParseOpenAiModels("{not json"));
        Assert.Empty(LlmCatalog.ParseGeminiModels("{}"));
    }

    private static (string AuthPath, List<(string Key, string? Value)> Originals) IsolateAuthAndProviderEnv()
    {
        var keys = new[]
        {
            "REPTAR_AGENT_AUTH", "REPTAR_AGENT_PROVIDER", "REPTAR_AGENT_MODEL", "REPTAR_AGENT_API_KEY",
            "OPENAI_API_KEY", "OPENAI_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL",
            "GEMINI_API_KEY", "GOOGLE_API_KEY", "GEMINI_BASE_URL", "OLLAMA_HOST",
            "OPENROUTER_API_KEY", "AI_GATEWAY_API_KEY"
        };

        var originals = keys.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToList();

        foreach (var key in keys)
            Environment.SetEnvironmentVariable(key, null);

        var authPath = Path.Combine(Path.GetTempPath(), $"reptar-auth-{Guid.NewGuid():N}", "auth.json");
        Environment.SetEnvironmentVariable("REPTAR_AGENT_AUTH", authPath);

        return (authPath, originals);
    }

    private static void RestoreEnv(List<(string Key, string? Value)> originals)
    {
        foreach (var (key, value) in originals)
            Environment.SetEnvironmentVariable(key, value);
    }

}
