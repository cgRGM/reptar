#if !NETWASM
using System.Text.RegularExpressions;
using static Reptar.ReptarApi;

namespace Reptar.AI;

/// <summary>
/// Chooses which of the app's registered AI tools the agent may call. Ported from pi's
/// <c>tools</c> / <c>excludeTools</c> / <c>noTools</c> options, including the wildcard and
/// <c>+name</c> / <c>-name</c> modifier syntax.
/// </summary>
/// <remarks>
/// Three shapes, and the differences matter:
/// <list type="bullet">
/// <item><see cref="Only"/> — an allowlist. Nothing outside it is callable.</item>
/// <item><see cref="AllExcept"/> — a denylist applied to everything.</item>
/// <item>
/// <see cref="Adjusting"/> — a list of only <c>+name</c>/<c>-name</c> entries. This is <em>not</em>
/// an allowlist; it edits the default selection. Mixing bare names with modifiers is an error,
/// because "add this one" and "only these" are contradictory instructions.
/// </item>
/// </list>
/// </remarks>
public sealed record ToolSelection
{
    /// <summary>Allowlist patterns. Null means "everything the app registered".</summary>
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>Denylist patterns, applied after <see cref="Include"/>.</summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary><c>+name</c> / <c>-name</c> edits against the default selection.</summary>
    public IReadOnlyList<string>? Adjust { get; init; }

    /// <summary>Start with nothing enabled.</summary>
    public bool None { get; init; }

    /// <summary>Everything the app registered. The default.</summary>
    public static ToolSelection Default { get; } = new();

    /// <summary>Only the named tools (wildcards allowed).</summary>
    public static ToolSelection Only(params string[] names) => new() { Include = names };

    /// <summary>Everything except the named tools.</summary>
    public static ToolSelection AllExcept(params string[] names) => new() { Exclude = names };

    /// <summary>Add and remove tools relative to the default selection.</summary>
    public static ToolSelection Adjusting(params string[] modifiers) => new() { Adjust = modifiers };

    /// <summary>No tools at all — for a pure chat agent.</summary>
    public static ToolSelection NoTools { get; } = new() { None = true };

    /// <summary>Read-only subset, for an agent that may inspect but not mutate.</summary>
    public static ToolSelection ReadOnly { get; } =
        new() { Include = ["read_file", "list_dir", "run_command"] };

    /// <summary>
    /// Applies this selection to the tools an app exposes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selection mixes bare names with <c>+</c>/<c>-</c> modifiers, which is ambiguous.
    /// </exception>
    public IReadOnlyList<AiToolDefinition> Apply(IReadOnlyList<AiToolDefinition> available)
    {
        if (None)
            return [];

        var result = available.ToList();

        if (Adjust is { Count: > 0 } modifiers)
        {
            if (Include is { Count: > 0 })
            {
                throw new ArgumentException(
                    "Tool selection mixes an allowlist with +name/-name modifiers. Use one or the other: "
                    + "an allowlist says 'only these', modifiers say 'adjust the default'.",
                    nameof(available));
            }

            foreach (var modifier in modifiers)
            {
                if (modifier.Length < 2 || (modifier[0] != '+' && modifier[0] != '-'))
                {
                    throw new ArgumentException(
                        $"Tool modifier '{modifier}' must start with '+' or '-'.", nameof(available));
                }

                var name = modifier[1..];
                var matches = result.Where(tool => Matches(name, tool.Name)).ToList();

                if (modifier[0] == '-')
                    result.RemoveAll(matches.Contains);
                else
                    result.AddRange(available.Where(tool => Matches(name, tool.Name) && !result.Contains(tool)));
            }
        }

        if (Include is { Count: > 0 } include)
            result = result.Where(tool => include.Any(pattern => Matches(pattern, tool.Name))).ToList();

        if (Exclude is { Count: > 0 } exclude)
            result = result.Where(tool => !exclude.Any(pattern => Matches(pattern, tool.Name))).ToList();

        return result;
    }

