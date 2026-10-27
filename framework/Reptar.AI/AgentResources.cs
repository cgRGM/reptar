#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>One context file the agent loaded, such as <c>AGENTS.md</c>.</summary>
public sealed record ContextFileInfo(string Path, int Bytes);

/// <summary>
/// Everything the agent loaded before the first turn, and everything it rejected.
/// </summary>
/// <remarks>
/// Ported from the panel pi shows on startup (<c>ctrl+o</c> to expand). The reason it exists is
/// the rejection lists: a shadowed skill, an unparseable <c>SKILL.md</c>, or a project settings
/// file ignored because the directory is untrusted are all silent otherwise. An agent that
/// behaves unexpectedly with no explanation is much harder to debug than one that prints what it
/// loaded.
/// </remarks>
public sealed record AgentResourcesReport
{
    /// <summary>Context files that were found and included.</summary>
    public IReadOnlyList<ContextFileInfo> ContextFiles { get; init; } = [];

    /// <summary>Skills that will be available, with their scope.</summary>
    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    /// <summary>Skills that lost to a same-named skill elsewhere.</summary>
    public IReadOnlyList<SkillDiagnostic> ShadowedSkills { get; init; } = [];

    /// <summary>Skill files that could not be used.</summary>
    public IReadOnlyList<SkillDiagnostic> InvalidSkills { get; init; } = [];

    /// <summary>Prompt templates found, by name.</summary>
    public IReadOnlyList<PromptTemplate> PromptTemplates { get; init; } = [];

    /// <summary>MCP servers that connected.</summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>MCP servers that failed, with the reason.</summary>
    public IReadOnlyList<SkillDiagnostic> McpFailures { get; init; } = [];

    /// <summary>Settings warnings, such as an unparseable file.</summary>
    public IReadOnlyList<string> SettingsWarnings { get; init; } = [];

    /// <summary>True when the workspace was trusted, so project-local resources loaded.</summary>
    public bool Trusted { get; init; }

    /// <summary>True when anything was rejected or failed. Drives the warning colour.</summary>
    public bool HasProblems =>
        ShadowedSkills.Count > 0
        || InvalidSkills.Count > 0
        || McpFailures.Count > 0
        || SettingsWarnings.Count > 0;

    /// <summary>Names of the context files, for a one-line summary.</summary>
    public IEnumerable<string> ContextFileNames =>
        ContextFiles.Select(file => Path.GetFileName(file.Path));

