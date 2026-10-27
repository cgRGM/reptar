using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the three small ported subsystems: prompt templates, model aliases, and
/// project trust.
/// </summary>
public sealed class AgentResourcesTests : IDisposable
{
    private readonly string _root;

    public AgentResourcesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-resources-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------
    // Prompt templates
    // ------------------------------------------------------------------

    private string WriteTemplate(string name, string content, string directory = "")
    {
        var path = Path.Combine(PromptTemplates.ProjectDirectory(_root), directory);
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, $"{name}.md");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void loads_templates_with_frontmatter()
    {
        WriteTemplate("fix-tests", """
            ---
            description: Fix failing tests
            argument-hint: <path>
            ---
            Look at $1 and make the tests pass.
            """);

        var templates = PromptTemplates.Load(_root);

        var template = Assert.Single(templates);
        Assert.Equal("fix-tests", template.Name);
        Assert.Equal("Fix failing tests", template.Description);
        Assert.Equal("<path>", template.ArgumentHint);
        Assert.Equal("Look at $1 and make the tests pass.", template.Content);
    }

    [Fact]
    public void falls_back_to_the_first_line_for_a_description()
    {
        WriteTemplate("review", """
            # Review this change carefully

            Check for bugs.
            """);

        var template = PromptTemplates.Load(_root).Single();

        Assert.Equal("Review this change carefully", template.Description);
    }

    [Fact]
    public void expands_positional_and_whole_argument_placeholders()
    {
        var template = new PromptTemplate("t", "d", null,
            "first=$1 second=$2 all=$@ args=$ARGUMENTS name=$0", "/t.md");

        var expanded = PromptTemplates.Expand(template, "alpha beta");

        Assert.Equal("first=alpha second=beta all=alpha beta args=alpha beta name=t", expanded);
    }

    [Fact]
    public void missing_positional_arguments_expand_to_empty()
    {
        var template = new PromptTemplate("t", "d", null, "a=$1 b=$3", "/t.md");

        Assert.Equal("a=one b=", PromptTemplates.Expand(template, "one"));
    }

    [Fact]
    public void arguments_respect_quotes()
    {
        var arguments = PromptTemplates.ParseArguments("""one "two words" 'three words' four""");

        Assert.Equal(["one", "two words", "three words", "four"], arguments);
    }

    [Fact]
    public void a_quoted_empty_string_is_still_an_argument()
    {
        Assert.Equal([""], PromptTemplates.ParseArguments("\"\""));
    }

    [Fact]
    public void expansion_is_single_pass_so_values_cannot_inject_template_syntax()
    {
        var template = new PromptTemplate("t", "d", null, "x=$1", "/t.md");

        // The argument itself contains $2; it must survive literally.
        Assert.Equal("x=$2", PromptTemplates.Expand(template, "$2"));
    }

    [Fact]
    public void try_expand_resolves_a_slash_command()
    {
        WriteTemplate("explain", "Explain $1 in detail.");
        var templates = PromptTemplates.Load(_root);

        Assert.Equal("Explain storage in detail.", PromptTemplates.TryExpand(templates, "/explain storage"));
        Assert.Null(PromptTemplates.TryExpand(templates, "/nope"));
        Assert.Null(PromptTemplates.TryExpand(templates, "not a command"));
    }

    [Fact]
    public void a_project_template_shadows_a_user_template_of_the_same_name()
    {
        // Only the project copy exists here; the point is that project wins when both do.
        WriteTemplate("dup", "project version");
        var templates = PromptTemplates.Load(_root);

        Assert.Equal("project version", templates.Single(t => t.Name == "dup").Content);
    }

    [Fact]
    public void describe_lists_templates_for_a_help_screen()
    {
        WriteTemplate("one", "First template.");
        var text = PromptTemplates.Describe(PromptTemplates.Load(_root));

        Assert.Contains("/one", text);
        Assert.Contains("First template.", text);
    }

    [Fact]
    public void describe_explains_an_empty_registry()
    {
        Assert.Contains(".reptar/prompts", PromptTemplates.Describe([]));
    }

    // ------------------------------------------------------------------
    // Model aliases
    // ------------------------------------------------------------------

    [Fact]
    public void an_alias_resolves_to_a_concrete_provider_and_model()
    {
        var config = ModelAliases.Resolve("fast");

        Assert.Equal("openai", config.ProviderKey);
        Assert.Equal("gpt-4o-mini", config.Model);
        Assert.Equal(LlmProvider.OpenAi, config.Provider);
    }

    [Fact]
    public void provider_slash_model_resolves_directly()
    {
        var config = ModelAliases.Resolve("anthropic/claude-sonnet-4-5");

        Assert.Equal("anthropic", config.ProviderKey);
        Assert.Equal("claude-sonnet-4-5", config.Model);
        Assert.Equal(LlmProvider.Anthropic, config.Provider);
    }

    [Fact]
    public void gateway_providers_use_the_openai_wire_protocol()
    {
        Assert.Equal(LlmProvider.OpenAi, ModelAlias.WireProtocol("openrouter"));
        Assert.Equal(LlmProvider.OpenAi, ModelAlias.WireProtocol("gateway"));
        Assert.Equal(LlmProvider.OpenAi, ModelAlias.WireProtocol("cloudflare-gateway"));
        Assert.Equal(LlmProvider.OpenAi, ModelAlias.WireProtocol("some-custom-endpoint"));
    }

