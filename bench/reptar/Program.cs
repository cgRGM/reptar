using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Reptar;
using Reptar.Hosting;
using ReptarBench;
using static Reptar.ReptarApi;

var pgConn = Environment.GetEnvironmentVariable("BENCH_PG_CONN");
var databasePath = Environment.GetEnvironmentVariable("BENCH_DB") ?? "bench.db";
var breakdown = new BreakdownCollector
{
    Enabled = Environment.GetEnvironmentVariable("BENCH_BREAKDOWN") == "1",
};

IDbClient db = !string.IsNullOrWhiteSpace(pgConn)
    ? Db.Postgres(pgConn)
    : new SqliteDbClient(databasePath);

if (breakdown.Enabled && db is SqliteDbClient sqliteDb)
{
    sqliteDb.WriteTimingObserver = timing => breakdown.RecordWrite(timing);
}

var app = ReptarApp.Create(new ReptarConfig
{
    Database = db,
    Json = new BenchJsonCodec(),
});

// Logging/telemetry are off by default so the benchmark measures the framework,
// not stdout. Set BENCH_MIDDLEWARE=1 to measure their overhead separately.
if (Environment.GetEnvironmentVariable("BENCH_MIDDLEWARE") == "1")
{
    app.Use(RequestId());
    app.Use(Telemetry());
}

if (Environment.GetEnvironmentVariable("BENCH_SWR") == "1")
{
    app.Use(CacheControl.SWR(ttlSeconds: 15, swrSeconds: 30));
}

var jwtOptions = new JwtOptions
{
    Secret = Environment.GetEnvironmentVariable("BENCH_JWT_SECRET") ?? "bench-jwt-secret",
    Issuer = "reptar-bench",
    Audience = "reptar-bench",
    LifetimeMinutes = 60 * 24 * 365 * 10,
};

var taskSequence = 0L;

ReptarMiddleware requireAuth = async (ctx, next) =>
{
    var authStarted = breakdown.Enabled ? Stopwatch.GetTimestamp() : 0;
    var user = await ResolveUser(ctx);

    if (breakdown.Enabled)
        breakdown.RecordAuth(Stopwatch.GetTimestamp() - authStarted);

    if (user is null)
        return Error(ctx, 401, "unauthorized", "Missing or invalid bearer token.");

    return await next();
};

// ---------------------------------------------------------------------------
// Calibration tiers (no database)
// ---------------------------------------------------------------------------

app.Get("/__bench/plaintext", ctx =>
    Task.FromResult(ctx.Text("Hello, World!")));

app.Get("/__bench/framework-error", ctx =>
    Task.FromResult(ctx.Error(404, "not_found", "Framework error path.")));

app.Get("/__bench/json", ctx =>
    Task.FromResult(ctx.Json(new BenchJson(
        "Hello, World!",
        true,
        1,
        ["bench", "reptar", "http"],
        new NestedJson(1, 2)))));

app.Get("/__bench/router/:orgId/:projectId/:taskId", ctx =>
    Task.FromResult(ctx.Json(new RouterParams(
        ctx.Req.Param("orgId")!,
        ctx.Req.Param("projectId")!,
        ctx.Req.Param("taskId")!))));

app.Post("/__bench/validate", async ctx =>
{
    var input = ctx.Req.Body is { Length: > 0 } body
        ? ctx.JsonCodec.Deserialize<ValidateInput>(body.AsSpan())
        : null;

    if (input is null)
        return ValidationError(ctx, [new ValidationIssueDto("body", "A JSON body is required.")]);

    var issues = ValidateTask(input.Title, input.Description, input.Priority, input.Status);

    if (issues.Count > 0)
        return ValidationError(ctx, issues);

    return ctx.Json(new ValidateResponse(true, input.Title, input.Priority, input.Status), 201);
});

app.Get("/__bench/breakdown", ctx =>
    Task.FromResult(ctx.Json(breakdown.Snapshot())));

app.Post("/__bench/breakdown/reset", ctx =>
{
    breakdown.Reset();
    return Task.FromResult(ctx.Text("ok"));
});

// ---------------------------------------------------------------------------
// Realistic API
// ---------------------------------------------------------------------------

app.Get("/api/v1/tasks/:id", async ctx =>
{
    var row = await ctx.Db.QuerySingleOrDefaultAsync<TaskDetailRow>(
        """
        select t.id, t.project_id, t.title, t.description, t.status, t.priority,
               t.author_id, au.name as author_name,
               t.assignee_id, su.name as assignee_name,
               t.created_at, t.updated_at
        from tasks t
        join users au on au.id = t.author_id
        left join users su on su.id = t.assignee_id
        where t.id = @id
        """,
        ("id", ctx.Req.Param("id")));

    if (row is null)
        return Error(ctx, 404, "not_found", "Task not found.");

    return ctx.Json(ToDetail(row));
});

