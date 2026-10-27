using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for skill discovery diagnostics and the resources report. The rejections are
/// the point: a shadowed or unparseable skill is silent otherwise.
/// </summary>
public sealed class SkillDiscoveryTests : IDisposable
{
    private readonly string _root;

    public SkillDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-skills-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ProjectSkills => Path.Combine(_root, "project", ".agents", "skills");
    private string UserSkills => Path.Combine(_root, "user", ".agents", "skills");

    private string WriteSkill(string skillsRoot, string directory, string content)
    {
        var path = Path.Combine(skillsRoot, directory);
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "SKILL.md");
        File.WriteAllText(file, content);
        return file;
    }

    private static string Skill(string name, string description = "does a thing") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\nBody of {name}.";

    // ------------------------------------------------------------------
    // Discovery
    // ------------------------------------------------------------------

    [Fact]
    public void a_valid_skill_is_discovered_with_its_scope()
    {
        WriteSkill(ProjectSkills, "alpha", Skill("alpha"));

        var result = SkillsDirectory.DiscoverAll(ProjectSkills);

        var skill = Assert.Single(result.Skills);
        Assert.Equal("alpha", skill.Name);
        Assert.Equal(SkillScope.Project, skill.Scope);
        Assert.Empty(result.Shadowed);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void a_project_skill_shadows_a_user_skill_and_both_are_reported()
    {
        var userFile = WriteSkill(UserSkills, "dup", Skill("dup", "user version"));
        var projectFile = WriteSkill(ProjectSkills, "dup", Skill("dup", "project version"));

        var result = SkillsDirectory.DiscoverAll(ProjectSkills, UserSkills);

        var winner = Assert.Single(result.Skills);
        Assert.Equal("project version", winner.Description);
        Assert.Equal(SkillScope.Project, winner.Scope);
        Assert.Equal(projectFile, winner.SkillFile);

        // The losing file is named, not silently dropped.
        var shadowed = Assert.Single(result.Shadowed);
        Assert.Equal("dup", shadowed.Name);
        Assert.Equal(userFile, shadowed.SkillFile);
        Assert.Contains(projectFile, shadowed.Reason);
    }

    [Fact]
    public void a_skill_missing_a_description_is_rejected_with_a_reason()
    {
        WriteSkill(ProjectSkills, "nodesc", "---\nname: nodesc\n---\n\nBody.");

        var result = SkillsDirectory.DiscoverAll(ProjectSkills);

        Assert.Empty(result.Skills);
        var invalid = Assert.Single(result.Invalid);
        Assert.Equal("description is required", invalid.Reason);
    }

    [Fact]
    public void a_skill_missing_frontmatter_is_rejected()
    {
        WriteSkill(ProjectSkills, "plain", "# Just a heading\n\nNo frontmatter.");

        var result = SkillsDirectory.DiscoverAll(ProjectSkills);

        Assert.Empty(result.Skills);
        Assert.Single(result.Invalid);
        Assert.Contains("frontmatter", result.Invalid[0].Reason);
    }

    [Fact]
    public void an_unsafe_skill_name_is_rejected()
    {
        WriteSkill(ProjectSkills, "bad", "---\nname: Bad_Name\ndescription: x\n---\n\nBody.");

        var result = SkillsDirectory.DiscoverAll(ProjectSkills);

        Assert.Empty(result.Skills);
        Assert.Contains("lowercase slug", result.Invalid[0].Reason);
    }

    [Fact]
    public void a_directory_without_a_skill_file_is_ignored_silently()
    {
        Directory.CreateDirectory(Path.Combine(ProjectSkills, "empty"));

        var result = SkillsDirectory.DiscoverAll(ProjectSkills);

        Assert.Empty(result.Skills);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void a_missing_root_is_not_an_error()
    {
        var result = SkillsDirectory.DiscoverAll(Path.Combine(_root, "nope"));

        Assert.Empty(result.Skills);
        Assert.False(result.HasProblems);
    }

    [Fact]
    public void the_user_root_is_optional()
    {
        WriteSkill(UserSkills, "onlyuser", Skill("onlyuser"));

        Assert.Empty(SkillsDirectory.DiscoverAll(ProjectSkills).Skills);
        Assert.Single(SkillsDirectory.DiscoverAll(ProjectSkills, UserSkills).Skills);
    }

    // ------------------------------------------------------------------
    // Report
    // ------------------------------------------------------------------

    private AgentResourcesReport Report(bool trusted) =>
        AgentResourcesReport.Gather(_root, trusted, userSkillsRoot: "");

    [Fact]
    public void the_report_lists_context_files()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# rules");

        var report = Report(trusted: true);

        var file = Assert.Single(report.ContextFiles);
        Assert.EndsWith("AGENTS.md", file.Path);
        Assert.Contains("AGENTS.md", report.Render());
    }

    [Fact]
    public void the_report_surfaces_skill_conflicts()
    {
        // A real shadow: the same skill name in the user root and the project root.
        WriteSkill(UserSkills, "dup", Skill("dup", "user version"));
        WriteSkill(Path.Combine(_root, ".agents", "skills"), "dup", Skill("dup", "project version"));

        var report = AgentResourcesReport.Gather(
            _root, trusted: true, userSkillsRoot: UserSkills);

        Assert.Single(report.ShadowedSkills);
        Assert.True(report.HasProblems);

        var rendered = report.Render();
        Assert.Contains("Skill conflicts", rendered);
        Assert.Contains("dup", rendered);
        // Both paths are named, so you can see which file lost.
        Assert.Contains(UserSkills, rendered);
    }

    [Fact]
    public void an_untrusted_workspace_says_so_and_loads_no_project_skills()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".agents", "skills", "alpha"));
        File.WriteAllText(
            Path.Combine(_root, ".agents", "skills", "alpha", "SKILL.md"),
            Skill("alpha"));

        var report = AgentResourcesReport.Gather(_root, trusted: false, userSkillsRoot: "");

        Assert.False(report.Trusted);
        Assert.DoesNotContain(report.Skills, skill => skill.Scope == SkillScope.Project);
        Assert.Contains("not trusted", report.Render());
    }

    [Fact]
    public void settings_warnings_reach_the_report()
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        File.WriteAllText(settingsPath, "{ broken");

        var store = AgentSettingsStore.Load(_root, trusted: false, userPath: settingsPath);
        var report = AgentResourcesReport.Gather(
            _root, trusted: true, settings: store, userSkillsRoot: "");

        Assert.True(report.HasProblems);
        Assert.Contains("Settings warnings", report.Render());
    }

    [Fact]
    public void a_clean_report_has_no_problems_and_hints_at_ctrl_o()
    {
        var report = Report(trusted: true);

        Assert.False(report.HasProblems);
        Assert.Contains("ctrl+o", report.Render(compact: true));
    }
}
