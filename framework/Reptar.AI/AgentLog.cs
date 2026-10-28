#if !NETWASM
using System.Text;

namespace Reptar.AI;

/// <summary>
/// Opt-in diagnostic log for an agent run.
/// </summary>
/// <remarks>
/// Enabled with <c>REPTAR_AGENT_LOG=/path/to/agent.log</c>. Writes one line per event and one
/// record per tool call, including the exact arguments and the exact result.
///
/// This exists because the interesting failures are invisible from the transcript: a model that
/// invented a parameter name, a tool that rejected an argument the model thought was fine, a run
/// that retried twice. The terminal shows the outcome; the log shows why.
///
/// Never writes to stdout. A client may be using stdout for a protocol, and a stray log line
/// corrupts it.
/// </remarks>
public static class AgentLog
{
    private static readonly object Gate = new();
    private static readonly string? Path = ResolvePath();
    private static bool _writeFailed;

    /// <summary>True when a log file is configured.</summary>
    public static bool Enabled => Path is not null && !_writeFailed;

    /// <summary>Where the log is being written, for a startup banner.</summary>
    public static string? FilePath => Path;

    /// <summary>Writes one line. No-op when logging is off.</summary>
    public static void Write(string message)
    {
        if (!Enabled)
            return;

        try
        {
            lock (Gate)
            {
                System.IO.File.AppendAllText(
                    Path!,
                    $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken log must never break the run.
            _writeFailed = true;
        }
    }

    /// <summary>
    /// Writes one agent event. Deltas are summarised rather than logged individually, or a single
    /// response would produce thousands of lines.
    /// </summary>
    public static void Event(AgentEvent @event)
    {
        if (!Enabled)
            return;

        switch (@event)
        {
            case AgentEvent.MessageUpdate update:
                return;   // handled by the caller's delta accumulator

            case AgentEvent.ToolExecutionStart start:
                Write($"tool.start  {start.ToolName}  {Clip(start.ArgumentsJson, 500)}");
                break;

            case AgentEvent.ToolExecutionEnd end:
                Write($"tool.end    {end.ToolName}  {(end.IsError ? "ERROR" : "ok")}  " +
                      $"{end.DurationMs:0}ms  {Clip(end.Result, 500)}");
                break;

            case AgentEvent.ToolExecutionUpdate update:
                Write($"tool.delta  {update.ToolName}  {Clip(update.PartialResult, 200)}");
                break;

            case AgentEvent.AgentError error:
                Write($"agent.error {error.Message}");
                break;

            case AgentEvent.RetryScheduled retry:
                Write($"retry       attempt {retry.Attempt} in {retry.DelayMs:0}ms — {retry.Reason}");
                break;

            case AgentEvent.CompactionEnd compaction:
                Write($"compact     {compaction.TokensBefore} -> {compaction.EstimatedTokensAfter}" +
                      (compaction.Error is { Length: > 0 } failure ? $"  ({failure})" : ""));
                break;

            case AgentEvent.AgentEnd end:
                Write($"agent.end   {end.Summary.StopReason}  {end.Summary.StepsTaken} steps  " +
                      $"{end.Summary.ToolCallsExecuted} tool calls  {end.Summary.ElapsedMs:0}ms");
                break;

            default:
                Write($"event       {@event.GetType().Name}");
                break;
        }
    }

    /// <summary>Writes a user prompt or an assistant reply, so the log reads as a transcript.</summary>
    public static void Message(string role, string content) =>
        Write($"message     {role}: {Clip(content, 800)}");

    /// <summary>Clears the log at the start of a session.</summary>
    public static void Reset()
    {
        if (!Enabled)
            return;

        try
        {
            lock (Gate)
                System.IO.File.WriteAllText(Path!, $"# reptar agent log — {DateTimeOffset.Now:O}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writeFailed = true;
        }
    }

    private static string? ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable("REPTAR_AGENT_LOG");

        if (string.IsNullOrWhiteSpace(configured))
            return null;

        // A bare "1" or "true" means "log somewhere sensible".
        if (configured is "1" or "true" or "yes")
        {
            configured = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".reptar", "agent", "agent.log");
        }

        var directory = System.IO.Path.GetDirectoryName(configured);
        if (directory is { Length: > 0 })
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        return configured;
    }

    private static string Clip(string value, int limit)
    {
        var single = value.ReplaceLineEndings(" ");
        return single.Length <= limit ? single : single[..limit] + "…";
    }
}
#endif
