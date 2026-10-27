using Reptar;
using Reptar.AI;

namespace Reptar.Tests;

/// <summary>
/// Contract tests for the MCP client. A shell script plays the server so the handshake, the
/// tool list, the call path, error mapping, and the namespacing are exercised without a network
/// dependency or a Node toolchain.
/// </summary>
public sealed class McpTests : IDisposable
{
    private readonly string _root;
    private readonly string _serverScript;

    /// <summary>
    /// A minimal MCP server: JSON-RPC over stdio, newline-delimited. It answers
    /// <c>initialize</c>, <c>tools/list</c>, and <c>tools/call</c>, and ignores notifications.
    /// </summary>
    private const string ServerScript = """
        #!/bin/sh
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
          case "$line" in
            *'"initialize"'*)
              printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"fake-server","version":"1.0"}}}\n' "$id"
              ;;
            *'"tools/list"'*)
              printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"echo","description":"Echo text back.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}},{"name":"fail","description":"Always fails.","inputSchema":{"type":"object","properties":{}}},{"name":"slow-tool","description":"Name with a dash.","inputSchema":{"type":"object","properties":{}}}]}}\n' "$id"
              ;;
            *'"tools/call"'*)
              case "$line" in
                *'"fail"'*)
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"tool blew up"}],"isError":true}}\n' "$id"
                  ;;
                *)
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"echoed ok"}]}}\n' "$id"
                  ;;
              esac
              ;;
            *)
              ;;
          esac
        done
        """;

    public McpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reptar-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _serverScript = Path.Combine(_root, "fake-mcp-server.sh");
        File.WriteAllText(_serverScript, ServerScript);
        File.SetUnixFileMode(
            _serverScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private McpServerConfig FakeServer(string name = "fake") => new()
    {
        Name = name,
        Command = "/bin/sh",
        Arguments = [_serverScript],
        WorkingDirectory = _root,
        HandshakeTimeout = TimeSpan.FromSeconds(10)
    };

    // ------------------------------------------------------------------
    // Handshake
    // ------------------------------------------------------------------

    [Fact]
    public async Task connect_performs_the_initialize_handshake()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer());