    /// <summary>
    /// Gathers the report. Every step is best-effort: a failure to read one resource must not
    /// stop the agent, it just becomes a line in the report.
    /// </summary>
    /// <param name="userSkillsRoot">
    /// User skills root. Defaults to <c>~/.agents/skills</c>; pass a path (or an empty string) to
    /// make the report hermetic, which matters for tests and for sandboxes.
    /// </param>
    public static AgentResourcesReport Gather(
        string workspaceRoot,
        bool trusted,
        AgentSettingsStore? settings = null,
        IReadOnlyList<PromptTemplate>? templates = null,
        IReadOnlyList<string>? mcpServers = null,
        IReadOnlyList<SkillDiagnostic>? mcpFailures = null,
        string? userSkillsRoot = null)
    {
        var contextFiles = new List<ContextFileInfo>();

        // The convention pi uses, plus the file Reptar's own repo already carries.
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md", ".cursorrules" })
        {
            var path = Path.Combine(workspaceRoot, name);
            if (!File.Exists(path))
                continue;

            try
            {
                contextFiles.Add(new ContextFileInfo(path, (int)new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable context file: skip it rather than failing the start.
            }
        }

        var projectSkillsRoot = Path.Combine(workspaceRoot, ".agents", "skills");
        var userRoot = userSkillsRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");

        // Project resources only load from a trusted workspace, which is the whole point of the
        // trust decision — so an untrusted workspace reports no project skills.
        var discovery = SkillsDirectory.DiscoverAll(
            trusted ? projectSkillsRoot : Path.Combine(workspaceRoot, ".agents", "not-trusted"),
            userRoot.Length > 0 ? userRoot : null);

        return new AgentResourcesReport
        {
            ContextFiles = contextFiles,
            Skills = discovery.Skills,
            ShadowedSkills = discovery.Shadowed,
            InvalidSkills = discovery.Invalid,
            PromptTemplates = templates ?? [],
            McpServers = mcpServers ?? [],
            McpFailures = mcpFailures ?? [],
            SettingsWarnings = settings?.Warnings ?? [],
            Trusted = trusted
        };
    }

    /// <summary>
    /// Renders the report as the panel pi shows, in the same section order.
    /// </summary>
    /// <param name="compact">
    /// True for the collapsed startup view: counts only. False for the full <c>ctrl+o</c> view
    /// with paths and conflict detail.
    /// </param>
    public string Render(bool compact = false)
    {
        var builder = new StringBuilder();

        builder.AppendLine(compact ? "**Resources**" : "**Resources loaded**");
        builder.AppendLine();

        if (!Trusted)
        {
            builder.AppendLine(
                "> This workspace is **not trusted**, so project-local skills, templates, and "
                + "settings were not loaded. Run `/trust` to review.");
            builder.AppendLine();
        }

        // Context
        if (ContextFiles.Count > 0)
        {
            builder.AppendLine("`[Context]`");
            foreach (var file in ContextFiles)
                builder.AppendLine(compact
                    ? $"  {Path.GetFileName(file.Path)}"
                    : $"  {file.Path}  ({file.Bytes:N0} bytes)");
            builder.AppendLine();
        }

        // Skills
        if (Skills.Count > 0)
        {
            builder.Append("`[Skills]` ").Append(Skills.Count).AppendLine();

            if (!compact)
            {
                foreach (var skill in Skills)
                    builder.AppendLine($"  {skill.Name}  ({skill.Scope.ToString().ToLowerInvariant()})");
            }
            else
            {
                builder.AppendLine("  " + string.Join(", ", Skills.Take(40).Select(skill => skill.Name)));
            }

            builder.AppendLine();
        }

        // Templates
        if (PromptTemplates.Count > 0)
        {
            builder.Append("`[Templates]` ").AppendLine(
                string.Join(", ", PromptTemplates.Select(template => "/" + template.Name)));
            builder.AppendLine();
        }

        // MCP
        if (McpServers.Count > 0)
        {
            builder.Append("`[MCP]` ").AppendLine(string.Join(", ", McpServers));
            builder.AppendLine();
        }

        // Problems, in pi's order: conflicts then invalid.
        if (ShadowedSkills.Count > 0)
        {
            builder.AppendLine("`[Skill conflicts]`");
            foreach (var group in ShadowedSkills.GroupBy(entry => entry.Name, StringComparer.Ordinal))
            {
                builder.Append("  \"").Append(group.Key).AppendLine("\" collision:");
                foreach (var entry in group)
                    builder.AppendLine($"    {entry.SkillFile}  ({entry.Reason})");
            }
            builder.AppendLine();
        }

        if (InvalidSkills.Count > 0)
        {
            builder.AppendLine("`[Skill errors]`");
            foreach (var entry in InvalidSkills)
                builder.AppendLine($"  {entry.SkillFile}");
            foreach (var entry in InvalidSkills)
                builder.AppendLine($"  {entry.Reason}");
            builder.AppendLine();
        }

        if (McpFailures.Count > 0)
        {
            builder.AppendLine("`[MCP failures]`");
            foreach (var entry in McpFailures)
                builder.AppendLine($"  {entry.Name}: {entry.Reason}");
            builder.AppendLine();
        }

        if (SettingsWarnings.Count > 0)
        {
            builder.AppendLine("`[Settings warnings]`");
            foreach (var warning in SettingsWarnings)
                builder.AppendLine($"  {warning}");
            builder.AppendLine();
        }

        if (compact && !HasProblems)
        {
            builder.AppendLine(Trusted
                ? "Press `ctrl+o` for full startup detail."
                : "Press `ctrl+o` for full startup detail.");
        }

        return builder.ToString().TrimEnd();
    }
}
#endif
