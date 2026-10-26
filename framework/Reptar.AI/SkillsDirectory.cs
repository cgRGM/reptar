#if !NETWASM
using System.Text.RegularExpressions;

namespace Reptar.AI;

/// <summary>
/// A discovered agent skill from a <c>SKILL.md</c> directory.
/// </summary>
public sealed record AgentSkill(
    string Name,
    string Description,
    string Instructions,
    string SkillFile);

/// <summary>
/// G20 — Loads a repository's agent-skills directory (the
/// <c>.agents/skills/&lt;name&gt;/SKILL.md</c> convention) and exposes the skills
/// to the agent as a module: a catalog in the instructions plus one
/// <c>use_skill</c> tool that loads a skill's full instructions on demand
/// (progressive disclosure — the agent pulls the skill it needs).
///
/// Skills are read-only content in this version: executing skill scripts is
/// deliberately left to a later integration with the G4 approval gate.
/// Skill names are restricted to a safe pattern and every file read is
/// confined to the skills root.
/// </summary>
public sealed class SkillsDirectory
{
    public const string UseSkillToolName = "use_skill";

    // Top-level compiled regex (AOT-safe): skill dirs are lowercase slugs.
    private static readonly Regex SafeSkillNamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly Dictionary<string, AgentSkill> _skills;

    private SkillsDirectory(string root, Dictionary<string, AgentSkill> skills)
    {
        _root = root;
        _skills = skills;
    }

    public IReadOnlyCollection<AgentSkill> Skills => _skills.Values.OrderBy(skill => skill.Name).ToList();

    public AgentSkill? Find(string name) =>
        _skills.TryGetValue(name, out var skill) ? skill : null;

    /// <summary>
    /// Discovers skills under <paramref name="rootDirectory"/>. Each
    /// subdirectory containing a <c>SKILL.md</c> with <c>name:</c> /
    /// <c>description:</c> frontmatter becomes a skill. Returns null when the
    /// directory does not exist.
    /// </summary>
    public static SkillsDirectory? Discover(string rootDirectory, int maxSkills = 64)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        var skills = new Dictionary<string, AgentSkill>(StringComparer.Ordinal);

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory).OrderBy(d => d))
        {
            if (skills.Count >= maxSkills)
            {
                break;
            }

            var skillFile = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            if (TryParseFrontmatter(File.ReadAllText(skillFile), out var name, out var description, out var body) &&
                SafeSkillNamePattern.IsMatch(name))
            {
                skills[name] = new AgentSkill(name, description, body, skillFile);
            }
        }

        return new SkillsDirectory(rootDirectory, skills);
    }

    /// <summary>
    /// The skills module: instructions cataloging every discovered skill plus
    /// the <c>use_skill</c> tool that loads a skill's full instructions.
    /// </summary>
    public AgentModule ToModule() => new()
    {
        Name = "skills",
        Instructions = BuildCatalogInstructions(),
        StateKeys = ["skills"],
        Tools =
        [
            new AiToolDefinition
            {
                Name = UseSkillToolName,
                Description = "Load the full instructions for a skill by name. Call this before performing a task that matches a skill in the catalog.",
                Parameters = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["name"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "The skill name from the catalog",
                            ["enum"] = _skills.Values.OrderBy(skill => skill.Name).Select(skill => skill.Name).ToList()
                        }
                    },
                    ["required"] = new List<string> { "name" }
                },
                Method = "post",
                Path = "/agent/module/use_skill"
            }
        ],
        ExecuteAsync = ExecuteToolAsync,
    };

    private async Task<AiToolResult> ExecuteToolAsync(string name, string argumentsJson)
    {
        await Task.CompletedTask;

        if (name != UseSkillToolName)
        {
            return AiToolResult.Failure(404, "unknown_tool", $"SkillsDirectory has no tool '{name}'.");
        }

        string skillName;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return AiToolResult.Failure(400, "invalid_arguments", "use_skill requires a 'name' string.");
            }

            skillName = nameElement.GetString() ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            return AiToolResult.Failure(400, "invalid_arguments", "use_skill arguments must be valid JSON.");
        }

        // Validate the name against the discovered set BEFORE touching the
        // filesystem: unknown names are rejected structurally, so no path
        // can ever be constructed from raw input.
        var skill = Find(skillName);
        if (skill is null)
        {
            var available = string.Join(", ", _skills.Keys.OrderBy(key => key).Take(20));
            return AiToolResult.Failure(404, "unknown_skill",
                $"No skill named '{skillName}'. Available: {available}");
        }

        // Defense in depth: re-read via the skill's canonical file and verify
        // the path is still inside the skills root.
        var fullPath = Path.GetFullPath(skill.SkillFile);
        if (!fullPath.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(fullPath))
        {
            return AiToolResult.Failure(404, "unknown_skill", $"Skill '{skillName}' is not available.");
        }

        var content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        return AiToolResult.Ok(200, content);
    }

    private string BuildCatalogInstructions()
    {
        if (_skills.Count == 0)
        {
            return "";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Agent Skills");
        builder.AppendLine();
        builder.AppendLine("You have access to the following skills, loaded on demand with the `use_skill` tool:");
        builder.AppendLine();

        foreach (var skill in _skills.Values.OrderBy(candidate => candidate.Name))
        {
            builder.AppendLine($"- **{skill.Name}** — {skill.Description}");
        }

        builder.AppendLine();
        builder.AppendLine("Before performing a task that matches one of these skills, load it with use_skill and follow its instructions.");
        return builder.ToString();
    }

    private static bool TryParseFrontmatter(
        string content, out string name, out string description, out string body)
    {
        name = "";
        description = "";
        body = content;

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var frontmatterEnd = trimmed.IndexOf("\n---", StringComparison.Ordinal);
        if (frontmatterEnd < 0)
        {
            return false;
        }

        var frontmatter = trimmed[..frontmatterEnd]["---".Length..].TrimStart('\n', '\r');
        body = trimmed[(frontmatterEnd + "\n---".Length)..].TrimStart('\n', '\r');

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmedLine = line.TrimEnd('\r').TrimStart();
            if (trimmedLine.StartsWith("name:", StringComparison.Ordinal))
            {
                name = Unquote(trimmedLine["name:".Length..].Trim());
            }
            else if (trimmedLine.StartsWith("description:", StringComparison.Ordinal))
            {
                description = Unquote(trimmedLine["description:".Length..].Trim());
            }
            else if (trimmedLine.StartsWith("metadata:", StringComparison.Ordinal))
            {
                break; // multi-line block we don't parse; description came before it
            }
        }

        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 &&
        ((value.StartsWith('"') && value.EndsWith('"')) ||
         (value.StartsWith('\'') && value.EndsWith('\'')))
            ? value[1..^1]
            : value;
}
#endif