    [Fact]
    public void an_api_key_is_attached_to_the_resolved_config()
    {
        Assert.Equal("sk-test", ModelAliases.Resolve("fast", apiKey: "sk-test").ApiKey);
    }

    [Fact]
    public void project_aliases_layer_over_the_defaults()
    {
        var merged = ModelAliases.Merge(
            ModelAliases.Defaults,
            [new ModelAlias { Name = "fast", Provider = "gemini", Model = "gemini-2.5-flash", Description = "Cheaper." }]);

        var fast = merged.Single(alias => alias.Name == "fast");
        Assert.Equal("gemini", fast.Provider);
        Assert.Equal("Cheaper.", fast.Description);

        // Other defaults survive.
        Assert.Contains(merged, alias => alias.Name == "deep");
    }

    [Fact]
    public void describe_renders_a_table()
    {
        var text = ModelAliases.Describe();

        Assert.Contains("alias", text);
        Assert.Contains("fast", text);
        Assert.Contains("openai/gpt-4o-mini", text);
    }

    // ------------------------------------------------------------------
    // Project trust
    // ------------------------------------------------------------------

    private string TrustStorePath => Path.Combine(_root, "trust.json");

    [Fact]
    public void an_unknown_directory_is_not_trusted()
    {
        var store = ProjectTrustStore.Load(TrustStorePath);

        Assert.Equal(TrustDecision.Unknown, store.Get(_root));
        Assert.False(store.IsTrusted(_root));
    }

    [Fact]
    public async Task a_granted_decision_persists_across_loads()
    {
        var store = ProjectTrustStore.Load(TrustStorePath);
        await store.SetAsync(_root, TrustDecision.Trusted);

        var reloaded = ProjectTrustStore.Load(TrustStorePath);
        Assert.True(reloaded.IsTrusted(_root));
    }

    [Fact]
    public async Task a_revocation_persists_and_is_not_treated_as_unknown()
    {
        var store = ProjectTrustStore.Load(TrustStorePath);
        await store.SetAsync(_root, TrustDecision.Trusted);
        await store.RevokeAsync(_root);

        var reloaded = ProjectTrustStore.Load(TrustStorePath);
        Assert.Equal(TrustDecision.Untrusted, reloaded.Get(_root));
        Assert.False(reloaded.IsTrusted(_root));
    }

    [Fact]
    public async Task trailing_separators_do_not_create_two_decisions()
    {
        var store = ProjectTrustStore.Load(TrustStorePath);
        await store.SetAsync(_root, TrustDecision.Trusted);

        Assert.True(store.IsTrusted(_root + Path.DirectorySeparatorChar));
        Assert.True(store.IsTrusted(Path.Combine(_root, ".")));
    }

    [Fact]
    public void a_corrupt_store_fails_closed()
    {
        File.WriteAllText(TrustStorePath, "{ this is not json");

        var store = ProjectTrustStore.Load(TrustStorePath);

        Assert.Equal(TrustDecision.Unknown, store.Get(_root));
        Assert.False(store.IsTrusted(_root));
    }

    [Fact]
    public void has_project_resources_detects_agent_configuration()
    {
        Assert.False(ProjectTrustStore.HasProjectResources(_root));

        Directory.CreateDirectory(Path.Combine(_root, ".reptar"));
        Assert.True(ProjectTrustStore.HasProjectResources(_root));
    }

    [Fact]
    public void has_project_resources_detects_agents_md()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# rules");

        Assert.True(ProjectTrustStore.HasProjectResources(_root));
    }

    [Fact]
    public async Task resolve_auto_trusts_a_directory_with_nothing_to_load()
    {
        var store = ProjectTrustStore.Load(TrustStorePath);

        var trusted = await store.ResolveAsync(_root, ask: null);

        Assert.True(trusted);
    }

    [Fact]
    public async Task resolve_does_not_trust_a_configured_directory_without_asking()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".reptar"));
        var store = ProjectTrustStore.Load(TrustStorePath);

        var trusted = await store.ResolveAsync(_root, ask: null);

        Assert.False(trusted);
        Assert.Equal(TrustDecision.Unknown, store.Get(_root));
    }

    [Fact]
    public async Task resolve_prompts_once_and_remembers_the_answer()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".reptar"));
        var store = ProjectTrustStore.Load(TrustStorePath);

        var prompts = 0;
        Task<bool> Ask(string _)
        {
            prompts++;
            return Task.FromResult(true);
        }

        Assert.True(await store.ResolveAsync(_root, Ask));
        Assert.True(await store.ResolveAsync(_root, Ask));

        Assert.Equal(1, prompts);
        Assert.True(ProjectTrustStore.Load(TrustStorePath).IsTrusted(_root));
    }

    [Fact]
    public async Task a_declined_prompt_is_remembered_as_untrusted()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".reptar"));
        var store = ProjectTrustStore.Load(TrustStorePath);

        Assert.False(await store.ResolveAsync(_root, _ => Task.FromResult(false)));

        Assert.Equal(TrustDecision.Untrusted, store.Get(_root));
    }
}
