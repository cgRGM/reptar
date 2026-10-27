#if !NETWASM
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reptar.AI;

/// <summary>
/// Options for the standard workspace toolset mounted by
/// <see cref="WorkspaceToolsExtensions.UseWorkspaceTools"/>.
/// </summary>
public sealed record WorkspaceToolOptions
{
    public string RootPath { get; set; } = ".";
    /// <summary>
    /// Authorization gate evaluated before every mounted workspace tool route.
    /// When null (default), the toolset is reachable from loopback peers and
    /// in-process AI tool dispatch only — the intended local-agent companion
    /// deployment. Exposing the tools to any other client requires an explicit
    /// gate, e.g. <c>ctx => Auth.CurrentUser(ctx) is not null</c> or a shared
    /// token check.
    /// </summary>
    public Func<Ctx, bool>? Authorize { get; set; }
    /// <summary>
    /// Mounts the shell tool. Off by default: a shell is a full sandbox escape
    /// by design, so enabling it must be an explicit app decision.
    /// </summary>
    public bool AllowRunCommand { get; set; } = false;
    /// <summary>
    /// Optional command prefixes allowed when <see cref="AllowRunCommand"/> is
    /// true. Empty means "allow any command" (the explicit opt-in is the gate).
    /// </summary>
    public string[] AllowedCommandPrefixes { get; set; } = [];
    public bool AllowWrite { get; set; } = true;
    public int MaxReadLines { get; set; } = 2_000;
    public int MaxReadBytes { get; set; } = 50 * 1024;
    public int MaxListEntries { get; set; } = 500;
    public int MaxCommandOutputBytes { get; set; } = 32 * 1024;
    public int DefaultCommandTimeoutMs { get; set; } = 15_000;
    public int MaxCommandTimeoutMs { get; set; } = 120_000;
    public string[] IgnoredDirectories { get; set; } =
        [".git", "bin", "obj", "node_modules", ".turbo", ".reptar", "dist", ".next", "publish"];
}

public static class WorkspaceToolsExtensions
{
    /// <summary>
    /// Mounts the standard development tools as Reptar AI tools:
    /// read_file, write_file, edit_file, list_dir, and (opt-in) run_command.
    /// Tool calls dispatch in-process with zero HTTP overhead, and every
    /// path argument is validated against the workspace root.
    /// </summary>
    public static ReptarApp UseWorkspaceTools(
        this ReptarApp app,
        string rootPath = ".",
        Action<WorkspaceToolOptions>? configure = null)
    {
        var options = new WorkspaceToolOptions { RootPath = rootPath };
        configure?.Invoke(options);
        var workspace = new WorkspaceRoot(options);

        // Gate every workspace tool route: the toolset is a full filesystem
        // (and optionally shell) surface, so without an explicit Authorize
        // gate it accepts loopback peers and in-process dispatch only.
        app.Use(async (ctx, next) =>
        {
            if (IsWorkspaceToolPath(ctx.Req.Path) && !IsAllowed(ctx, options))
            {
                return ctx.Error(
                    403,
                    "forbidden",
                    "Workspace tools are not exposed to this client. Pass an Authorize gate in WorkspaceToolOptions to enable remote access.");
            }

            return await next();
        });

        WorkspaceTools.Mount(app, workspace);
        return app;
    }

    private static bool IsWorkspaceToolPath(string path) =>
        path.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowed(Ctx ctx, WorkspaceToolOptions options) =>
        options.Authorize is { } authorize
            ? authorize(ctx)
            : IsLoopbackPeer(ctx.Req.RemoteIp);

    private static bool IsLoopbackPeer(string? remoteIp)
    {
        // Null peer = in-process AI tool dispatch (synthetic requests) and the
        // in-memory test client; real remote peers are gated below.
        if (string.IsNullOrEmpty(remoteIp))
            return true;

        return remoteIp is "::1" or "::ffff:127.0.0.1" ||
            remoteIp.StartsWith("127.", StringComparison.Ordinal);
    }
}

/// <summary>
/// Resolves and guards paths against the configured workspace root.
/// </summary>
public sealed class WorkspaceRoot(WorkspaceToolOptions options)
{
    public string Root { get; } = Canonicalize(Path.GetFullPath(options.RootPath));

    internal WorkspaceToolOptions Options { get; } = options;

