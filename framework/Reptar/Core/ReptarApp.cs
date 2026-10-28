using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Reptar;

public sealed class RouteMeta
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Type? InputType { get; internal set; }
    public Type? QueryType { get; internal set; }
    public List<(int Status, Type? Type)> Produces { get; } = new();
    public string? Name { get; internal set; }
    public string? Summary { get; internal set; }
    public string? Description { get; internal set; }
    public List<string> Tags { get; } = new();
    public object? Validator { get; internal set; }
    public bool IsAiTool { get; internal set; }
    public string? AiToolName { get; internal set; }
    public string? AiToolDescription { get; internal set; }

    /// <summary>
    /// Explicit JSON Schema for this tool's arguments. Set by
    /// <see cref="RouteBuilder.WithAiToolSchema"/> for routes that parse their own body, which
    /// otherwise expose an empty parameter list and leave the model guessing names and types.
    /// </summary>
    public Dictionary<string, object?>? AiToolParameters { get; internal set; }
    public string? FileFieldName { get; internal set; }
    public string? FileDescription { get; internal set; }
    public bool FileRequired { get; internal set; } = true;
    public long? MaxBodyBytes { get; internal set; }
    public bool StreamingBody { get; internal set; }

    internal RouteMeta CloneWithPath(string path)
    {
        var clone = new RouteMeta
        {
            Method = Method,
            Path = path,
            InputType = InputType,
            QueryType = QueryType,
            Name = Name,
            Summary = Summary,
            AiToolParameters = AiToolParameters,
            Description = Description,
            Validator = Validator,
            IsAiTool = IsAiTool,
            AiToolName = AiToolName,
            AiToolDescription = AiToolDescription,
            FileFieldName = FileFieldName,
            FileDescription = FileDescription,
            FileRequired = FileRequired,
            MaxBodyBytes = MaxBodyBytes,
            StreamingBody = StreamingBody,
        };

        clone.Produces.AddRange(Produces);
        clone.Tags.AddRange(Tags);
        return clone;
    }
}

internal sealed class RouteEntry
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string[] Segments { get; init; }
    public required bool HasParams { get; init; }
    public required bool[] IsParamSegment { get; init; }
    public required string[] ParamNames { get; init; }
    public required int[] ParamIndices { get; init; }
    public bool IsCatchAll { get; init; }
    public string? CatchAllName { get; init; }
    public int CatchAllIndex { get; init; } = -1;
    public ReptarMiddleware? Middleware { get; init; }
    public required Func<Ctx, Task<ReptarResponse>> Handler { get; init; }
    public required RouteMeta Meta { get; init; }
}

internal sealed record WebSocketRoute(
    string[] Segments,
    bool[] IsParamSegment,
    string[] ParamNames,
    int[] ParamIndices,
    WebSocketHandlers Handlers,
    bool IsCatchAll = false,
    int CatchAllIndex = -1);

public sealed record QueueMessage(
    string Id,
    string Body,
    string Timestamp,
    int Attempts);

public sealed record QueueBatch(
    string BatchId,
    string Queue,
    List<QueueMessage> Messages);

public sealed record QueueBatchResult(
    bool Success,
    string[] FailedMessageIds,
    List<QueueRetry>? Retries = null);

public sealed record ScheduledEvent(
    string Cron,
    string ScheduledTime);

public sealed record ScheduledResult(
    bool Success,
    string[] Errors);

public sealed record ReptarHandledResponse(
    ReptarResponse Response,
    Task Background);

