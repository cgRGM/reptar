using Reptar.Term;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the markdown renderer. The property that matters is that inline emphasis
/// becomes a styled run rather than a stripped marker.
/// </summary>
public sealed class MarkdownRendererTests
{
    private static List<MarkdownLine> Render(string markdown, int width = 80) =>
        MarkdownRenderer.Render(markdown, width);

    private static string Text(MarkdownLine line) => line.Text;

    // ------------------------------------------------------------------
    // Inline styling
    // ------------------------------------------------------------------

    [Fact]
    public void strong_text_becomes_a_bold_run_without_its_markers()
    {
        var line = Assert.Single(Render("this is **important** here"));

        Assert.Equal("this is important here", Text(line));
        Assert.Contains(line.Runs, run => run.Bold && run.Text == "important");
        Assert.DoesNotContain("**", Text(line));
    }

    [Fact]
    public void emphasis_becomes_an_italic_run()
    {
        var line = Assert.Single(Render("a *subtle* point"));

        Assert.Contains(line.Runs, run => run.Italic && run.Text == "subtle");
        Assert.DoesNotContain("*", Text(line));
    }

    [Fact]
    public void inline_code_is_styled_and_keeps_its_text()
    {
        var line = Assert.Single(Render("call `read_file` first"));

        Assert.Contains(line.Runs, run => run.Text == "read_file" && run.Fg != "\x1b[37m");
        Assert.DoesNotContain("`", Text(line));
    }

    [Fact]
    public void strike_through_is_dimmed()
    {
        var line = Assert.Single(Render("~~obsolete~~"));

        Assert.Contains(line.Runs, run => run.Dim && run.Text == "obsolete");
    }

    [Fact]
    public void a_markdown_link_keeps_its_label_and_drops_the_url()
    {
        var line = Assert.Single(Render("see [the docs](https://example.test/x) for more"));

        Assert.Contains(line.Runs, run => run.Text == "the docs" && run.Underline);
        Assert.DoesNotContain("https://example.test", Text(line));
    }

    [Fact]
    public void a_lone_asterisk_is_left_alone()
    {
        var line = Assert.Single(Render("2 * 3 = 6"));

        Assert.Equal("2 * 3 = 6", Text(line));
    }

    // ------------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------------

    [Fact]
    public void a_heading_is_bold_and_loses_its_hashes()
    {
        var line = Assert.Single(Render("## Plan"));

        Assert.Equal("Plan", Text(line));
        Assert.All(line.Runs, run => Assert.True(run.Bold));
    }

    [Fact]
    public void a_bullet_becomes_a_glyph_at_a_deeper_indent()
    {
        var line = Assert.Single(Render("- first item"));

        Assert.Equal("• first item", Text(line));
        Assert.Equal(4, line.Indent);
    }

    [Fact]
    public void an_ordered_item_keeps_its_number()
    {
        var line = Assert.Single(Render("3. third"));

        Assert.Equal("3. third", Text(line));
    }

    [Fact]
    public void a_blockquote_gets_a_bar()
    {
        var line = Assert.Single(Render("> quoted"));

        Assert.Equal("│ quoted", Text(line));
    }

    [Fact]
    public void a_rule_becomes_a_horizontal_line()
    {
        var line = Assert.Single(Render("---"));

        Assert.Contains("─", Text(line));
    }

    [Fact]
    public void a_fenced_block_is_marked_and_its_content_kept_verbatim()
    {
        var lines = Render("```csharp\nvar x = 1;\n```");

        Assert.Contains(lines, line => Text(line).Contains("csharp"));
        Assert.Contains(lines, line => Text(line).Contains("var x = 1;"));
    }

    [Fact]
    public void markers_inside_a_fence_are_not_parsed()
    {
        var lines = Render("```\n**not bold**\n```");

        Assert.Contains(lines, line => Text(line).Contains("**not bold**"));
    }

    [Fact]
    public void blank_lines_are_preserved_as_spacing()
    {
        var lines = Render("one\n\ntwo");

        Assert.Equal(3, lines.Count);
        Assert.Empty(lines[1].Runs);
    }

    // ------------------------------------------------------------------
    // Wrapping
    // ------------------------------------------------------------------

    [Fact]
    public void long_paragraphs_wrap_within_the_width()
    {
        var lines = Render(new string('a', 200), width: 40);

        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(Text(line).Length <= 40, Text(line)));
    }

    [Fact]
    public void wrapping_breaks_on_spaces_rather_than_mid_word()
    {
        var lines = Render("alpha beta gamma delta epsilon zeta", width: 16);

        Assert.All(lines, line => Assert.DoesNotContain("  ", Text(line).Trim()));
        Assert.Equal(
            "alpha beta gamma delta epsilon zeta",
            string.Join(" ", lines.Select(line => Text(line).Trim())));
    }

    [Fact]
    public void an_empty_document_renders_nothing()
    {
        Assert.Empty(Render(""));
    }
}