    /// <summary>
    /// Resolves a tool-supplied path against the workspace root, rejecting any escape.
    /// </summary>
    /// <remarks>
    /// An empty path means the workspace root, not an error. Models pass <c>""</c> or
    /// <c>"."</c> constantly when they mean "here", and rejecting the first turns a trivial
    /// <c>list_dir</c> into a tool error the model then has to recover from.
    /// </remarks>
    public string Resolve(string path)
    {
        var normalized = (path ?? "").Trim();

        if (normalized.Length == 0)
            normalized = ".";

        var combined = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(Root, normalized));

        // Path.GetFullPath does not follow symlinks. Canonicalize the path
        // (and the nearest existing ancestor) so a symlink inside the
        // workspace cannot be used to read or write outside it.
        var canonical = Canonicalize(combined);

        if (!IsInside(canonical))
            throw new UnauthorizedAccessException(
                $"Path '{path}' escapes the workspace root.");

        return canonical;
    }

    public string Describe(string path)
    {
        try
        {
            return Path.GetRelativePath(Root, Resolve(path)).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private bool IsInside(string fullPath)
    {
        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var withSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, Root, comparison)
            || fullPath.StartsWith(withSeparator, comparison);
    }

    /// <summary>
    /// Resolves symlinks in every segment of a path, including dangling links
    /// and links in intermediate directories. Walking segment-by-segment is
    /// what catches `workspace/link-to-outside/file.txt` even when the final
    /// file exists and Path.GetFullPath would happily return the link path.
    /// </summary>
    private static string Canonicalize(string fullPath, int depth = 0)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (depth > 32)
            return normalized;

        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
            return normalized;

        var current = root;
        var relative = normalized[root.Length..];
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var linkTarget = TryResolveLink(current);
            if (linkTarget is not null)
                current = Canonicalize(linkTarget, depth + 1);
        }

        return Path.GetFullPath(current);
    }

    private static string? TryResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                return Path.GetFullPath(resolved.FullName);

            // Dangling symlink: ResolveLinkTarget may return null while
            // LinkTarget still exposes the (missing) destination.
            var rawTarget = info.LinkTarget;
            if (rawTarget is not null)
            {
                var parent = Path.GetDirectoryName(path);
                return parent is null
                    ? Path.GetFullPath(rawTarget)
                    : Path.GetFullPath(rawTarget, parent);
            }
        }
        catch
        {
            // Not a link, or the target is unreadable. Treat as a normal path.
        }

        return null;
    }
}

