using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for tool parameter schemas and argument coercion.
///
/// The workspace tools read raw JSON bodies, so without a declared schema the model receives an
/// empty property list and guesses — which is how it sends <c>"wait_ms": "10000"</c> or invents
/// <c>waitMs</c>. These pin both halves: the schema the model sees, and the coercion that forgives
/// a string-typed number.
/// </summary>
public sealed class ToolSchemaTests : IDisposable
{
    private readonly string _root;

    public ToolSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-schema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\nbeta\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ReptarApp App()
    {
        var app = ReptarApp.Create();
        app.UseWorkspaceTools(_root, options => options.AllowRunCommand = true);
        return app;
    }

    private static Dictionary<string, object?> Properties(AiToolDefinition tool) =>
        tool.Parameters.TryGetValue("properties", out var props) &&
        props is Dictionary<string, object?> map
            ? map
            : [];

    // ------------------------------------------------------------------
    // The schema the model sees
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("read_file", "path")]
    [InlineData("list_dir", "depth")]
    [InlineData("write_file", "content")]
    [InlineData("edit_file", "target")]
    [InlineData("run_command", "command")]
    public void every_workspace_tool_declares_its_parameters(string toolName, string expected)
    {
        var tool = App().GetAiTools(onlyExplicit: true).Single(t => t.Name == toolName);

        Assert.Contains(expected, Properties(tool).Keys);
    }

    [Fact]
    public void a_declared_parameter_carries_a_type_and_a_description()
    {
        var tool = App().GetAiTools(onlyExplicit: true).Single(t => t.Name == "run_command");

        var command = Assert.IsType<Dictionary<string, object?>>(Properties(tool)["command"]);
        Assert.Equal("string", command["type"]);
        Assert.False(string.IsNullOrWhiteSpace(command["description"] as string));
    }

    [Fact]
    public void required_fields_are_listed()
    {
        var tool = App().GetAiTools(onlyExplicit: true).Single(t => t.Name == "read_file");

        var required = Assert.IsType<List<object?>>(tool.Parameters["required"]);
        Assert.Contains("path", required);
        Assert.DoesNotContain("start_line", required);
    }

    [Fact]
    public void list_dir_has_no_required_fields()
    {
        var tool = App().GetAiTools(onlyExplicit: true).Single(t => t.Name == "list_dir");

        Assert.False(tool.Parameters.ContainsKey("required"));
    }

    // ------------------------------------------------------------------
    // Coercion
    // ------------------------------------------------------------------

    [Fact]
    public async Task a_string_typed_integer_is_coerced_to_the_declared_type()
    {
        var result = await App().ExecuteAiToolAsync(
            "run_command", """{"command":"echo hi","wait_ms":"9000"}""");

        Assert.True(result.Success, result.ToLlmResponse());
    }

    [Fact]
    public async Task a_string_typed_line_number_is_coerced()
    {
        var result = await App().ExecuteAiToolAsync(
            "read_file", """{"path":"a.txt","start_line":"2"}""");

        Assert.True(result.Success, result.ToLlmResponse());
        Assert.Contains("beta", result.Content);
        Assert.DoesNotContain("alpha", result.Content);
    }

    [Fact]
    public async Task a_string_typed_boolean_is_coerced()
    {
        // edit_file's `all` flag: "true" as a string must behave like true.
        var result = await App().ExecuteAiToolAsync(
            "write_file", """{"path":"b.txt","content":"x x"}""");
        Assert.True(result.Success);

        var edit = await App().ExecuteAiToolAsync(
            "edit_file", """{"path":"b.txt","target":"x","replacement":"y","all":"true"}""");

        Assert.True(edit.Success, edit.ToLlmResponse());
    }

    [Fact]
    public async Task an_off_schema_alias_still_parses_its_numeric_string()
    {
        // The schema declares wait_ms; a model may still send waitMs. The reader is tolerant so
        // an alias degrades to a working call instead of a conversion exception.
        var result = await App().ExecuteAiToolAsync(
            "run_command", """{"command":"echo hi","waitMs":"9000"}""");

        Assert.True(result.Success, result.ToLlmResponse());
    }

    [Fact]
    public async Task a_string_field_is_never_rewritten_even_when_it_looks_numeric()
    {
        // `content` is declared as a string, so "12345" must be written verbatim.
        var write = await App().ExecuteAiToolAsync(
            "write_file", """{"path":"nums.txt","content":"12345"}""");
        Assert.True(write.Success);

        Assert.Equal("12345", File.ReadAllText(Path.Combine(_root, "nums.txt")));
    }
}