app.Get("/api/v1/projects/:projectId/tasks", async ctx =>
{
    var projectId = ctx.Req.Param("projectId")!;
    var status = ctx.Req.QueryValue("status") ?? "open";
    var limit = ParseInt(ctx.Req.QueryValue("limit"), 20, 1, 100);
    var offset = ParseInt(ctx.Req.QueryValue("cursor"), 0, 0, 1_000_000);

    var data = await ctx.Db.QueryAsync<TaskSummary>(
        """
        select id, project_id, title, description, status, priority,
               assignee_id, created_at, updated_at
        from tasks
        where project_id = @projectId and status = @status
        order by created_at desc
        limit @limit offset @offset
        """,
        ("projectId", projectId),
        ("status", status),
        ("limit", limit),
        ("offset", offset));

    var nextCursor = data.Count == limit
        ? (offset + limit).ToString()
        : null;

    return ctx.Json(new TaskListResponse(data, nextCursor));
});

app.Get("/api/v1/tasks/:id/comments", async ctx =>
{
    var taskId = ctx.Req.Param("id")!;
    var limit = ParseInt(ctx.Req.QueryValue("limit"), 20, 1, 100);
    var offset = ParseInt(ctx.Req.QueryValue("cursor"), 0, 0, 1_000_000);

    var rows = await ctx.Db.QueryAsync<CommentRow>(
        """
        select c.id, c.task_id, c.body, c.created_at,
               u.id as author_id, u.name as author_name
        from comments c
        join users u on u.id = c.author_id
        where c.task_id = @taskId
        order by c.created_at asc
        limit @limit offset @offset
        """,
        ("taskId", taskId),
        ("limit", limit),
        ("offset", offset));

    var data = new List<CommentItem>(rows.Count);

    foreach (var row in rows)
    {
        data.Add(new CommentItem(
            row.Id,
            row.TaskId,
            row.Body,
            new UserRef(row.AuthorId, row.AuthorName),
            row.CreatedAt));
    }

    var nextCursor = rows.Count == limit
        ? (offset + limit).ToString()
        : null;

    return ctx.Json(new CommentListResponse(data, nextCursor));
});

app.Post("/api/v1/projects/:projectId/tasks", requireAuth, async ctx =>
{
    var handlerStarted = breakdown.Enabled ? Stopwatch.GetTimestamp() : 0;
    var input = ctx.Req.Body is { Length: > 0 } body
        ? ctx.JsonCodec.Deserialize<CreateTaskInput>(body.AsSpan())
        : null;

    if (input is null)
        return ValidationError(ctx, [new ValidationIssueDto("body", "A JSON body is required.")]);

    var issues = ValidateTask(input.Title, input.Description, input.Priority, input.Status);

    if (issues.Count > 0)
        return ValidationError(ctx, issues);

    var projectId = ctx.Req.Param("projectId")!;
    var user = ctx.User!;
    var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    var id = $"tsk_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref taskSequence)}";

    await ctx.Db.ExecuteAsync(
        """
        insert into tasks
          (id, project_id, author_id, assignee_id, title, description, status, priority, created_at, updated_at)
        values
          (@id, @projectId, @authorId, @assigneeId, @title, @description, @status, @priority, @createdAt, @updatedAt)
        """,
        ("id", id),
        ("projectId", projectId),
        ("authorId", user.Id),
        ("assigneeId", input.AssigneeId),
        ("title", input.Title),
        ("description", input.Description),
        ("status", input.Status),
        ("priority", input.Priority),
        ("createdAt", now),
        ("updatedAt", now));

    var assigneeStarted = breakdown.Enabled ? Stopwatch.GetTimestamp() : 0;
    var assignee = await ResolveAssignee(ctx, input.AssigneeId);

    if (breakdown.Enabled)
        breakdown.RecordAssignee(Stopwatch.GetTimestamp() - assigneeStarted);

    if (breakdown.Enabled)
        breakdown.RecordHandler(Stopwatch.GetTimestamp() - handlerStarted);

    return ctx.Json(
        new TaskDetail(
            id,
            projectId,
            input.Title,
            input.Description,
            input.Status,
            input.Priority,
            new UserRef(user.Id, user.Name),
            assignee,
            now,
            now),
        201);
});