public static class WorkspaceTools
{
    internal static void Mount(ReptarApp app, WorkspaceRoot workspace)
    {
        var o = workspace.Options;

        app.Get("/workspace/read_file", ctx =>
        {
            var path = ctx.Req.QueryValue("path");
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(ctx.BadRequest("Query parameter 'path' is required."));

            var start = ParseInt(ctx.Req.QueryValue("start_line"), 1);
            var end = ParseInt(ctx.Req.QueryValue("end_line"), o.MaxReadLines);

            try
            {
                var (text, totalLines, truncated) = ReadFile(workspace, path, start, end);
                return Task.FromResult(ctx.JsonRaw(new JsonObject
                {
                    ["path"] = workspace.Describe(path),
                    ["start_line"] = start,
                    ["end_line"] = start + CountLines(text) - 1,
                    ["total_lines"] = totalLines,
                    ["truncated"] = truncated,
                    ["content"] = text
                }.ToJsonString()));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Task.FromResult(ctx.NotFound($"File not found: {workspace.Describe(path)}"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Task.FromResult(ctx.Error(400, "path_escape", ex.Message));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ctx.Error(400, "read_error", ex.Message));
            }
        })
        .WithName("read_file")
        .WithSummary("Read a workspace file")
        .WithDescription("""
            Reads a file inside the workspace, optionally between start_line and end_line.
            Content is truncated to a safe byte limit. Returns path, line numbers, and content.
            """)
        .AsAiTool("read_file", """
            Read a file from the workspace. Params: path (relative to workspace root, required),
            start_line (optional 1-based), end_line (optional inclusive). Returns numbered content.
            For directories use list_dir instead.
            """)
        .WithAiToolSchema(Schema(
                ("path", "string", "Path relative to the workspace root", true),
                ("start_line", "integer", "First line to return, 1-based", false),
                ("end_line", "integer", "Last line to return, inclusive", false)));

        if (o.AllowWrite)
        {
            MountWriteTools(app, workspace);
        }

        app.Get("/workspace/list_dir", ctx =>
        {
            var path = ctx.Req.QueryValue("path", ".");
            var depth = Math.Clamp(ParseInt(ctx.Req.QueryValue("depth"), 3), 1, 6);

            try
            {
                var entries = ListDirectory(workspace, path, depth);
                var array = new JsonArray();
                foreach (var entry in entries)
                {
                    array.Add(new JsonObject
                    {
                        ["path"] = entry.Path,
                        ["type"] = entry.Type,
                        ["size"] = entry.Size
                    });
                }
                return Task.FromResult(ctx.JsonRaw(new JsonObject
                {
                    ["path"] = workspace.Describe(path),
                    ["entries"] = array
                }.ToJsonString()));
            }
            catch (DirectoryNotFoundException)
            {
                return Task.FromResult(ctx.NotFound($"Directory not found: {workspace.Describe(path)}"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Task.FromResult(ctx.Error(400, "path_escape", ex.Message));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ctx.Error(400, "list_error", ex.Message));
            }
        })
        .WithName("list_file_tree")
        .WithSummary("List workspace directory tree")
        .AsAiTool("list_dir", """
            Recursively list files and folders under a workspace path, ignoring .git, bin, obj,
            node_modules, and build output. Params: path (optional, defaults to workspace root),
            depth (optional, 1..6, default 3). Use this to discover project structure.
            """)
        .WithAiToolSchema(Schema(
                ("path", "string", "Directory relative to the workspace root; omit for the root", false),
                ("depth", "integer", "How deep to recurse, 1-6, default 3", false)));

        if (o.AllowRunCommand)
        {
            MountRunCommand(app, workspace);
        }
    }

    private static void MountWriteTools(ReptarApp app, WorkspaceRoot workspace)
    {
        var o = workspace.Options;

        app.Post("/workspace/write_file", async ctx =>
        {
            var node = WorkspaceTools.ReadJsonNode(ctx);
            var input = new WriteFileInput(
                WorkspaceTools.Str(node?["path"]) ?? "",
                WorkspaceTools.Str(node?["content"]) ?? "");
            if (string.IsNullOrWhiteSpace(input.Path))
                return ctx.BadRequest("Body fields 'path' and 'content' are required.");

            try
            {
                var (created, diff) = WriteFile(workspace, input.Path, input.Content);
                return ctx.JsonRaw(new JsonObject
                {
                    ["path"] = workspace.Describe(input.Path),
                    ["created"] = created,
                    ["diff"] = diff
                }.ToJsonString(), created ? 201 : 200);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ctx.Error(400, "path_escape", ex.Message);
            }
            catch (Exception ex)
            {
                return ctx.Error(400, "write_error", ex.Message);
            }
        })
        .WithName("write_file")
        .WithSummary("Create or overwrite a workspace file")
        .AsAiTool("write_file", """
            Create or overwrite a file in the workspace. Params: path (required), content (required, full file text).
            Parent directories are created automatically. A unified diff of the change is returned.
            """)
        .WithAiToolSchema(Schema(
                ("path", "string", "Path relative to the workspace root", true),
                ("content", "string", "Full file text", true)));

        app.Post("/workspace/edit_file", async ctx =>
        {
            var node = WorkspaceTools.ReadJsonNode(ctx);
            var input = new EditFileInput(
                WorkspaceTools.Str(node?["path"]) ?? "",
                WorkspaceTools.Str(node?["target"]) ?? "",
                WorkspaceTools.Str(node?["replacement"]) ?? "",
                node?["all"] is { } all && all.GetValueKind() == JsonValueKind.True);
            if (string.IsNullOrWhiteSpace(input.Path) || string.IsNullOrEmpty(input.Target))
                return ctx.BadRequest("Body fields 'path', 'target', and 'replacement' are required.");

            try
            {
                var (applied, diff) = EditFile(workspace, input.Path, input.Target, input.Replacement, input.All);
                if (!applied)
                    return ctx.NotFound($"Target text not found in {workspace.Describe(input.Path)}.");
                return ctx.JsonRaw(new JsonObject
                {
                    ["path"] = workspace.Describe(input.Path),
                    ["diff"] = diff
                }.ToJsonString());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ctx.Error(400, "path_escape", ex.Message);
            }
            catch (Exception ex)
            {
                return ctx.Error(400, "edit_error", ex.Message);
            }
        })
        .WithName("edit_file")
        .WithSummary("Replace text inside a workspace file")
        .AsAiTool("edit_file", """
            Perform an exact string replacement inside a workspace file. Params: path (required),
            target (required, exact text to replace), replacement (required), all (optional, replace every
            occurrence, default false). Returns a unified diff of the applied change.
            """)
        .WithAiToolSchema(Schema(
                ("path", "string", "Path relative to the workspace root", true),
                ("target", "string", "Exact text to replace", true),
                ("replacement", "string", "Replacement text", true),
                ("all", "boolean", "Replace every occurrence, default false", false)));
    }

    private static void MountRunCommand(ReptarApp app, WorkspaceRoot workspace)
    {
        var o = workspace.Options;

        app.Post("/workspace/run_command", async ctx =>
        {
            var node = WorkspaceTools.ReadJsonNode(ctx);
            var command = WorkspaceTools.Str(node?["command"]);
            if (string.IsNullOrWhiteSpace(command))
                return ctx.BadRequest("Body field 'command' is required.");

            try
            {
                var waitMs = Math.Clamp(
                    WorkspaceTools.Int(node?["waitMs"] ?? node?["wait_ms"]) ?? o.DefaultCommandTimeoutMs,
                    100, o.MaxCommandTimeoutMs);
                var result = await RunCommandAsync(workspace, command, waitMs);

                // The tail is what matters for a build or test run, so truncate from the head
                // and spill the full log so the model can read it back.
                var stdout = ToolOutput.Tail(result.Stdout, maxBytes: o.MaxCommandOutputBytes);
                var stderr = ToolOutput.Tail(result.Stderr, maxBytes: o.MaxCommandOutputBytes);
                string? fullLogPath = null;

                if (stdout.Truncated || stderr.Truncated)
                {
                    fullLogPath = ToolOutput.SpillToFile(
                        $"$ {command}\n\n--- stdout ---\n{result.Stdout}\n--- stderr ---\n{result.Stderr}",
                        Path.Combine(workspace.Root, ".reptar", "tool-output"),
                        "run_command");
                }

                return ctx.JsonRaw(new JsonObject
                {
                    ["exitCode"] = result.ExitCode,
                    ["stdout"] = stdout.Content,
                    ["stderr"] = stderr.Content,
                    ["timedOut"] = result.TimedOut,
                    ["elapsedMs"] = Math.Round(result.ElapsedMs, 1),
                    ["truncated"] = stdout.Truncated || stderr.Truncated,
                    ["fullOutputPath"] = fullLogPath,
                    ["truncationNote"] = ToolOutput.DescribeTruncation(stdout, fullLogPath)
                }.ToJsonString());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ctx.Error(400, "command_denied", ex.Message);
            }
            catch (Exception ex)
            {
                return ctx.Error(400, "command_error", ex.Message);
            }
        })
        .WithName("run_command")
        .WithSummary("Run a shell command in the workspace")
        .AsAiTool("run_command", """
            Execute a shell command in the workspace root with a timeout. Params: command (required),
            wait_ms (optional, default 15000). Use it for builds, tests, git, and inspection commands.
            Prefer reading/writing files with the file tools instead of shell heredocs.
            """)
        .WithAiToolSchema(Schema(
                ("command", "string", "Shell command to run in the workspace root", true),
                ("wait_ms", "integer", "Timeout in milliseconds, default 15000", false)));
    }

    /// <summary>
    /// Builds a JSON Schema object for a tool's arguments.
    /// </summary>
    /// <remarks>
    /// The workspace tools read the raw request body, so there is no input type for the framework
    /// to derive a schema from. Without an explicit one the model receives an empty property list
    /// and has to guess every name and type — which is how it ends up sending
    /// <c>"wait_ms": "10000"</c> or inventing <c>waitMs</c>.
    /// </remarks>
    private static Dictionary<string, object?> Schema(
        params (string Name, string Type, string Description, bool Required)[] fields)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<object?>();

        foreach (var (name, type, description, isRequired) in fields)
        {
            properties[name] = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["description"] = description
            };

            if (isRequired)
                required.Add(name);
        }

        return Finish(properties, required);
    }