    /// <summary>Glob match where <c>*</c> matches any run of characters.</summary>
    private static bool Matches(string pattern, string name)
    {
        if (pattern == "*")
            return true;

        if (!pattern.Contains('*'))
            return string.Equals(pattern, name, StringComparison.Ordinal);

        // Escape first so regex metacharacters in a tool name are literal, then turn the
        // escaped wildcard into a match-anything group.
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}

/// <summary>Token, cost, and context accounting for one agent session.</summary>
public sealed record AgentSessionStats(
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolResults,
    int TotalMessages,
    AgentUsage Usage,
    int ContextTokens,
    int ContextWindow)
{
    /// <summary>Context fill percentage, or 0 when the window is unknown.</summary>
    public int ContextPercent =>
        ContextWindow <= 0 ? 0 : (int)Math.Round(ContextTokens * 100.0 / ContextWindow);

    /// <summary>Builds stats from a transcript and a known context window.</summary>
    public static AgentSessionStats From(
        IReadOnlyList<ChatMessage> messages,
        int contextWindow = 0)
    {
        var usage = new AgentUsage(0, 0);
        var toolCalls = 0;

        foreach (var message in messages)
        {
            if (message.Usage is { } messageUsage)
                usage = usage.Add(messageUsage);
            toolCalls += message.ToolCalls?.Count ?? 0;
        }

        return new AgentSessionStats(
            messages.Count(m => m.Role == AgentRoles.User),
            messages.Count(m => m.Role == AgentRoles.Assistant),
            toolCalls,
            messages.Count(m => m.Role == AgentRoles.Tool),
            messages.Count,
            usage,
            ContextCompaction.EstimateContextTokens(messages),
            contextWindow);
    }
}

/// <summary>
/// Default tables for SDK-managed sessions. Defined here rather than in the example app so the
/// SDK is self-contained; pass your own <see cref="AgentSessionStore"/> to
/// <see cref="ReptarAgent.Attach"/> to reuse existing tables.
/// </summary>
public static class ReptarAgentSchema
{
    public static readonly TableDef Sessions = DefineTable("agent_sessions",
        Col.Text("id").PrimaryKey(),
        Col.Text("title").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Messages = DefineTable("agent_messages",
        Col.Text("id").PrimaryKey(),
        Col.Text("session_id").NotNull().Indexed(),
        Col.Text("role").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("tool_calls"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef[] All = [Sessions!, Messages!];
}

/// <summary>Options for <see cref="ReptarAgent.CreateAsync"/>.</summary>
public sealed record ReptarAgentOptions
{
    /// <summary>Workspace root for the mounted file and shell tools.</summary>
    public string WorkspaceRoot { get; init; } = ".";

    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? BaseUrl { get; init; }

    /// <summary>Replaces the built-in coding-agent prompt when set.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Additional prompt text appended after the built-in prompt.</summary>
    public string? AppendSystemPrompt { get; init; }

    public int MaxSteps { get; init; } = 24;

    /// <summary>Which tools the agent may call.</summary>
    public ToolSelection Tools { get; init; } = ToolSelection.Default;

    /// <summary>Extra tools to register alongside the app's own.</summary>
    public IReadOnlyList<AiToolDefinition>? AdditionalTools { get; init; }

    /// <summary>Auto-compaction thresholds. <see cref="CompactionSettings.Disabled"/> turns it off.</summary>
    public CompactionSettings Compaction { get; init; } = CompactionSettings.Default;

    /// <summary>Retry policy for transient provider failures.</summary>
    public AgentRetryPolicy Retry { get; init; } = new();

    /// <summary>Enable the shell tool. Off by default: a library user should opt in.</summary>
    public bool AllowShell { get; init; }

    /// <summary>Resume an existing session id instead of starting a new one.</summary>
    public string? SessionId { get; init; }

    /// <summary>Persist the transcript to SQLite. Off means in-memory only.</summary>
    public bool Persist { get; init; } = true;

    /// <summary>SQLite file for persistence. Defaults to <c>&lt;workspace&gt;/.reptar/agent.db</c>.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Override the model's context window instead of looking it up.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>
    /// Aliases consulted when <see cref="Model"/> is not a <c>provider/model</c> spec. Pass
    /// <see cref="ModelAliases.Merge"/> to layer project aliases over the defaults.
    /// </summary>
    public IReadOnlyList<ModelAlias>? Aliases { get; init; }

    /// <summary>MCP servers whose tools should be mounted alongside the workspace tools.</summary>
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>Receives a warning per MCP server that failed to connect.</summary>
    public Action<string>? OnWarning { get; init; }

    /// <summary>
    /// Send one throwaway request so the provider caches the system prompt and tool definitions
    /// before the first real turn. Only happens when the prefix is large enough to pay for itself.
    /// </summary>
    public bool WarmPromptCache { get; init; } = true;

    /// <summary>Load <c>.reptar/prompts/*.md</c> so <c>/name</c> expands in <see cref="ReptarAgent.ExpandPrompt"/>.</summary>
    public bool LoadPromptTemplates { get; init; } = true;

    /// <summary>
    /// Project-local resources (templates, settings) load only when the workspace is trusted.
    /// Null skips the check, which is the right default for a library caller that already
    /// decided the directory is safe.
    /// </summary>
    public ProjectTrustStore? Trust { get; init; }
}

/// <summary>
/// A ready-to-use coding agent. This is the C# equivalent of pi's <c>createAgentSession</c>:
/// it resolves the provider, mounts tools, opens a session, and wires compaction and retry,
/// so callers do not have to assemble the loop by hand.
/// </summary>
/// <remarks>
/// <code>
/// await using var agent = await ReptarAgent.CreateAsync(new ReptarAgentOptions
/// {
///     WorkspaceRoot = "/path/to/project",
///     AllowShell = true
/// });
///
/// var result = await agent.PromptAsync("fix the failing test", onText: Console.Write);
/// Console.WriteLine(result.Summary.StopReason);
/// </code>
/// </remarks>
public sealed class ReptarAgent : IAsyncDisposable
{
    private readonly ReptarApp _app;
    private readonly AgentSessionStore? _store;
    private AgentSessionRow? _row;
    private readonly ToolSelection _selection;
    private readonly IReadOnlyList<AiToolDefinition> _tools;
    private readonly ILlmClient _client;
    private readonly ReptarAgentOptions _options;
    private readonly int _contextWindow;
    private McpToolSource? _mcp;

    private ReptarAgent(
        ReptarApp app,
        AgentSessionStore? store,
        AgentSessionRow? row,
        ToolSelection selection,
        IReadOnlyList<AiToolDefinition> tools,
        ILlmClient client,
        ReptarAgentOptions options,
        int contextWindow,
        IReadOnlyList<PromptTemplate>? templates = null,
        McpToolSource? mcp = null)
    {
        Templates = templates ?? [];
        _app = app;
        _store = store;
        _row = row;
        _selection = selection;
        _tools = tools;
        _client = client;
        _options = options;
        _contextWindow = contextWindow;
        _mcp = mcp;
    }

    /// <summary>Prompt templates discovered from the workspace and the user config.</summary>
    public IReadOnlyList<PromptTemplate> Templates { get; }

    /// <summary>The live transcript, including tool results.</summary>
    public List<ChatMessage> Messages { get; private set; } = [];

    /// <summary>The resolved provider configuration.</summary>
    public LlmConfig Config => _client.Config;

    /// <summary>Tools this agent may call, after selection.</summary>
    public IReadOnlyList<AiToolDefinition> Tools => _tools;

    /// <summary>Session id when persisting, otherwise null.</summary>
    public string? SessionId => _row?.Id;

    /// <summary>True while a run is in flight.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Context window used for compaction decisions, or 0 when unknown.</summary>
    public int ContextWindow => _contextWindow;

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds an agent: resolves the provider, mounts workspace tools, opens or resumes a
    /// session, and resolves the tool selection.
    /// </summary>
    public static async Task<ReptarAgent> CreateAsync(
        ReptarAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.WorkspaceRoot);
        Directory.CreateDirectory(root);

        var dbPath = options.DatabasePath ?? Path.Combine(root, ".reptar", "agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var app = ReptarApp.Create(new ReptarConfig { Database = Db.Sqlite(dbPath) });

        app.UseWorkspaceTools(root, workspace => workspace.AllowRunCommand = options.AllowShell);

        // Aliases resolve before the environment so `--model fast` works.
        var client = options.Model is { Length: > 0 } spec && options.Provider is null
            ? LlmClientFactory.Create(ModelAliases.Resolve(spec, options.Aliases))
            : LlmClientFactory.Create(
                LlmConfig.FromEnvironment(options.Provider, options.Model, options.BaseUrl));

        // Project-local resources stay off until the directory is trusted.
        var trusted = options.Trust is null
            || await options.Trust.ResolveAsync(root, ask: null, cancellationToken).ConfigureAwait(false);

        var templates = options.LoadPromptTemplates && trusted
            ? PromptTemplates.Load(root)
            : [];

        var available = app.GetAiTools(onlyExplicit: true).ToList();
        if (options.AdditionalTools is { Count: > 0 } extra)
            available.AddRange(extra);

        McpToolSource? mcp = null;
        if (options.McpServers.Count > 0)
        {
            mcp = await McpToolSource
                .ConnectAsync(options.McpServers, options.OnWarning, cancellationToken)
                .ConfigureAwait(false);
            available.AddRange(mcp.Mount(app));
        }

        var tools = options.Tools.Apply(available);

        AgentSessionStore? store = null;
        AgentSessionRow? row = null;

        if (options.Persist)
        {
            store = new AgentSessionStore(app.Db, ReptarAgentSchema.Sessions, ReptarAgentSchema.Messages);
            await store.EnsureCreatedAsync().ConfigureAwait(false);

            if (options.SessionId is { Length: > 0 } resumeId)
                row = await store.FindSessionAsync(resumeId).ConfigureAwait(false);

            row ??= await store.CreateSessionAsync(options.SystemPrompt is { Length: > 0 } prompt
                ? Truncate(prompt, 60)
                : "agent session").ConfigureAwait(false);
        }

        // Identify the app to providers: OpenRouter only credits an application in its rankings
        // when the attribution headers are present.
        if (client.Config.Attribution is null)
            client = LlmClientFactory.Create(client.Config with { Attribution = ClientIdentity.Reptar });

        var contextWindow = options.ContextWindow ?? 0;
        var maxOutput = 0;

        if (contextWindow <= 0 || maxOutput <= 0)
        {
            try
            {
                var info = await LlmCatalog
                    .GetModelInfoAsync(client.Config.ProviderKey, client.Config.Model)
                    .ConfigureAwait(false);

                if (info is not null)
                {
                    if (contextWindow <= 0)
                        contextWindow = (int)Math.Min(int.MaxValue, info.ContextTokens);
                    maxOutput = info.MaxOutputTokens;
                }
            }
            catch
            {
                // offline: compaction and the output clamp stay off
            }
        }

        // Thread the real limits into the client: the window clamps max_tokens to the space left,
        // and the output ceiling bounds the thinking budget.
        if ((contextWindow > 0 && client.Config.ContextWindow != contextWindow) ||
            (maxOutput > 0 && client.Config.MaxOutputTokens != maxOutput))
        {
            client = LlmClientFactory.Create(client.Config with
            {
                ContextWindow = contextWindow > 0 ? contextWindow : client.Config.ContextWindow,
                MaxOutputTokens = maxOutput > 0 ? maxOutput : client.Config.MaxOutputTokens
            });
        }

        var agent = new ReptarAgent(
            app, store, row, options.Tools, tools, client, options, contextWindow, templates, mcp);

        // Warm the provider's prompt cache before the first real turn. Costs one cheap request
        // and saves the full prefix on every turn after it.
        if (options.WarmPromptCache)
            _ = CacheWarmer.WarmAsync(client, agent.SystemPrompt(), tools, cancellationToken);

        if (store is not null && row is not null)
        {
            agent.Messages = await store.LoadMessagesAsync(row.Id).ConfigureAwait(false);

            if (agent.Messages.Count > 0 && options.SystemPrompt is null)
                agent.Messages.Insert(0, new ChatMessage(AgentRoles.System, agent.SystemPrompt()));
        }

        return agent;
    }

    /// <summary>
    /// Creates an agent over an app the caller already configured. Use this when you have
    /// custom routes, auth, or tool mounts and do not want the SDK to build a fresh app.
    /// </summary>
    public static ReptarAgent Attach(
        ReptarApp app,
        ReptarAgentOptions options,
        ILlmClient? client = null,
        AgentSessionStore? store = null)
    {
        var resolvedClient = client ?? LlmClientFactory.Create(
            LlmConfig.FromEnvironment(options.Provider, options.Model, options.BaseUrl));

        var available = app.GetAiTools(onlyExplicit: true).ToList();
        if (options.AdditionalTools is { Count: > 0 } extra)
            available.AddRange(extra);

        return new ReptarAgent(
            app,
            store,
            null,
            options.Tools,
            options.Tools.Apply(available),
            resolvedClient,
            options,
            options.ContextWindow ?? 0);
    }

    // ------------------------------------------------------------------
    // Prompting
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends a prompt and runs to settlement. Assistant text streams to
    /// <paramref name="onText"/>; every event also goes to <paramref name="onEvent"/>.
    /// </summary>
    public async Task<AgentRunSummary> PromptAsync(
        string message,
        Action<string>? onText = null,
        Action<AgentEvent>? onEvent = null,
        IReadOnlyList<AgentImage>? images = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = new ChatMessage(AgentRoles.User, message, Images: images);
        Messages.Add(userMessage);

        if (_store is not null && _row is not null)
            await _store.AppendMessageAsync(_row.Id, userMessage).ConfigureAwait(false);

        Action<AgentEvent> sink = evt =>
        {
            if (evt is AgentEvent.MessageUpdate { Kind: AgentContentKind.Text } delta)
                onText?.Invoke(delta.Delta);
            onEvent?.Invoke(evt);
        };

        IsStreaming = true;
        try
        {
            var summary = await AgentLoop.RunAsync(
                Messages,
                BuildConfig(),
                _client.StreamAsync,
                _tools,
                sink,
                cancellationToken).ConfigureAwait(false);

            await PersistAsync().ConfigureAwait(false);
            return summary;
        }
        finally
        {
            IsStreaming = false;
        }
    }

    /// <summary>
    /// Expands a <c>/name args</c> prompt template. Returns null when the input is not a known
    /// template, so a caller can fall through to its own slash-command handling.
    /// </summary>
    public string? ExpandPrompt(string input) => PromptTemplates.TryExpand(Templates, input);

    /// <summary>Queues a message delivered after the current turn's tool calls.</summary>
    public void Steer(string message) => _steering.Enqueue(new ChatMessage(AgentRoles.User, message));

    /// <summary>Queues a message delivered once the agent would otherwise stop.</summary>
    public void FollowUp(string message) => _followUp.Enqueue(new ChatMessage(AgentRoles.User, message));

    private readonly Queue<ChatMessage> _steering = new();
    private readonly Queue<ChatMessage> _followUp = new();

    // ------------------------------------------------------------------
    // Context and stats
    // ------------------------------------------------------------------

    /// <summary>Current token, cost, and context accounting.</summary>
    public AgentSessionStats Stats() => AgentSessionStats.From(Messages, _contextWindow);

    /// <summary>
    /// Compacts the transcript now. Returns null when there is nothing worth compacting or the
    /// summary failed.
    /// </summary>
    public async Task<CompactionResult?> CompactAsync(
        string? instructions = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ContextCompactor.CompactAsync(
            Messages,
            _client,
            _options.Compaction,
            CompactionReason.Manual,
            customInstructions: instructions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome is null)
            return null;

        var (result, projection) = outcome.Value;
        Messages = projection;

        if (_store is not null && _row is not null)
        {
            for (var i = 0; i < Messages.Count; i++)
                await _store.AppendMessageAsync(_row.Id, Messages[i]).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Starts a fresh transcript, keeping the same provider and tools.</summary>
    public async Task NewSessionAsync(string title = "agent session", CancellationToken cancellationToken = default)
    {
        Messages.Clear();

        if (_store is not null)
        {
            var previous = _row;
            _row = await _store.CreateSessionAsync(title).ConfigureAwait(false);
            _persistedCount = 0;

            // Keep the old transcript around rather than deleting it: a caller may still want to
            // export or inspect it, and storage is cheap.
            _ = previous;
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private AgentLoopConfig BuildConfig()
    {
        var settings = _options.Compaction;

        return new AgentLoopConfig
        {
            Model = _client.Config.Model,
            MaxSteps = _options.MaxSteps,
            ToolExecutor = (invocation, _) =>
                _app.ExecuteAiToolAsync(invocation.ToolName, invocation.ArgumentsJson),
            SequentialTools = new HashSet<string>(StringComparer.Ordinal) { "run_command" },
            Retry = _options.Retry,
            GetSteeringMessages = () => Drain(_steering),
            GetFollowUpMessages = () => Drain(_followUp),
            PrepareNextTurn = async (turn, ct) =>
            {
                if (_contextWindow <= 0 || !settings.Enabled)
                    return null;

                var tokens = ContextCompaction.EstimateContextTokens(turn.History);
                if (!ContextCompaction.ShouldCompact(tokens, _contextWindow, settings))
                    return null;

                var outcome = await ContextCompactor.CompactAsync(
                    turn.History, _client, settings, CompactionReason.Threshold, cancellationToken: ct)
                    .ConfigureAwait(false);

                return outcome is null ? null : new AgentNextTurn(ContextMessages: outcome.Value.Projection);
            }
        };
    }

    private static IReadOnlyList<ChatMessage> Drain(Queue<ChatMessage> queue)
    {
        if (queue.Count == 0)
            return [];
        return [queue.Dequeue()];
    }

    private async Task PersistAsync()
    {
        if (_store is null || _row is null)
            return;

        for (var i = Math.Max(0, _persistedCount); i < Messages.Count; i++)
        {
            var message = Messages[i];
            if (message.Role is AgentRoles.Assistant or AgentRoles.Tool)
                await _store.AppendMessageAsync(_row.Id, message).ConfigureAwait(false);
        }

        _persistedCount = Messages.Count;
    }

    private int _persistedCount;

    internal string SystemPrompt()
    {
        if (_options.SystemPrompt is { Length: > 0 } custom)
            return custom;

        var prompt =
            "You are a coding agent embedded in the Reptar framework.\n\n"
            + $"Working directory: {Path.GetFullPath(_options.WorkspaceRoot)}\n\n"
            + "Rules:\n"
            + "- Use the workspace tools to inspect and change files.\n"
            + "- Read a file before editing it. Prefer edit_file with a unique target string over rewriting whole files.\n"
            + "- Run commands when a build or test can verify your work.\n"
            + "- Paths are relative to the working directory; never touch files outside it.\n"
            + "- Answer concisely in Markdown. Show the commands you ran when you changed something.";

        if (_options.AppendSystemPrompt is { Length: > 0 } append)
            prompt += "\n\n" + append;

        return prompt;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        _steering.Clear();
        _followUp.Clear();

        if (_mcp is not null)
        {
            await _mcp.DisposeAsync().ConfigureAwait(false);
            _mcp = null;
        }
    }
}
#endif
