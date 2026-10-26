using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

public sealed class TerminalEditorTests
{
    [Fact]
    public void DetectMode_switches_on_slash_at_and_palette()
    {
        Assert.Equal(TerminalEditor.DropdownMode.Commands,
            TerminalEditor.DetectMode("/mo", 3, "", paletteActive: false));

        Assert.Equal(TerminalEditor.DropdownMode.Files,
            TerminalEditor.DetectMode("read @not", 9, "", paletteActive: false));

        Assert.Equal(TerminalEditor.DropdownMode.Files,
            TerminalEditor.DetectMode("hi @", 4, "", paletteActive: false));

        Assert.Equal(TerminalEditor.DropdownMode.None,
            TerminalEditor.DetectMode("plain text", 10, "", paletteActive: false));

        Assert.Equal(TerminalEditor.DropdownMode.Palette,
            TerminalEditor.DetectMode("anything", 8, "mo", paletteActive: true));
    }

    [Fact]
    public void Token_prefixes_extract_the_text_after_the_sigil()
    {
        Assert.Equal("mo", TerminalEditor.SlashPrefix("/mo"));
        Assert.Equal("", TerminalEditor.SlashPrefix("/"));

        Assert.Equal("not", TerminalEditor.FilePrefix("read @not", 9));
        Assert.Equal("", TerminalEditor.FilePrefix("read @", 6));
        Assert.Equal("", TerminalEditor.FilePrefix("read notes", 10));

        // The token ends at whitespace: only the last @-token counts.
        Assert.Equal("b", TerminalEditor.FilePrefix("see @a.md and @b", 16));

        // Out-of-range cursors clamp to end-of-string instead of throwing.
        Assert.Equal("b", TerminalEditor.FilePrefix("see @a.md and @b", 99));
    }

    [Fact]
    public void AcceptSuggestion_replaces_the_at_token_and_keeps_the_marker()
    {
        var buffer = new System.Text.StringBuilder("read @not");
        var cursor = buffer.Length;
        var palette = new System.Text.StringBuilder();

        TerminalEditor.AcceptSuggestion(
            new TerminalEditor.Suggestion("notes.md", "notes.md"),
            ref buffer, ref cursor, ref palette,
            TerminalEditor.DropdownMode.Files);

        Assert.Equal("read @notes.md ", buffer.ToString());
        Assert.Equal(buffer.Length, cursor);
    }

    [Fact]
    public void AcceptSuggestion_for_commands_replaces_the_whole_line()
    {
        var buffer = new System.Text.StringBuilder("/mo");
        var cursor = buffer.Length;
        var palette = new System.Text.StringBuilder();

        TerminalEditor.AcceptSuggestion(
            new TerminalEditor.Suggestion("/model", "/model"),
            ref buffer, ref cursor, ref palette,
            TerminalEditor.DropdownMode.Commands);

        Assert.Equal("/model", buffer.ToString());
        Assert.Equal(6, cursor);
    }

    [Fact]
    public void OpenAi_wire_format_turns_user_images_into_multimodal_content()
    {
        var history = new List<ChatMessage>
        {
            new(
                Role: "user",
                Content: "what is this?",
                Images:
                [
                    new AgentImage("image/png", "aGVsbG8=")
                ])
        };

        var messages = OpenAiLlmClient.BuildMessages(history);

        var content = messages[0]["content"];
        Assert.NotNull(content);
        var array = Assert.IsType<System.Text.Json.Nodes.JsonArray>(content);
        Assert.Equal(2, array.Count);

        Assert.Equal("text", array[0]!["type"]!.GetValue<string>());
        Assert.Equal("what is this?", array[0]!["text"]!.GetValue<string>());

        Assert.Equal("image_url", array[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,aGVsbG8=",
            array[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_wire_format_keeps_plain_text_when_no_images()
    {
        var messages = OpenAiLlmClient.BuildMessages([new("user", "hello")]);

        Assert.Equal("hello", messages[0]["content"]!.GetValue<string>());
    }

    [Fact]
    public void SniffImageMime_recognizes_png_jpeg_gif_webp_and_rejects_text()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        Assert.Equal("image/png", TerminalEditor.SniffImageMime(png));

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };
        Assert.Equal("image/jpeg", TerminalEditor.SniffImageMime(jpeg));

        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x00 };
        Assert.Equal("image/gif", TerminalEditor.SniffImageMime(gif));

        var webp = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P' };
        Assert.Equal("image/webp", TerminalEditor.SniffImageMime(webp));

        Assert.Null(TerminalEditor.SniffImageMime("plain text bytes"u8.ToArray()));
        Assert.Null(TerminalEditor.SniffImageMime([]));
    }

    [Fact]
    public void History_persists_across_instances_and_hides_leading_space_entries()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"reptar-history-{Guid.NewGuid():N}", "history");
        try
        {
            var first = new TerminalEditor { HistoryPath = historyPath };
            first.AddHistory("bun reptar agent");
            first.AddHistory("  secret thing");      // leading space: never persisted
            first.AddHistory("bun reptar agent");   // duplicate of last: ignored
            first.AddHistory("bun reptar test");

            var second = new TerminalEditor { HistoryPath = historyPath };
            second.LoadHistory();

            Assert.Equal(["bun reptar agent", "bun reptar test"], second.History);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(historyPath)!, true); }
            catch { }
        }
    }

    [Fact]
    public void History_trims_to_the_cap_on_load()
    {
        var historyPath = Path.Combine(Path.GetTempPath(), $"reptar-history-{Guid.NewGuid():N}", "history");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var lines = Enumerable.Range(1, 600).Select(i => $"cmd-{i}");
            File.WriteAllLines(historyPath, lines);

            var editor = new TerminalEditor { HistoryPath = historyPath };
            editor.LoadHistory();

            Assert.Equal(500, editor.History.Count);
            Assert.Equal("cmd-101", editor.History[0]);
            Assert.Equal("cmd-600", editor.History[^1]);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(historyPath)!, true); }
            catch { }
        }
    }
}
