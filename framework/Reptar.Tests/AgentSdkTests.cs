using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the SDK facade: tool selection semantics, session lifecycle, stats, and
/// the one-liner prompt path.
/// </summary>
public sealed class AgentSdkTests : IDisposable
{
    private readonly string _root;

    public AgentSdkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-sdk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "one\ntwo\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static List<AiToolDefinition> Tools(params string[] names) =>
        [.. names.Select(name => new AiToolDefinition
        {
            Name = name,
            Description = $"{name} tool",
            Method = "POST",
            Path = $"/{name}",
            Parameters = new Dictionary<string, object?>()
        })];

    private static string[] Names(IReadOnlyList<AiToolDefinition> tools) =>
        [.. tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal)];

    // ------------------------------------------------------------------
    // Tool selection
    // ------------------------------------------------------------------

    [Fact]
    public void default_selection_keeps_everything()
    {
        var selected = ToolSelection.Default.Apply(Tools("read_file", "write_file", "run_command"));

        Assert.Equal(["read_file", "run_command", "write_file"], Names(selected));
    }

    [Fact]
    public void no_tools_selects_nothing()
    {
        Assert.Empty(ToolSelection.NoTools.Apply(Tools("read_file", "write_file")));
    }

    [Fact]
    public void allowlist_keeps_only_the_named_tools()
    {
        var selected = ToolSelection.Only("read_file").Apply(Tools("read_file", "write_file", "list_dir"));

        Assert.Equal(["read_file"], Names(selected));
    }

    [Fact]
    public void allowlist_supports_wildcards()
    {
        var selected = ToolSelection.Only("*_file").Apply(Tools("read_file", "write_file", "run_command"));

        Assert.Equal(["read_file", "write_file"], Names(selected));
    }

    [Fact]
    public void a_bare_star_allows_everything()
    {
        var selected = ToolSelection.Only("*").Apply(Tools("a", "b"));

        Assert.Equal(["a", "b"], Names(selected));
    }

    [Fact]
    public void denylist_removes_only_the_named_tools()
    {
        var selected = ToolSelection.AllExcept("run_command").Apply(Tools("read_file", "run_command"));

        Assert.Equal(["read_file"], Names(selected));
    }

    [Fact]
    public void denylist_is_applied_after_the_allowlist()
    {
        var selection = new ToolSelection
        {
            Include = ["read_file", "write_file"],
            Exclude = ["write_file"]
        };

        Assert.Equal(["read_file"], Names(selection.Apply(Tools("read_file", "write_file", "list_dir"))));
    }

    [Fact]
    public void modifiers_add_and_remove_against_the_default_selection()
    {
        var selection = ToolSelection.Adjusting("+run_command", "-write_file");

        Assert.Equal(["read_file", "run_command"], Names(selection.Apply(Tools("read_file", "write_file", "run_command"))));
    }

    [Fact]
    public void modifiers_can_add_a_tool_that_was_not_present()
    {
        var selection = ToolSelection.Adjusting("+list_dir");
        var selected = selection.Apply(Tools("read_file", "list_dir"));

        Assert.Equal(["list_dir", "read_file"], Names(selected));
    }

    [Fact]
    public void mixing_an_allowlist_with_modifiers_is_rejected()
    {
        var selection = new ToolSelection
        {
            Include = ["read_file"],
            Adjust = ["+write_file"]
        };

        var error = Assert.Throws<ArgumentException>(() => selection.Apply(Tools("read_file")));
        Assert.Contains("allowlist", error.Message);
    }

    [Fact]
    public void a_modifier_without_a_sign_is_rejected()
    {
        var selection = ToolSelection.Adjusting("read_file");

        Assert.Throws<ArgumentException>(() => selection.Apply(Tools("read_file")));
    }

    [Fact]
    public void read_only_preset_excludes_mutating_tools()
    {
        var selected = ToolSelection.ReadOnly.Apply(Tools("read_file", "write_file", "edit_file", "list_dir", "run_command"));

        Assert.Equal(["list_dir", "read_file", "run_command"], Names(selected));
    }

    // ------------------------------------------------------------------
    // Stats
    // ------------------------------------------------------------------

    [Fact]
    public void stats_sum_usage_and_count_messages()
    {
        var messages = new List<ChatMessage>
        {
            new(AgentRoles.User, "hi"),
            new(AgentRoles.Assistant, "", ToolCalls: [new AiToolCall("c1", "read_file", "{}")],
                Usage: new AgentUsage(100, 20)),
            new(AgentRoles.Tool, "contents", ToolCallId: "c1", Name: "read_file"),
            new(AgentRoles.Assistant, "done", Usage: new AgentUsage(150, 30))
        };

        var stats = AgentSessionStats.From(messages, contextWindow: 1_000);

        Assert.Equal(1, stats.UserMessages);
        Assert.Equal(2, stats.AssistantMessages);
        Assert.Equal(1, stats.ToolCalls);
        Assert.Equal(1, stats.ToolResults);
        Assert.Equal(4, stats.TotalMessages);
        Assert.Equal(250, stats.Usage.InputTokens);
        Assert.Equal(50, stats.Usage.OutputTokens);
        Assert.Equal(1_000, stats.ContextWindow);
    }

    [Fact]
    public void stats_report_zero_percent_without_a_window()
    {
        var stats = AgentSessionStats.From([new ChatMessage(AgentRoles.User, "hi")]);
        Assert.Equal(0, stats.ContextPercent);
    }

    // ------------------------------------------------------------------
    // Facade
    // ------------------------------------------------------------------

    [Fact]
    public async Task create_async_resolves_tools_and_a_session()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = true
        });

        Assert.NotNull(agent.SessionId);
        Assert.NotEmpty(agent.Tools);
        Assert.Contains(agent.Tools, tool => tool.Name == "read_file");
        Assert.DoesNotContain(agent.Tools, tool => tool.Name == "run_command"); // opt-in
    }

    [Fact]
    public async Task allow_shell_mounts_run_command()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            AllowShell = true,
            Persist = false
        });

        Assert.Contains(agent.Tools, tool => tool.Name == "run_command");
    }

    [Fact]
    public async Task tool_selection_is_applied_at_creation()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false,
            Tools = ToolSelection.Only("read_file")
        });

        Assert.Equal(["read_file"], Names(agent.Tools));
    }

    [Fact]
    public async Task attach_reuses_an_app_the_caller_configured()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root);

        var agent = ReptarAgent.Attach(app, new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false
        });

        Assert.Contains(agent.Tools, tool => tool.Name == "read_file");
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task prompt_streams_text_and_returns_a_summary()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root);

        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("all done"));

        var agent = ReptarAgent.Attach(app, new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false
        }, client);

        var text = new System.Text.StringBuilder();
        var summary = await agent.PromptAsync("hello", onText: delta => text.Append(delta));

        Assert.Equal(AgentStopReason.Completed, summary.StopReason);
        Assert.Equal("all done", text.ToString());
        Assert.Contains(agent.Messages, m => m.Role == AgentRoles.User && m.Content == "hello");
        Assert.Contains(agent.Messages, m => m.Role == AgentRoles.Assistant && m.Content == "all done");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task compact_returns_null_when_there_is_nothing_to_compact()
    {
        var app = ReptarApp.Create();
        var client = new ScriptedEventLlmClient(ScriptedEvents.TextEvents("summary"));

        var agent = ReptarAgent.Attach(app, new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false
        }, client);

        var result = await agent.CompactAsync();

        Assert.Null(result);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task model_aliases_resolve_at_creation()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false,
            Model = "fast"
        });

        Assert.Equal("openai", agent.Config.ProviderKey);
        Assert.Equal("gpt-4o-mini", agent.Config.Model);
    }

    [Fact]
    public async Task project_aliases_layer_over_the_defaults()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false,
            Model = "fast",
            Aliases = ModelAliases.Merge(
                ModelAliases.Defaults,
                [new ModelAlias { Name = "fast", Provider = "gemini", Model = "gemini-2.5-flash" }])
        });

        Assert.Equal("gemini", agent.Config.ProviderKey);
    }

    [Fact]
    public async Task prompt_templates_load_and_expand()
    {
        var directory = PromptTemplates.ProjectDirectory(_root);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "explain.md"),
            "Explain $1 in detail.");

        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false
        });

        Assert.Single(agent.Templates);
        Assert.Equal("Explain storage in detail.", agent.ExpandPrompt("/explain storage"));
        Assert.Null(agent.ExpandPrompt("/unknown"));
    }

    [Fact]
    public async Task an_untrusted_workspace_does_not_load_its_templates()
    {
        var directory = PromptTemplates.ProjectDirectory(_root);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sneaky.md"), "do something else");

        var trust = ProjectTrustStore.Load(Path.Combine(_root, "trust.json"));

        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = false,
            Trust = trust
        });

        Assert.Empty(agent.Templates);
    }

    [Fact]
    public async Task a_second_session_starts_from_an_empty_transcript()
    {
        await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
        {
            WorkspaceRoot = _root,
            Persist = true
        });

        agent.Messages.Add(new ChatMessage(AgentRoles.User, "old"));
        await agent.NewSessionAsync("fresh");

        Assert.Empty(agent.Messages);
    }
}
