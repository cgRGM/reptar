using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for <see cref="ToolOutput"/>: the two independent limits, the
/// no-partial-lines guarantee, and the spill-to-file path.
/// </summary>
public sealed class ToolOutputTests
{
    [Fact]
    public void head_returns_small_content_unchanged()
    {
        var result = ToolOutput.Head("one\ntwo\nthree");

        Assert.False(result.Truncated);
        Assert.Equal(TruncationLimit.None, result.Limit);
        Assert.Equal("one\ntwo\nthree", result.Content);
        Assert.Equal(3, result.TotalLines);
    }

    [Fact]
    public void head_stops_at_the_line_limit()
    {
        var content = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var result = ToolOutput.Head(content, maxLines: 10);

        Assert.True(result.Truncated);
        Assert.Equal(TruncationLimit.Lines, result.Limit);
        Assert.Equal(10, result.OutputLines);
        Assert.Equal(100, result.TotalLines);
        Assert.StartsWith("line 1\n", result.Content);
        Assert.EndsWith("line 10", result.Content);
        Assert.DoesNotContain("line 11", result.Content);
    }

    [Fact]
    public void head_stops_at_the_byte_limit_without_cutting_a_line()
    {
        var content = string.Join('\n', Enumerable.Range(1, 50).Select(i => new string('x', 100)));

        var result = ToolOutput.Head(content, maxLines: 1_000, maxBytes: 250);

        Assert.True(result.Truncated);
        Assert.Equal(TruncationLimit.Bytes, result.Limit);
        // Whole lines only: 250 bytes fits two 101-byte lines.
        Assert.Equal(2, result.OutputLines);
        Assert.Equal(2, result.Content.Split('\n').Length);
        Assert.DoesNotContain('…', result.Content);
    }

    [Fact]
    public void tail_keeps_the_end_which_is_what_a_failed_build_needs()
    {
        var content = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var result = ToolOutput.Tail(content, maxLines: 5);

        Assert.True(result.Truncated);
        Assert.Equal(5, result.OutputLines);
        Assert.EndsWith("line 100", result.Content);
        Assert.StartsWith("line 96", result.Content);
        Assert.DoesNotContain("line 1\n", result.Content);
    }

    [Fact]
    public void trailing_newline_does_not_count_as_a_line()
    {
        var result = ToolOutput.Head("a\nb\n");
        Assert.Equal(2, result.TotalLines);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void carriage_returns_are_stripped_so_windows_output_stays_readable()
    {
        var result = ToolOutput.Head("a\r\nb\r\nc");
        Assert.Equal("a\nb\nc", result.Content.Replace("\r", ""));
        Assert.DoesNotContain('\r', result.Content);
    }

    [Fact]
    public void empty_content_is_a_no_op()
    {
        Assert.False(ToolOutput.Head("").Truncated);
        Assert.False(ToolOutput.Tail(null).Truncated);
        Assert.Equal("", ToolOutput.Head("").Content);
    }

    [Fact]
    public void describe_truncation_names_the_limit_and_the_spill_path()
    {
        var result = ToolOutput.Tail(string.Join('\n', Enumerable.Range(1, 50)), maxLines: 5);

        var withoutPath = ToolOutput.DescribeTruncation(result, null);
        Assert.Contains("output truncated", withoutPath);
        Assert.Contains("5 of 50 lines", withoutPath);

        var withPath = ToolOutput.DescribeTruncation(result, "/tmp/full.log");
        Assert.Contains("/tmp/full.log", withPath);

        Assert.Equal("", ToolOutput.DescribeTruncation(TruncationResult.Empty, null));
    }

    [Fact]
    public void spill_writes_the_full_output_and_returns_a_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reptar-tooloutput", Guid.NewGuid().ToString("N"));

        try
        {
            var full = string.Join('\n', Enumerable.Range(1, 500));
            var path = ToolOutput.SpillToFile(full, directory, "run_command");

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(full, File.ReadAllText(path!));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void format_size_is_human_readable()
    {
        Assert.Equal("512B", ToolOutput.FormatSize(512));
        Assert.Equal("1.0KB", ToolOutput.FormatSize(1024));
        Assert.Equal("2.0MB", ToolOutput.FormatSize(2 * 1024 * 1024));
    }
}
