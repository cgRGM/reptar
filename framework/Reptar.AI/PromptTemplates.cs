#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>A reusable prompt loaded from a markdown file.</summary>
/// <param name="Name">Command name, derived from the file name.</param>
/// <param name="Description">From frontmatter, or the first line of the body.</param>
/// <param name="ArgumentHint">From frontmatter <c>argument-hint</c>, for completion UIs.</param>
/// <param name="Content">The template body with frontmatter stripped.</param>
/// <param name="FilePath">Absolute path, for diagnostics.</param>
public sealed record PromptTemplate(
    string Name,
    string Description,
    string? ArgumentHint,
    string Content,
    string FilePath);

/// <summary>
/// Discovers and expands prompt templates: <c>/fix-tests src/app.cs</c> becomes the template
/// body with <c>$1</c>, <c>$@</c>, and <c>$ARGUMENTS</c> substituted.
/// </summary>
/// <remarks>
/// Ported from pi's <c>core/prompt-templates.ts</c>. Templates are the cheapest way to turn a
/// prompt you retype into a command: one markdown file, no code.
///
/// Lookup order, first match wins per name: <c>.reptar/prompts/</c> in the project, then
/// <c>~/.reptar/prompts/</c>. A project template shadows a user template of the same name, which
/// is what you want when a repo has its own conventions.
/// </remarks>
public static class PromptTemplates
{
    /// <summary>Relative directory, for docs and error messages.</summary>
    public const string RelativeDirectory = ".reptar/prompts";

    /// <summary>Loads every template visible from <paramref name="workspaceRoot"/>.</summary>
    public static IReadOnlyList<PromptTemplate> Load(string workspaceRoot)
    {
        var byName = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);

        // User templates first, so a project template of the same name replaces them.
        foreach (var directory in new[] { UserDirectory(workspaceRoot), ProjectDirectory(workspaceRoot) })
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories))
            {
                var template = TryLoad(file);
                if (template is not null)
                    byName[template.Name] = template;
            }
        }

        return [.. byName.Values.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Path of the project-local template directory.</summary>
    public static string ProjectDirectory(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), ".reptar", "prompts");


    /// <summary>Path of the user-level template directory.</summary>
    public static string UserDirectory(string workspaceRoot) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".reptar", "prompts");

    /// <summary>
    /// Parses one template file. Returns null when the file cannot be read.
    /// </summary>
    public static PromptTemplate? TryLoad(string filePath)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (frontmatter, body) = SplitFrontmatter(raw);
        var name = Path.GetFileNameWithoutExtension(filePath);

        var description = frontmatter.GetValueOrDefault("description") ?? "";
        if (description.Length == 0)
        {
            // Fall back to the first non-empty line, which is usually a decent summary.
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim().TrimStart('#').Trim();
                if (trimmed.Length == 0)
                    continue;

                description = trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
                break;
            }
        }

        var hint = frontmatter.GetValueOrDefault("argument-hint");

        return new PromptTemplate(name, description, hint, body.Trim(), filePath);
    }

    /// <summary>
    /// Expands a template with arguments. Supports <c>$1</c>..<c>$9</c> for positional
    /// arguments, <c>$@</c> and <c>$ARGUMENTS</c> for the whole argument string.
    /// </summary>
    /// <remarks>
    /// Substitution is single-pass: a value that itself contains <c>$1</c> is not expanded again,
    /// which stops a prompt from being able to inject further template syntax.
    /// </remarks>
    public static string Expand(PromptTemplate template, string arguments)
    {
        var positional = ParseArguments(arguments);
        var content = template.Content;
        var builder = new StringBuilder(content.Length + arguments.Length);

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '$' || i + 1 >= content.Length)
            {
                builder.Append(content[i]);
                continue;
            }

            var next = content[i + 1];

            // $ARGUMENTS
            if (content.AsSpan(i + 1).StartsWith("ARGUMENTS", StringComparison.Ordinal))
            {
                builder.Append(arguments);
                i += "ARGUMENTS".Length;
                continue;
            }

            if (next == '@')
            {
                builder.Append(arguments);
                i += 1;
                continue;
            }

            if (char.IsAsciiDigit(next))
            {
                var position = next - '0';
                builder.Append(position == 0
                    ? template.Name
                    : position <= positional.Count ? positional[position - 1] : "");
                i += 1;
                continue;
            }

            builder.Append('$');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits an argument string on whitespace, honouring single and double quotes. A quoted
    /// empty string counts as an argument, so <c>/t ""</c> passes one empty value.
    /// </summary>
    public static List<string> ParseArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        char? quote = null;

        foreach (var ch in arguments)
        {
            if (quote is { } open)
            {
                if (ch == open)
                    quote = null;
                else
                    current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(ch);
            hasToken = true;
        }

        if (hasToken)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>
    /// Resolves a slash command against loaded templates. Returns null when the input is not a
    /// template invocation or no template matches.
    /// </summary>
    public static string? TryExpand(
        IReadOnlyList<PromptTemplate> templates,
        string input)
    {
        if (!input.StartsWith('/'))
            return null;

        var body = input[1..];
        var space = body.IndexOf(' ');
        var name = space < 0 ? body : body[..space];
        var arguments = space < 0 ? "" : body[(space + 1)..].Trim();

        var template = templates.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        return template is null ? null : Expand(template, arguments);
    }

    /// <summary>Renders the template list for a help screen or slash-command dropdown.</summary>
    public static string Describe(IReadOnlyList<PromptTemplate> templates)
    {
        if (templates.Count == 0)
            return "No prompt templates found. Add markdown files under .reptar/prompts/.";

        var builder = new StringBuilder();
        builder.AppendLine($"{templates.Count} prompt template(s):");

        foreach (var template in templates)
        {
            builder.Append("  /").Append(template.Name);
            if (template.ArgumentHint is { Length: > 0 } hint)
                builder.Append(' ').Append(hint);
            builder.Append("  — ").AppendLine(template.Description);
        }

        return builder.ToString().TrimEnd();
    }

    private static string At(IReadOnlyList<string> values, int index) =>
        index < values.Count ? values[index] : "";

    /// <summary>
    /// Splits optional <c>---</c> frontmatter from the body. Deliberately tiny: only
    /// <c>key: value</c> lines, which is all a prompt template needs.
    /// </summary>
    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string raw)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var text = raw.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (frontmatter, text);

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (frontmatter, text);

        var header = text[4..end];
        foreach (var line in header.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');

            if (key.Length > 0)
                frontmatter[key] = value;
        }

        var bodyStart = end + 4;
        if (bodyStart < text.Length && text[bodyStart] == '\n')
            bodyStart++;

        return (frontmatter, text[bodyStart..]);
    }
}
#endif
