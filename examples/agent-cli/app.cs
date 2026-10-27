#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.AI/Reptar.AI.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj
#:include schema.cs
#:include features/chat.cs
#:include selftest.cs
#:include rpc/RpcMode.cs
#:include rpc/RpcClient.cs
#:include web.cs
#:include terminal/Ansi.cs
#:include terminal/TermSelector.cs
#:include terminal/TermLayout.cs
#:include terminal/ReptarTerm.cs
#:include terminal/TerminalAgent.cs
#:include terminal/TerminalBuffer.cs
#:include terminal/TerminalEditor.cs
#:include terminal/TerminalInput.cs
#:include terminal/TerminalScreen.cs
#:include terminal/TerminalUi.cs

global using Reptar;
global using Reptar.AI;
global using Reptar.Hosting;
global using ReptarWeb;

// ---------------------------------------------------------------------------
// Reptar Agent CLI + Web Harness
//
//   interactive terminal loop:   ./agent-cli            (or: bun reptar agent)
//   web companion on :4040:     ./agent-cli --web
//   resume a session:           ./agent-cli --session sess_xxx
//   pick a model:               ./agent-cli --model anthropic/claude-sonnet-4-5
//   tool self-test (offline):   ./agent-cli --self-test
//   JSONL RPC server:           ./agent-cli --mode rpc   (stdin/stdout protocol)
// ---------------------------------------------------------------------------

var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
string? ValueOf(string name) =>
    args.SkipWhile(a => !a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase))
        .Select(a => a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase) ? a[(name.Length + 3)..] : null)
        .FirstOrDefault(v => v is not null)
    ?? (Array.IndexOf(args, "--" + name) >= 0 && Array.IndexOf(args, "--" + name) + 1 < args.Length
        ? args[Array.IndexOf(args, "--" + name) + 1]
        : null);

var workspaceRoot = Path.GetFullPath(ValueOf("workspace") ?? ".");
var dbPath = ValueOf("db") ?? Path.Combine(workspaceRoot, ".reptar", "agent.db");
#if !NETWASM
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
#endif

var app = ReptarApp.Create(new ReptarConfig
{
    Database = Db.Sqlite(dbPath),
    MaxBodyBytes = 4 * 1024 * 1024,
});

// Mount the standard workspace tools (read_file, write_file, edit_file,
// list_dir, run_command) as in-process Reptar AI tools with path traversal
// protection against the workspace root.
app.UseWorkspaceTools(workspaceRoot, options =>
{
    // A coding agent needs the shell; library users must opt in explicitly.
    options.AllowRunCommand = true;
});

var store = new AgentSessionStore(app.Db, Schema.Sessions, Schema.Messages);
await store.EnsureCreatedAsync();

// The session tree is additive: it has its own tables so an existing database keeps working,
// and old flat sessions are imported on first load.
var tree = new SessionTreeStore(app.Db);
await tree.EnsureCreatedAsync();

// Chat API + session persistence + embedded web companion.
Chat.Configure(app, store, workspaceRoot);

app.Get("/api/health", ctx => ctx.JsonRaw("""{"ok":true}"""));
app.Get("/", ctx => ctx.Html(WebSpa.IndexHtml));

// -----------------------------------------------------------------------
// Self-test: prove the tool bridge works offline, end-to-end, through the
// same in-process dispatch the agent loop uses (ExecuteAiToolAsync).
// -----------------------------------------------------------------------
if (flags.Contains("--self-test"))
{
    await SelfTest.RunAsync(app, workspaceRoot);
    return;
}

// -----------------------------------------------------------------------
// Provider management: status table and interactive connect (pi /login,
// opencode `auth login` parity) before any mode handling.
// -----------------------------------------------------------------------
var agent = new TerminalAgent(app, tree, new TerminalAgentOptions
{
    WorkspaceRoot = workspaceRoot,
    Provider = ValueOf("provider"),
    Model = ValueOf("model"),
    BaseUrl = ValueOf("base-url"),
    SystemPrompt = null,
    ResumeSessionId = ValueOf("session"),
}, store);

if (flags.Contains("--providers"))
{
    agent.PrintProviders();
    return;
}