app.Put("/api/v1/tasks/:id", requireAuth, async ctx =>
{
    var input = ctx.Req.Body is { Length: > 0 } body
        ? ctx.JsonCodec.Deserialize<UpdateTaskInput>(body.AsSpan())
        : null;

    if (input is null)
        return ValidationError(ctx, [new ValidationIssueDto("body", "A JSON body is required.")]);

    var issues = ValidateUpdate(input);

    if (issues.Count > 0)
        return ValidationError(ctx, issues);

    var id = ctx.Req.Param("id")!;
    var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    var existing = await ctx.Db.QuerySingleOrDefaultAsync<TaskDetailRow>(
        """
        select t.id, t.project_id, t.title, t.description, t.status, t.priority,
               t.author_id, au.name as author_name,
               t.assignee_id, su.name as assignee_name,
               t.created_at, t.updated_at
        from tasks t
        join users au on au.id = t.author_id
        left join users su on su.id = t.assignee_id
        where t.id = @id
        """,
        ("id", id));

    if (existing is null)
        return Error(ctx, 404, "not_found", "Task not found.");

    var title = input.Title ?? existing.Title;
    var description = input.Description ?? existing.Description;
    var status = input.Status ?? existing.Status;
    var priority = input.Priority ?? existing.Priority;
    var assigneeId = input.AssigneeId ?? existing.AssigneeId;

    await ctx.Db.ExecuteAsync(
        """
        update tasks
        set title = @title,
            description = @description,
            status = @status,
            priority = @priority,
            assignee_id = @assigneeId,
            updated_at = @updatedAt
        where id = @id
        """,
        ("id", id),
        ("title", title),
        ("description", description),
        ("status", status),
        ("priority", priority),
        ("assigneeId", assigneeId),
        ("updatedAt", now));

    return ctx.Json(new TaskDetail(
        existing.Id,
        existing.ProjectId,
        title,
        description,
        status,
        priority,
        new UserRef(existing.AuthorId, existing.AuthorName),
        await ResolveAssignee(ctx, assigneeId),
        existing.CreatedAt,
        now));
});

app.Get("/api/v1/me", requireAuth, ctx =>
{
    var user = ctx.User!;
    return Task.FromResult(ctx.Json(new MeResponse(user.Id, user.Email, user.Name, user.CreatedAt ?? "")));
});

app.Post("/api/v1/auth/login", async ctx =>
{
    var input = ctx.Req.Body is { Length: > 0 } body
        ? ctx.JsonCodec.Deserialize<LoginRequest>(body.AsSpan())
        : null;

    if (input is null)
        return Error(ctx, 422, "validation_error", "A JSON body is required.");

    var row = await ctx.Db.QuerySingleOrDefaultAsync<AuthUserWithPasswordRow>(
        "select id, email, name, password_hash, created_at from users where email = @email",
        ("email", input.Email));

    if (row is null || !await ctx.Crypto.VerifyPassword(input.Password, row.PasswordHash))
        return Error(ctx, 401, "unauthorized", "Invalid email or password.");

    var payload = Jwt.CreatePayload(
        new SessionUser(row.Id, row.Email, row.Name),
        role: "user",
        lifetime: TimeSpan.FromDays(3650));

    var token = await Jwt.SignAsync(ctx.Crypto, jwtOptions, payload);

    return ctx.Json(new LoginResponse(token, "Bearer", payload.ExpiresAt));
});

await app.RunAsync(args);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async Task<SessionUser?> ResolveUser(Ctx ctx)
{
    var token = Jwt.ExtractToken(ctx.Req);

    if (token is null)
        return null;

    var payload = await Jwt.VerifyAsync(ctx.Crypto, jwtOptions, token);

    if (payload is null)
        return null;

    var row = await ctx.Db.QuerySingleOrDefaultAsync<AuthUserRow>(
        "select id, email, name, created_at from users where id = @id",
        ("id", payload.Subject));

    if (row is null)
        return null;

    var user = new SessionUser(row.Id, row.Email, row.Name, CreatedAt: row.CreatedAt);
    ctx.User = user;
    ctx.Claims = payload;
    return user;
}

async Task<UserRef?> ResolveAssignee(Ctx ctx, string? assigneeId)
{
    if (string.IsNullOrWhiteSpace(assigneeId))
        return null;

    var row = await ctx.Db.QuerySingleOrDefaultAsync<AuthUserRow>(
        "select id, email, name, created_at from users where id = @id",
        ("id", assigneeId));

    return row is null ? null : new UserRef(row.Id, row.Name);
}

