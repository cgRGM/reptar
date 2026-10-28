using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Path handling for tool arguments. Models pass <c>""</c> and <c>"."</c> to mean "here"
/// constantly, so the root has to resolve rather than error.
/// </summary>
public sealed class WorkspacePathTests : IDisposable
{
    private readonly string _root;

    public WorkspacePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ReptarApp App()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root);
        return app;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public async Task list_dir_treats_an_empty_or_dot_path_as_the_workspace_root(string path)
    {
        var result = await App().ExecuteAiToolAsync("list_dir", $$"""{"path":"{{path}}"}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("a.txt", result.Content);
    }

    [Fact]
    public async Task list_dir_still_rejects_a_traversal()
    {
        var result = await App().ExecuteAiToolAsync("list_dir", """{"path":"../../etc"}""");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task read_file_treats_an_empty_path_as_an_error_not_the_root()
    {
        // Reading the root directory as a file cannot work, and saying so is clearer than a
        // confusing IO error.
        var result = await App().ExecuteAiToolAsync("read_file", """{"path":""}""");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task a_numeric_string_argument_is_accepted_for_an_int_field()
    {
        // Models routinely send numbers as strings.
        var result = await App().ExecuteAiToolAsync("list_dir", """{"path":".","depth":"2"}""");

        Assert.True(result.Success, result.ToLlmResponse());
    }
}