    private static Dictionary<string, object?> Finish(
        Dictionary<string, object?> properties,
        List<object?> required)
    {

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    // ---------- tool bodies (shared by HTTP routes, self-test, and unit tests) ----------

    internal sealed record WriteFileInput(string Path, string Content);

    internal sealed record EditFileInput(string Path, string Target, string Replacement, bool All = false);

    internal sealed record RunCommandInput(string Command, int? WaitMs);

    internal static (string Text, int TotalLines, bool Truncated) ReadFile(
        WorkspaceRoot workspace, string path, int startLine, int endLine)
    {
        var resolved = workspace.Resolve(path);

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {path}");

        var o = workspace.Options;
        var raw = File.ReadAllText(resolved, Encoding.UTF8);
        var totalLines = CountLines(raw);

        var from = Math.Max(1, startLine);
        var to = Math.Min(Math.Max(from, endLine), totalLines);

        var sb = new StringBuilder();
        var truncated = false;

        using (var reader = new StringReader(raw))
        {
            var lineNo = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNo++;
                if (lineNo < from) continue;
                if (lineNo > to) break;
                sb.Append(lineNo).Append('\t').Append(line).Append('\n');

                if (sb.Length > o.MaxReadBytes)
                {
                    truncated = true;
                    break;
                }
            }
        }

        return (sb.ToString(), totalLines, truncated);
    }