if (flags.Contains("--aliases"))
{
    Console.WriteLine(ModelAliases.Describe());
    return;
}

if (flags.Contains("--templates"))
{
    Console.WriteLine(PromptTemplates.Describe(PromptTemplates.Load(workspaceRoot)));
    return;
}

if (flags.Contains("--connect") || flags.Contains("--login"))
{
    var connectArg = ValueOf("connect") ?? ValueOf("login");
    await agent.ConnectAsync(string.IsNullOrEmpty(connectArg) ? null : connectArg);
    // fall through into the interactive session with the new provider active
}

// -----------------------------------------------------------------------
// RPC mode: long-lived JSONL agent server on stdin/stdout. Any language can
// drive it; see examples/agent-cli/rpc/RpcMode.cs for the protocol.
// -----------------------------------------------------------------------
if (string.Equals(ValueOf("mode"), "rpc", StringComparison.OrdinalIgnoreCase))
{
    var rpc = new RpcMode(app, new TerminalAgentOptions
    {
        WorkspaceRoot = workspaceRoot,
        Provider = ValueOf("provider"),
        Model = ValueOf("model"),
        BaseUrl = ValueOf("base-url"),
        ResumeSessionId = ValueOf("session"),
    }, store);

    await rpc.RunAsync();
    return;
}

// -----------------------------------------------------------------------
// Web mode: start Kestrel and serve the API + SPA.
// -----------------------------------------------------------------------
if (flags.Contains("--web"))
{
    var port = ValueOf("web") is { Length: > 0 } portArg && int.TryParse(portArg, out var parsedPort)
        ? parsedPort
        : 4040;
    Environment.SetEnvironmentVariable("PORT", port.ToString());
    Console.WriteLine($"Reptar agent web companion → http://localhost:{port}");
    await app.RunAsync([]);
    return;
}

// -----------------------------------------------------------------------
// RPC client demo: spawn this same agent as a child server and drive it over
// JSONL. Proves the protocol from the C# side and doubles as a smoke test.
// -----------------------------------------------------------------------
if (flags.Contains("--rpc-demo"))
{
    var prompt = ValueOf("rpc-demo") ?? string.Join(' ', args.Where(a => !a.StartsWith('-')));
    if (string.IsNullOrWhiteSpace(prompt))
        prompt = "List the files in this workspace.";

    await using var rpc = RpcClient.Start(
        workspace: workspaceRoot,
        provider: ValueOf("provider"),
        model: ValueOf("model"),
        repositoryRoot: workspaceRoot);

    rpc.DiagnosticReceived += line => Console.Error.WriteLine($"[server] {line}");

    rpc.EventReceived += evt =>
    {
        switch (evt)
        {
            case AgentEvent.ToolExecutionStart start:
                Console.Error.WriteLine($"[client] → {start.ToolName}");
                break;
            case AgentEvent.ToolExecutionEnd end:
                Console.Error.WriteLine(
                    $"[client] {(end.IsError ? "✗" : "✓")} {end.ToolName} {end.DurationMs:0}ms");
                break;
            case AgentEvent.RetryScheduled retry:
                Console.Error.WriteLine($"[client] retrying in {retry.DelayMs:0}ms ({retry.Reason})");
                break;
            case AgentEvent.CompactionEnd compaction:
                Console.Error.WriteLine(
                    $"[client] compacted {compaction.TokensBefore:N0} → {compaction.EstimatedTokensAfter:N0} tokens");
                break;
        }
    };

    var state = await rpc.GetStateAsync();
    Console.Error.WriteLine(
        $"[client] connected · {state?["model"]?["provider"]}/{state?["model"]?["id"]}");

    var result = await rpc.PromptAndWaitAsync(prompt, delta => Console.Write(delta));
    Console.WriteLine();
    Console.Error.WriteLine(
        $"[client] {result.Summary.StopReason} · {result.Summary.StepsTaken} steps · " +
        $"{result.Summary.ToolCallsExecuted} tool calls · {result.Summary.ElapsedMs:0}ms");

    return;
}

// -----------------------------------------------------------------------
// Default: interactive terminal agent.
// -----------------------------------------------------------------------

await agent.RunAsync();