public sealed class ReptarApp
{
    private static readonly Dictionary<string, string> EmptyParams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RouteEntry> _routes = new();
    private readonly Dictionary<string, RouteEntry> _staticGet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouteEntry> _staticPost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouteEntry> _staticPut = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouteEntry> _staticDelete = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouteEntry> _staticPatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouteEntry> _staticOther = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Method, int SegmentCount), List<RouteEntry>> _paramRoutes = new();
    private readonly Dictionary<string, List<RouteEntry>> _catchAllRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReptarMiddleware> _middlewares = new();
    private readonly Dictionary<string, Func<Ctx, QueueMessage, Task>> _queueHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<Ctx, ScheduledEvent, Task>> _scheduledHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WebSocketHandlers> _webSocketHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WebSocketRoute> _webSocketParamRoutes = [];
    private readonly List<WebSocketRoute> _webSocketCatchAllRoutes = [];
    private Func<Ctx, Task>? _alarmHandler;
    // WebSocket sessions are touched concurrently by the host's request
    // threads (open/message/close), so this must be the concurrent dictionary —
    // a plain Dictionary corrupts under simultaneous opens.
    private readonly ConcurrentDictionary<string, (WebSocketConnection Connection, WebSocketHandlers Handlers)> _webSocketSessions =
        new(StringComparer.Ordinal);
    private readonly ReptarConfig _config;
    private readonly (ReptarApp Parent, string Prefix)? _groupParent;
    private readonly List<IReptarModule> _modules = [];
    private ServiceSet? _services;

    internal ReptarApp(ReptarConfig config, (ReptarApp Parent, string Prefix)? groupParent = null)
    {
        _config = config;
        _groupParent = groupParent;
    }

    public ReptarConfig Config => _config;

    public void SetWebSocketTransport(IWebSocketTransport transport)
    {
        _config.WebSocket = transport;
        _services = null;
    }

    /// <summary>
    /// Swaps the mail transport and rebuilds the cached service set on the
    /// next request. <see cref="ReptarTestClient"/> uses this to install an
    /// in-memory sink automatically; dev tools can use it at runtime.
    /// </summary>
    public void SetMailTransport(IMailClient transport)
    {
        _config.Mail = transport;
        _services = null;
    }

    public static ReptarApp Create(ReptarConfig? config = null)
    {
        var resolved = config ?? new ReptarConfig();

        if (resolved.Auth is not null)
            Auth.Configure(resolved.Auth);

        return new ReptarApp(resolved);
    }

    public List<TableDef> Tables { get; } = new();

    public MetricsRegistry Metrics { get; } = new();

    public IReadOnlyList<RouteMeta> RouteMetadata =>
        _routes.Select(route => route.Meta).ToList();

    public ReptarApp Use(ReptarMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    public IPolicyManager Policies { get; } = new PolicyManager();

    /// <summary>
    /// Registers a declarative resource authorization policy.
    /// Inspired by AdonisJS Bouncer and Laravel Gates.
    /// </summary>
    public ReptarApp DefinePolicy<TResource>(string action, Func<SessionUser?, TResource, bool> policy)
    {
        Policies.Define(action, policy);
        return this;
    }

    /// <summary>
    /// Evaluates whether the given user can perform the specified action on a resource.
    /// </summary>
    public bool Can<TResource>(SessionUser? user, string action, TResource resource) =>
        Policies.Can(user, action, resource);

    /// <summary>
    /// Creates an in-memory HTTP test client that executes requests directly through the
    /// route and middleware pipeline without binding network sockets.
    /// </summary>
    public ReptarTestClient CreateTestClient() => new(this);

    /// <summary>
    /// Enables standard perimeter security headers (CSP, HSTS, X-Content-Type-Options, etc.).
    /// Works universally across both Native AOT and NetWasm Cloudflare Workers.
    /// </summary>
    public ReptarApp UseSecurityHeaders(SecurityHeadersOptions? options = null) =>
        Use(Middlewares.SecurityHeaders(options));

    public ReptarApp UseSecurityHeaders(Action<SecurityHeadersOptions> configure)
    {
        var opts = new SecurityHeadersOptions();
        configure(opts);
        return Use(Middlewares.SecurityHeaders(opts));
    }

    /// <summary>
    /// Rejects requests with 413 Payload Too Large if the body exceeds <paramref name="maxBytes"/>.
    /// </summary>
    public ReptarApp UseBodyLimit(long maxBytes) => Use(Middlewares.BodyLimit(maxBytes));

    public ReptarApp UseETag(ETagOptions? options = null) =>
        Use(Middlewares.ETag(options));

    public ReptarApp UseTelemetry(TelemetryOptions? options = null) =>
        Use(Middlewares.Telemetry(options));

    /// <summary>
    /// Adds stale-while-revalidate caching. Cache outcomes are reported to the
    /// app's metrics registry by default.
    /// </summary>
    public ReptarApp UseSWR(
        int ttlSeconds = 15,
        int swrSeconds = 30,
        Action<SwrOptions>? configure = null) =>
        Use(Middlewares.SWR(
            ttlSeconds,
            swrSeconds,
            options =>
            {
                options.Metrics ??= Metrics;
                configure?.Invoke(options);
            }));

    /// <summary>
    /// Adds an application module. Configure runs immediately; Start/Stop run
    /// from hosts that support a lifecycle (Kestrel/Lambda).
    /// </summary>
    public ReptarApp AddModule(IReptarModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Configure(this);
        _modules.Add(module);
        return this;
    }

    public async Task StartModulesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var module in _modules)
            await module.StartAsync(this, cancellationToken);
    }

    public async Task StopModulesAsync(CancellationToken cancellationToken = default)
    {
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            try
            {
                await _modules[index].StopAsync(this, cancellationToken);
            }
            catch (Exception ex)
            {
                _services?.Log.Error($"Module stop failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops modules (best effort) and disposes the configured database client.
    /// Kestrel/Lambda call this on host shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopModulesAsync();

        if (_services?.Db is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_services?.Db is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _services = null;
    }

    public void RegisterSchema(params TableDef[] tables) => Tables.AddRange(tables);

    public string ExportSchemaJson() => SchemaRegistry.ExportJson(Tables);

    public IDbClient Db => Services.Db;
    public IDbClient Database => Services.Db;
    public IQueueClient Queues => Services.Queues;

    /// <summary>Queue names with registered <see cref="OnQueue"/> consumers.</summary>
    public IReadOnlyList<string> QueueNames =>
        [.. _queueHandlers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Cron expressions registered through <see cref="OnScheduled"/>.</summary>
    public IReadOnlyList<string> CronExpressions =>
        [.. _scheduledHandlers.Keys.OrderBy(cron => cron, StringComparer.Ordinal)];

    public Task EnsureCreatedAsync() =>
        Services.Db.EnsureCreatedAsync(Tables.ToArray());

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        Services.Db.ExecuteAsync(sql, param);

    public RouteBuilder Get(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("GET", path, null, handler);

    public RouteBuilder Get(string path, Func<Ctx, ReptarResponse> handler) =>
        AddRoute("GET", path, null, ctx => Task.FromResult(handler(ctx)));

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("GET", path, middleware, handler);

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, ReptarResponse> handler) =>
        AddRoute("GET", path, middleware, ctx => Task.FromResult(handler(ctx)));

    public RouteBuilder Post(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("POST", path, null, handler);

    public RouteBuilder Post(string path, Func<Ctx, ReptarResponse> handler) =>
        AddRoute("POST", path, null, ctx => Task.FromResult(handler(ctx)));

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("POST", path, middleware, handler);

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, ReptarResponse> handler) =>
        AddRoute("POST", path, middleware, ctx => Task.FromResult(handler(ctx)));

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, null, null, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, middleware, null, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, null, validator, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, middleware, validator, handler);

    public RouteBuilder Put(
        string path,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("PUT", path, null, handler);

    public RouteBuilder Put(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("PUT", path, middleware, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, null, null, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, middleware, null, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, null, validator, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, middleware, validator, handler);

    public RouteBuilder Patch(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("PATCH", path, null, handler);

    public RouteBuilder Patch(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("PATCH", path, middleware, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PATCH", path, null, null, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PATCH", path, middleware, null, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PATCH", path, null, validator, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PATCH", path, middleware, validator, handler);

    public RouteBuilder Delete(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("DELETE", path, null, handler);

    public RouteBuilder Delete(string path, Func<Ctx, ReptarResponse> handler) =>
        AddRoute("DELETE", path, null, ctx => Task.FromResult(handler(ctx)));

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("DELETE", path, middleware, handler);

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, ReptarResponse> handler) =>
        AddRoute("DELETE", path, middleware, ctx => Task.FromResult(handler(ctx)));

    public ReptarApp Group(string basePath, Action<ReptarApp>? configure = null)
    {
        var prefix = Normalize(basePath);
        var groupApp = new ReptarApp(_config, (this, prefix));
        configure?.Invoke(groupApp);
        return groupApp;
    }

    public ReptarApp Route(string basePath, ReptarApp subApp)
    {
        var prefix = Normalize(basePath);

        foreach (var route in subApp._routes)
        {
            var path = Combine(prefix, route.Path);

            var entry = CreateRouteEntry(
                route.Method,
                path,
                route.Middleware,
                route.Handler,
                route.Meta.CloneWithPath(path));

            RegisterRouteEntry(entry);
        }

        Tables.AddRange(subApp.Tables);

        foreach (var (queueName, handler) in subApp._queueHandlers)
        {
            _queueHandlers[queueName] = handler;
        }

        foreach (var (cron, handler) in subApp._scheduledHandlers)
        {
            _scheduledHandlers[cron] = handler;
        }

        foreach (var (path, handlers) in subApp._webSocketHandlers)
        {
            _webSocketHandlers[path] = handlers;
        }

        _webSocketParamRoutes.AddRange(subApp._webSocketParamRoutes);
        _webSocketCatchAllRoutes.AddRange(subApp._webSocketCatchAllRoutes);

        return this;
    }

    public ReptarApp OnQueue(string queueName, Func<Ctx, QueueMessage, Task> handler)
    {
        _queueHandlers[queueName] = handler;
        return this;
    }

    public ReptarApp OnQueue<T>(string queueName, Func<Ctx, QueueMessage, T, Task> handler)
    {
        _queueHandlers[queueName] = async (ctx, msg) =>
        {
            var input = ctx.JsonCodec.Deserialize<T>(msg.Body);
            if (input is not null)
            {
                await handler(ctx, msg, input);
            }
        };
        return this;
    }

    public ReptarApp OnScheduled(
        string cron,
        Func<Ctx, ScheduledEvent, Task> handler)
    {
        _scheduledHandlers[cron] = handler;
        return this;
    }

    /// <summary>
    /// Registers the Durable Object alarm handler. The reserved
    /// <c>/_reptar/alarm</c> path dispatches through the normal request
    /// pipeline, so the Durable Object host's alarm() reuses the existing
    /// fetch export — no additional JS exports (which Binaryen currently
    /// rejects). Native targets can hit the path from any HTTP client.
    /// </summary>
    public ReptarApp OnAlarm(Func<Ctx, Task> handler)
    {
        _alarmHandler = handler;
        return this;
    }

    public ReptarApp OnWebSocket(string path, WebSocketHandlers handlers)
    {
        var normalized = Normalize(path);
        var segments = Split(normalized);
        var isParamSegment = new bool[segments.Length];
        var paramNames = new List<string>();
        var paramIndices = new List<int>();
        var isCatchAll = false;
        var catchAllIndex = -1;

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('*'))
            {
                if (i != segments.Length - 1)
                {
                    throw new ArgumentException(
                        $"Catch-all segment '{segments[i]}' must be the final WebSocket route segment.",
                        nameof(path));
                }

                if (segments[i].Length == 1)
                {
                    throw new ArgumentException(
                        "Catch-all segments must be named, e.g. '/ws/*path'.",
                        nameof(path));
                }

                var catchAllName = segments[i][1..];
                if (!IsValidParamName(catchAllName))
                {
                    throw new ArgumentException(
                        $"Catch-all parameter '{catchAllName}' must be a valid identifier.",
                        nameof(path));
                }

                isCatchAll = true;
                catchAllIndex = i;
                isParamSegment[i] = true;
                paramNames.Add(catchAllName);
                paramIndices.Add(i);
                continue;
            }

            if (TryParseParamSegment(segments[i], out var name))
            {
                isParamSegment[i] = true;
                paramNames.Add(name);
                paramIndices.Add(i);
            }
        }

        var route = new WebSocketRoute(
            segments,
            isParamSegment,
            [.. paramNames],
            [.. paramIndices],
            handlers,
            isCatchAll,
            catchAllIndex);

        if (isCatchAll)
        {
            _webSocketCatchAllRoutes.Add(route);
        }
        else if (paramNames.Count > 0)
        {
            _webSocketParamRoutes.Add(route);
        }
        else
        {
            _webSocketHandlers[normalized] = handlers;
        }

        return this;
    }

    public bool HasWebSocketHandler(string path) =>
        TryMatchWebSocket(path) is not null;

    internal (WebSocketHandlers Handlers, Dictionary<string, string> Params)? TryMatchWebSocket(string path)
    {
        var normalized = Normalize(path);

        // O(1) fast path: exact static match
        if (_webSocketHandlers.TryGetValue(normalized, out var exact))
        {
            return (exact, EmptyParams);
        }

        var segments = Split(normalized);

        foreach (var route in _webSocketParamRoutes)
        {
            if (route.Segments.Length != segments.Length)
                continue;

            var matched = true;
            for (var i = 0; i < segments.Length; i++)
            {
                if (!route.IsParamSegment[i] &&
                    !string.Equals(route.Segments[i], segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            var routeParams = new Dictionary<string, string>(route.ParamNames.Length, StringComparer.OrdinalIgnoreCase);
            for (var p = 0; p < route.ParamNames.Length; p++)
            {
                routeParams[route.ParamNames[p]] = segments[route.ParamIndices[p]];
            }

            return (route.Handlers, routeParams);
        }

        foreach (var route in _webSocketCatchAllRoutes)
        {
            if (segments.Length <= route.CatchAllIndex)
                continue;

            var matched = true;
            for (var i = 0; i < route.CatchAllIndex; i++)
            {
                if (!route.IsParamSegment[i] &&
                    !string.Equals(route.Segments[i], segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            var routeParams = new Dictionary<string, string>(
                route.ParamNames.Length,
                StringComparer.OrdinalIgnoreCase);

            for (var p = 0; p < route.ParamNames.Length; p++)
            {
                var index = route.ParamIndices[p];
                routeParams[route.ParamNames[p]] = index == route.CatchAllIndex
                    ? string.Join("/", segments[index..])
                    : segments[index];
            }

            return (route.Handlers, routeParams);
        }

        return null;
    }

    private static bool IsValidParamName(string name)
    {
        if (name.Length == 0)
            return false;

        var first = name[0];
        if (first != '_' &&
            first is not (>= 'A' and <= 'Z') &&
            first is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            var character = name[i];
            if (character != '_' &&
                character is not (>= 'A' and <= 'Z') &&
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    // Accepts both ":param" (HTTP route style) and "{param}" (OpenAPI style).
    private static bool TryParseParamSegment(string segment, out string name)
    {
        if (segment.StartsWith(':'))
        {
            name = segment[1..];
            return name.Length > 0;
        }

        if (segment.Length > 2 && segment[0] == '{' && segment[^1] == '}')
        {
            name = segment[1..^1];
            return true;
        }

        name = segment;
        return false;
    }

    public async Task HandleWebSocketOpenAsync(
        string sessionId,
        ReptarRequest request)
    {
        var match = TryMatchWebSocket(request.Path);

        if (match is null)
        {
            Services.WebSocket.Close(sessionId, 1008, "No WebSocket handler.");
            return;
        }

        var handlers = match.Value.Handlers;
        request.RouteParams = match.Value.Params;

        var ctx = new Ctx(request, Services)
        {
            RequestId = sessionId,
        };
        var connection = new WebSocketConnection(sessionId, ctx);

        // The app middleware pipeline gates WebSocket upgrades the same way it
        // gates HTTP requests: any middleware may refuse the connection
        // (auth, rate limit, origin gate) and a short-circuit response closes
        // the socket instead of reaching the open handler. The sentinel
        // reference distinguishes "pipeline passed" from a middleware's
        // refusal response.
        var proceed = new ReptarResponse { Status = 0 };

        Func<Task<ReptarResponse>> next = handlers.Open is null
            ? () => Task.FromResult(proceed)
            : async () =>
            {
                await handlers.Open(connection);
                return proceed;
            };

        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var inner = next;
            next = () => middleware(ctx, inner);
        }

        try
        {
            var gate = await next();

            if (!ReferenceEquals(gate, proceed))
            {
                ctx.Log.Info(
                    $"[WebSocket:{sessionId}] Upgrade refused by middleware (HTTP {gate.Status}).");
                Services.WebSocket.Close(sessionId, 1008, "WebSocket upgrade refused.");
                return;
            }
        }
        catch (Exception exception)
        {
            ctx.Log.Error(
                $"[WebSocket:{sessionId}] Open failed: {exception.Message}");
            Services.WebSocket.Close(sessionId, 1011, "WebSocket open failed.");
            return;
        }

        _webSocketSessions[sessionId] = (connection, handlers);
    }

    public async Task HandleWebSocketMessageAsync(
        string sessionId,
        string message)
    {
        if (!_webSocketSessions.TryGetValue(sessionId, out var session))
            return;

        if (session.Handlers.Message is null)
            return;

        try
        {
            await session.Handlers.Message(session.Connection, message);
        }
        catch (Exception exception)
        {
            session.Connection.Ctx.Log.Error(
                $"[WebSocket:{sessionId}] Message failed: {exception.Message}");
            _webSocketSessions.TryRemove(sessionId, out _);
            Services.WebSocket.Close(sessionId, 1011, "WebSocket message failed.");
        }
    }

    public async Task HandleWebSocketCloseAsync(string sessionId)
    {
        if (!_webSocketSessions.TryRemove(sessionId, out var session))
            return;

        if (session.Handlers.Close is null)
            return;

        try
        {
            await session.Handlers.Close(session.Connection);
        }
        catch (Exception exception)
        {
            session.Connection.Ctx.Log.Error(
                $"[WebSocket:{sessionId}] Close failed: {exception.Message}");
        }
    }

    public Task<ScheduledResult> HandleScheduledAsync(ScheduledEvent evt) =>
        HandleScheduledAsync(evt, CancellationToken.None);

    public async Task<ScheduledResult> HandleScheduledAsync(ScheduledEvent evt, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var handlers = _scheduledHandlers
            .Where(pair =>
                pair.Key == "*" ||
                string.Equals(pair.Key, evt.Cron, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToArray();

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ReptarRequest
            {
                Method = "SCHEDULED",
                Path = "/scheduled",
                Query = "",
                Aborted = cancellationToken,
                Headers = new Dictionary<string, string>
                {
                    ["x-cron"] = evt.Cron,
                },
                Cookies = new Dictionary<string, string>(),
                Body = [],
            };
            var ctx = new Ctx(request, Services)
            {
                RequestId = Ids.New("cron"),
                RequestAborted = cancellationToken,
            };

            try
            {
                await handler(ctx, evt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
                ctx.Log.Error(
                    $"[Cron:{evt.Cron}] {exception.Message}");
            }
        }

        return new ScheduledResult(errors.Count == 0, errors.ToArray());
    }

    public Task<QueueBatchResult> HandleQueueBatchAsync(QueueBatch batch) =>
        HandleQueueBatchAsync(batch, CancellationToken.None);

    public async Task<QueueBatchResult> HandleQueueBatchAsync(QueueBatch batch, CancellationToken cancellationToken)
    {
        if (!_queueHandlers.TryGetValue(batch.Queue, out var handler))
        {
            return new QueueBatchResult(false, batch.Messages.Select(m => m.Id).ToArray());
        }

        var observer = _config.QueueObserver;
        var failed = new List<string>();
        List<QueueRetry>? retries = null;

        foreach (var message in batch.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ReptarRequest
            {
                Method = "QUEUE",
                Path = $"/queue/{batch.Queue}",
                Query = "",
                Aborted = cancellationToken,
                Headers = new Dictionary<string, string>
                {
                    ["x-queue-name"] = batch.Queue,
                    ["x-message-id"] = message.Id,
                },
                Cookies = new Dictionary<string, string>(),
                Body = System.Text.Encoding.UTF8.GetBytes(message.Body),
            };

            var ctx = new Ctx(request, Services)
            {
                RequestId = message.Id,
                RequestAborted = cancellationToken,
            };

            try
            {
                await handler(ctx, message);
                observer?.Completed(batch.Queue, message.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // No acknowledgement/retry-budget consumption for an interrupted batch.
                // Already performed effects can be redelivered: handlers must be idempotent.
                throw;
            }
            catch (RetryIn retry)
            {
                var delaySeconds = Math.Max(1, (int)retry.Delay.TotalSeconds);
                failed.Add(message.Id);
                (retries ??= []).Add(new QueueRetry(message.Id, delaySeconds));
                observer?.Retried(batch.Queue, message.Id, message.Attempts, delaySeconds);
                ctx.Log.Warn(
                    $"[Queue:{batch.Queue}] Message {message.Id} retry in {delaySeconds}s: {retry.Message}");
            }
            catch (Exception exception)
            {
                failed.Add(message.Id);
                observer?.Failed(batch.Queue, message.Id, message.Attempts, exception);
                ctx.Log.Error(
                    $"[Queue:{batch.Queue}] Message {message.Id} processing failed: {exception.Message}");
            }
        }

        return new QueueBatchResult(failed.Count == 0, failed.ToArray(), retries);
    }

    /// <summary>
    /// Resolves the host's body-reading policy with one route lookup. Hosts
    /// need both values before reading a body; resolving them separately
    /// repeats parameterized matching and its temporary allocations.
    /// </summary>
    public (long MaxBodyBytes, bool StreamingBody) GetRequestBodyOptions(
        string method,
        string path)
    {
        var meta = Match(method, path)?.Entry.Meta;
        return (meta?.MaxBodyBytes ?? _config.MaxBodyBytes, meta?.StreamingBody ?? false);
    }

    public long GetMaxBodyBytes(string method, string path) =>
        GetRequestBodyOptions(method, path).MaxBodyBytes;

    /// <summary>
    /// True when the matched route opted into a streaming request body via
    /// <see cref="RouteBuilder.StreamBody"/>.
    /// </summary>
    public bool IsStreamingBody(string method, string path) =>
        GetRequestBodyOptions(method, path).StreamingBody;

    private static bool IsBodyOverLimit(ReptarRequest request, long maxBodyBytes)
    {
        if (request.BodyTooLarge)
            return true;

        if (maxBodyBytes <= 0)
            return false;

        if (request.Body is { Length: var len } && len > maxBodyBytes)
            return true;

        if (long.TryParse(request.Header("Content-Length"), out var contentLength) && contentLength > maxBodyBytes)
            return true;

        return false;
    }

    /// <summary>
    /// Executes one request and returns the response. Background tasks are
    /// started on the native hosts; the Workers entrypoint should call
    /// <see cref="HandleWithBackgroundAsync"/> so the host can hold the event
    /// open until the returned task completes.
    /// </summary>
    public async Task<ReptarResponse> HandleAsync(ReptarRequest request) =>
        (await ExecuteAsync(request, startBackground: true)).Response;

    /// <summary>
    /// Executes one request and returns both the response and the background
    /// work registered through <see cref="Ctx.WaitUntil"/>. The caller should
    /// send the response first, then await <see cref="ReptarHandledResponse.Background"/>.
    /// </summary>
    public async Task<ReptarHandledResponse> HandleWithBackgroundAsync(
        ReptarRequest request)
    {
        var result = await ExecuteAsync(request, startBackground: false);
        var background = result.Context.BackgroundTasks.Count == 0
            ? Task.CompletedTask
            : Task.WhenAll(result.Context.BackgroundTasks);

        return new ReptarHandledResponse(result.Response, background);
    }

    private async Task<(ReptarResponse Response, Ctx Context)> ExecuteAsync(
        ReptarRequest request,
        bool startBackground)
    {
        // Durable Object alarm dispatch: a reserved internal path that rides
        // the normal pipeline (no extra JS exports — Binaryen rejects those).
        // Guarded by a null check so apps without OnAlarm pay nothing.
        if (_alarmHandler is not null &&
            string.Equals(request.Path, "/_reptar/alarm", StringComparison.Ordinal))
        {
            var alarmCtx = new Ctx(new ReptarRequest
            {
                Method = "ALARM",
                Path = "/_reptar/alarm",
                Query = "",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Body = [],
            }, Services)
            {
                RequestId = Ids.New("alarm"),
            };

            ReptarResponse alarmResponse;
            try
            {
                await _alarmHandler(alarmCtx);
                alarmResponse = ReptarResponse.Json("{\"ran\":true}", 200);
            }
            catch (Exception exception)
            {
                alarmCtx.Log.Error($"[Alarm] {exception.Message}");
                alarmResponse = ReptarResponse.Json(
                    $"{{\"ran\":false,\"error\":{System.Text.Json.Nodes.JsonValue.Create(exception.Message)!.ToJsonString()}}}", 500);
            }

            if (startBackground && alarmCtx.BackgroundTasks.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.WhenAll(alarmCtx.BackgroundTasks);
                    }
                    catch (Exception ex)
                    {
                        alarmCtx.Log.Error($"Background task failed: {ex.Message}");
                    }
                });
            }

            return (alarmResponse, alarmCtx);
        }

        var ctx = new Ctx(request, Services);
        ctx.RequestAborted = request.Aborted;
        var traceParent = TraceParent.Parse(request.Header("traceparent"));
        ctx.InitializeTrace(traceParent);
        using var traceScope = TraceContext.BeginDeferred(ctx);
        using var dbScope = DbMetrics.Begin(ctx.DbMetrics);

        var match = Match(request.Method, request.Path);
        ctx.RoutePattern = match?.Entry.Meta.Path ?? request.Path;
        var maxBodyBytes = match?.Entry.Meta.MaxBodyBytes ?? _config.MaxBodyBytes;
        ReptarResponse response;

        if (match is null && _middlewares.Count == 0)
        {
            response = ctx.NotFound("Route not found.");
        }
        else if (match is not null && match.Value.Entry.Middleware is null && _middlewares.Count == 0)
        {
            if (IsBodyOverLimit(request, maxBodyBytes))
            {
                response = ctx.Error(
                    413,
                    "payload_too_large",
                    $"Request body exceeds the maximum allowed size of {maxBodyBytes} bytes.");
            }
            else
            {
                request.RouteParams = match.Value.Params;
                try
                {
                    response = await match.Value.Entry.Handler(ctx);
                }
                catch (PayloadTooLargeException exception)
                {
                    response = ctx.Error(413, "payload_too_large", exception.Message);
                }
                catch (Exception exception)
                {
                    ctx.Log.Error(exception.ToString());
                    response = ctx.Error(
                        500,
                        "internal_error",
                        _config.Debug
                            ? exception.ToString()
                            : "Something went wrong.");
                }
            }
        }
        else
        {
            Func<Task<ReptarResponse>> next;

            if (IsBodyOverLimit(request, maxBodyBytes))
            {
                next = () => Task.FromResult(ctx.Error(
                    413,
                    "payload_too_large",
                    $"Request body exceeds the maximum allowed size of {maxBodyBytes} bytes."));
            }
            else if (match is not null)
            {
                request.RouteParams = match.Value.Params;
                var entry = match.Value.Entry;
                next = () => entry.Handler(ctx);

                if (entry.Middleware is not null)
                {
                    var middleware = entry.Middleware;
                    var inner = next;
                    next = () => middleware(ctx, inner);
                }
            }
            else
            {
                next = () => Task.FromResult(ctx.NotFound("Route not found."));
            }

            for (var i = _middlewares.Count - 1; i >= 0; i--)
            {
                var current = _middlewares[i];
                var inner = next;
                next = () => current(ctx, inner);
            }

            try
            {
                response = await next();
            }
            catch (PayloadTooLargeException exception)
            {
                response = ctx.Error(413, "payload_too_large", exception.Message);
            }
            catch (Exception exception)
            {
                ctx.Log.Error(exception.ToString());
                response = ctx.Error(
                    500,
                    "internal_error",
                    _config.Debug
                        ? exception.ToString()
                        : "Something went wrong.");
            }
        }

        foreach (var (key, value) in ctx.PendingHeaders)
            response.Headers[key] = value;

        foreach (var cookie in ctx.Cookies.Pending)
            response.SetCookies.Add(cookie);

        var statusHasNoBody = response.Status is 204 or 205 or 304;
        var isHead = request.Method.Length == 4 &&
            string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        if (statusHasNoBody || (isHead && (response.Body is not null || response.BodyStream is not null)))
        {
            var head = new ReptarResponse
            {
                Status = response.Status,
                ContentType = response.ContentType,
            };

            foreach (var (name, value) in response.Headers)
                head.Headers[name] = value;

            head.SetCookies.AddRange(response.SetCookies);

            // RFC 9110: 204 must not send Content-Length; 205 is explicitly
            // zero-length. A declared representation length is permitted on 304.
            if (response.Status == 204)
                head.Headers.Remove("Content-Length");
            else if (response.Status == 205)
                head.Headers["Content-Length"] = "0";
            else if (!statusHasNoBody && response.Body is { } headBody &&
                !head.Headers.ContainsKey("Content-Length"))
            {
                head.Headers["Content-Length"] =
                    headBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            response = head;
        }

        if (startBackground && ctx.BackgroundTasks.Count > 0)
        {
#if NETWASM
            // The Worker entrypoint uses HandleWithBackgroundAsync and awaits
            // the returned task; detaching work on the single-threaded Wasm
            // host would let the isolate finish before it runs.
#else
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(ctx.BackgroundTasks);
                }
                catch (Exception ex)
                {
                    ctx.Log.Error($"Background task failed: {ex.Message}");
                }
            });
#endif
        }

        return (response, ctx);
    }

    private RouteBuilder AddTypedRoute<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string method,
        string path,
        ReptarMiddleware? middleware,
        Validator<TInput>? validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler)
    {
        var meta = new RouteMeta
        {
            Method = method,
            Path = Normalize(path),
            InputType = typeof(TInput),
            Validator = validator,
        };

        Func<Ctx, Task<ReptarResponse>> wrapped = async ctx =>
        {
            var bodyBytes = ctx.Req.Body;

            if (bodyBytes is null || bodyBytes.Length == 0)
            {
                return ctx.Invalid(
                    [new ValidationIssue("Request body is required.", ["body"])]);
            }

            TInput? input;

            try
            {
                input = ctx.JsonCodec.Deserialize<TInput>(bodyBytes.AsSpan());
            }
            catch (JsonException)
            {
                return ctx.Invalid(
                    [new ValidationIssue("Invalid JSON body.", ["body"])]);
            }

            if (input is null)
            {
                return ctx.Invalid(
                    [new ValidationIssue("Invalid JSON body.", ["body"])]);
            }

            IReadOnlyList<ValidationIssue> issues = validator is null
                ? Array.Empty<ValidationIssue>()
                : validator.Validate(input);

            if (issues.Count > 0)
                return ctx.Invalid(issues);

            return await handler(ctx, input);
        };

        return AddRoute(method, path, middleware, wrapped, meta);
    }

    private RouteBuilder AddRoute(
        string method,
        string path,
        ReptarMiddleware? middleware,
        Func<Ctx, Task<ReptarResponse>> handler,
        RouteMeta? meta = null)
    {
        var normalized = Normalize(path);
        var routeMeta = meta ?? new RouteMeta { Method = method, Path = normalized };

        var entry = CreateRouteEntry(method, normalized, middleware, handler, routeMeta);
        RegisterRouteEntry(entry);

        return new RouteBuilder(this, routeMeta);
    }

    private static RouteEntry CreateRouteEntry(
        string method,
        string path,
        ReptarMiddleware? middleware,
        Func<Ctx, Task<ReptarResponse>> handler,
        RouteMeta meta)
    {
        var segments = Split(path);
        var isParamSegment = new bool[segments.Length];
        var paramNames = new List<string>();
        var paramIndices = new List<int>();
        var isCatchAll = false;
        string? catchAllName = null;
        var catchAllIndex = -1;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.StartsWith(':'))
            {
                isParamSegment[i] = true;
                paramNames.Add(segment[1..]);
                paramIndices.Add(i);
                continue;
            }

            if (!segment.StartsWith('*'))
                continue;

            if (i != segments.Length - 1)
            {
                throw new ArgumentException(
                    $"Catch-all segment '{segment}' must be the final route segment.",
                    nameof(path));
            }

            if (segment.Length == 1)
            {
                throw new ArgumentException(
                    "Catch-all segments must be named, e.g. '/files/*path'.",
                    nameof(path));
            }

            var name = segment[1..];
            if (!IsValidParamName(name))
            {
                throw new ArgumentException(
                    $"Catch-all parameter '{name}' must be a valid identifier.",
                    nameof(path));
            }

            isCatchAll = true;
            catchAllName = name;
            catchAllIndex = i;
            isParamSegment[i] = true;
            paramNames.Add(catchAllName);
            paramIndices.Add(i);
        }

        return new RouteEntry
        {
            Method = method,
            Path = path,
            Segments = segments,
            HasParams = paramNames.Count > 0,
            IsParamSegment = isParamSegment,
            ParamNames = paramNames.ToArray(),
            ParamIndices = paramIndices.ToArray(),
            IsCatchAll = isCatchAll,
            CatchAllName = catchAllName,
            CatchAllIndex = catchAllIndex,
            Middleware = middleware,
            Handler = handler,
            Meta = meta,
        };
    }

    private Dictionary<string, RouteEntry> GetStaticDict(string method)
    {
        if (method.Length == 3)
        {
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return _staticGet;
            if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)) return _staticPut;
        }
        else if (method.Length == 4)
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return _staticPost;
        }
        else if (method.Length == 6)
        {
            if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)) return _staticDelete;
        }
        else if (method.Length == 5)
        {
            if (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)) return _staticPatch;
        }

        return _staticOther;
    }

    private static string NormalizeMethod(string method)
    {
        if (method.Length == 3)
        {
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return "GET";
            if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)) return "PUT";
        }
        else if (method.Length == 4)
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return "POST";
        }
        else if (method.Length == 6)
        {
            if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)) return "DELETE";
        }
        else if (method.Length == 5)
        {
            if (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)) return "PATCH";
        }

        return method.ToUpperInvariant();
    }

    private void RegisterRouteEntry(RouteEntry entry)
    {
        _routes.Add(entry);

        if (entry.IsCatchAll)
        {
            var upperMethod = NormalizeMethod(entry.Method);
            if (!_catchAllRoutes.TryGetValue(upperMethod, out var catchAll))
            {
                catchAll = new List<RouteEntry>();
                _catchAllRoutes[upperMethod] = catchAll;
            }
            catchAll.Add(entry);
        }
        else if (!entry.HasParams)
        {
            var dict = GetStaticDict(entry.Method);
            dict[entry.Path] = entry;
        }
        else
        {
            var upperMethod = NormalizeMethod(entry.Method);
            var key = (upperMethod, entry.Segments.Length);
            if (!_paramRoutes.TryGetValue(key, out var list))
            {
                list = new List<RouteEntry>();
                _paramRoutes[key] = list;
            }
            list.Add(entry);
        }
        if (_groupParent is (var parent, var prefix))
        {
            var prefixedPath = Combine(prefix, entry.Path);
            var forwarded = CreateRouteEntry(
                entry.Method,
                prefixedPath,
                entry.Middleware,
                entry.Handler,
                entry.Meta.CloneWithPath(prefixedPath));
            parent.RegisterRouteEntry(forwarded);
        }
    }

    private (RouteEntry Entry, Dictionary<string, string> Params)? Match(string method, string path)
    {
        var staticDict = GetStaticDict(method);

        // 1. O(1) Fast-Path: Static Routes (Zero-Allocation, No Loops, No String Formatting)
        // If path already starts with '/' and has no trailing slash (standard HTTP path), query directly
        if (path.Length > 0 && path[0] == '/' && (path.Length == 1 || path[^1] != '/'))
        {
            if (staticDict.TryGetValue(path, out var directEntry))
            {
                return (directEntry, EmptyParams);
            }
        }

        var normalizedPath = Normalize(path);
        if (staticDict.TryGetValue(normalizedPath, out var staticEntry))
        {
            return (staticEntry, EmptyParams);
        }

        // 2. Parameterized Routes (Pre-Filtered by Method and Segment Count)
        var upperMethod = NormalizeMethod(method);
        var segments = Split(normalizedPath);
        var paramKey = (upperMethod, segments.Length);

        if (_paramRoutes.TryGetValue(paramKey, out var candidates))
        {
            for (var c = 0; c < candidates.Count; c++)
            {
                var entry = candidates[c];
                var matched = true;

                // Check static segments first (fail fast before allocating dictionary)
                for (var i = 0; i < segments.Length; i++)
                {
                    if (!entry.IsParamSegment[i] &&
                        !string.Equals(entry.Segments[i], segments[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }

                if (!matched) continue;

                // Match confirmed! Allocate dictionary with exact capacity and zero substring allocation
                var routeParams = new Dictionary<string, string>(entry.ParamNames.Length, StringComparer.OrdinalIgnoreCase);
                for (var p = 0; p < entry.ParamNames.Length; p++)
                {
                    routeParams[entry.ParamNames[p]] = segments[entry.ParamIndices[p]];
                }

                return (entry, routeParams);
            }
        }

        // 3. Catch-all Routes (e.g. /files/*path). Static and single-segment
        // parameter routes always win, so catch-alls are the last resort.
        if (_catchAllRoutes.TryGetValue(upperMethod, out var catchAllCandidates))
        {
            for (var c = 0; c < catchAllCandidates.Count; c++)
            {
                var entry = catchAllCandidates[c];
                if (segments.Length <= entry.CatchAllIndex)
                    continue;

                var matched = true;
                for (var i = 0; i < entry.CatchAllIndex; i++)
                {
                    if (!entry.IsParamSegment[i] &&
                        !string.Equals(
                            entry.Segments[i],
                            segments[i],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }

                if (!matched)
                    continue;

                var routeParams = new Dictionary<string, string>(
                    entry.ParamNames.Length,
                    StringComparer.OrdinalIgnoreCase);

                for (var p = 0; p < entry.ParamNames.Length; p++)
                {
                    var index = entry.ParamIndices[p];
                    routeParams[entry.ParamNames[p]] = index == entry.CatchAllIndex
                        ? string.Join("/", segments[index..])
                        : segments[index];
                }

                return (entry, routeParams);
            }
        }

        // HEAD is defined as GET without the response body. Prefer an explicit
        // HEAD route (registered through the fallback table), then fall back to
        // the GET route so every read endpoint supports HEAD for free.
        if (method.Length == 4 &&
            string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return Match("GET", path);
        }

        return null;
    }

    private ServiceSet Services => _services ??= new ServiceSet(
        Instrument(_config.Database ?? DefaultDatabase()),
        _config.Storage ?? DefaultStorage(),
        _config.AI ?? DefaultAI(),
        _config.RateLimit ?? new MemoryRateLimiter(),
        _config.Cache ?? DefaultCache(),
        _config.Queues ?? DefaultQueues(this),
        _config.Durable ?? DefaultDurable(),
        _config.Mail ?? DefaultMail(),
        _config.Http ?? DefaultHttp(),
        _config.Clock ?? new SystemClock(),
        _config.Crypto ?? DefaultCrypto(),
        _config.Environment ?? DefaultEnvironment(),
        _config.Json ?? new DefaultJsonCodec(),
        _config.Errors ?? new DefaultErrorMapper(),
        _config.Log ?? DefaultLog(),
        _config.MailObserver,
        _config.WebSocket ?? new NoopWebSocketTransport(),
        Policies);

    private static IAppLogger DefaultLog()
    {
#if NETWASM
        return new WasmAppLogger();
#else
        return new ConsoleAppLogger();
#endif
    }

    private static IMailClient DefaultMail() => new NoopMailClient();

    private static IDbClient Instrument(IDbClient db) =>
        db is InstrumentedDbClient ? db : new InstrumentedDbClient(db);

    private static IDbClient DefaultDatabase()
    {
#if NETWASM
        return new D1DbClient(new DefaultJsonCodec());
#else
        return new SqliteDbClient("reptar.db");
#endif
    }

    private static IStorage DefaultStorage()
    {
#if NETWASM
        return new R2Client();
#else
        return new NoopR2Client();
#endif
    }

    private static IAiClient DefaultAI()
    {
#if NETWASM
        return new AiClient();
#else
        return new NoopAiClient();
#endif
    }

    private static IKvClient DefaultCache()
    {
#if NETWASM
        return new KvClient();
#else
        return new NoopKvClient();
#endif
    }

    private static IQueueClient DefaultQueues(ReptarApp app)
    {
#if NETWASM
        return new QueueClient();
#else
        // Native targets default to inline execution instead of a silent
        // no-op: dev/test apps dispatch real work, and the log tells the
        // truth about unused options.
        return new SyncQueueClient(() => app);
#endif
    }

    private static IDurableBridge DefaultDurable()
    {
#if NETWASM
        return new DurableBridge();
#else
        return new NoopDurableBridge();
#endif
    }

    private static IHttpClient DefaultHttp()
    {
#if NETWASM
        return new WasmHttpClient();
#else
        return new DefaultHttpClient();
#endif
    }

    private static ICrypto DefaultCrypto()
    {
#if NETWASM
        return new WasmCrypto();
#else
        return new DevCrypto();
#endif
    }

    private static IEnvironment DefaultEnvironment()
    {
#if NETWASM
        return new WasmEnvironment();
#else
        return new SystemEnvironment();
#endif
    }





    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return "/";

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string[] Split(string path) =>
        path == "/" ? [] : path[1..].Split('/');

    private static string Combine(string prefix, string path) =>
        path == "/" ? prefix : prefix == "/" ? path : prefix + path;
}

public sealed class RouteBuilder(ReptarApp app, RouteMeta meta)
{
    public ReptarApp Build() => app;

    public RouteBuilder Produces<T>(int status = 200)
    {
        meta.Produces.Add((status, typeof(T)));
        return this;
    }

    public RouteBuilder Produces(int status)
    {
        meta.Produces.Add((status, null));
        return this;
    }

    /// <summary>
    /// Declares the query-string contract for codegen and OpenAPI. The handler
    /// still parses/validates values explicitly; this does not bind a JSON body.
    /// Query records should contain scalar string, number, boolean, or enum fields.
    /// </summary>
    public RouteBuilder WithQuery<T>()
    {
        meta.QueryType = typeof(T);
        return this;
    }

    public RouteBuilder WithName(string name)
    {
        meta.Name = name;
        return this;
    }

    public RouteBuilder WithSummary(string summary)
    {
        meta.Summary = summary;
        return this;
    }

    public RouteBuilder WithDescription(string description)
    {
        meta.Description = description;
        return this;
    }

    public RouteBuilder WithTags(params string[] tags)
    {
        meta.Tags.AddRange(tags);
        return this;
    }

    public RouteBuilder WithTag(string tag)
    {
        meta.Tags.Add(tag);
        return this;
    }

    public RouteBuilder AsAiTool(string? name = null, string? description = null)
    {
        meta.IsAiTool = true;
        meta.AiToolName = name;
        meta.AiToolDescription = description;
        return this;
    }

    /// <summary>
    /// Declares the JSON Schema for this tool's arguments.
    /// </summary>
    /// <remarks>
    /// Routes that bind a typed body get a schema generated from it. A route that reads the raw
    /// body — which is how the workspace tools parse JSON — has no type information to reflect
    /// over, so without this the model receives <c>{"type":"object","properties":{}}</c> and has to
    /// guess every parameter name. Declare it explicitly:
    /// <code>
    /// .AsAiTool("read_file", "Read a file")
    /// .WithAiToolSchema(new()
    /// {
    ///     ["type"] = "object",
    ///     ["properties"] = new Dictionary&lt;string, object?&gt;
    ///     {
    ///         ["path"] = new Dictionary&lt;string, object?&gt; { ["type"] = "string" }
    ///     },
    ///     ["required"] = new List&lt;object?&gt; { "path" }
    /// });
    /// </code>
    /// </remarks>
    public RouteBuilder WithAiToolSchema(Dictionary<string, object?> parameters)
    {
        meta.AiToolParameters = parameters;
        return this;
    }

    public RouteBuilder AcceptsFile(
        string fieldName = "file",
        string? description = null,
        bool required = true)
    {
        meta.FileFieldName = fieldName;
        meta.FileDescription = description;
        meta.FileRequired = required;
        return this;
    }

    public RouteBuilder WithMaxBody(long maxBytes)
    {
        meta.MaxBodyBytes = maxBytes;
        return this;
    }

    /// <summary>
    /// Opts the route into a streaming request body. Kestrel hands the handler
    /// the live request stream instead of buffering it first. <paramref name="maxBytes"/>
    /// sets the route limit (0 = unlimited); the pipeline still returns 413 when
    /// Content-Length or the streamed byte count exceeds it.
    /// </summary>
    public RouteBuilder StreamBody(long maxBytes = 0)
    {
        meta.StreamingBody = true;
        meta.MaxBodyBytes = Math.Max(0, maxBytes);
        return this;
    }

    public RouteBuilder Get(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Get(path, handler);

    public RouteBuilder Get(string path, Func<Ctx, ReptarResponse> handler) =>
        app.Get(path, handler);

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Get(path, middleware, handler);

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, ReptarResponse> handler) =>
        app.Get(path, middleware, handler);

    public RouteBuilder Post(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Post(path, handler);

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Post(path, middleware, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, middleware, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, validator, handler);

    public RouteBuilder Post<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, middleware, validator, handler);

    public RouteBuilder Put(
        string path,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Put(path, handler);

    public RouteBuilder Put(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Put(path, middleware, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, middleware, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, validator, handler);

    public RouteBuilder Put<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, middleware, validator, handler);

    public RouteBuilder Patch(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Patch(path, handler);

    public RouteBuilder Patch(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Patch(path, middleware, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Patch(path, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Patch(path, middleware, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Patch(path, validator, handler);

    public RouteBuilder Patch<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Patch(path, middleware, validator, handler);

    public RouteBuilder Delete(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Delete(path, handler);

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Delete(path, middleware, handler);
}
