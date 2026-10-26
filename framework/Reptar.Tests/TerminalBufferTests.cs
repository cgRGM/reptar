#if !NETWASM
using Reptar.AI;
using Xunit;

namespace Reptar.Tests;

public sealed class TerminalBufferTests
{
    [Fact]
    public void Buffer_InitializesWithCorrectDimensions()
    {
        var buffer = new TerminalBuffer(80, 24);
        Assert.Equal(80, buffer.Width);
        Assert.Equal(24, buffer.Height);

        // Clamps minimums
        var tiny = new TerminalBuffer(5, 2);
        Assert.Equal(20, tiny.Width);
        Assert.Equal(8, tiny.Height);
    }

    [Fact]
    public void SetAndGetCell_BoundsAreSafe()
    {
        var buffer = new TerminalBuffer(40, 10);
        buffer.SetCell(5, 5, new TerminalCell('X', Fg: "\x1b[31m", Bold: true));

        var cell = buffer.GetCell(5, 5);
        Assert.Equal('X', cell.Char);
        Assert.Equal("\x1b[31m", cell.Fg);
        Assert.True(cell.Bold);

        // Out of bounds does not throw
        buffer.SetCell(-1, -1, new TerminalCell('Z'));
        buffer.SetCell(100, 100, new TerminalCell('Z'));
        Assert.Equal(' ', buffer.GetCell(-1, -1).Char);
        Assert.Equal(' ', buffer.GetCell(100, 100).Char);
    }

    [Fact]
    public void Print_PlacesCharactersCorrectly()
    {
        var buffer = new TerminalBuffer(40, 10);
        buffer.Print(2, 3, "Hello, Reptar!");

        var expected = "Hello, Reptar!";
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], buffer.GetCell(2 + i, 3).Char);
        }
    }

    [Fact]
    public void PrintCentered_CentersTextAccurately()
    {
        var buffer = new TerminalBuffer(40, 10);
        var text = "Reptar";
        buffer.PrintCentered(2, text);

        var expectedX = (40 - text.Length) / 2; // (40 - 6) / 2 = 17
        for (var i = 0; i < text.Length; i++)
        {
            Assert.Equal(text[i], buffer.GetCell(expectedX + i, 2).Char);
        }
    }

    [Fact]
    public void DrawBox_DrawsRoundedBordersAndTitle()
    {
        var buffer = new TerminalBuffer(40, 10);
        buffer.DrawBox(2, 2, 20, 5, title: "Test", rounded: true);

        // Corners
        Assert.Equal('╭', buffer.GetCell(2, 2).Char);
        Assert.Equal('╮', buffer.GetCell(21, 2).Char);
        Assert.Equal('╰', buffer.GetCell(2, 6).Char);
        Assert.Equal('╯', buffer.GetCell(21, 6).Char);

        // Horizontal and vertical edges
        Assert.Equal('│', buffer.GetCell(2, 3).Char);
        Assert.Equal('│', buffer.GetCell(21, 3).Char);
        Assert.Equal('─', buffer.GetCell(10, 6).Char);

        // Title printed inside top border
        Assert.Equal('T', buffer.GetCell(5, 2).Char);
        Assert.Equal('e', buffer.GetCell(6, 2).Char);
        Assert.Equal('s', buffer.GetCell(7, 2).Char);
        Assert.Equal('t', buffer.GetCell(8, 2).Char);
    }

    [Fact]
    public void DrawShadow_DimsUnderlyingCells()
    {
        var buffer = new TerminalBuffer(40, 10);
        buffer.Print(5, 5, "Background");
        buffer.DrawShadow(5, 5, 10, 1);

        var cell = buffer.GetCell(5, 5);
        Assert.Equal('B', cell.Char);
        Assert.True(cell.Dim);
        Assert.Equal("\x1b[90m", cell.Fg);
    }

    [Fact]
    public void RenderDiff_EmitsMinimalAnsi()
    {
        var front = new TerminalBuffer(30, 8);
        var back = new TerminalBuffer(30, 8);

        // Both empty -> diff should be empty (apart from hide/restore cursor and reset)
        var diffEmpty = back.RenderDiff(front);
        Assert.DoesNotContain("H", diffEmpty); // no cursor movements needed

        // Write a word on line 2
        back.Print(5, 2, "Reptar");
        var diffWord = back.RenderDiff(front);

        Assert.Contains("\x1b[3;6H", diffWord); // 1-based ANSI CUP for row 3, col 6
        Assert.Contains("Reptar", diffWord);
    }

    [Fact]
    public void KnownProviders_HaveAutomaticDefaultsWithoutUserPrompts()
    {
        // Verified: The agent should handle BaseUrl and DefaultModel automatically.
        // User is only prompted for API key on known providers.
        foreach (var p in LlmProviders.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Key));
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(p.DefaultModel));
            Assert.False(string.IsNullOrWhiteSpace(p.DefaultBaseUrl));

            // Only Cloudflare Gateway requires a custom BaseUrl because of {account_id}
            if (p.Key != LlmProviders.CloudflareGateway)
            {
                Assert.False(p.RequiresBaseUrl, $"Provider {p.Key} should not require BaseUrl prompt");
            }
        }
    }

    [Fact]
    public void LlmAuthFile_SavesAndRetrievesProviderCredentials()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"reptar_auth_test_{Guid.NewGuid():N}.json");
        try
        {
            var auth = new LlmAuthFile(tempFile);
            Assert.Null(auth.Get(LlmProviders.Anthropic));

            auth.Set(LlmProviders.Anthropic, new ProviderCredential(
                ApiKey: "sk-ant-test-key",
                BaseUrl: null,
                DefaultModel: "claude-sonnet-4-5"
            ));

            var loaded = new LlmAuthFile(tempFile);
            var cred = loaded.Get(LlmProviders.Anthropic);
            Assert.NotNull(cred);
            Assert.Equal("sk-ant-test-key", cred.ApiKey);
            Assert.Equal("claude-sonnet-4-5", cred.DefaultModel);
            Assert.Equal(LlmProviders.Anthropic, loaded.DefaultProvider);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
#endif
