using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for settings layering: user file, project file, defaults, and the trust gate
/// that decides whether the project file loads at all.
/// </summary>
public sealed class AgentSettingsTests : IDisposable
{
    private readonly string _root;

    public AgentSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string UserFile => Path.Combine(_root, "user-settings.json");
    private string ProjectFile => AgentSettingsStore.ProjectPath(_root);

    private void WriteUser(string json) => File.WriteAllText(UserFile, json);

    private void WriteProject(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectFile)!);
        File.WriteAllText(ProjectFile, json);
    }

    private AgentSettingsStore Load(bool trusted) =>
        AgentSettingsStore.Load(_root, trusted, UserFile, ProjectFile);

    // ------------------------------------------------------------------
    // Defaults
    // ------------------------------------------------------------------

    [Fact]
    public void with_no_files_the_defaults_apply()
    {
        var store = Load(trusted: false);

        Assert.Equal("off", store.Settings.Thinking);
        Assert.True(store.Settings.PromptCache);
        Assert.True(store.Settings.EffectiveCompaction.Enabled);
        Assert.Equal(TrustDefault.Ask, store.Settings.DefaultProjectTrust);
        Assert.Empty(store.Warnings);
    }

    // ------------------------------------------------------------------
    // Layering
    // ------------------------------------------------------------------

    [Fact]
    public void the_user_file_overrides_defaults()
    {
        WriteUser("""{ "model": "anthropic/claude-sonnet-4-5", "thinking": "high" }""");

        var store = Load(trusted: false);

        Assert.Equal("anthropic/claude-sonnet-4-5", store.Settings.Model);
        Assert.Equal(ThinkingLevel.High, store.Settings.ThinkingLevel);
    }

    [Fact]
    public void the_project_file_overrides_the_user_file_field_by_field()
    {
        WriteUser("""{ "model": "user/model", "thinking": "low" }""");
        WriteProject("""{ "thinking": "high" }""");

        var store = Load(trusted: true);

        // Project wins on thinking, user value survives on model.
        Assert.Equal(ThinkingLevel.High, store.Settings.ThinkingLevel);
        Assert.Equal("user/model", store.Settings.Model);
    }

    [Fact]
    public void an_untrusted_workspace_ignores_its_project_file()
    {
        WriteProject("""{ "model": "sneaky/model", "thinking": "max" }""");

        var store = Load(trusted: false);

        Assert.Null(store.Settings.Model);
        Assert.Equal(ThinkingLevel.Off, store.Settings.ThinkingLevel);
    }

    // ------------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------------

    [Fact]
    public void a_broken_file_is_reported_and_ignored()
    {
        WriteUser("{ this is not json");

        var store = Load(trusted: false);

        Assert.Single(store.Warnings);
        Assert.Equal(ThinkingLevel.Off, store.Settings.ThinkingLevel);
    }

    [Fact]
    public void an_empty_file_is_treated_as_absent()
    {
        WriteUser("   ");

        var store = Load(trusted: false);

        Assert.Empty(store.Warnings);
        Assert.Null(store.Settings.Model);
    }

    [Fact]
    public void comments_and_trailing_commas_are_accepted()
    {
        WriteUser("""
            {
              // the model I actually want
              "thinking": "medium",
            }
            """);

        var store = Load(trusted: false);

        Assert.Equal(ThinkingLevel.Medium, store.Settings.ThinkingLevel);
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task saving_the_model_writes_the_user_file_only()
    {
        var store = Load(trusted: false);

        await store.SaveModelAsync("openai", "gpt-4o-mini");

        Assert.True(File.Exists(UserFile));
        Assert.False(File.Exists(ProjectFile));

        var reloaded = Load(trusted: false);
        Assert.Equal("openai", reloaded.Settings.Provider);
        Assert.Equal("gpt-4o-mini", reloaded.Settings.Model);
    }

    [Fact]
    public async Task saving_thinking_preserves_other_fields()
    {
        WriteUser("""{ "model": "keep/me" }""");
        var store = Load(trusted: false);

        await store.SaveThinkingAsync(ThinkingLevel.High);

        var reloaded = Load(trusted: false);
        Assert.Equal("keep/me", reloaded.Settings.Model);
        Assert.Equal(ThinkingLevel.High, reloaded.Settings.ThinkingLevel);
    }

    // ------------------------------------------------------------------
    // Reflection-free JSON
    // ------------------------------------------------------------------

    [Fact]
    public void settings_round_trip_without_reflection()
    {
        var original = new AgentSettings
        {
            Model = "llama3.1:8b",
            Provider = "ollama",
            Thinking = "high",
            PromptCache = false,
            AutocompleteMaxVisible = 8,
            SteeringMode = AgentQueueMode.All,
            DefaultProjectTrust = TrustDefault.Never,
            Compaction = new CompactionSettings { Enabled = false, KeepRecentTokens = 1234, ReserveTokens = 567 },
            Retry = new AgentRetryPolicy { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(3) },
            ScopedModels = [new ScopedModel("ollama", "llama3.1:8b", "high")]
        };

        var restored = AgentSettings.FromJson(original.ToJson());

        Assert.Equal("llama3.1:8b", restored.Model);
        Assert.Equal("ollama", restored.Provider);
        Assert.Equal("high", restored.Thinking);
        Assert.False(restored.PromptCache);
        Assert.Equal(8, restored.AutocompleteMaxVisible);
        Assert.Equal(AgentQueueMode.All, restored.SteeringMode);
        Assert.Equal(TrustDefault.Never, restored.DefaultProjectTrust);
        Assert.False(restored.EffectiveCompaction.Enabled);
        Assert.Equal(1234, restored.EffectiveCompaction.KeepRecentTokens);
        Assert.Equal(5, restored.EffectiveRetry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), restored.EffectiveRetry.BaseDelay);

        var scoped = Assert.Single(restored.ScopedModels!);
        Assert.Equal("ollama", scoped.Provider);
        Assert.Equal("high", scoped.Thinking);
    }

    [Fact]
    public void a_malformed_field_falls_back_to_unset_rather_than_discarding_the_file()
    {
        var restored = AgentSettings.FromJson(
            System.Text.Json.Nodes.JsonNode.Parse("{ \"model\": 42, \"provider\": \"ollama\" }"));

        Assert.Null(restored.Model);
        Assert.Equal("ollama", restored.Provider);
    }

    [Fact]
    public void an_empty_document_yields_defaults()
    {
        var restored = AgentSettings.FromJson(null);

        Assert.Null(restored.Model);
        Assert.Null(restored.ScopedModels);
    }

    // ------------------------------------------------------------------
    // Describe
    // ------------------------------------------------------------------

    [Fact]
    public void describe_lists_the_effective_values_and_both_paths()
    {
        WriteUser("""{ "model": "a/b", "thinking": "high" }""");

        var text = Load(trusted: false).Describe();

        Assert.Contains("thinking         high", text);
        Assert.Contains("a/b", text);
        Assert.Contains(UserFile, text);
    }
}
