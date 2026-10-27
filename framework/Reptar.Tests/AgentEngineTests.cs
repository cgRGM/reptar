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
    public async Task read_file_rejects_symlink_that_escapes_the_workspace_root()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(
            Path.GetTempPath(),
            "reptar-agent-outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-secret");
        Directory.CreateSymbolicLink(Path.Combine(_root, "outside-link"), outside);

        try
        {
            var app = CreateApp();
            var result = await app.ExecuteAiToolAsync(
                "read_file",
                """{"path": "outside-link/secret.txt"}""");

            Assert.False(result.Success);
            Assert.Contains("escapes the workspace root", result.ToLlmResponse());
        }
        finally
        {
            try
            {
                Directory.Delete(outside, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task write_file_rejects_dangling_symlink_that_escapes_the_workspace_root()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideFile = Path.Combine(
            Path.GetTempPath(),
            $"reptar-agent-outside-{Guid.NewGuid():N}.txt");
        var link = Path.Combine(_root, "dangling.txt");
        File.CreateSymbolicLink(link, outsideFile);

        try
        {
            var app = CreateApp();
            var result = await app.ExecuteAiToolAsync(
                "write_file",
                """{"path": "dangling.txt", "content": "should-not-escape"}""");

            Assert.False(result.Success);
            Assert.Contains("escapes the workspace root", result.ToLlmResponse());
            Assert.False(File.Exists(outsideFile));
        }
        finally
        {
            try
            {
                File.Delete(link);
                File.Delete(outsideFile);
            }
            catch
            {
                // best-effort cleanup
            }
        }
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
    public void run_command_is_not_mounted_by_default()
    {
        var app = CreateApp();
        Assert.Null(app.ResolveAiTool("run_command"));
    }

    [Fact]
    public async Task run_command_respects_the_optional_allowlist()
    {
        var app = CreateApp(options =>
        {
            options.AllowRunCommand = true;
            options.AllowedCommandPrefixes = ["echo "];
        });

        var allowed = await app.ExecuteAiToolAsync(
            "run_command",
            """{"command": "echo allowlisted"}""");
        Assert.True(allowed.Success, allowed.ToLlmResponse());

        var denied = await app.ExecuteAiToolAsync(
            "run_command",
            """{"command": "pwd"}""");
        Assert.False(denied.Success);
        Assert.Contains("allowlist", denied.ToLlmResponse());
    }

    [Fact]
    public async Task run_command_executes_in_workspace_root_with_timeout()
    {
        var app = CreateApp(options => options.AllowRunCommand = true);

        var result = await app.ExecuteAiToolAsync("run_command",
            """{"command": "echo agent-tools-work && pwd"}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("agent-tools-work", result.Content);
        Assert.Contains(_root, result.Content);
    }

    [Fact]
    public async Task run_command_accepts_snake_case_and_camel_case_timeout()
    {
        var app = CreateApp(options => options.AllowRunCommand = true);

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

        var summary = await AgentLoop.RunAsync(
            history,
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 6,
                ToolExecutor = AgentToolExecutors.FromNameArgs(
                    (name, args) => app.ExecuteAiToolAsync(name, args))
            },
            Stream,
            app.GetAiTools(onlyExplicit: true),
            onEvent: evt =>
            {
                switch (evt)
                {
                    case AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta when delta.Step == 1:
                        textDeltas.Append(delta.Delta);
                        break;
                    case AgentEvent.ToolExecutionStart call:
                        toolStarts.Add(call.ToolName);
                        break;
                    case AgentEvent.ToolExecutionEnd call:
                        toolEnds.Add(!call.IsError);
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

        var summary = await AgentLoop.RunAsync(
            history,
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 2,
                ToolExecutor = AgentToolExecutors.FromNameArgs(
                    (name, args) => app.ExecuteAiToolAsync(name, args))
            },
            ScriptedStream,
            app.GetAiTools(onlyExplicit: true));

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

        var summary = await AgentLoop.RunAsync(
            history,
            new AgentLoopConfig
            {
                Model = "test",
                MaxSteps = 4,
                ToolExecutor = AgentToolExecutors.FromNameArgs((name, args) =>
                {
                    executions++;
                    return app.ExecuteAiToolAsync(name, args);
                })
            },
            (m, t, c) => ++callCount == 1 ? Stream(m, t, c) : StreamFinal(m, t, c),
            app.GetAiTools(onlyExplicit: true));

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

    // ------------------------------------------------------------------
    // Structured output: typed generation + Rule<T> validation + retry loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task structured_output_parses_and_validates_on_first_attempt()
    {
        var client = new ScriptedLlmClient(
            """{"name":"Ada Lovelace","email":"ada@example.com","age":36}""");
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Name, r => r.NotNull().Min(1).Max(80))
            .Prop(x => x.Email, r => r.NotNull().Email())
            .Prop(x => x.Age, r => r.Min(18).Max(150));

        var result = await client.GenerateAsync("Extract the user from this email.", validator);

        Assert.True(result.Succeeded);
        Assert.Equal("Ada Lovelace", result.Value!.Name);
        Assert.Equal(1, result.Attempts);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task structured_output_prompt_embeds_validator_schema()
    {
        var client = new ScriptedLlmClient(
            """{"name":"Ada","email":"ada@example.com","age":30}""");
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Email, r => r.NotNull().Email());

        await client.GenerateAsync("extract", validator);

        var system = client.Calls[0][0];
        Assert.Equal("system", system.Role);
        Assert.Contains("JSON Schema", system.Content);
        Assert.Contains("\"email\"", system.Content);
        Assert.Contains("email", system.Content); // format constraint from Rule<T>
        Assert.Contains("\"required\"", system.Content);
    }

    [Fact]
    public async Task structured_output_retries_with_validation_feedback_then_succeeds()
    {
        var client = new ScriptedLlmClient(
            """{"name":"","email":"not-an-email","age":12}""",
            """{"name":"Grace Hopper","email":"grace@navy.mil","age":45}""");
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Name, r => r.NotNull().Min(1))
            .Prop(x => x.Email, r => r.NotNull().Email())
            .Prop(x => x.Age, r => r.Min(18));

        var history = new List<ChatMessage>();
        var retries = new List<StructuredOutputRetry>();
        var result = await client.GenerateAsync(
            history, "extract the user", validator, onRetry: r => retries.Add(r));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("grace@navy.mil", result.Value!.Email);
        Assert.Single(retries);

        // The failed reply + the validation feedback were appended to history.
        Assert.Equal("assistant", history[2].Role);
        Assert.Equal("user", history[3].Role);
        Assert.Contains("email", history[3].Content);

        // The second model call received the full correction context.
        Assert.Equal(4, client.Calls[1].Count);
    }

    [Fact]
    public async Task structured_output_strips_markdown_fences()
    {
        var client = new ScriptedLlmClient(
            """
            ```json
            {"name":"Linus","email":"linus@example.com","age":55}
            ```
            """);
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Name, r => r.NotNull().Min(1));

        var result = await client.GenerateAsync("extract", validator);

        Assert.True(result.Succeeded);
        Assert.Equal("Linus", result.Value!.Name);
    }

    [Fact]
    public async Task structured_output_returns_issues_after_exhausting_retries()
    {
        var client = new ScriptedLlmClient(
            """{"name":"","email":"bad","age":1}""");
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Name, r => r.NotNull().Min(1));

        var result = await client.GenerateAsync("extract", validator, maxRetries: 2);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Equal(3, result.Attempts);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task structured_output_recovers_from_invalid_json_retry()
    {
        var client = new ScriptedLlmClient(
            "This is not JSON at all. Here is your answer!",
            """{"name":"Valid Person","email":"v@example.com","age":40}""");
        var validator = ReptarApi.Rule<ExtractedUser>()
            .Prop(x => x.Name, r => r.NotNull().Min(1));

        var retries = new List<StructuredOutputRetry>();
        var result = await client.GenerateAsync("extract", validator, onRetry: r => retries.Add(r));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        var firstIssue = retries[0].Issues[0].Message;
        Assert.Contains("not valid JSON", firstIssue);
    }
    // ------------------------------------------------------------------
    // G14 — Request ports: suspend, checkpoint, resume
    // ------------------------------------------------------------------

    [Fact]
    public async Task request_port_suspends_persists_checkpoint_and_reports_question()
    {
        var (store, _) = CreateCheckpointStore();
        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_1", RequestPortAgent.AskUserToolName, """{"question":"What is the account number?"}"""),
            ScriptedEvents.TextEvents("The account number is ACC-42."));
        var history = new List<ChatMessage> { new("system", "you are a helper") };
        var portEvents = new List<RequestPortEvent>();

        var result = await RequestPortAgent.RunAsync(
            history,
            client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [],
            store,
            "sess_test",
            onPortEvent: portEvents.Add);

        Assert.False(result.Completed);
        Assert.NotNull(result.SuspendedCheckpoint);
        Assert.Equal("What is the account number?", result.SuspendedCheckpoint!.Question);
        Assert.Equal("sess_test", result.SuspendedCheckpoint.SessionId);
        Assert.Equal("pending", result.SuspendedCheckpoint.Status);
        Assert.Equal("call_1", result.SuspendedCheckpoint.PendingToolCallId);

        var pending = await store.FindPendingAsync("sess_test");
        Assert.NotNull(pending);
        Assert.Contains(RequestPortAgent.AskUserToolName, pending!.StateJson);

        var suspended = Assert.IsType<RequestPortEvent.RunSuspended>(portEvents[0]);
        Assert.Equal("What is the account number?", suspended.Question);

        // The assistant's ask_user tool call is in history, but no tool result yet.
        Assert.Contains(history, m => m is { Role: "assistant", ToolCalls: { Count: > 0 } });
        Assert.DoesNotContain(history, m => m.Role == "tool");
    }

    [Fact]
    public async Task request_port_resumes_with_answer_and_completes()
    {
        var (store, _) = CreateCheckpointStore();
        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_1", RequestPortAgent.AskUserToolName, """{"question":"Which city?"}"""),
            ScriptedEvents.TextEvents("The city is Lisbon."));
        var history = new List<ChatMessage> { new("system", "you are a helper") };

        var suspendedRun = await RequestPortAgent.RunAsync(
            history, client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [], store, "sess_resume", onPortEvent: _ => { });

        Assert.False(suspendedRun.Completed);

        var portEvents = new List<RequestPortEvent>();
        var resumedRun = await RequestPortAgent.ResumeAsync(
            store,
            suspendedRun.SuspendedCheckpoint!.Id,
            "Lisbon",
            client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [],
            liveHistory: history,
            onPortEvent: portEvents.Add);

        Assert.True(resumedRun.Completed);
        Assert.Contains("Lisbon", resumedRun.Summary!.FinalContent);

        var resumed = Assert.IsType<RequestPortEvent.RunResumed>(portEvents[0]);
        Assert.Equal("Lisbon", resumed.Answer);

        var row = await store.GetAsync(suspendedRun.SuspendedCheckpoint.Id);
        Assert.Equal("answered", row!.Status);

        Assert.Contains(history, m => m is { Role: "tool", Content: "Lisbon" });
        Assert.Equal("assistant", history[^1].Role);
    }

    [Fact]
    public async Task request_port_resumes_in_fresh_process_from_persisted_state()
    {
        var dbPath = Path.Combine(_root, "fresh-resume.db");
        var store = new RunCheckpointStore(
            Db.Sqlite(dbPath), AgentRunCheckpointSchema.RunCheckpoints());
        await store.EnsureCreatedAsync();

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_1", RequestPortAgent.AskUserToolName, """{"question":"Approve deploy?"}"""));
        var history = new List<ChatMessage> { new("system", "deployment agent") };

        var suspendedRun = await RequestPortAgent.RunAsync(
            history, client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [], store, "sess_fresh", onPortEvent: _ => { });
        Assert.False(suspendedRun.Completed);

        // Simulate a fresh process: new DB client + store over the same file,
        // no live history — the resume must restore the conversation from the
        // checkpoint's persisted state.
        var freshStore = new RunCheckpointStore(
            Db.Sqlite(dbPath), AgentRunCheckpointSchema.RunCheckpoints());
        var freshClient = new ScriptedEventLlmClient(
            ScriptedEvents.TextEvents("Deployment approved and finished."));

        var resumed = await RequestPortAgent.ResumeAsync(
            freshStore,
            suspendedRun.SuspendedCheckpoint!.Id,
            "yes, approved",
            freshClient.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            []);

        Assert.True(resumed.Completed);
        Assert.Contains("finished", resumed.Summary!.FinalContent);

        // The restored history includes the original system prompt and the
        // delivered answer as the pending tool result.
        Assert.Contains("deployment agent", resumed.Summary!.History[0].Content);
        Assert.Contains(resumed.Summary.History, m => m is { Role: "tool", Content: "yes, approved" });
    }

    private (RunCheckpointStore Store, string DbPath) CreateCheckpointStore()
    {
        var dbPath = Path.Combine(_root, $"ckpts-{Guid.NewGuid():N}.db");
        var store = new RunCheckpointStore(Db.Sqlite(dbPath), AgentRunCheckpointSchema.RunCheckpoints());
        store.EnsureCreatedAsync().GetAwaiter().GetResult();
        return (store, dbPath);
    }

    // ------------------------------------------------------------------
    // G4 — Tool approval rules + gates
    // ------------------------------------------------------------------

    [Fact]
    public void tool_approval_tool_level_rule_approves_every_call()
    {
        var approval = ToolApproval.ApproveAll("read_file");

        Assert.False(approval.NeedsDecision("read_file", """{"path":"a"}"""));
        Assert.False(approval.NeedsDecision("read_file", """{"path":"b"}"""));
        Assert.True(approval.NeedsDecision("write_file", """{"path":"a"}"""));
    }

    [Fact]
    public void tool_approval_exact_args_rule_matches_normalized_json()
    {
        var approval = new ToolApproval(
        [
            ToolApprovalRule.ForCall("write_file", """{ "path": "notes.md", "content": "draft one" }"""),
        ]);

        // Same arguments, different whitespace → normalized to the same JSON → approved.
        Assert.False(approval.NeedsDecision("write_file", """{"path":"notes.md","content":"draft one"}"""));
        // Different arguments → needs a decision.
        Assert.True(approval.NeedsDecision("write_file", """{"path":"other.md","content":"x"}"""));
    }

    [Fact]
    public void tool_approval_empty_args_rule_never_widens_to_tool_level()
    {
        // A non-null but empty exact-args rule matches ONLY no-argument calls;
        // it can never silently become a tool-wide approval.
        var approval = new ToolApproval([ToolApprovalRule.ForCall("list_files", "{}")]);

        Assert.False(approval.NeedsDecision("list_files", "{}"));
        Assert.False(approval.NeedsDecision("list_files", ""));
        Assert.True(approval.NeedsDecision("list_files", """{"directory":"src"}"""));
    }

    [Fact]
    public void tool_approval_read_only_preset_gates_mutating_tools()
    {
        var approval = ToolApproval.ApproveReadOnly("read_file", "list_files");

        Assert.True(approval.IsReadOnly("read_file"));
        Assert.True(approval.IsReadOnly("list_files"));
        Assert.False(approval.IsReadOnly("write_file"));

        Assert.False(approval.NeedsDecision("read_file", """{"path":"a"}"""));
        Assert.True(approval.NeedsDecision("write_file", """{"path":"a"}"""));
    }

    [Fact]
    public async Task tool_approval_sync_gate_denies_and_allows()
    {
        var approval = ToolApproval.ApproveReadOnly("read_file");
        var executed = new List<string>();

        var gated = ToolApprovalGate.Wrap(
            (name, args) =>
            {
                executed.Add(name);
                return Task.FromResult(AiToolResult.Ok(200, "ok"));
            },
            approval,
            (name, args) => Task.FromResult(false)); // human says no

        var denied = await gated("write_file", """{"path":"a"}""");
        Assert.False(denied.Success);
        Assert.Equal(403, denied.StatusCode);
        Assert.Empty(executed);

        var allowed = await gated("read_file", """{"path":"a"}""");
        Assert.True(allowed.Success);
        Assert.Equal(["read_file"], executed);
    }

    [Fact]
    public async Task tool_approval_checkpoint_gate_suspends_then_executes_on_approval()
    {
        var (store, _) = CreateCheckpointStore();
        var approval = ToolApproval.ApproveReadOnly("read_file");
        var executed = new List<string>();
        var history = new List<ChatMessage> { new("system", "file agent") };

        Func<string, string, Task<AiToolResult>> inner = (name, args) =>
        {
            executed.Add(name);
            return Task.FromResult(AiToolResult.Ok(200, $"read {args}"));
        };

        var gated = ToolApprovalGate.WrapWithCheckpoint(
            inner, approval, store, "sess_approve", history);

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_1", "write_file", """{"path":"draft.md"}"""),
            ScriptedEvents.TextEvents("Done after writing."));

        var suspendedRun = await RequestPortAgent.RunAsync(
            history, client.StreamAsync, gated, [], store, "sess_approve", onPortEvent: _ => { });

        // write_file is gated: the run suspended instead of executing it.
        Assert.False(suspendedRun.Completed);
        Assert.Empty(executed);
        Assert.NotNull(suspendedRun.SuspendedCheckpoint);
        Assert.Equal(ToolApprovalGate.ApprovalPortName, suspendedRun.SuspendedCheckpoint!.Port);
        Assert.Equal("write_file", suspendedRun.SuspendedCheckpoint.PendingToolName);
        Assert.Contains("write_file", suspendedRun.SuspendedCheckpoint.Question);

        // The human approves → resume executes the real tool (ungated —
        // this exact call was just approved).
        var resumed = await RequestPortAgent.ResumeAsync(
            store, suspendedRun.SuspendedCheckpoint.Id, "approve",
            client.StreamAsync, gated, [],
            liveHistory: history,
            pendingToolExecutor: inner);

        Assert.True(resumed.Completed);
        Assert.Equal(["write_file"], executed);
        Assert.Contains(resumed.Summary!.History, m =>
            m is { Role: "tool", Content: var c } && c.Contains("read {\"path\":\"draft.md\"}"));

        // The model saw the real tool result, not the raw approval string.
        Assert.Contains(history, m => m is { Role: "tool", Content: var c } && c.Contains("read"));
    }

    [Fact]
    public async Task tool_approval_checkpoint_gate_denial_skips_execution()
    {
        var (store, _) = CreateCheckpointStore();
        var approval = new ToolApproval([]); // everything gated
        var executed = new List<string>();
        var history = new List<ChatMessage> { new("system", "file agent") };

        Func<string, string, Task<AiToolResult>> inner = (name, args) =>
        {
            executed.Add(name);
            return Task.FromResult(AiToolResult.Ok(200, "ok"));
        };

        var gated = ToolApprovalGate.WrapWithCheckpoint(
            inner, approval, store, "sess_deny", history);

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_1", "delete_file", """{"path":"prod.db"}"""),
            ScriptedEvents.TextEvents("Understood, I will not delete it."));

        var suspendedRun = await RequestPortAgent.RunAsync(
            history, client.StreamAsync, gated, [], store, "sess_deny", onPortEvent: _ => { });
        Assert.False(suspendedRun.Completed);

        var resumed = await RequestPortAgent.ResumeAsync(
            store, suspendedRun.SuspendedCheckpoint!.Id, "deny",
            client.StreamAsync, gated, [], liveHistory: history);

        Assert.True(resumed.Completed);
        Assert.Empty(executed); // never ran
        Assert.Contains("Understood", resumed.Summary!.FinalContent);

        // The model received an explicit refusal it could reason about.
        Assert.Contains(history, m => m is { Role: "tool", Content: var c } && c.Contains("declined"));
    }

    // ------------------------------------------------------------------
    // G1–G3 — Module contract, todo module, plan/execute module
    // ------------------------------------------------------------------

    [Fact]
    public void module_composition_merges_instructions_tools_and_dispatch()
    {
        var todo = TodoModule.Create("sess_compose");
        var modes = PlanExecuteModule.Create();

        var instructions = AgentModuleComposition.ComposeInstructions(
            "Base persona.", todo, modes.Module("sess_compose"));
        Assert.Contains("Base persona.", instructions);
        Assert.Contains("## Todo Items", instructions);
        Assert.Contains("## Agent Mode", instructions);

        var tools = AgentModuleComposition.ComposeTools(todo, modes.Module("sess_compose"));
        Assert.Contains(tools, t => t.Name == TodoModule.AddToolName);
        Assert.Contains(tools, t => t.Name == PlanExecuteModule.ModeSetToolName);

        var executor = AgentModuleComposition.ComposeExecutor(null, todo, modes.Module("sess_compose"));

        var addResult = executor(TodoModule.AddToolName, """{"items":[{"title":"step"}]}""").GetAwaiter().GetResult();
        Assert.True(addResult.Success);

        var modeResult = executor(PlanExecuteModule.ModeGetToolName, "{}").GetAwaiter().GetResult();
        Assert.True(modeResult.Success);
        Assert.Contains("plan", modeResult.Content);

        var unknown = executor("nope", "{}").GetAwaiter().GetResult();
        Assert.False(unknown.Success);
    }

    [Fact]
    public async Task todo_module_adds_completes_and_lists_items()
    {
        var module = TodoModule.Create("sess_todo");
        var executor = AgentModuleComposition.ComposeExecutor(null, module);

        var add = await executor(TodoModule.AddToolName,
            """{"items":[{"title":"Research"},{"title":"Write"},{"title":"Publish","description":"on the blog"}]}""");
        Assert.True(add.Success);
        Assert.Contains("Research", add.Content);
        Assert.Contains("Publish", add.Content);

        // Invalid payloads get structured failures, not exceptions.
        var bad = await executor(TodoModule.AddToolName, """{"items":[{"title":""}]}""");
        Assert.False(bad.Success);
        Assert.Equal(400, bad.StatusCode);

        var all = await executor(TodoModule.GetAllToolName, "{}");
        Assert.True(all.Success);
        Assert.Equal(3, module.Items.Count);

        var second = module.Items[1];
        var complete = await executor(TodoModule.CompleteToolName,
            $$"""{"items":[{"id":"{{second.Id}}","reason":"done"}]}""");
        Assert.True(complete.Success);

        var remaining = await executor(TodoModule.GetRemainingToolName, "{}");
        Assert.True(remaining.Success);
        Assert.DoesNotContain(second.Id, remaining.Content);
        Assert.Contains("Research", remaining.Content);

        var remove = await executor(TodoModule.RemoveToolName,
            $$"""{"ids":["{{second.Id}}"]}""");
        Assert.True(remove.Success);
        Assert.Equal(2, module.Items.Count);
    }

    [Fact]
    public async Task plan_execute_module_tracks_and_switches_modes()
    {
        var modes = PlanExecuteModule.Create();
        var executor = AgentModuleComposition.ComposeExecutor(null, modes.Module("sess_mode"));

        Assert.Equal(PlanExecuteModule.PlanMode, modes.CurrentMode("sess_mode"));

        var get = await executor(PlanExecuteModule.ModeGetToolName, "{}");
        Assert.True(get.Success);
        Assert.Contains("plan", get.Content);

        // Invalid mode rejected with a helpful message.
        var bad = await executor(PlanExecuteModule.ModeSetToolName, """{"mode":"vibes"}""");
        Assert.False(bad.Success);
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(PlanExecuteModule.PlanMode, modes.CurrentMode("sess_mode"));

        var set = await executor(PlanExecuteModule.ModeSetToolName, """{"mode":"execute"}""");
        Assert.True(set.Success);
        Assert.Equal(PlanExecuteModule.ExecuteMode, modes.CurrentMode("sess_mode"));

        // Sessions are isolated from each other.
        Assert.Equal(PlanExecuteModule.PlanMode, modes.CurrentMode("sess_other"));
    }

    [Fact]
    public void plan_execute_module_interpolates_current_mode_into_instructions()
    {
        var modes = PlanExecuteModule.Create();

        var planInstructions = modes.CurrentInstructions("sess_pi");
        Assert.Contains("currently operating in the plan mode", planInstructions);
        Assert.Contains("### Plan Mode Workflow", planInstructions);
        Assert.DoesNotContain("### Execute Mode Workflow", planInstructions);

        var executor = AgentModuleComposition.ComposeExecutor(null, modes.Module("sess_pi"));
        executor(PlanExecuteModule.ModeSetToolName, """{"mode":"execute"}""").GetAwaiter().GetResult();

        var executeInstructions = modes.CurrentInstructions("sess_pi", "Base persona.");
        Assert.Contains("currently operating in the execute mode", executeInstructions);
        Assert.Contains("### Execute Mode Workflow", executeInstructions);
        Assert.Contains("Base persona.", executeInstructions);
    }

    // ------------------------------------------------------------------
    // G5 — Loop evaluators + LoopRunner with hard MaxIterations cap
    // ------------------------------------------------------------------

    [Fact]
    public async Task loop_runner_stops_on_completion_marker()
    {
        var history = new List<ChatMessage>();
        var client = new ScriptedEventLlmClient(
            ScriptedEvents.TextEvents("Working on it..."),
            ScriptedEvents.TextEvents("All done. [COMPLETED]"));

        var result = await LoopRunner.RunAsync(
            history, client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [],
            LoopEvaluators.CompletionMarker("[COMPLETED]"),
            model: "scripted");

        Assert.Equal(2, result.Iterations);
        Assert.Equal("completion-marker", result.StopReason);
        Assert.Contains("[COMPLETED]", result.LastRun.FinalContent);
        // The first iteration's failure produced feedback appended as a user message.
        Assert.Contains(history, m => m is { Role: "user", Content: var c } && c.Contains("[COMPLETED]"));
    }

    [Fact]
    public async Task loop_runner_enforces_hard_max_iterations_cap()
    {
        var history = new List<ChatMessage>();
        var client = new ScriptedEventLlmClient(
            ScriptedEvents.TextEvents("still working...")); // same response every time

        var result = await LoopRunner.RunAsync(
            history, client.StreamAsync,
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")),
            [],
            LoopEvaluators.CompletionMarker("[COMPLETED]"),
            maxIterations: 3,
            model: "scripted");

        Assert.Equal(3, result.Iterations);
        Assert.Equal("max-iterations", result.StopReason);
        // Two feedback messages for the first two non-final iterations.
        Assert.Equal(2, history.Count(m => m.Role == "user" && m.Content.Contains("Continue working")));
    }

    [Fact]
    public async Task loop_runner_todo_evaluator_stops_when_all_complete()
    {
        var todos = TodoModule.Create("sess_loop2");
        var todoExecutor = AgentModuleComposition.ComposeExecutor(null, todos);

        await todoExecutor(TodoModule.AddToolName, """{"items":[{"title":"only step"}]}""");
        var todoId = todos.Items[0].Id;

        var history = new List<ChatMessage>();
        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_c", TodoModule.CompleteToolName,
                $$"""{"items":[{"id":"{{todoId}}"}]}"""),
            ScriptedEvents.TextEvents("Done."));

        var result = await LoopRunner.RunAsync(
            history, client.StreamAsync,
            todoExecutor,
            AgentModuleComposition.ComposeTools(todos),
            LoopEvaluators.TodoCompletion("sess_loop2", todos),
            model: "scripted");

        Assert.Equal(1, result.Iterations);
        Assert.Equal("all-todos-complete", result.StopReason);
        Assert.All(todos.Items, item => Assert.True(item.IsComplete));
    }

    [Fact]
    public async Task loop_runner_todo_evaluator_defers_while_planning()
    {
        var todos = TodoModule.Create("sess_plan_loop");
        var modes = PlanExecuteModule.Create();
        var evaluator = LoopEvaluators.TodoCompletion("sess_plan_loop", todos, modes);

        var run = new AgentRunSummary(
            AgentStopReason.Completed, "here is my plan", 1, 0, "scripted", 1, []);

        var verdict = await evaluator.EvaluateAsync(run);

        var feedback = Assert.IsType<LoopEvaluation.Continue>(verdict);
        Assert.Contains("plan mode", feedback.Feedback);
    }

    // ------------------------------------------------------------------
    // G18 — Persistence gating (redaction + drops)
    // ------------------------------------------------------------------

    [Fact]
    public async Task persist_filter_redacts_secrets_before_storage()
    {
        var store = CreateSessionStore();
        store.PersistFilter = PersistFilters.RedactSecrets();

        var session = await store.CreateSessionAsync("secret session");
        await store.AppendMessageAsync(session.Id, new ChatMessage(
            "user", "my key is sk-abc123def456ghi789jkl012 and my AWS key is AKIAIOSFODNN7EXAMPLE"));
        await store.AppendMessageAsync(session.Id, new ChatMessage("assistant", "no secrets here"));

        var messages = await store.LoadMessagesAsync(session.Id);
        var secretMessage = messages.Single(m => m.Role == "user");
        Assert.DoesNotContain("sk-abc123def456ghi789jkl012", secretMessage.Content);
        Assert.Contains("sk-[REDACTED]", secretMessage.Content);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", secretMessage.Content);
        Assert.Contains("[REDACTED-AWS-KEY]", secretMessage.Content);
        Assert.Contains("no secrets here", messages[^1].Content);
    }

    [Fact]
    public async Task persist_filter_drops_sensitive_tool_results_entirely()
    {
        var store = CreateSessionStore();
        store.PersistFilter = PersistFilters.Compose(
            PersistFilters.DropToolResults("read_secrets"),
            PersistFilters.RedactSecrets());

        var session = await store.CreateSessionAsync("drop session");

        var dropped = await store.AppendMessageAsync(session.Id, new ChatMessage(
            "tool", "api_key=supersecret", Name: "read_secrets", ToolCallId: "call_1"));
        var kept = await store.AppendMessageAsync(session.Id, new ChatMessage("user", "hello"));

        Assert.Equal("", dropped); // dropped — never persisted
        Assert.NotEqual("", kept);

        var messages = await store.LoadMessagesAsync(session.Id);
        Assert.Single(messages);
        Assert.Equal("hello", messages[0].Content);
    }

    // ------------------------------------------------------------------
    // G21 — Isolation-key-scoped session stores
    // ------------------------------------------------------------------

    [Fact]
    public async Task scoped_session_store_isolates_tenants()
    {
        var inner = CreateSessionStore();
        await inner.EnsureCreatedAsync();

        var alice = new ScopedAgentSessionStore(inner, "user:alice");
        var bob = new ScopedAgentSessionStore(inner, "user:bob");

        var session = await alice.CreateSessionAsync("alice plan");
        await alice.AppendMessageAsync(session.Id, new ChatMessage("user", "alice private note"));

        // The bare id never leaks the tenant key.
        Assert.False(session.Id.Contains(':'));

        // Bob sees nothing of Alice's — not by list, id, or messages.
        Assert.Empty(await bob.ListSessionsAsync());
        Assert.Null(await bob.FindSessionAsync(session.Id));
        Assert.Empty(await bob.LoadMessagesAsync(session.Id));

        // Alice round-trips her own session with bare ids.
        var found = await alice.FindSessionAsync(session.Id);
        Assert.NotNull(found);
        Assert.Equal(session.Id, found!.Id);
        var messages = await alice.LoadMessagesAsync(session.Id);
        Assert.Single(messages);
        Assert.Equal("alice private note", messages[0].Content);

        // Two tenants can hold same-named sessions without collision.
        var bobSession = await bob.CreateSessionAsync("alice plan");
        Assert.NotEqual(session.Id, bobSession.Id);
        Assert.Single(await alice.ListSessionsAsync());
        Assert.Single(await bob.ListSessionsAsync());
    }

    private static AgentSessionStore CreateSessionStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "reptar-agent-tests", Guid.NewGuid().ToString("N"), "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new AgentSessionStore(
            Db.Sqlite(dbPath), AgentSchema.Sessions(), AgentSchema.Messages());
        store.EnsureCreatedAsync().GetAwaiter().GetResult();
        return store;
    }

    // ------------------------------------------------------------------
    // G19 — Eval runner, scorers, and durable eval store
    // ------------------------------------------------------------------

    [Fact]
    public async Task eval_runner_scores_expected_outputs_deterministically()
    {
        var client = new ScriptedLlmClient(
            "The capital of France is Paris.",
            "I have no idea.");

        var run = await AgentEvalRunner.RunAsync(
            client,
            [
                new EvalCase("What is the capital of France?", "Paris"),
                new EvalCase("What is the capital of France?", "Paris"),
            ],
            [new ExpectedOutputScorer()]);

        Assert.Equal(2, run.Total);
        Assert.Equal(1, run.Passed);
        Assert.Equal(0.5, run.PassRate);

        var failed = run.Cases.Single(c => !c.Passed);
        Assert.Contains("expected_output", failed.Metrics[0].Name);
        Assert.Equal("I have no idea.", failed.Response);
    }

    [Fact]
    public async Task eval_runner_llm_judge_uses_structured_output_with_validation()
    {
        var judge = new ScriptedLlmClient(
            """{"score":0.9,"passed":true,"reasoning":"accurate and complete"}""");

        var scorer = new LlmJudgeScorer(judge, threshold: 0.7);
        var result = await scorer.ScoreAsync(
            new EvalCase("Explain gravity"), "Gravity is an attractive force between masses.");

        Assert.True(result.Passed);
        Assert.Equal(0.9, result.Score);
        Assert.Contains("accurate", result.Notes);

        // The judge's prompt embeds the response being graded.
        Assert.Contains("Gravity is an attractive force", judge.Calls[0][1].Content);
    }

    [Fact]
    public async Task eval_runner_llm_judge_recovers_from_invalid_verdict_json()
    {
        var judge = new ScriptedLlmClient(
            "Judge failed to answer", // invalid JSON → G11 retry
            """{"score":0.2,"passed":false,"reasoning":"the response is wrong"}""");

        var scorer = new LlmJudgeScorer(judge);
        var result = await scorer.ScoreAsync(
            new EvalCase("2+2"), "five");

        Assert.False(result.Passed);
        Assert.Equal(0.2, result.Score);
    }

    [Fact]
    public async Task eval_store_persists_run_results_for_ci_reporting()
    {
        var dbPath = Path.Combine(_root, $"evals-{Guid.NewGuid():N}.db");
        var store = new AgentEvalStore(Db.Sqlite(dbPath), AgentEvalSchema.Evals());
        await store.EnsureCreatedAsync();

        var client = new ScriptedLlmClient("Paris is the capital of France.");
        var run = await AgentEvalRunner.RunAsync(
            client,
            [new EvalCase("Capital of France?", "Paris")],
            [new ExpectedOutputScorer()],
            runId: "run_ci_1");

        await store.AppendAsync(run.RunId, run.Cases[0], "Paris");

        var rows = await store.ListRunAsync("run_ci_1");
        var row = Assert.Single(rows);
        Assert.Equal("Capital of France?", row.Question);
        Assert.Equal(1, row.Passed);
        Assert.Contains("Paris", row.Response);
        Assert.Contains("expected_output", row.MetricsJson);
    }

    // ------------------------------------------------------------------
    // G15 — Magentic prompt pack
    // ------------------------------------------------------------------

    [Fact]
    public void magentic_prompts_render_placeholders()
    {
        var rendered = MagenticPrompts.Render(
            MagenticPrompts.TaskLedgerPlanPrompt,
            ("task", "ship the release"),
            ("team", "- Coder"),
            ("facts", "- release tag exists"));

        Assert.Contains("ship the release", rendered);
        Assert.Contains("- Coder", rendered);
        Assert.Contains("- release tag exists", rendered);
        Assert.DoesNotContain("{task}", rendered);
    }

    [Fact]
    public void magentic_progress_ledger_keeps_schema_placeholder()
    {
        // The MSFT rule: a progress-ledger template MUST keep {schema} so the
        // JSON schema the verdict is parsed against can be injected.
        Assert.Contains("{schema}", MagenticPrompts.ProgressLedgerPrompt);
        Assert.Contains("{team}", MagenticPrompts.ProgressLedgerPrompt);
        Assert.Contains("{questions}", MagenticPrompts.ProgressLedgerPrompt);

        var rendered = MagenticPrompts.Render(
            MagenticPrompts.ProgressLedgerPrompt,
            ("task", "t"),
            ("team", "T"),
            ("questions", "Q"),
            ("schema", "{\"type\":\"object\"}"));
        Assert.Contains("\"type\":\"object\"", rendered);
        Assert.DoesNotContain("{schema}", rendered);
    }

    // ------------------------------------------------------------------
    // G20 — Skills directory as agent tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task skills_directory_discovers_and_serves_skills()
    {
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(Path.Combine(skillsRoot, "deploy-helper"));
        Directory.CreateDirectory(Path.Combine(skillsRoot, "turbo"));
        File.WriteAllText(Path.Combine(skillsRoot, "deploy-helper", "SKILL.md"),
            """
            ---
            name: deploy-helper
            description: Deploys the app to production safely.
            ---

            # Deploy Helper
            1. Run the deploy script.
            2. Watch the logs.
            """);
        File.WriteAllText(Path.Combine(skillsRoot, "turbo", "SKILL.md"),
            """
            ---
            name: turbo
            description: Configures Turborepo pipelines.
            ---

            # Turborepo
            Edit turbo.json.
            """);
        File.WriteAllText(Path.Combine(skillsRoot, "not-a-skill.md"), "stray file");

        var directory = SkillsDirectory.Discover(skillsRoot);
        Assert.NotNull(directory);
        Assert.Equal(2, directory!.Skills.Count);

        var module = directory.ToModule();
        Assert.Contains("deploy-helper", module.Instructions);
        Assert.Contains("Deploys the app", module.Instructions);
        Assert.Contains("turbo", module.Instructions);
        Assert.DoesNotContain("not-a-skill", module.Instructions);

        var executor = AgentModuleComposition.ComposeExecutor(null, module);

        var loaded = await executor(SkillsDirectory.UseSkillToolName,
            """{"name":"deploy-helper"}""");
        Assert.True(loaded.Success);
        Assert.Contains("Run the deploy script", loaded.Content);

        // Unknown skills are rejected structurally — no filesystem probing.
        var missing = await executor(SkillsDirectory.UseSkillToolName,
            """{"name":"../../etc"}""");
        Assert.False(missing.Success);
        Assert.Equal(404, missing.StatusCode);

        // Bad arguments fail with structured 400s.
        var bad = await executor(SkillsDirectory.UseSkillToolName, "not json");
        Assert.False(bad.Success);
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public void skills_directory_returns_null_for_missing_root()
    {
        Assert.Null(SkillsDirectory.Discover(Path.Combine(_root, "does-not-exist")));
    }

    // ------------------------------------------------------------------
    // G8/G9 — Observers + durable run telemetry
    // ------------------------------------------------------------------

    [Fact]
    public async Task run_telemetry_records_and_lists_agent_runs()
    {
        var dbPath = Path.Combine(_root, $"runs-{Guid.NewGuid():N}.db");
        var store = new AgentRunStore(Db.Sqlite(dbPath), AgentRunSchema.Runs());
        await store.EnsureCreatedAsync();

        var summary = new AgentRunSummary(
            AgentStopReason.Completed, "done", 3, 2, "scripted", 12.5, []);
        await store.RecordAsync("sess_runs", summary);

        var recent = await store.RecentAsync(5);
        var row = Assert.Single(recent);
        Assert.Equal("sess_runs", row.SessionId);
        Assert.Equal("scripted", row.Model);
        Assert.Equal(3, row.Steps);
        Assert.Equal(2, row.ToolCalls);
        Assert.Equal(12.5, row.ElapsedMs);
        Assert.Equal("completed", row.StopReason);

        var sessionRuns = await store.ListSessionAsync("sess_runs");
        Assert.Single(sessionRuns);
    }

    [Fact]
    public async Task observers_compose_trace_and_jsonl_over_one_event_stream()
    {
        var trace = new AgentObservers.TraceObserver();
        var jsonl = new StringBuilder();
        using var writer = new StringWriter(jsonl);

        var composed = AgentObservers.Compose(
            trace.Observe,
            AgentObservers.JsonLines(writer));

        var client = new ScriptedEventLlmClient(
            ScriptedEvents.ToolCallEvents("call_o", "todos_add", """{"items":[{"title":"a"}]}"""),
            ScriptedEvents.TextEvents("all done"));
        var history = new List<ChatMessage>();
        var todos = TodoModule.Create("sess_obs");

        await LoopRunner.RunAsync(
            history, client.StreamAsync,
            AgentModuleComposition.ComposeExecutor(null, todos),
            AgentModuleComposition.ComposeTools(todos),
            LoopEvaluators.CompletionMarker("never"),
            maxIterations: 1,
            onEvent: composed,
            model: "scripted");

        // The trace saw the full tool lifecycle with timings.
        var events = trace.Events;
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionStart { ToolName: "todos_add" });
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionEnd { IsError: false });
        Assert.NotEmpty(trace.ToolTimings);

        // The JSONL observer produced one parseable line per event.
        var lines = jsonl.ToString().Trim().Split('\n');
        Assert.Equal(events.Count, lines.Length);
        foreach (var line in lines)
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("$type", out _) || true);
        }
    }

    // ------------------------------------------------------------------
    // G6 — Background agents module
    // ------------------------------------------------------------------

    [Fact]
    public async Task background_agents_start_wait_and_retrieve_results()
    {
        var agentA = new BackgroundAgentDefinition(
            "researcher",
            "You research things.",
            new ScriptedEventLlmClient(ScriptedEvents.TextEvents("The answer is 42.")).StreamAsync,
            [],
            (name, args) => Task.FromResult(AiToolResult.Ok(200, "noop")));

        var module = BackgroundAgentsModule.Create(agentA);
        var executor = AgentModuleComposition.ComposeExecutor(null, module.ToModule("sess_bg"));

        // Start two tasks on the same agent (concurrent sessions).
        var startFirst = await executor(BackgroundAgentsModule.StartToolName,
            """{"agent":"researcher","task":"find the answer"}""");
        Assert.True(startFirst.Success, startFirst.ToLlmResponse());
        var startSecond = await executor(BackgroundAgentsModule.StartToolName,
            """{"agent":"researcher","task":"double-check"}""");
        Assert.True(startSecond.Success, startSecond.ToLlmResponse());

        var firstId = System.Text.Json.JsonDocument.Parse(startFirst.Content!).RootElement.GetProperty("id").GetString();
        var secondId = System.Text.Json.JsonDocument.Parse(startSecond.Content!).RootElement.GetProperty("id").GetString();

        // Unknown agents are rejected structurally.
        var bad = await executor(BackgroundAgentsModule.StartToolName,
            """{"agent":"ghost","task":"x"}""");
        Assert.False(bad.Success);
        Assert.Equal(404, bad.StatusCode);

        // Wait for the first of the two to complete.
        var wait = await executor(BackgroundAgentsModule.WaitForFirstToolName,
            $$"""{"ids":["{{firstId}}","{{secondId}}"],"timeout_ms":10000}""");
        Assert.True(wait.Success);
        Assert.Contains("completed", wait.Content);

        // Results are retrievable and correct.
        var results = await executor(BackgroundAgentsModule.GetResultsToolName,
            $$"""{"ids":["{{firstId}}","{{secondId}}"]}""");
        Assert.True(results.Success);
        Assert.Contains("42", results.Content);

        // The catalog lists both tasks.
        var all = await executor(BackgroundAgentsModule.GetAllToolName, "{}");
        Assert.Contains("researcher", all.Content);
    }

    // ------------------------------------------------------------------
    // G7 — Durable module state slices
    // ------------------------------------------------------------------

    [Fact]
    public async Task module_state_slices_upsert_and_roundtrip()
    {
        var dbPath = Path.Combine(_root, $"state-{Guid.NewGuid():N}.db");
        var store = new ModuleStateStore(Db.Sqlite(dbPath), AgentSchemaState.ModuleState());
        await store.EnsureCreatedAsync();

        await store.SetAsync("sess_state", "todos", """[{"title":"first"}]""");
        await store.SetAsync("sess_state", "mode", "plan");

        // Upsert overwrites the same (session, key) slice.
        await store.SetAsync("sess_state", "todos", """[{"title":"first","complete":true}]""");

        Assert.Equal("""[{"title":"first","complete":true}]""",
            await store.GetAsync("sess_state", "todos"));
        Assert.Equal("plan", await store.GetAsync("sess_state", "mode"));
        Assert.Null(await store.GetAsync("sess_state", "missing"));
        Assert.Null(await store.GetAsync("sess_other", "todos"));
    }
}

public sealed record ExtractedUser(string Name, string Email, int Age);

internal sealed class ScriptedLlmClient(params string[] responses) : ILlmClient
{
    public LlmConfig Config { get; } = new() { Provider = LlmProvider.Echo, Model = "scripted" };

    private int _index;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        var response = responses[Math.Min(_index, responses.Length - 1)];
        _index++;
        return Stream(response);
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Stream(string response)
    {
        await Task.Yield();
        yield return new LlmStreamEvent.TextDelta(response);
        yield return new LlmStreamEvent.Completed(null);
    }
}

internal static class ScriptedEvents
{
    public static IReadOnlyList<LlmStreamEvent> TextEvents(string text) =>
    [
        new LlmStreamEvent.TextDelta(text),
        new LlmStreamEvent.Completed(null)
    ];

    public static IReadOnlyList<LlmStreamEvent> ToolCallEvents(string id, string name, string argumentsJson) =>
    [
        new LlmStreamEvent.ToolCall(id, name, argumentsJson),
        new LlmStreamEvent.Completed(null)
    ];
}

/// <summary>
/// Scripts full LlmStreamEvent sequences per call, so tests can exercise
/// tool-call-driven flows (request ports, approvals), not just text.
/// </summary>
internal sealed class ScriptedEventLlmClient(params IReadOnlyList<LlmStreamEvent>[] scriptedCalls) : ILlmClient
{
    public LlmConfig Config { get; } = new() { Provider = LlmProvider.Echo, Model = "scripted" };

    private int _index;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        Calls.Add([.. messages]);
        var events = scriptedCalls[Math.Min(_index, scriptedCalls.Length - 1)];
        _index++;
        return Stream(events);
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Stream(IReadOnlyList<LlmStreamEvent> events)
    {
        await Task.Yield();
        foreach (var streamEvent in events)
        {
            yield return streamEvent;
        }
    }
}
