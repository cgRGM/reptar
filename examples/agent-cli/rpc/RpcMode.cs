#if !NETWASM
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

/// <summary>
/// RPC mode: Reptar runs as a long-lived agent server speaking JSONL on stdin/stdout.
/// A client in any language drives it with <c>{"type":"prompt", ...}</c> records and reads
/// back command responses plus the agent event stream.
/// </summary>
/// <remarks>
/// The protocol is a deliberate subset of pi's RPC mode so existing pi clients and the
/// documented record shapes carry over:
/// <list type="bullet">
/// <item>One JSON object per line, LF-framed. Never split on U+2028/U+2029.</item>
/// <item>Commands may carry an <c>id</c>; the matching response echoes it. Commands are
/// handled asynchronously, so clients must correlate by id, not order.</item>
/// <item>Agent activity streams as <c>agent_start</c>, <c>turn_start</c>, <c>message_update</c>,
/// <c>tool_execution_*</c>, <c>turn_end</c>, <c>agent_end</c>, <c>agent_settled</c>.</item>
/// <item>stdout is protocol only. Diagnostics go to stderr.</item>
/// <item>A successful <c>prompt</c> response means accepted, not finished. Wait for
/// <c>agent_settled</c>.</item>
/// </list>
/// </remarks>
public sealed class RpcMode
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ReptarApp _app;
    private readonly TerminalAgentOptions _options;
    private readonly AgentSessionStore? _store;

    private readonly AgentSession _session;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly SemaphoreSlim _stdoutGate = new(1, 1);
    private readonly List<Task> _inFlight = [];
    private readonly object _inFlightGate = new();

    private readonly SessionTreeStore? _tree;

    private ILlmClient _client;
    private AgentSessionRow? _row;
    private bool _autoCompaction = true;
    private bool _autoRetry = true;
    private int _contextLimit;
    private bool _shutdownRequested;

    public RpcMode(ReptarApp app, TerminalAgentOptions options, AgentSessionStore? store)
    {
        _app = app;
        _options = options;
        _store = store;
        _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(options));
        _session = new AgentSession(_client.Config.Model);
        _tree = store is null ? null : new SessionTreeStore(app.Db);
    }

    /// <summary>
    /// Runs the protocol until stdin closes. Returns when the client is done.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSessionAsync().ConfigureAwait(false);
        _ = RefreshContextLimitAsync();

        // Pump agent events onto stdout independently of command handling.
        var pump = Task.Run(() => PumpEventsAsync(cancellationToken), cancellationToken);

        var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
                continue;

            JsonObject command;
            try
            {
                command = JsonNode.Parse(line) as JsonObject
                    ?? throw new JsonException("record must be a JSON object");
            }
            catch (JsonException ex)
            {
                await WriteAsync(stdout, new JsonObject
                {
                    ["type"] = "response",
                    ["command"] = "parse",
                    ["success"] = false,
                    ["error"] = $"Failed to parse command: {ex.Message}"
                }).ConfigureAwait(false);
                continue;
            }

            // Commands are handled off the read loop so a long prompt never blocks stdin.
            var task = Task.Run(() => DispatchAsync(command, stdout, cancellationToken), cancellationToken);
            lock (_inFlightGate)
            {
                _inFlight.Add(task);
                _inFlight.RemoveAll(t => t.IsCompleted);
            }

            if (_shutdownRequested)
                break;
        }

        // stdin closing is not a reason to drop work: finish accepted commands, then let the
        // run settle so its events still reach the client.
        Task[] pending;
        lock (_inFlightGate)
            pending = [.. _inFlight];

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                // individual failures were already reported as responses
            }
        }

        await _session.WaitForIdleAsync().ConfigureAwait(false);

        _session.Abort();
        _events.Writer.TryComplete();

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Event pump
    // ------------------------------------------------------------------

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

        await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteRawAsync(stdout, evt.ToJson()).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Command dispatch
    // ------------------------------------------------------------------

    private async Task DispatchAsync(JsonObject command, StreamWriter stdout, CancellationToken cancellationToken)
    {
        var id = command["id"]?.GetValue<string>();
        var type = command["type"]?.GetValue<string>() ?? "";

        try
        {
            switch (type)
            {
                case "prompt":
                    await HandlePromptAsync(command, id, stdout, cancellationToken).ConfigureAwait(false);
                    break;

                case "steer":
                    _session.Steer(new ChatMessage("user", Text(command)));
                    await RespondAsync(stdout, id, "steer", new JsonObject { ["disposition"] = "queued" })
                        .ConfigureAwait(false);
                    break;

                case "follow_up":
                    _session.FollowUp(new ChatMessage("user", Text(command)));
                    await RespondAsync(stdout, id, "follow_up", new JsonObject { ["disposition"] = "queued" })
                        .ConfigureAwait(false);
                    break;

                case "abort":
                    _session.Abort();
                    await RespondAsync(stdout, id, "abort", null).ConfigureAwait(false);
                    break;

                case "clear_queue":
                    var (steering, followUp) = _session.ClearQueue();
                    await RespondAsync(stdout, id, "clear_queue", new JsonObject
                    {
                        ["steering"] = new JsonArray([.. steering.Select(s => (JsonNode)JsonValue.Create(s)!)]),
                        ["followUp"] = new JsonArray([.. followUp.Select(s => (JsonNode)JsonValue.Create(s)!)])
                    }).ConfigureAwait(false);
                    break;

                case "new_session":
                    await NewSessionAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "get_state":
                    await RespondAsync(stdout, id, "get_state", StateNode()).ConfigureAwait(false);
                    break;

                case "get_messages":
                    await RespondAsync(stdout, id, "get_messages", new JsonObject
                    {
                        ["messages"] = new JsonArray([.. _session.Messages.Select(m => (JsonNode)m.ToRpcNode())])
                    }).ConfigureAwait(false);
                    break;

                case "get_last_assistant_text":
                    var last = _session.Messages.LastOrDefault(m => m.Role == "assistant");
                    await RespondAsync(stdout, id, "get_last_assistant_text", new JsonObject
                    {
                        ["text"] = last?.Content
                    }).ConfigureAwait(false);
                    break;

                case "set_model":
                    await SetModelAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "get_available_models":
                    await RespondAsync(stdout, id, "get_available_models", new JsonObject
                    {
                        ["models"] = await ModelsNodeAsync().ConfigureAwait(false)
                    }).ConfigureAwait(false);
                    break;

                case "set_steering_mode":
                    _session.SteeringMode = ParseMode(command) ?? _session.SteeringMode;
                    await RespondAsync(stdout, id, "set_steering_mode", null).ConfigureAwait(false);
                    break;

                case "set_follow_up_mode":
                    _session.FollowUpMode = ParseMode(command) ?? _session.FollowUpMode;
                    await RespondAsync(stdout, id, "set_follow_up_mode", null).ConfigureAwait(false);
                    break;

                case "set_auto_compaction":
                    _autoCompaction = Bool(command, "enabled", true);
                    await RespondAsync(stdout, id, "set_auto_compaction", null).ConfigureAwait(false);
                    break;

                case "set_auto_retry":
                    _autoRetry = Bool(command, "enabled", true);
                    await RespondAsync(stdout, id, "set_auto_retry", null).ConfigureAwait(false);
                    break;

                case "compact":
                    await CompactAsync(command, id, stdout, cancellationToken).ConfigureAwait(false);
                    break;

                case "get_session_stats":
                    await RespondAsync(stdout, id, "get_session_stats", StatsNode()).ConfigureAwait(false);
                    break;

                case "fork":
                    await ForkAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "get_tree":
                    await GetTreeAsync(id, stdout).ConfigureAwait(false);
                    break;

                case "get_entries":
                    await GetEntriesAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "switch_leaf":
                    await SwitchLeafAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "set_session_name":
                    await SetSessionNameAsync(command, id, stdout).ConfigureAwait(false);
                    break;

                case "bash":
                    await BashAsync(command, id, stdout, cancellationToken).ConfigureAwait(false);
                    break;

                case "shutdown":
                    _shutdownRequested = true;
                    await RespondAsync(stdout, id, "shutdown", null).ConfigureAwait(false);
                    break;

                default:
                    await FailAsync(stdout, id, type, $"Unknown command: {type}").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await FailAsync(stdout, id, type, ex.Message).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private async Task HandlePromptAsync(
        JsonObject command, string? id, StreamWriter stdout, CancellationToken cancellationToken)
    {
        var message = Text(command);
        if (message.Length == 0)
        {
            await FailAsync(stdout, id, "prompt", "Field 'message' is required.").ConfigureAwait(false);
            return;
        }

        var streamingBehavior = command["streamingBehavior"]?.GetValue<string>();

        if (_session.IsStreaming)
        {
            switch (streamingBehavior)
            {
                case "steer":
                    _session.Steer(new ChatMessage("user", message));
                    await RespondAsync(stdout, id, "prompt", new JsonObject { ["disposition"] = "queued" })
                        .ConfigureAwait(false);
                    return;

                case "followUp":
                    _session.FollowUp(new ChatMessage("user", message));
                    await RespondAsync(stdout, id, "prompt", new JsonObject { ["disposition"] = "queued" })
                        .ConfigureAwait(false);
                    return;

                default:
                    await FailAsync(stdout, id, "prompt",
                        "Agent is streaming; pass streamingBehavior 'steer' or 'followUp'.").ConfigureAwait(false);
                    return;
            }
        }

        var userMessage = new ChatMessage("user", message, Images: Images(command));
        await PersistAsync(userMessage).ConfigureAwait(false);

        var task = _session.PromptAsync(
            userMessage,
            BuildConfig(),
            _client.StreamAsync,
            _app.GetAiTools(onlyExplicit: true),
            onEvent: evt => _events.Writer.TryWrite(evt),
            cancellationToken: cancellationToken);

        if (task is null)
        {
            await FailAsync(stdout, id, "prompt", "A run is already in flight.").ConfigureAwait(false);
            return;
        }

        // The response means "accepted"; events carry the outcome.
        await RespondAsync(stdout, id, "prompt", new JsonObject { ["disposition"] = "started" })
            .ConfigureAwait(false);

        _ = task.ContinueWith(
            _ => PersistRunAsync(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task NewSessionAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        _session.Abort();
        _session.Messages.Clear();
        _persistedCount = 0;

        if (_store is not null)
        {
            _row = await _store.CreateSessionAsync(
                command["name"]?.GetValue<string>() ?? "rpc session").ConfigureAwait(false);
        }

        await RespondAsync(stdout, id, "new_session", new JsonObject { ["cancelled"] = false })
            .ConfigureAwait(false);
    }

    private async Task SetModelAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        var provider = command["provider"]?.GetValue<string>();
        var modelId = command["modelId"]?.GetValue<string>() ?? command["model"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            await FailAsync(stdout, id, "set_model", "Field 'modelId' is required.").ConfigureAwait(false);
            return;
        }

        _client = LlmClientFactory.Create(AgentConfigResolver.Resolve(
            _options with { Provider = provider, Model = modelId }));
        _session.Model = _client.Config.Model;
        _ = RefreshContextLimitAsync();

        await RespondAsync(stdout, id, "set_model", new JsonObject
        {
            ["id"] = _client.Config.Model,
            ["provider"] = _client.Config.ProviderKey,
            ["name"] = _client.Config.DisplayName
        }).ConfigureAwait(false);
    }

    private async Task CompactAsync(
        JsonObject command, string? id, StreamWriter stdout, CancellationToken cancellationToken)
    {
        _events.Writer.TryWrite(new AgentEvent.CompactionStart(CompactionReason.Manual));

        var outcome = await ContextCompactor.CompactAsync(
            _session.Messages,
            _client,
            new CompactionSettings(),
            CompactionReason.Manual,
            customInstructions: command["customInstructions"]?.GetValue<string>(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome is null)
        {
            _events.Writer.TryWrite(new AgentEvent.CompactionEnd(
                CompactionReason.Manual, 0, 0, "nothing to compact"));
            await FailAsync(stdout, id, "compact", "Nothing to compact.").ConfigureAwait(false);
            return;
        }

        var (result, projection) = outcome.Value;
        _events.Writer.TryWrite(new AgentEvent.CompactionEnd(
            CompactionReason.Manual, result.TokensBefore, result.EstimatedTokensAfter, null));

        await RespondAsync(stdout, id, "compact", new JsonObject
        {
            ["summary"] = result.Summary,
            ["firstKeptIndex"] = result.FirstKeptMessageIndex,
            ["tokensBefore"] = result.TokensBefore,
            ["estimatedTokensAfter"] = result.EstimatedTokensAfter,
            ["projectedMessages"] = projection.Count
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>fork</c>: copies the branch up to <c>entryId</c> (or the leaf) into a new session and
    /// switches to it. The source session is untouched.
    /// </summary>
    private async Task ForkAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        if (_tree is null || _store is null || _row is null)
        {
            await FailAsync(stdout, id, "fork", "Session tree is not available.").ConfigureAwait(false);
            return;
        }

        var entryId = command["entryId"]?.GetValue<string>();
        var fork = await _store.CreateSessionAsync(
            (_row.Title ?? "session") + " (fork)").ConfigureAwait(false);

        if (!await _tree.ForkAsync(_row.Id, fork.Id, entryId).ConfigureAwait(false))
        {
            await _store.DeleteSessionAsync(fork.Id).ConfigureAwait(false);
            await FailAsync(stdout, id, "fork", "Nothing to fork at that entry.").ConfigureAwait(false);
            return;
        }

        _row = fork;
        _session.Messages.Clear();
        _session.Messages.AddRange(await _tree.LoadBranchAsync(fork.Id).ConfigureAwait(false));
        _persistedCount = _session.Messages.Count;

        await RespondAsync(stdout, id, "fork", new JsonObject
        {
            ["sessionId"] = fork.Id,
            ["messageCount"] = _session.Messages.Count
        }).ConfigureAwait(false);
    }

    /// <summary><c>get_tree</c>: the whole session as a tree, with the active leaf.</summary>
    private async Task GetTreeAsync(string? id, StreamWriter stdout)
    {
        if (_tree is null || _row is null)
        {
            await FailAsync(stdout, id, "get_tree", "Session tree is not available.").ConfigureAwait(false);
            return;
        }

        var tree = await _tree.TreeAsync(_row.Id).ConfigureAwait(false);
        var leafId = await _tree.LeafAsync(_row.Id).ConfigureAwait(false);

        await RespondAsync(stdout, id, "get_tree", new JsonObject
        {
            ["tree"] = new JsonArray([.. tree.Select(node => (JsonNode)TreeNodeJson(node))]),
            ["leafId"] = leafId
        }).ConfigureAwait(false);
    }

    private static JsonObject TreeNodeJson(SessionTreeNode node) => new()
    {
        ["entry"] = EntryJson(node.Entry),
        ["children"] = new JsonArray([.. node.Children.Select(child => (JsonNode)TreeNodeJson(child))])
    };

    private static JsonObject EntryJson(SessionEntryRow entry) => new()
    {
        ["id"] = entry.Id,
        ["parentId"] = entry.ParentId,
        ["seq"] = entry.Seq,
        ["role"] = entry.Role,
        ["content"] = entry.Content,
        ["name"] = entry.Name,
        ["toolCallId"] = entry.ToolCallId,
        ["createdAt"] = entry.CreatedAt
    };

    /// <summary>
    /// <c>get_entries</c>: the flat append order, with an optional <c>since</c> cursor so a client
    /// can poll for new entries instead of refetching the session.
    /// </summary>
    private async Task GetEntriesAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        if (_tree is null || _row is null)
        {
            await FailAsync(stdout, id, "get_entries", "Session tree is not available.").ConfigureAwait(false);
            return;
        }

        var entries = await _tree.EntriesAsync(_row.Id).ConfigureAwait(false);
        var leafId = await _tree.LeafAsync(_row.Id).ConfigureAwait(false);

        var since = command["since"]?.GetValue<string>();
        if (since is { Length: > 0 })
        {
            var index = entries.FindIndex(entry => entry.Id == since);
            if (index < 0)
            {
                await FailAsync(stdout, id, "get_entries", $"Unknown cursor '{since}'.").ConfigureAwait(false);
                return;
            }

            entries = entries[(index + 1)..];
        }

        await RespondAsync(stdout, id, "get_entries", new JsonObject
        {
            ["entries"] = new JsonArray([.. entries.Select(entry => (JsonNode)EntryJson(entry))]),
            ["leafId"] = leafId
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>switch_leaf</c>: moves the active leaf to an entry. The next prompt continues from
    /// there; the previous continuation stays in the tree.
    /// </summary>
    private async Task SwitchLeafAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        if (_tree is null || _row is null)
        {
            await FailAsync(stdout, id, "switch_leaf", "Session tree is not available.").ConfigureAwait(false);
            return;
        }

        var entryId = command["entryId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(entryId))
        {
            await FailAsync(stdout, id, "switch_leaf", "Field 'entryId' is required.").ConfigureAwait(false);
            return;
        }

        var entries = await _tree.EntriesAsync(_row.Id).ConfigureAwait(false);
        if (entries.All(entry => entry.Id != entryId))
        {
            await FailAsync(stdout, id, "switch_leaf", $"No entry '{entryId}' in this session.").ConfigureAwait(false);
            return;
        }

        await _tree.SetLeafAsync(_row.Id, entryId).ConfigureAwait(false);

        _session.Messages.Clear();
        _session.Messages.AddRange(await _tree.LoadBranchAsync(_row.Id).ConfigureAwait(false));
        _persistedCount = _session.Messages.Count;

        await RespondAsync(stdout, id, "switch_leaf", new JsonObject
        {
            ["leafId"] = entryId,
            ["messageCount"] = _session.Messages.Count
        }).ConfigureAwait(false);
    }

    private async Task SetSessionNameAsync(JsonObject command, string? id, StreamWriter stdout)
    {
        var name = command["name"]?.GetValue<string>();
        if (name is null || _store is null || _row is null)
        {
            await RespondAsync(stdout, id, "set_session_name", null).ConfigureAwait(false);
            return;
        }

        await _store.RenameSessionAsync(_row.Id, name).ConfigureAwait(false);
        _row = _row with { Title = name };

        _events.Writer.TryWrite(new AgentEvent.AgentError(""));
        await RespondAsync(stdout, id, "set_session_name", null).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a shell command in the workspace and records it as a transcript entry, so the model
    /// sees it on the next prompt. Mirrors pi's <c>bash</c> command.
    /// </summary>
    private async Task BashAsync(
        JsonObject command, string? id, StreamWriter stdout, CancellationToken cancellationToken)
    {
        var shell = Text(command, "command");
        if (shell.Length == 0)
        {
            await FailAsync(stdout, id, "bash", "Field 'command' is required.").ConfigureAwait(false);
            return;
        }

        var workspace = new WorkspaceRoot(new WorkspaceToolOptions
        {
            RootPath = _options.WorkspaceRoot,
            AllowRunCommand = true
        });

        var result = await WorkspaceTools
            .RunCommandAsync(workspace, shell, waitMs: 60_000)
            .ConfigureAwait(false);

        var truncated = ToolOutput.Tail(result.Stdout, maxBytes: 32 * 1024);
        var text = $"Ran `{shell}`\n```\n{truncated.Content}\n```";
        if (result.TimedOut)
            text += "\n\n(command timed out)";
        else if (result.ExitCode != 0)
            text += $"\n\nCommand exited with code {result.ExitCode}";

        _session.Messages.Add(new ChatMessage("user", text));

        await RespondAsync(stdout, id, "bash", new JsonObject
        {
            ["output"] = truncated.Content,
            ["exitCode"] = result.ExitCode,
            ["cancelled"] = false,
            ["truncated"] = truncated.Truncated,
            ["elapsedMs"] = Math.Round(result.ElapsedMs, 1)
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private AgentLoopConfig BuildConfig()
    {
        var settings = new CompactionSettings();

        return new AgentLoopConfig
        {
            Model = _client.Config.Model,
            MaxSteps = _options.MaxSteps,
            ToolExecutor = (invocation, ct) => _app.ExecuteAiToolAsync(invocation.ToolName, invocation.ArgumentsJson),
            SequentialTools = new HashSet<string>(StringComparer.Ordinal) { "run_command" },
            Retry = _autoRetry ? new AgentRetryPolicy() : AgentRetryPolicy.Disabled,
            PrepareNextTurn = async (turn, ct) =>
            {
                if (!_autoCompaction || _contextLimit <= 0)
                    return null;

                var tokens = ContextCompaction.EstimateContextTokens(turn.History);
                if (!ContextCompaction.ShouldCompact(tokens, _contextLimit, settings))
                    return null;

                _events.Writer.TryWrite(new AgentEvent.CompactionStart(CompactionReason.Threshold));

                var outcome = await ContextCompactor.CompactAsync(
                    turn.History, _client, settings, CompactionReason.Threshold, cancellationToken: ct)
                    .ConfigureAwait(false);

                if (outcome is null)
                {
                    _events.Writer.TryWrite(new AgentEvent.CompactionEnd(
                        CompactionReason.Threshold, tokens, tokens, "summary unavailable"));
                    return null;
                }

                var (result, projection) = outcome.Value;
                _events.Writer.TryWrite(new AgentEvent.CompactionEnd(
                    CompactionReason.Threshold, result.TokensBefore, result.EstimatedTokensAfter, null));

                return new AgentNextTurn(ContextMessages: projection);
            }
        };
    }

    private JsonObject StateNode() => new()
    {
        ["model"] = new JsonObject
        {
            ["id"] = _client.Config.Model,
            ["provider"] = _client.Config.ProviderKey,
            ["name"] = _client.Config.DisplayName
        },
        ["isStreaming"] = _session.IsStreaming,
        ["steeringMode"] = ModeName(_session.SteeringMode),
        ["followUpMode"] = ModeName(_session.FollowUpMode),
        ["sessionId"] = _row?.Id,
        ["sessionName"] = _row?.Title,
        ["autoCompactionEnabled"] = _autoCompaction,
        ["autoRetryEnabled"] = _autoRetry,
        ["messageCount"] = _session.Messages.Count,
        ["pendingMessageCount"] = _session.PendingSteering.Count + _session.PendingFollowUp.Count,
        ["contextWindow"] = _contextLimit,
        ["contextTokens"] = ContextCompaction.EstimateContextTokens(_session.Messages)
    };

    private JsonObject StatsNode()
    {
        var usage = new AgentUsage(0, 0);
        var toolCalls = 0;

        foreach (var message in _session.Messages)
        {
            if (message.Usage is { } u)
                usage = usage.Add(u);
            toolCalls += message.ToolCalls?.Count ?? 0;
        }

        var contextTokens = ContextCompaction.EstimateContextTokens(_session.Messages);

        var node = new JsonObject
        {
            ["sessionId"] = _row?.Id,
            ["sessionName"] = _row?.Title,
            ["userMessages"] = _session.Messages.Count(m => m.Role == "user"),
            ["assistantMessages"] = _session.Messages.Count(m => m.Role == "assistant"),
            ["toolCalls"] = toolCalls,
            ["toolResults"] = _session.Messages.Count(m => m.Role == "tool"),
            ["totalMessages"] = _session.Messages.Count,
            ["tokens"] = new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens,
                ["total"] = usage.TotalTokens
            },
            ["cost"] = Math.Round(usage.Cost, 6)
        };

        if (_contextLimit > 0)
        {
            node["contextUsage"] = new JsonObject
            {
                ["tokens"] = contextTokens,
                ["contextWindow"] = _contextLimit,
                ["percent"] = Math.Min(999, (int)Math.Round(contextTokens * 100.0 / _contextLimit))
            };
        }

        return node;
    }

    private async Task<JsonArray> ModelsNodeAsync()
    {
        var array = new JsonArray();
        try
        {
            foreach (var model in await LlmCatalog.ListCuratedAsync(_client.Config.ProviderKey).ConfigureAwait(false))
            {
                array.Add(new JsonObject
                {
                    ["id"] = model.Id,
                    ["name"] = model.Name,
                    ["provider"] = _client.Config.ProviderKey,
                    ["contextWindow"] = model.ContextTokens,
                    ["cost"] = new JsonObject
                    {
                        ["input"] = model.InputCostPerMillion,
                        ["output"] = model.OutputCostPerMillion
                    }
                });
            }
        }
        catch
        {
            // Offline: report the active model only.
        }

        if (array.Count == 0)
        {
            array.Add(new JsonObject
            {
                ["id"] = _client.Config.Model,
                ["provider"] = _client.Config.ProviderKey,
                ["name"] = _client.Config.DisplayName
            });
        }

        return array;
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private async Task EnsureSessionAsync()
    {
        if (_store is null)
            return;

        await _store.EnsureCreatedAsync().ConfigureAwait(false);

        if (_options.ResumeSessionId is { Length: > 0 } resumeId)
        {
            var found = await _store.FindSessionAsync(resumeId).ConfigureAwait(false);
            if (found is not null)
            {
                _row = found;
                _session.Messages.AddRange(await LoadBranchAsync(found.Id).ConfigureAwait(false));
                _persistedCount = _session.Messages.Count;
                return;
            }
        }

        _row = await _store.CreateSessionAsync("rpc session").ConfigureAwait(false);
    }

    /// <summary>Reads the active branch, importing a flat session on first open.</summary>
    private async Task<List<ChatMessage>> LoadBranchAsync(string sessionId)
    {
        if (_tree is null || _store is null)
            return _store is null ? [] : await _store.LoadMessagesAsync(sessionId).ConfigureAwait(false);

        await _tree.ImportFlatAsync(sessionId, _store.Messages).ConfigureAwait(false);
        return await _tree.LoadBranchAsync(sessionId).ConfigureAwait(false);
    }

    private async Task PersistAsync(ChatMessage message)
    {
        if (_row is null)
            return;

        if (_tree is not null)
        {
            await _tree.AppendAsync(_row.Id, message, _store?.PersistFilter).ConfigureAwait(false);
            return;
        }

        if (_store is not null)
            await _store.AppendMessageAsync(_row.Id, message).ConfigureAwait(false);
    }

    private async Task PersistRunAsync()
    {
        if (_store is null || _row is null)
            return;

        try
        {
            for (var i = Math.Max(0, _persistedCount); i < _session.Messages.Count; i++)
            {
                var message = _session.Messages[i];
                if (message.Role is "assistant" or "tool")
                    await PersistAsync(message).ConfigureAwait(false);
            }

            _persistedCount = _session.Messages.Count;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rpc: failed to persist session: {ex.Message}");
        }
    }

    private int _persistedCount;

    private async Task RefreshContextLimitAsync()
    {
        try
        {
            var info = await LlmCatalog
                .GetModelInfoAsync(_client.Config.ProviderKey, _client.Config.Model)
                .ConfigureAwait(false);

            if (info is not null)
                _contextLimit = (int)Math.Min(int.MaxValue, info.ContextTokens);
        }
        catch
        {
            // offline: compaction stays off
        }
    }

    // ------------------------------------------------------------------
    // Framing
    // ------------------------------------------------------------------

    private Task RespondAsync(StreamWriter stdout, string? id, string command, JsonObject? data) =>
        WriteAsync(stdout, new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["command"] = command,
            ["success"] = true,
            ["data"] = data
        });

    private Task FailAsync(StreamWriter stdout, string? id, string command, string error) =>
        WriteAsync(stdout, new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["command"] = command,
            ["success"] = false,
            ["error"] = error
        });

    private async Task WriteAsync(StreamWriter stdout, JsonObject record) =>
        await WriteRawAsync(stdout, record.ToJsonString(WriteOptions)).ConfigureAwait(false);

    /// <summary>
    /// Serializes writes so a response and an event never interleave mid-line.
    /// </summary>
    private async Task WriteRawAsync(StreamWriter stdout, string json)
    {
        await _stdoutGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await stdout.WriteAsync(json.ReplaceLineEndings("\n")).ConfigureAwait(false);
            await stdout.WriteAsync('\n').ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _stdoutGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static string Text(JsonObject command, string field = "message") =>
        command[field]?.GetValue<string>() ?? "";

    private static bool Bool(JsonObject command, string field, bool fallback) =>
        command[field] is JsonNode node && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? node.GetValue<bool>()
            : fallback;

    private static AgentQueueMode? ParseMode(JsonObject command) =>
        command["mode"]?.GetValue<string>() switch
        {
            "all" => AgentQueueMode.All,
            "one-at-a-time" => AgentQueueMode.OneAtATime,
            _ => null
        };

    private static string ModeName(AgentQueueMode mode) =>
        mode == AgentQueueMode.All ? "all" : "one-at-a-time";

    private static IReadOnlyList<AgentImage>? Images(JsonObject command)
    {
        if (command["images"] is not JsonArray array || array.Count == 0)
            return null;

        var images = new List<AgentImage>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject image)
                continue;

            var data = image["data"]?.GetValue<string>();
            var mime = image["mimeType"]?.GetValue<string>() ?? "image/png";
            if (data is { Length: > 0 })
                images.Add(new AgentImage(mime, data));
        }

        return images.Count > 0 ? images : null;
    }
}

/// <summary>JSON shape for <see cref="ChatMessage"/> on the RPC wire.</summary>
public static class RpcMessageJson
{
    public static JsonObject ToRpcNode(this ChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (message.Name is { Length: > 0 })
            node["name"] = message.Name;

        if (message.ToolCallId is { Length: > 0 })
            node["toolCallId"] = message.ToolCallId;

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            var array = new JsonArray();
            foreach (var call in calls)
            {
                array.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments
                });
            }
            node["toolCalls"] = array;
        }

        if (message.Usage is { } usage)
        {
            node["usage"] = new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens,
                ["totalTokens"] = usage.TotalTokens
            };
        }

        return node;
    }
}
#endif