static TaskDetail ToDetail(TaskDetailRow row) =>
    new(
        row.Id,
        row.ProjectId,
        row.Title,
        row.Description,
        row.Status,
        row.Priority,
        new UserRef(row.AuthorId, row.AuthorName),
        row.AssigneeId is null || row.AssigneeName is null
            ? null
            : new UserRef(row.AssigneeId, row.AssigneeName),
        row.CreatedAt,
        row.UpdatedAt);

static int ParseInt(string? value, int fallback, int min, int max)
{
    if (!int.TryParse(value, out var parsed))
        return fallback;

    return Math.Clamp(parsed, min, max);
}

static ReptarResponse Error(Ctx ctx, int status, string code, string message) =>
    ctx.Json(new ErrorResponse(new ErrorBody(code, message)), status);

static ReptarResponse ValidationError(Ctx ctx, List<ValidationIssueDto> issues) =>
    ctx.Json(
        new ValidationErrorResponse(
            new ErrorBody("validation_error", "Validation failed."),
            issues.ToArray()),
        422);

static List<ValidationIssueDto> ValidateTask(
    string title,
    string description,
    int priority,
    string status)
{
    var issues = new List<ValidationIssueDto>();

    if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        issues.Add(new ValidationIssueDto("title", "Title must be between 1 and 200 characters."));

    if (description.Length > 2000)
        issues.Add(new ValidationIssueDto("description", "Description must be at most 2000 characters."));

    if (priority is < 0 or > 3)
        issues.Add(new ValidationIssueDto("priority", "Priority must be between 0 and 3."));

    if (status is not ("open" or "in_progress" or "done"))
        issues.Add(new ValidationIssueDto("status", "Status must be open, in_progress, or done."));

    return issues;
}

static List<ValidationIssueDto> ValidateUpdate(UpdateTaskInput input)
{
    var issues = new List<ValidationIssueDto>();

    if (input.Title is not null && (input.Title.Length == 0 || input.Title.Length > 200))
        issues.Add(new ValidationIssueDto("title", "Title must be between 1 and 200 characters."));

    if (input.Description is not null && input.Description.Length > 2000)
        issues.Add(new ValidationIssueDto("description", "Description must be at most 2000 characters."));

    if (input.Priority is < 0 or > 3)
        issues.Add(new ValidationIssueDto("priority", "Priority must be between 0 and 3."));

    if (input.Status is not null && input.Status is not ("open" or "in_progress" or "done"))
        issues.Add(new ValidationIssueDto("status", "Status must be open, in_progress, or done."));

    return issues;
}

// ---------------------------------------------------------------------------
// DTOs and row shapes
// ---------------------------------------------------------------------------

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record BenchJson(
    string Message,
    bool Ok,
    int Version,
    string[] Tags,
    NestedJson Nested);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record NestedJson(int A, int B);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RouterParams(string OrgId, string ProjectId, string TaskId);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record ValidateInput(
    string Title,
    string Description,
    int Priority,
    string Status,
    string? AssigneeId);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record ValidateResponse(bool Ok, string Title, int Priority, string Status);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record UserRef(string Id, string Name);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record TaskDetail(
    string Id,
    string ProjectId,
    string Title,
    string Description,
    string Status,
    int Priority,
    UserRef Author,
    UserRef? Assignee,
    string CreatedAt,
    string UpdatedAt);

