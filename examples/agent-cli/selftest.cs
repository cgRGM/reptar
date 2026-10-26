using System.Text;
using Reptar;
using Reptar.AI;

#if !NETWASM
public static class SelfTest
{
    public static async Task RunAsync(ReptarApp app, string workspaceRoot)
    {
        Console.WriteLine($"Reptar agent self-test (workspace: {workspaceRoot})");
        Console.WriteLine(new string('─', 64));

        var scratch = Path.Combine(workspaceRoot, ".reptar", "selftest");
        Directory.CreateDirectory(scratch);

        var passed = 0;
        var failed = 0;

        async Task Check(string name, string tool, string arguments, Func<AiToolResult, bool> assert)
        {
            var result = await app.ExecuteAiToolAsync(tool, arguments);
            var ok = assert(result);
            if (ok) passed++; else failed++;
            Console.WriteLine($"{(ok ? "✓" : "✗")} {tool,-12} {name}");
            if (!ok)
                Console.WriteLine($"    {result.ToLlmResponse()[..Math.Min(160, result.ToLlmResponse().Length)]}");
        }

        var knownFile = File.Exists(Path.Combine(workspaceRoot, "app.cs"))
            ? "app.cs"
            : (File.Exists(Path.Combine(workspaceRoot, "package.json")) ? "package.json" : "README.md");

        await Check("reads a known file", "read_file",
            $$"""{"path": "{{knownFile}}", "start_line": 1, "end_line": 5}""",
            r => r.Success && !string.IsNullOrEmpty(r.Content));

        await Check("rejects path traversal", "read_file",
            """{"path": "../../etc/passwd"}""",
            r => !r.Success);

        await Check("writes a new file", "write_file",
            $$"""{"path": ".reptar/selftest/hello.txt", "content": "reptar agent self-test"}""",
            r => r.Success && r.Content?.Contains("reptar agent self-test") == true);

        await Check("edits the file it wrote", "edit_file",
            $$"""{"path": ".reptar/selftest/hello.txt", "target": "self-test", "replacement": "works"}""",
            r => r.Success);

        await Check("lists the workspace tree", "list_dir",
            """{"path": ".", "depth": 2}""",
            r => r.Success && (r.Content?.Contains(knownFile) == true || r.Content?.Contains("package.json") == true));

        await Check("runs a shell command", "run_command",
            """{"command": "echo reptar-tools-ok"}""",
            r => r.Success && r.Content?.Contains("reptar-tools-ok") == true);

        await Check("times out runaway commands", "run_command",
            """{"command": "sleep 30", "wait_ms": 500}""",
            r => r.Success && r.Content?.Contains("\"timedOut\":true") == true);

        try
        {
            File.Delete(Path.Combine(scratch, "hello.txt"));
        }
        catch
        {
            // scratch cleanup is best-effort
        }

        Console.WriteLine(new string('─', 64));
        Console.WriteLine($"{passed} passed, {failed} failed");
        Environment.Exit(failed == 0 ? 0 : 1);
    }
}
#endif