    internal static (bool Created, string Diff) WriteFile(WorkspaceRoot workspace, string path, string content)
    {
        var resolved = workspace.Resolve(path);
        var existed = File.Exists(resolved);
        var before = existed ? File.ReadAllText(resolved, Encoding.UTF8) : null;

        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(resolved, content, Encoding.UTF8);

        return (Created: !existed, Diff: UnifiedDiff(before, content, workspace.Describe(path)));
    }

    internal static (bool Applied, string Diff) EditFile(
        WorkspaceRoot workspace, string path, string target, string replacement, bool replaceAll)
    {
        var resolved = workspace.Resolve(path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {path}");

        var before = File.ReadAllText(resolved, Encoding.UTF8);
        var count = CountOccurrences(before, target);

        if (count == 0)
            return (false, "");

        var after = replaceAll
            ? before.Replace(target, replacement)
            : ReplaceFirst(before, target, replacement);

        File.WriteAllText(resolved, after, Encoding.UTF8);

        return (true, UnifiedDiff(before, after, workspace.Describe(path)));
    }

    internal sealed record ListEntry(string Path, string Type, long Size);

    internal static List<ListEntry> ListDirectory(WorkspaceRoot workspace, string path, int depth)
    {
        var resolved = workspace.Resolve(path);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var o = workspace.Options;
        var ignored = new HashSet<string>(o.IgnoredDirectories, StringComparer.OrdinalIgnoreCase);
        var entries = new List<ListEntry>();

        Walk(resolved, workspace.Root, "", depth, ignored, entries);

        if (entries.Count > o.MaxListEntries)
            entries = entries.GetRange(0, o.MaxListEntries);

        return entries;

        static void Walk(
            string directory,
            string root,
            string prefix,
            int remainingDepth,
            HashSet<string> ignored,
            List<ListEntry> entries)
        {
            if (remainingDepth < 0 || entries.Count >= 500)
                return;

            foreach (var dir in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(dir);
                if (ignored.Contains(name) || name.StartsWith('.'))
                    continue;

                var relative = prefix.Length == 0 ? name + "/" : prefix + name + "/";
                entries.Add(new ListEntry(relative, "dir", 0));

                if (remainingDepth > 0)
                    Walk(dir, root, relative, remainingDepth - 1, ignored, entries);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var relative = prefix.Length == 0 ? name : prefix + name;
                var info = new FileInfo(file);
                entries.Add(new ListEntry(relative, "file", info.Length));
            }
        }
    }

    public sealed record CommandResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        double ElapsedMs);