[ReptarEntity]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public partial record TaskSummary(
    string Id,
    string ProjectId,
    string Title,
    string Description,
    string Status,
    int Priority,
    string? AssigneeId,
    string CreatedAt,
    string UpdatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record TaskListResponse(List<TaskSummary> Data, string? NextCursor);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record CommentItem(
    string Id,
    string TaskId,
    string Body,
    UserRef Author,
    string CreatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record CommentListResponse(List<CommentItem> Data, string? NextCursor);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record MeResponse(string Id, string Email, string Name, string CreatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record LoginRequest(string Email, string Password);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record LoginResponse(string Token, string TokenType, long ExpiresAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record CreateTaskInput(
    string Title,
    string Description,
    int Priority,
    string Status,
    string? AssigneeId);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record UpdateTaskInput(
    string? Title,
    string? Description,
    int? Priority,
    string? Status,
    string? AssigneeId);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record ErrorBody(string Code, string Message);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record ErrorResponse(ErrorBody Error);

public sealed record ValidationIssueDto(string Path, string Message);

public sealed record ValidationErrorResponse(ErrorBody Error, ValidationIssueDto[] Issues);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record StageStats(
    string Name,
    int Count,
    double AvgMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record BreakdownSnapshot(bool Enabled, StageStats[] Stages);

/// <summary>
/// Collects opt-in T7 write-path timings. All queues are empty and the
/// observer is null unless BENCH_BREAKDOWN=1.
/// </summary>
public sealed class BreakdownCollector
{
    private readonly ConcurrentQueue<long> _auth = new();
    private readonly ConcurrentQueue<long> _gate = new();
    private readonly ConcurrentQueue<long> _insert = new();
    private readonly ConcurrentQueue<long> _assignee = new();
    private readonly ConcurrentQueue<long> _handler = new();

    public bool Enabled { get; init; }

    public void RecordAuth(long ticks)
    {
        if (Enabled)
            _auth.Enqueue(ticks);
    }

    public void RecordWrite(DbWriteTiming timing)
    {
        if (!Enabled)
            return;

        _gate.Enqueue(timing.GateWaitTicks);
        _insert.Enqueue(timing.ExecuteTicks);
    }

    public void RecordAssignee(long ticks)
    {
        if (Enabled)
            _assignee.Enqueue(ticks);
    }

    public void RecordHandler(long ticks)
    {
        if (Enabled)
            _handler.Enqueue(ticks);
    }

    public void Reset()
    {
        _auth.Clear();
        _gate.Clear();
        _insert.Clear();
        _assignee.Clear();
        _handler.Clear();
    }

    public BreakdownSnapshot Snapshot() => new(
        Enabled,
        [
            Stage("auth", _auth),
            Stage("writeGate", _gate),
            Stage("insert", _insert),
            Stage("assignee", _assignee),
            Stage("handler", _handler),
        ]);

    private static StageStats Stage(string name, ConcurrentQueue<long> queue)
    {
        var values = queue.ToArray();

        if (values.Length == 0)
            return new StageStats(name, 0, 0, 0, 0, 0, 0);

        Array.Sort(values);
        var toMs = 1000.0 / Stopwatch.Frequency;
        var total = 0L;

        foreach (var value in values)
            total += value;

        return new StageStats(
            name,
            values.Length,
            total / (double)values.Length * toMs,
            Percentile(values, 0.50) * toMs,
            Percentile(values, 0.95) * toMs,
            Percentile(values, 0.99) * toMs,
            values[^1] * toMs);
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Source-generated JSON codec. The default Reptar codec uses reflection, which
/// trims nested DTO properties under Native AOT. This is the production-safe path
/// and the one the benchmark app uses.
/// </summary>
public sealed class BenchJsonCodec : IJsonCodec
{
    private static JsonTypeInfo? Resolve(Type type) =>
        BenchJsonContext.Default.GetTypeInfo(type) ??
        FrameworkJsonContext.Default.GetTypeInfo(type);

    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        var typeInfo = Resolve(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T)}.");

        return JsonSerializer.Serialize(value, typeInfo);
    }

    public byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        var typeInfo = Resolve(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T)}.");

        return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    }

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json)
    {
        var typeInfo = Resolve(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T)}.");

        return (T?)JsonSerializer.Deserialize(json, typeInfo);
    }

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> utf8Json)
    {
        var typeInfo = Resolve(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata registered for {typeof(T)}.");

        return (T?)JsonSerializer.Deserialize(utf8Json, typeInfo);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchJson))]
[JsonSerializable(typeof(NestedJson))]
[JsonSerializable(typeof(RouterParams))]
[JsonSerializable(typeof(ValidateInput))]
[JsonSerializable(typeof(ValidateResponse))]
[JsonSerializable(typeof(UserRef))]
[JsonSerializable(typeof(TaskDetail))]
[JsonSerializable(typeof(TaskSummary))]
[JsonSerializable(typeof(TaskListResponse))]
[JsonSerializable(typeof(CommentItem))]
[JsonSerializable(typeof(CommentListResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(CreateTaskInput))]
[JsonSerializable(typeof(UpdateTaskInput))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ValidationIssueDto))]
[JsonSerializable(typeof(ValidationErrorResponse))]
[JsonSerializable(typeof(StageStats))]
[JsonSerializable(typeof(BreakdownSnapshot))]
public sealed partial class BenchJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Reptar.ErrorBody))]
[JsonSerializable(typeof(Reptar.ValidationIssue))]
[JsonSerializable(typeof(Reptar.ValidationError))]
public sealed partial class FrameworkJsonContext : JsonSerializerContext;
