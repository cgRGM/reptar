using System.Text.Json;

namespace Reptar;

public sealed class RouteMeta
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Type? InputType { get; internal set; }
    public List<(int Status, Type? Type)> Produces { get; } = new();
    public string? Name { get; internal set; }

    internal RouteMeta CloneWithPath(string path)
    {
        var clone = new RouteMeta
        {
            Method = Method,
            Path = path,
            InputType = InputType,
            Name = Name,
        };

        clone.Produces.AddRange(Produces);
        return clone;
    }
}

internal sealed class RouteEntry
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string[] Segments { get; init; }
    public ReptarMiddleware? Middleware { get; init; }
    public required Func<Ctx, Task<ReptarResponse>> Handler { get; init; }
    public required RouteMeta Meta { get; init; }
}

public sealed class ReptarApp
{
    private readonly List<RouteEntry> _routes = new();
    private readonly List<ReptarMiddleware> _middlewares = new();
    private readonly ReptarConfig _config;
    private ServiceSet? _services;

    internal ReptarApp(ReptarConfig config) => _config = config;

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

    public Task EnsureCreatedAsync() =>
        Services.Db.EnsureCreatedAsync(Tables.ToArray());

    public RouteBuilder Get(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("GET", path, null, handler);

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("GET", path, middleware, handler);

    public RouteBuilder Post(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("POST", path, null, handler);

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("POST", path, middleware, handler);

    public RouteBuilder Post<TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, null, validator, handler);

    public RouteBuilder Post<TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("POST", path, middleware, validator, handler);

    public RouteBuilder Put<TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, null, validator, handler);

    public RouteBuilder Put<TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        AddTypedRoute("PUT", path, middleware, validator, handler);

    public RouteBuilder Delete(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("DELETE", path, null, handler);

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        AddRoute("DELETE", path, middleware, handler);

    public ReptarApp Route(string basePath, ReptarApp subApp)
    {
        var prefix = Normalize(basePath);

        foreach (var route in subApp._routes)
        {
            var path = Combine(prefix, route.Path);

            _routes.Add(new RouteEntry
            {
                Method = route.Method,
                Path = path,
                Segments = Split(path),
                Middleware = route.Middleware,
                Handler = route.Handler,
                Meta = route.Meta.CloneWithPath(path),
            });
        }

        Tables.AddRange(subApp.Tables);
        return this;
    }

    public async Task<ReptarResponse> HandleAsync(ReptarRequest request)
    {
        var ctx = new Ctx(request, Services);
        var match = Match(request.Method, request.Path);
        ReptarResponse response;

        if (match is null)
        {
            response = ctx.NotFound("Route not found.");
        }
        else
        {
            request.RouteParams = match.Value.Params;
            var entry = match.Value.Entry;

            Func<Task<ReptarResponse>> next = () => entry.Handler(ctx);

            if (entry.Middleware is not null)
            {
                var middleware = entry.Middleware;
                var inner = next;
                next = () => middleware(ctx, inner);
            }

            foreach (var middleware in _middlewares.AsEnumerable().Reverse())
            {
                var current = middleware;
                var inner = next;
                next = () => current(ctx, inner);
            }

            try
            {
                response = await next();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                response = ctx.Error(500, "internal_error", "Something went wrong.");
            }
        }

        foreach (var (key, value) in ctx.PendingHeaders)
            response.Headers[key] = value;

        foreach (var cookie in ctx.Cookies.Pending)
            response.SetCookies.Add(cookie);

        return response;
    }

    private RouteBuilder AddTypedRoute<TInput>(
        string method,
        string path,
        ReptarMiddleware? middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler)
    {
        var meta = new RouteMeta
        {
            Method = method,
            Path = Normalize(path),
            InputType = typeof(TInput),
        };

        Func<Ctx, Task<ReptarResponse>> wrapped = async ctx =>
        {
            var body = ctx.Req.BodyText;

            if (string.IsNullOrWhiteSpace(body))
            {
                return ctx.Json(new ValidationError(
                    "validation_error",
                    "Request body is required.",
                    ctx.RequestId,
                    [new ValidationIssue("Request body is required.", ["body"])]), 400);
            }

            TInput? input;

            try
            {
                input = JsonSerializer.Deserialize<TInput>(body, ReptarJson.Options);
            }
            catch (JsonException)
            {
                return ctx.Json(new ValidationError(
                    "validation_error",
                    "Invalid JSON body.",
                    ctx.RequestId,
                    [new ValidationIssue("Invalid JSON body.", ["body"])]), 400);
            }

            if (input is null)
            {
                return ctx.Json(new ValidationError(
                    "validation_error",
                    "Invalid JSON body.",
                    ctx.RequestId,
                    [new ValidationIssue("Invalid JSON body.", ["body"])]), 400);
            }

            var issues = validator.Validate(input);

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

        _routes.Add(new RouteEntry
        {
            Method = method,
            Path = normalized,
            Segments = Split(normalized),
            Middleware = middleware,
            Handler = handler,
            Meta = routeMeta,
        });

        return new RouteBuilder(this, routeMeta);
    }

    private (RouteEntry Entry, Dictionary<string, string> Params)? Match(string method, string path)
    {
        var segments = Split(Normalize(path));

        foreach (var entry in _routes)
        {
            if (!string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Segments.Length != segments.Length)
                continue;

            var routeParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matched = true;

            for (var index = 0; index < segments.Length; index++)
            {
                var pattern = entry.Segments[index];

                if (pattern.StartsWith(':'))
                {
                    routeParams[pattern[1..]] = segments[index];
                    continue;
                }

                if (!string.Equals(pattern, segments[index], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return (entry, routeParams);
        }

        return null;
    }

    private ServiceSet Services => _services ??= new ServiceSet(
        _config.Database ?? new SqliteDbClient("reptar.db"),
        _config.Storage ?? new NoopR2Client(),
        _config.AI ?? new NoopAiClient(),
        _config.RateLimit ?? new NoopRateLimiter(),
        _config.Clock ?? new SystemClock(),
        _config.Crypto ?? new DevCrypto());

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

    public RouteBuilder Get(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Get(path, handler);

    public RouteBuilder Get(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Get(path, middleware, handler);

    public RouteBuilder Post(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Post(path, handler);

    public RouteBuilder Post(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Post(path, middleware, handler);

    public RouteBuilder Post<TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, validator, handler);

    public RouteBuilder Post<TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Post(path, middleware, validator, handler);

    public RouteBuilder Put<TInput>(
        string path,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, validator, handler);

    public RouteBuilder Put<TInput>(
        string path,
        ReptarMiddleware middleware,
        Validator<TInput> validator,
        Func<Ctx, TInput, Task<ReptarResponse>> handler) =>
        app.Put(path, middleware, validator, handler);

    public RouteBuilder Delete(string path, Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Delete(path, handler);

    public RouteBuilder Delete(
        string path,
        ReptarMiddleware middleware,
        Func<Ctx, Task<ReptarResponse>> handler) =>
        app.Delete(path, middleware, handler);
}
