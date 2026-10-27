using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the bottom-of-screen geometry. Every band must have its own rows and
/// nothing may be drawn over anything else — the bugs this pins were all overlaps.
/// </summary>
public sealed class TermLayoutTests
{
    [Fact]
    public void the_footer_owns_the_last_two_rows()
    {
        var layout = new TermLayout(40);

        Assert.Equal(39, layout.FooterStats);
        Assert.Equal(38, layout.FooterLocation);
    }

    [Fact]
    public void with_nothing_open_the_composer_sits_directly_above_the_footer()
    {
        var layout = new TermLayout(40);

        Assert.Equal(37, layout.Composer);
        Assert.Equal(36, layout.Working);
        Assert.Equal(35, layout.TranscriptBottom);
        Assert.False(layout.SelectorOpen);
    }

    [Fact]
    public void a_dropdown_pushes_the_composer_up_by_its_own_height()
    {
        var layout = new TermLayout(40, DropdownRows: 10);

        Assert.Equal(27, layout.Composer);
        Assert.Equal(28, layout.DropdownTop);

        // The band ends one row above the footer's location line.
        var bandBottom = layout.DropdownTop + 10 - 1;
        Assert.Equal(layout.FooterLocation - 1, bandBottom);
    }

    [Fact]
    public void the_dropdown_never_touches_the_footer()
    {
        foreach (var height in new[] { 12, 20, 40, 60, 120 })
        {
            foreach (var rows in new[] { 0, 1, 5, 10 })
            {
                var layout = new TermLayout(height, DropdownRows: rows);

                if (rows == 0)
                    continue;

                // A band taller than the screen cannot be placed; the layout reports that with
                // Fits rather than pretending, and the caller shrinks it.
                if (!layout.Fits)
                    continue;

                Assert.True(layout.DropdownTop > 0);
                Assert.True(layout.DropdownTop + rows <= layout.FooterLocation,
                    $"dropdown overlaps the footer at height {height}, {rows} rows");
            }
        }
    }

    [Fact]
    public void the_composer_never_overlaps_the_dropdown()
    {
        var layout = new TermLayout(40, DropdownRows: 10);

        Assert.True(layout.Composer < layout.DropdownTop);
    }

    [Fact]
    public void a_selector_takes_the_composer_slot_and_the_transcript_stops_above_it()
    {
        var layout = new TermLayout(40, SelectorRows: 12);

        Assert.True(layout.SelectorOpen);

        // No composer is drawn while a selector is open.
        Assert.Equal(-1, layout.Composer);
        Assert.Equal(-1, layout.Working);

        // The selector ends one row above the footer and the transcript ends above the selector.
        Assert.Equal(layout.FooterLocation - 1, layout.SelectorBottom);
        Assert.Equal(layout.SelectorTop - 1, layout.TranscriptBottom);
        Assert.True(layout.TranscriptBottom < layout.SelectorTop);
    }

    [Fact]
    public void a_selector_never_overlaps_the_footer()
    {
        foreach (var height in new[] { 20, 40, 80 })
        {
            foreach (var rows in new[] { 3, 10, 20 })
            {
                var layout = new TermLayout(height, SelectorRows: rows);

                Assert.True(layout.SelectorBottom < layout.FooterLocation,
                    $"selector overlaps the footer at height {height}, {rows} rows");
            }
        }
    }

    [Fact]
    public void a_selector_never_reaches_above_the_header()
    {
        // A selector taller than the screen must clamp, not draw over the header.
        var layout = new TermLayout(20, SelectorRows: 40);

        Assert.True(layout.SelectorTop >= 2);
        Assert.False(layout.Fits);
    }

    [Fact]
    public void a_dropdown_taller_than_the_screen_reports_that_it_does_not_fit()
    {
        var layout = new TermLayout(12, DropdownRows: 10);

        Assert.False(layout.Fits);
    }

    [Fact]
    public void a_normal_frame_fits()
    {
        Assert.True(new TermLayout(40).Fits);
        Assert.True(new TermLayout(40, DropdownRows: 10).Fits);
        Assert.True(new TermLayout(40, SelectorRows: 20).Fits);
    }

    [Fact]
    public void negative_inputs_are_clamped()
    {
        var layout = new TermLayout(40, DropdownRows: -5, SelectorRows: -2);

        Assert.Equal(0, layout.DropdownRows);
        Assert.Equal(0, layout.SelectorRows);
        Assert.False(layout.SelectorOpen);
    }

    [Fact]
    public void every_band_is_distinct_on_a_normal_screen()
    {
        var layout = new TermLayout(40, DropdownRows: 6);

        var rows = new List<int> { layout.FooterStats, layout.FooterLocation };
        rows.AddRange(Enumerable.Range(layout.DropdownTop, layout.DropdownRows));
        rows.Add(layout.Composer);
        rows.Add(layout.Working);

        Assert.Equal(rows.Count, rows.Distinct().Count());
        Assert.All(rows, row => Assert.True(row >= 1 && row < 40));
    }
}