        Assert.True(client.IsInitialized);
        Assert.Equal("2025-06-18", client.ProtocolVersion);
        Assert.Equal("fake-server", client.ServerName);
    }

    [Fact]
    public async Task connect_rejects_a_server_without_a_command()
    {
        var error = await Assert.ThrowsAsync<McpException>(
            () => McpClient.ConnectAsync(new McpServerConfig { Name = "http-only" }));

        Assert.Contains("no command", error.Message);
    }

    [Fact]
    public async Task connect_reports_a_command_that_cannot_start()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => McpClient.ConnectAsync(new McpServerConfig
        {
            Name = "missing",
            Command = "/nonexistent/binary-for-reptar-tests"
        }));

        Assert.Contains("failed to start", error.Message);
    }

    // ------------------------------------------------------------------
    // Tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task list_tools_reads_names_descriptions_and_schemas()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer());

        var tools = await client.ListToolsAsync();

        Assert.Equal(3, tools.Count);
        var echo = tools.Single(tool => tool.Name == "echo");
        Assert.Equal("Echo text back.", echo.Description);
        Assert.Equal("object", echo.InputSchema["type"]!.GetValue<string>());
        Assert.NotNull(echo.InputSchema["properties"]!["text"]);
    }

    [Fact]
    public async Task tool_definitions_are_namespaced_by_server()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer("my-server"));

        var definitions = await client.ToolDefinitionsAsync();

        Assert.Contains(definitions, tool => tool.Name == "mcp__my_server__echo");
        Assert.Contains(definitions, tool => tool.Name == "mcp__my_server__fail");
        // Dashes become underscores so the name is a valid codemode identifier too.
        Assert.Contains(definitions, tool => tool.Name == "mcp__my_server__slow_tool");
        Assert.All(definitions, tool => Assert.Contains("[MCP my-server]", tool.Description));
    }

    [Fact]
    public async Task excluded_tools_are_not_exposed()
    {
        var config = FakeServer() with { ExcludeTools = ["fail"] };
        await using var client = await McpClient.ConnectAsync(config);

        var tools = await client.ListToolsAsync();

        Assert.DoesNotContain(tools, tool => tool.Name == "fail");
        Assert.Equal(2, tools.Count);
    }

    // ------------------------------------------------------------------
    // Calls
    // ------------------------------------------------------------------

    [Fact]
    public async Task call_returns_the_flattened_text_content()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer());

        var result = await client.CallToolAsync("echo", """{"text":"hi"}""");

        Assert.True(result.Success);
        Assert.Equal("echoed ok", result.Content);
    }

    [Fact]
    public async Task a_server_reported_error_becomes_a_failed_result()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer());

        var result = await client.CallToolAsync("fail", "{}");

        Assert.False(result.Success);
        Assert.Equal("tool blew up", result.Content);
        Assert.Equal("mcp_tool_error", result.ErrorCode);
    }

    [Fact]
    public async Task invalid_arguments_are_rejected_before_reaching_the_server()
    {
        await using var client = await McpClient.ConnectAsync(FakeServer());

        var result = await client.CallToolAsync("echo", "{not json");

        Assert.False(result.Success);
        Assert.Equal("invalid_json", result.ErrorCode);
    }

    // ------------------------------------------------------------------
    // Tool source / mounting
    // ------------------------------------------------------------------

    [Fact]
    public async Task source_collects_tools_from_every_server()
    {
        await using var source = await McpToolSource.ConnectAsync([FakeServer("one"), FakeServer("two")]);

        Assert.Equal(2, source.Clients.Count);
        Assert.Equal(6, source.Tools.Count);
        Assert.Contains(source.Tools, tool => tool.Name == "mcp__one__echo");
        Assert.Contains(source.Tools, tool => tool.Name == "mcp__two__echo");
    }

    [Fact]
    public async Task a_broken_server_is_skipped_and_reported_without_failing_the_rest()
    {
        var warnings = new List<string>();

        await using var source = await McpToolSource.ConnectAsync(
            [
                new McpServerConfig { Name = "broken", Command = "/nonexistent/binary" },
                FakeServer("working")
            ],
            warnings.Add);

        Assert.Single(source.Clients);
        Assert.Contains(source.Tools, tool => tool.Name == "mcp__working__echo");
        Assert.Single(warnings);
        Assert.Contains("broken", warnings[0]);
    }

    [Fact]
    public async Task source_dispatches_by_namespaced_name()
    {
        await using var source = await McpToolSource.ConnectAsync([FakeServer()]);

        var ok = await source.CallAsync("mcp__fake__echo", """{"text":"hi"}""");
        var failed = await source.CallAsync("mcp__fake__fail", "{}");
        var missing = await source.CallAsync("mcp__fake__nope", "{}");

        Assert.True(ok.Success);
        Assert.False(failed.Success);
        Assert.Equal("tool_not_found", missing.ErrorCode);
    }

    [Fact]
    public async Task mount_registers_each_tool_on_the_app()
    {
        var app = ReptarApp.Create();
        await using var source = await McpToolSource.ConnectAsync([FakeServer()]);

        var mounted = source.Mount(app);

        Assert.Equal(3, mounted.Count);

        // The route is reachable through the same dispatcher the agent uses.
        var dispatchable = app.GetAiTools(onlyExplicit: true).Select(tool => tool.Name).ToList();
        Assert.Contains("mcp__fake__echo", dispatchable);
    }

    [Fact]
    public async Task mounted_tools_are_callable_through_the_agent_dispatcher()
    {
        var app = ReptarApp.Create();
        await using var source = await McpToolSource.ConnectAsync([FakeServer()]);
        source.Mount(app);

        var result = await app.ExecuteAiToolAsync("mcp__fake__echo", """{"text":"hi"}""");

        Assert.True(result.Success);
        Assert.Contains("echoed ok", result.Content);
    }

    [Fact]
    public async Task mcp_tools_are_visible_to_codemode_scripts()
    {
        var app = ReptarApp.Create();
        await using var source = await McpToolSource.ConnectAsync([FakeServer("files")]);
        var mounted = source.Mount(app);

        var described = CodeMode.DescribeTools(mounted);

        // "mcp__files__echo" becomes tools.mcp__files__echo in a script.
        Assert.Contains("tools.mcp__files__echo", described);
    }
}
