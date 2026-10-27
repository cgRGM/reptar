using Reptar.Term;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the terminal capability layer: synchronized frames, hyperlinks, mouse
/// decoding, and image protocol selection.
/// </summary>
public sealed class TerminalCapabilityTests
{
    // ------------------------------------------------------------------
    // Synchronized output
    // ------------------------------------------------------------------

    [Fact]
    public void a_frame_is_wrapped_in_a_synchronized_block_when_colour_is_on()
    {
        var wasEnabled = Ansi.Enabled;
        try
        {
            Ansi.ForceEnable(true);
            var framed = Ansi.Frame("body");

            Assert.StartsWith(Ansi.SyncStart, framed);
            Assert.EndsWith(Ansi.SyncEnd, framed);
            Assert.Contains("body", framed);
        }
        finally
        {
            Ansi.ForceEnable(wasEnabled);
        }
    }

    // ------------------------------------------------------------------
    // Hyperlinks
    // ------------------------------------------------------------------

    [Fact]
    public void a_link_wraps_the_label_in_an_osc8_sequence()
    {
        var wasEnabled = Ansi.Enabled;
        try
        {
            Ansi.ForceEnable(true);
            var link = Ansi.Link("example", "https://example.test/path");

            Assert.Contains("\x1b]8;;https://example.test/path", link);
            Assert.Contains("example", link);
            Assert.Equal(2, link.Split("\x1b]8;;").Length - 1);
        }
        finally
        {
            Ansi.ForceEnable(wasEnabled);
        }
    }

    [Fact]
    public void a_control_character_in_a_url_cannot_terminate_the_link_early()
    {
        var wasEnabled = Ansi.Enabled;
        try
        {
            Ansi.ForceEnable(true);

            // A raw ESC in the URL would end the OSC sequence and let the rest act as styling.
            var link = Ansi.Link("label", "https://x.test/\u001b[31mred");

            // Exactly four ESCs: open, the two ST terminators, close. A fifth would mean the
            // URL's own ESC survived and the payload can act as an escape sequence.
            Assert.Equal(4, link.Count(ch => ch == '\u001b'));
            Assert.Contains("%1B", link);
        }
        finally
        {
            Ansi.ForceEnable(wasEnabled);
        }
    }

    [Fact]
    public void a_link_degrades_to_plain_text_without_colour()
    {
        var wasEnabled = Ansi.Enabled;
        try
        {
            Ansi.ForceEnable(false);
            Assert.Equal("example", Ansi.Link("example", "https://example.test"));
        }
        finally
        {
            Ansi.ForceEnable(wasEnabled);
        }
    }

    // ------------------------------------------------------------------
    // Images
    // ------------------------------------------------------------------

    [Fact]
    public void kitty_images_are_chunked_and_only_the_first_carries_geometry()
    {
        var data = new string('A', 9000);

        var sequence = TerminalImage.Render(data, ImageProtocol.Kitty, columns: 40);

        var chunks = sequence.Split("\x1b_G", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, chunks.Length);                 // 4096 + 4096 + 808
        Assert.Contains("a=T", chunks[0]);
        Assert.Contains("c=40", chunks[0]);
        Assert.Contains("m=1", chunks[0]);
        Assert.DoesNotContain("a=T", chunks[1]);
        Assert.Contains("m=0", chunks[^1]);
    }

    [Fact]
    public void iterm2_images_carry_the_width()
    {
        var sequence = TerminalImage.Render("QUJD", ImageProtocol.Iterm2, columns: 30, rows: 10);

        Assert.StartsWith("\x1b]1337;File=", sequence);
        Assert.Contains("width=30", sequence);
        Assert.Contains("height=10", sequence);
        Assert.EndsWith("\a", sequence);
    }

    [Fact]
    public void an_unsupported_terminal_renders_nothing_rather_than_mojibake()
    {
        Assert.Equal("", TerminalImage.Render("QUJD", ImageProtocol.None, columns: 20));
        Assert.Equal("", TerminalImage.Render("", ImageProtocol.Kitty, columns: 20));
    }

    [Fact]
    public void the_placeholder_names_the_image_and_its_size()
    {
        Assert.Contains("chart.png", TerminalImage.Placeholder("chart.png", 2048));
        Assert.Contains("2 KB", TerminalImage.Placeholder("chart.png", 2048));
        Assert.Contains("image", TerminalImage.Placeholder(null, 10));
    }

    [Fact]
    public void protocol_detection_defaults_to_none()
    {
        // Whatever this machine reports, the result must be a defined protocol, never a throw.
        var detected = TerminalImage.Detect();
        Assert.True(Enum.IsDefined(detected));
    }

    // ------------------------------------------------------------------
    // Mouse
    // ------------------------------------------------------------------

    [Fact]
    public void the_wheel_moves_the_selection_without_a_list_top()
    {
        var selector = new TermSelector("t",
        [
            new TermSelectorItem("a"), new TermSelectorItem("b"),
            new TermSelectorItem("c"), new TermSelectorItem("d")
        ]);

        Assert.True(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.WheelDown, 0, 0)));
        Assert.Equal(3, selector.Index);

        Assert.True(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.WheelUp, 0, 0)));
        Assert.Equal(0, selector.Index);
    }

    [Fact]
    public void a_click_selects_the_row_under_the_pointer()
    {
        var selector = new TermSelector("t",
        [
            new TermSelectorItem("a"), new TermSelectorItem("b"), new TermSelectorItem("c")
        ]);

        // List starts at screen row 10; clicking row 12 is the third item.
        Assert.True(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.Press, 5, 12), listTop: 10));
        Assert.Equal(2, selector.Index);
    }

    [Fact]
    public void a_click_outside_the_list_is_ignored()
    {
        var selector = new TermSelector("t", [new TermSelectorItem("a")]);

        Assert.False(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.Press, 5, 3), listTop: 10));
        Assert.Equal(0, selector.Index);
    }

    [Fact]
    public void a_click_without_a_list_top_is_ignored()
    {
        var selector = new TermSelector("t", [new TermSelectorItem("a"), new TermSelectorItem("b")]);

        Assert.False(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.Press, 5, 12)));
    }

    [Fact]
    public void a_release_is_never_consumed()
    {
        var selector = new TermSelector("t", [new TermSelectorItem("a")]);

        Assert.False(selector.ApplyMouse(new TermMouseEvent(TermMouseKind.Release, 5, 12), listTop: 10));
    }
}