    public static async Task<CommandResult> RunCommandAsync(
        WorkspaceRoot workspace, string command, int waitMs)
    {
        var o = workspace.Options;

        if (o.AllowedCommandPrefixes.Length > 0 &&
            !o.AllowedCommandPrefixes.Any(prefix =>
                command.TrimStart().StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException(
                $"Command is not in the workspace allowlist: {command}");
        }

        var isWindows = OperatingSystem.IsWindows();

        using var timeoutCts = new CancellationTokenSource(waitMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = workspace.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(command);

            using var process = new Process { StartInfo = startInfo };

            process.Start();
            process.StandardInput.Close();

            // Read incrementally rather than buffering to the end, so a long build streams
            // to the agent UI instead of looking like a hang. Each line is also reported to
            // the ambient tool sink when one is attached (AgentToolStreaming).
            var stdoutTask = PumpAsync(process.StandardOutput, timeoutCts.Token);
            var stderrTask = PumpAsync(process.StandardError, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                stopwatch.Stop();

                var partialOut = await SafeReadAsync(stdoutTask);
                var partialErr = await SafeReadAsync(stderrTask);

                return new CommandResult(
                    -1,
                    Truncate(partialOut, o.MaxCommandOutputBytes),
                    Truncate(partialErr + $"\nCommand exceeded {waitMs}ms and was terminated.", o.MaxCommandOutputBytes),
                    TimedOut: true,
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            stopwatch.Stop();

            return new CommandResult(
                process.ExitCode,
                Truncate(await SafeReadAsync(stdoutTask), o.MaxCommandOutputBytes),
                Truncate(await SafeReadAsync(stderrTask), o.MaxCommandOutputBytes),
                TimedOut: false,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new CommandResult(-1, "", ex.Message, false, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Drains a redirected stream line by line, accumulating the full text and forwarding
    /// each line to <see cref="AgentToolStreaming"/> when a tool call is listening.
    /// </summary>
    private static async Task<string> PumpAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var accumulated = new StringBuilder();
        var observed = AgentToolStreaming.IsObserved;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                accumulated.AppendLine(line);

                if (observed)
                    AgentToolStreaming.Report(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or abort: keep whatever arrived.
        }

        return accumulated.ToString();
    }

    internal static JsonNode? ReadJsonNode(Ctx ctx)
    {
        var body = ctx.Req.BodyText;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? Str(JsonNode? node) =>
        node is null
            ? null
            : node.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToString();

    /// <summary>
    /// Reads an integer from a JSON value, accepting a numeric string.
    /// </summary>
    /// <remarks>
    /// Schema-guided coercion handles this for values the schema declares, but a model can also
    /// send an alias the schema does not list (<c>waitMs</c> for <c>wait_ms</c>). Reading
    /// tolerantly here means an off-schema spelling degrades to a working call instead of a
    /// conversion exception.
    /// </remarks>
    internal static int? Int(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<int>(out var direct) => direct,
        JsonValue value when value.TryGetValue<long>(out var wide) => (int)Math.Clamp(wide, int.MinValue, int.MaxValue),
        JsonValue value when value.TryGetValue<double>(out var real) => (int)Math.Clamp(real, int.MinValue, int.MaxValue),
        JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) => parsed,
        _ => null
    };

    internal static int ParseInt(string? value, int defaultValue) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;

    internal static int CountLines(string text)
    {
        var count = 0;
        foreach (var ch in text)
            if (ch == '\n') count++;
        return text.EndsWith('\n') || text.Length == 0 ? count : count + 1;
    }

    internal static string Truncate(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var charLimit = text.Length;
        while (charLimit > 0 && Encoding.UTF8.GetByteCount(text[..charLimit]) > maxBytes)
            charLimit--;

        return text[..charLimit] + "\n...[output truncated]";
    }

    internal static string ReplaceFirst(string text, string target, string replacement)
    {
        var index = text.IndexOf(target, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replacement + text[(index + target.Length)..];
    }

    internal static int CountOccurrences(string text, string target)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(target, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += target.Length;
        }
        return count;
    }

    private static async Task<string> SafeReadAsync(System.Threading.Tasks.Task<string> readTask)
    {
        try
        {
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch
        {
            return "";
        }
    }

    internal static string UnifiedDiff(string? before, string after, string path)
    {
        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');

        var beforeLines = (before ?? "").Split('\n');
        var afterLines = after.Split('\n');

        var prefix = CommonPrefix(beforeLines, afterLines);
        var suffix = CommonSuffix(beforeLines, afterLines, prefix);

        sb.Append("@@ -").Append(prefix + 1).Append(',').Append(beforeLines.Length - prefix - suffix)
          .Append(" +").Append(prefix + 1).Append(',').Append(afterLines.Length - prefix - suffix)
          .Append(" @@\n");

        for (var i = prefix; i < beforeLines.Length - suffix; i++)
            sb.Append('-').Append(beforeLines[i]).Append('\n');

        for (var i = prefix; i < afterLines.Length - suffix; i++)
            sb.Append('+').Append(afterLines[i]).Append('\n');

        if (sb.Length == 0)
            sb.Append("(no changes)\n");

        return sb.ToString();
    }

    private static int CommonPrefix(string[] a, string[] b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    private static int CommonSuffix(string[] a, string[] b, int prefix)
    {
        var i = 0;
        while (i < a.Length - prefix && i < b.Length - prefix &&
               a[a.Length - 1 - i] == b[b.Length - 1 - i])
            i++;
        return i;
    }
}
#endif
