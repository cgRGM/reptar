using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Reptar;

public sealed class RouteMeta
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Type? InputType { get; internal set; }
    public List<(int Status, Type? Type)> Produces { get; } = new();
    public string? Name { get; internal set; }
    public string? Summary { get; internal set; }
    public string? Description { get; internal set; }
    public List<string> Tags { get; } = new();
    public object? Validator { get; internal set; }
    public bool IsAiTool { get; internal set; }
    public string? AiToolName { get; internal set; }
    public string? AiToolDescription { get; internal set; }
    public string? FileFieldName { get; internal set; }
    public string? FileDescription { get; internal set; }
    public bool FileRequired { get; internal set; } = true;
    public long? MaxBodyBytes { get; internal set; }

    internal RouteMeta CloneWithPath(string path)
    {
        var clone = new RouteMeta
        {
            Method = Method,
            Path = path,
            InputType = InputType,
            Name = Name,
            Summary = Summary,
            Description = Description,
            Validator = Validator,
            IsAiTool = IsAiTool,
            AiToolName = AiToolName,
            AiToolDescription = AiToolDescription,
            FileFieldName = FileFieldName,
            FileDescription = FileDescription,
            FileRequired = FileRequired,
            MaxBodyBytes = MaxBodyBytes,
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
    public ReptarMiddleware? Middleware { get; init; }
    public required Func<Ctx, Task<ReptarResponse>> Handler { get; init; }
    public required RouteMeta Meta { get; init; }
}

internal sealed record WebSocketRoute(
    string[] Segments,
    bool[] IsParamSegment,
    string[] ParamNames,
    int[] ParamIndices,
    WebSocketHandlers Handlers);

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
    string[] FailedMessageIds);

public sealed record ScheduledEvent(
    string Cron,
    string ScheduledTime);

public sealed record ScheduledResult(
    bool Success,
    string[] Errors);

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
    private readonly List<ReptarMiddleware> _middlewares = new();
    private readonly Dictionary<string, Func<Ctx, QueueMessage, Task>> _queueHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<Ctx, ScheduledEvent, Task>> _scheduledHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WebSocketHandlers> _webSocketHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WebSocketRoute> _webSocketParamRoutes = [];
    private readonly Dictionary<string, (WebSocketConnection Connection, WebSocketHandlers Handlers)> _webSocketSessions =
        new(StringComparer.Ordinal);
    private readonly ReptarConfig _config;
    private readonly (ReptarApp Parent, string Prefix)? _groupParent;
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

    public static ReptarApp Create(ReptarConfig? config = null)
    {
        var resolved = config ?? new ReptarConfig();

        if (resolved.Auth is not null)
            Auth.Configure(resolved.Auth);

        return new ReptarApp(resolved);
    }

    public List<TableDef> Tables { get; } = new();

    public IReadOnlyList<RouteMeta> RouteMetadata =>
        _routes.Select(route => route.Meta).ToList();

    public ReptarApp Use(ReptarMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    public void RegisterSchema(params TableDef[] tables) => Tables.AddRange(tables);

    public string ExportSchemaJson() => SchemaRegistry.ExportJson(Tables);

    public IDbClient Db => Services.Db;
    public IDbClient Database => Services.Db;

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

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("POST", path, middleware, handler);

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

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("DELETE", path, middleware, handler);

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

    public ReptarApp OnWebSocket(string path, WebSocketHandlers handlers)
    {
        var normalized = Normalize(path);
        var segments = Split(normalized);
        var isParamSegment = new bool[segments.Length];
        var paramNames = new List<string>();
        var paramIndices = new List<int>();

        for (var i = 0; i < segments.Length; i++)
        {
            if (TryParseParamSegment(segments[i], out var name))
            {
                isParamSegment[i] = true;
                paramNames.Add(name);
                paramIndices.Add(i);
            }
        }

        if (paramNames.Count > 0)
        {
            _webSocketParamRoutes.Add(new WebSocketRoute(
                segments,
                isParamSegment,
                [.. paramNames],
                [.. paramIndices],
                handlers));
        }
        else
        {
            _webSocketHandlers[normalized] = handlers;
        }

        return this;
    }

    public bool HasWebSocketHandler(string path) =>
        TryMatchWebSocket(path) is not null;

    private (WebSocketHandlers Handlers, Dictionary<string, string> Params)? TryMatchWebSocket(string path)
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

        return null;
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
        _webSocketSessions[sessionId] = (connection, handlers);

        if (handlers.Open is null)
            return;

        try
        {
            await handlers.Open(connection);
        }
        catch (Exception exception)
        {
            ctx.Log.Error(
                $"[WebSocket:{sessionId}] Open failed: {exception.Message}");
            _webSocketSessions.Remove(sessionId);
            Services.WebSocket.Close(sessionId, 1011, "WebSocket open failed.");
        }
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
            _webSocketSessions.Remove(sessionId);
            Services.WebSocket.Close(sessionId, 1011, "WebSocket message failed.");
        }
    }

    public async Task HandleWebSocketCloseAsync(string sessionId)
    {
        if (!_webSocketSessions.Remove(sessionId, out var session))
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

    public async Task<ScheduledResult> HandleScheduledAsync(ScheduledEvent evt)
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
            var request = new ReptarRequest
            {
                Method = "SCHEDULED",
                Path = "/scheduled",
                Query = "",
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
            };

            try
            {
                await handler(ctx, evt);
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

    public async Task<QueueBatchResult> HandleQueueBatchAsync(QueueBatch batch)
    {
        if (!_queueHandlers.TryGetValue(batch.Queue, out var handler))
        {
            return new QueueBatchResult(false, batch.Messages.Select(m => m.Id).ToArray());
        }

        var failed = new List<string>();

        foreach (var message in batch.Messages)
        {
            var request = new ReptarRequest
            {
                Method = "QUEUE",
                Path = $"/queue/{batch.Queue}",
                Query = "",
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
            };

            try
            {
                await handler(ctx, message);
            }
            catch (Exception exception)
            {
                ctx.Log.Error(
                    $"[Queue:{batch.Queue}] Message {message.Id} processing failed: {exception.Message}");
                failed.Add(message.Id);
            }
        }

        return new QueueBatchResult(failed.Count == 0, failed.ToArray());
    }

    public long GetMaxBodyBytes(string method, string path) =>
        Match(method, path)?.Entry.Meta.MaxBodyBytes ?? _config.MaxBodyBytes;

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

    public async Task<ReptarResponse> HandleAsync(ReptarRequest request)
    {
        var ctx = new Ctx(request, Services);
        var match = Match(request.Method, request.Path);
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

        return response;
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

            List<ValidationIssue> issues = validator is null ? [] : validator.Validate(input);

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

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith(':'))
            {
                isParamSegment[i] = true;
                paramNames.Add(segments[i][1..]);
                paramIndices.Add(i);
            }
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

        if (!entry.HasParams)
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

        return null;
    }

    private ServiceSet Services => _services ??= new ServiceSet(
        _config.Database ?? DefaultDatabase(),
        _config.Storage ?? DefaultStorage(),
        _config.AI ?? DefaultAI(),
        _config.RateLimit ?? new MemoryRateLimiter(),
        _config.Cache ?? DefaultCache(),
        _config.Queues ?? DefaultQueues(),
        _config.Durable ?? DefaultDurable(),
        _config.Http ?? DefaultHttp(),
        _config.Clock ?? new SystemClock(),
        _config.Crypto ?? DefaultCrypto(),
        _config.Environment ?? DefaultEnvironment(),
        _config.Json ?? new DefaultJsonCodec(),
        _config.Errors ?? new DefaultErrorMapper(),
        _config.Log ?? new ConsoleAppLogger(),
        _config.WebSocket ?? new NoopWebSocketTransport());

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

    private static IQueueClient DefaultQueues()
    {
#if NETWASM
        return new QueueClient();
#else
        return new NoopQueueClient();
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

    public RouteBuilder Produces<T>(int status)
    {
        meta.Produces.Add((status, typeof(T)));
        return this;
    }

    public RouteBuilder Produces(int status)
    {
        meta.Produces.Add((status, null));
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
