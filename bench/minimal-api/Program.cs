using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

var databasePath = Environment.GetEnvironmentVariable("BENCH_DB") ?? "bench.db";
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort)
    ? configuredPort
    : 9102;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default);
});

var app = builder.Build();

var writeConnection = new SqliteConnection($"Data Source={databasePath};Default Timeout=5");
writeConnection.Open();
using (var pragma = writeConnection.CreateCommand())
{
    pragma.CommandText = "PRAGMA busy_timeout = 5000";
    pragma.ExecuteNonQuery();
}
using (var pragma = writeConnection.CreateCommand())
{
    // The seed sets synchronous=NORMAL on its own connection, but SQLite
    // synchronous is per-connection. Without this, every insert fsyncs the WAL.
    pragma.CommandText = "PRAGMA synchronous = NORMAL";
    pragma.ExecuteNonQuery();
}

var writeGate = new SemaphoreSlim(1, 1);
var taskSequence = 0L;

// ---------------------------------------------------------------------------
// Calibration tiers
// ---------------------------------------------------------------------------

app.MapGet("/__bench/plaintext", () =>
    Results.Text("Hello, World!", "text/plain; charset=utf-8"));

app.MapGet("/__bench/json", () =>
    Results.Json(
        new BenchJson(
            "Hello, World!",
            true,
            1,
            ["bench", "reptar", "http"],
            new NestedJson(1, 2)),
        AppJson.Default.BenchJson));

app.MapGet("/__bench/router/{orgId}/{projectId}/{taskId}", (string orgId, string projectId, string taskId) =>
    Results.Json(new RouterParams(orgId, projectId, taskId), AppJson.Default.RouterParams));

app.MapPost("/__bench/validate", async (HttpRequest request) =>
{
    var input = await request.ReadFromJsonAsync(AppJson.Default.ValidateInput);

    if (input is null)
        return ValidationError([new ValidationIssue("body", "A JSON body is required.")]);

    var issues = ValidateTask(input.Title, input.Description, input.Priority, input.Status);

    if (issues.Count > 0)
        return ValidationError(issues);

    return Results.Json(
        new ValidateResponse(true, input.Title, input.Priority, input.Status),
        AppJson.Default.ValidateResponse,
        statusCode: 201);
});

// ---------------------------------------------------------------------------
// Realistic API
// ---------------------------------------------------------------------------

app.MapGet("/api/v1/tasks/{id}", async (string id) =>
{
    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
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
    await using var reader = await command.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Error(404, "not_found", "Task not found.");

    return Results.Json(ReadTaskDetail(reader), AppJson.Default.TaskDetail);
});

app.MapGet("/api/v1/projects/{projectId}/tasks", async (string projectId, HttpRequest request) =>
{
    var status = request.Query["status"].ToString();
    if (string.IsNullOrWhiteSpace(status))
        status = "open";

    var limit = ParseInt(request.Query["limit"].ToString(), 20, 1, 100);
    var offset = ParseInt(request.Query["cursor"].ToString(), 0, 0, 1_000_000);

    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
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
    await using var reader = await command.ExecuteReaderAsync();

    var data = new List<TaskSummary>();
    while (await reader.ReadAsync())
    {
        data.Add(new TaskSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8)));
    }

    var nextCursor = data.Count == limit ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null;
    return Results.Json(new TaskListResponse(data, nextCursor), AppJson.Default.TaskListResponse);
});

app.MapGet("/api/v1/tasks/{id}/comments", async (string id, HttpRequest request) =>
{
    var limit = ParseInt(request.Query["limit"].ToString(), 20, 1, 100);
    var offset = ParseInt(request.Query["cursor"].ToString(), 0, 0, 1_000_000);

    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
        """
        select c.id, c.task_id, c.body, c.created_at,
               u.id as author_id, u.name as author_name
        from comments c
        join users u on u.id = c.author_id
        where c.task_id = @taskId
        order by c.created_at asc
        limit @limit offset @offset
        """,
        ("taskId", id),
        ("limit", limit),
        ("offset", offset));
    await using var reader = await command.ExecuteReaderAsync();

    var data = new List<CommentItem>();
    while (await reader.ReadAsync())
    {
        data.Add(new CommentItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new UserRef(reader.GetString(4), reader.GetString(5)),
            reader.GetString(3)));
    }

    var nextCursor = data.Count == limit ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null;
    return Results.Json(new CommentListResponse(data, nextCursor), AppJson.Default.CommentListResponse);
});

app.MapPost("/api/v1/projects/{projectId}/tasks", async (string projectId, HttpRequest request) =>
{
    var user = await ResolveUser(request, databasePath);

    if (user is null)
        return Error(401, "unauthorized", "Missing or invalid bearer token.");

    var input = await request.ReadFromJsonAsync(AppJson.Default.CreateTaskInput);

    if (input is null)
        return ValidationError([new ValidationIssue("body", "A JSON body is required.")]);

    var issues = ValidateTask(input.Title, input.Description, input.Priority, input.Status);

    if (issues.Count > 0)
        return ValidationError(issues);

    var now = IsoNow();
    var id = $"tsk_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref taskSequence)}";

    await writeGate.WaitAsync();
    try
    {
        await using var command = Command(
            writeConnection,
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
        await command.ExecuteNonQueryAsync();
    }
    finally
    {
        writeGate.Release();
    }

    var assignee = await ResolveAssignee(databasePath, input.AssigneeId);

    return Results.Json(
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
        AppJson.Default.TaskDetail,
        statusCode: 201);
});

app.MapPut("/api/v1/tasks/{id}", async (string id, HttpRequest request) =>
{
    var user = await ResolveUser(request, databasePath);

    if (user is null)
        return Error(401, "unauthorized", "Missing or invalid bearer token.");

    var input = await request.ReadFromJsonAsync(AppJson.Default.UpdateTaskInput);

    if (input is null)
        return ValidationError([new ValidationIssue("body", "A JSON body is required.")]);

    var issues = ValidateUpdate(input);

    if (issues.Count > 0)
        return ValidationError(issues);

    await using var readConnection = OpenRead(databasePath);
    await using var readCommand = Command(
        readConnection,
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
    await using var reader = await readCommand.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Error(404, "not_found", "Task not found.");

    var existing = ReadTaskDetail(reader);

    var title = input.Title ?? existing.Title;
    var description = input.Description ?? existing.Description;
    var status = input.Status ?? existing.Status;
    var priority = input.Priority ?? existing.Priority;
    var assigneeId = input.AssigneeId ?? existing.Assignee?.Id;
    var now = IsoNow();

    await writeGate.WaitAsync();
    try
    {
        await using var command = Command(
            writeConnection,
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
        await command.ExecuteNonQueryAsync();
    }
    finally
    {
        writeGate.Release();
    }

    return Results.Json(
        new TaskDetail(
            existing.Id,
            existing.ProjectId,
            title,
            description,
            status,
            priority,
            existing.Author,
            await ResolveAssignee(databasePath, assigneeId),
            existing.CreatedAt,
            now),
        AppJson.Default.TaskDetail);
});

app.MapGet("/api/v1/me", async (HttpRequest request) =>
{
    var user = await ResolveUser(request, databasePath);

    return user is null
        ? Error(401, "unauthorized", "Missing or invalid bearer token.")
        : Results.Json(new MeResponse(user.Id, user.Email, user.Name, user.CreatedAt), AppJson.Default.MeResponse);
});

app.MapPost("/api/v1/auth/login", async (HttpRequest request) =>
{
    var input = await request.ReadFromJsonAsync(AppJson.Default.LoginRequest);

    if (input is null)
        return ValidationError([new ValidationIssue("body", "A JSON body is required.")]);

    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
        "select id, email, name, password_hash, created_at from users where email = @email",
        ("email", input.Email));
    await using var reader = await command.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return Error(401, "unauthorized", "Invalid email or password.");

    var id = reader.GetString(0);
    var email = reader.GetString(1);
    var name = reader.GetString(2);
    var passwordHash = reader.GetString(3);

    if (!VerifyPassword(input.Password, passwordHash))
        return Error(401, "unauthorized", "Invalid email or password.");

    var (token, expiresAt) = SignJwt(id, email, name);
    return Results.Json(new LoginResponse(token, "Bearer", expiresAt), AppJson.Default.LoginResponse);
});

await app.RunAsync();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static SqliteConnection OpenRead(string databasePath)
{
    var connection = new SqliteConnection($"Data Source={databasePath};Default Timeout=5;Mode=ReadOnly");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA busy_timeout = 5000";
    command.ExecuteNonQuery();
    return connection;
}

static SqliteCommand Command(
    SqliteConnection connection,
    string sql,
    params (string Name, object? Value)[] arguments)
{
    var command = connection.CreateCommand();
    command.CommandText = sql;

    foreach (var (name, value) in arguments)
        command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);

    return command;
}

static string IsoNow() =>
    DateTimeOffset.UtcNow.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture);

static int ParseInt(string? value, int fallback, int min, int max)
{
    if (!int.TryParse(value, out var parsed))
        return fallback;

    return Math.Clamp(parsed, min, max);
}

static TaskDetail ReadTaskDetail(SqliteDataReader reader) =>
    new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        new UserRef(reader.GetString(6), reader.GetString(7)),
        reader.IsDBNull(8) ? null : new UserRef(reader.GetString(8), reader.GetString(9)),
        reader.GetString(10),
        reader.GetString(11));

static async Task<AuthUserRow?> ResolveUser(HttpRequest request, string databasePath)
{
    var header = request.Headers.Authorization.ToString();

    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;

    var token = header["Bearer ".Length..].Trim();

    if (!TryVerifyJwt(token, out var subject))
        return null;

    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
        "select id, email, name, created_at from users where id = @id",
        ("id", subject));
    await using var reader = await command.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
        return null;

    return new AuthUserRow(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3));
}

static async Task<UserRef?> ResolveAssignee(string databasePath, string? assigneeId)
{
    if (string.IsNullOrWhiteSpace(assigneeId))
        return null;

    await using var connection = OpenRead(databasePath);
    await using var command = Command(
        connection,
        "select id, name from users where id = @id",
        ("id", assigneeId));
    await using var reader = await command.ExecuteReaderAsync();

    return await reader.ReadAsync()
        ? new UserRef(reader.GetString(0), reader.GetString(1))
        : null;
}

static List<ValidationIssue> ValidateTask(
    string title,
    string description,
    int priority,
    string status)
{
    var issues = new List<ValidationIssue>();

    if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        issues.Add(new ValidationIssue("title", "Title must be between 1 and 200 characters."));

    if (description.Length > 2000)
        issues.Add(new ValidationIssue("description", "Description must be at most 2000 characters."));

    if (priority is < 0 or > 3)
        issues.Add(new ValidationIssue("priority", "Priority must be between 0 and 3."));

    if (status is not ("open" or "in_progress" or "done"))
        issues.Add(new ValidationIssue("status", "Status must be open, in_progress, or done."));

    return issues;
}

static List<ValidationIssue> ValidateUpdate(UpdateTaskInput input)
{
    var issues = new List<ValidationIssue>();

    if (input.Title is not null && (input.Title.Length == 0 || input.Title.Length > 200))
        issues.Add(new ValidationIssue("title", "Title must be between 1 and 200 characters."));

    if (input.Description is not null && input.Description.Length > 2000)
        issues.Add(new ValidationIssue("description", "Description must be at most 2000 characters."));

    if (input.Priority is < 0 or > 3)
        issues.Add(new ValidationIssue("priority", "Priority must be between 0 and 3."));

    if (input.Status is not null && input.Status is not ("open" or "in_progress" or "done"))
        issues.Add(new ValidationIssue("status", "Status must be open, in_progress, or done."));

    return issues;
}

static IResult Error(int status, string code, string message) =>
    Results.Json(
        new ErrorResponse(new ErrorBody(code, message)),
        AppJson.Default.ErrorResponse,
        statusCode: status);

static IResult ValidationError(List<ValidationIssue> issues) =>
    Results.Json(
        new ValidationErrorResponse(
            new ErrorBody("validation_error", "Validation failed."),
            issues.ToArray()),
        AppJson.Default.ValidationErrorResponse,
        statusCode: 422);

// ---------------------------------------------------------------------------
// JWT (HS256, same payload shape as Reptar)
// ---------------------------------------------------------------------------

const string JwtSecret = "bench-jwt-secret";
const string JwtIssuer = "reptar-bench";
const string JwtAudience = "reptar-bench";

static (string Token, long ExpiresAt) SignJwt(string subject, string email, string name)
{
    var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var expiresAt = DateTimeOffset.UtcNow.AddDays(3650).ToUnixTimeSeconds();

    var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
    var payload = Base64Url(Encoding.UTF8.GetBytes(
        $"{{\"sub\":{JsonString(subject)},\"email\":{JsonString(email)},\"name\":{JsonString(name)},\"role\":\"user\",\"iat\":{issuedAt},\"exp\":{expiresAt},\"iss\":\"{JwtIssuer}\",\"aud\":\"{JwtAudience}\"}}"));
    var unsigned = $"{header}.{payload}";
    var signature = Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(JwtSecret), Encoding.UTF8.GetBytes(unsigned)));

    return ($"{unsigned}.{signature}", expiresAt);
}

static bool TryVerifyJwt(string token, out string subject)
{
    subject = "";

    var parts = token.Split('.');

    if (parts.Length != 3)
        return false;

    var unsigned = $"{parts[0]}.{parts[1]}";
    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(JwtSecret), Encoding.UTF8.GetBytes(unsigned));

    if (!CryptographicOperations.FixedTimeEquals(expected, Base64UrlDecode(parts[2])))
        return false;

    try
    {
        using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = document.RootElement;

        if (!root.TryGetProperty("sub", out var subjectElement))
            return false;

        subject = subjectElement.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(subject))
            return false;

        var issuer = root.TryGetProperty("iss", out var issuerElement) ? issuerElement.GetString() : null;
        var audience = root.TryGetProperty("aud", out var audienceElement) ? audienceElement.GetString() : null;

        if (issuer != JwtIssuer || audience != JwtAudience)
            return false;

        if (!root.TryGetProperty("exp", out var expElement))
            return false;

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expElement.GetInt64();
    }
    catch (JsonException)
    {
        return false;
    }
}

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] Base64UrlDecode(string value)
{
    var padded = value.Replace('-', '+').Replace('_', '/');
    padded += new string('=', (4 - (padded.Length % 4)) % 4);
    return Convert.FromBase64String(padded);
}

static string JsonString(string? value)
{
    if (value is null)
        return "null";

    var builder = new StringBuilder(value.Length + 2);
    builder.Append('"');

    foreach (var character in value)
    {
        switch (character)
        {
            case '"':
                builder.Append("\\\"");
                break;
            case '\\':
                builder.Append("\\\\");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\r':
                builder.Append("\\r");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            default:
                builder.Append(character);
                break;
        }
    }

    builder.Append('"');
    return builder.ToString();
}

static bool VerifyPassword(string password, string stored)
{
    var parts = stored.Split('$');

    if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
        return false;

    var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
    var salt = Convert.FromBase64String(parts[2]);
    var expected = Convert.FromBase64String(parts[3]);
    var actual = Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password),
        salt,
        iterations,
        HashAlgorithmName.SHA256,
        expected.Length);

    return CryptographicOperations.FixedTimeEquals(actual, expected);
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public sealed record BenchJson(
    string Message,
    bool Ok,
    int Version,
    string[] Tags,
    NestedJson Nested);

public sealed record NestedJson(int A, int B);

public sealed record RouterParams(string OrgId, string ProjectId, string TaskId);

public sealed record ValidateInput(
    string Title,
    string Description,
    int Priority,
    string Status,
    string? AssigneeId);

public sealed record ValidateResponse(bool Ok, string Title, int Priority, string Status);

public sealed record UserRef(string Id, string Name);

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

public sealed record TaskSummary(
    string Id,
    string ProjectId,
    string Title,
    string Description,
    string Status,
    int Priority,
    string? AssigneeId,
    string CreatedAt,
    string UpdatedAt);

public sealed record TaskListResponse(List<TaskSummary> Data, string? NextCursor);

public sealed record CommentItem(
    string Id,
    string TaskId,
    string Body,
    UserRef Author,
    string CreatedAt);

public sealed record CommentListResponse(List<CommentItem> Data, string? NextCursor);

public sealed record MeResponse(string Id, string Email, string Name, string CreatedAt);

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, string TokenType, long ExpiresAt);

public sealed record CreateTaskInput(
    string Title,
    string Description,
    int Priority,
    string Status,
    string? AssigneeId);

public sealed record UpdateTaskInput(
    string? Title,
    string? Description,
    int? Priority,
    string? Status,
    string? AssigneeId);

public sealed record ErrorBody(string Code, string Message);

public sealed record ErrorResponse(ErrorBody Error);

public sealed record ValidationIssue(string Path, string Message);

public sealed record ValidationErrorResponse(ErrorBody Error, ValidationIssue[] Issues);

public sealed record AuthUserRow(string Id, string Email, string Name, string CreatedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchJson))]
[JsonSerializable(typeof(RouterParams))]
[JsonSerializable(typeof(ValidateInput))]
[JsonSerializable(typeof(ValidateResponse))]
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
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ValidationErrorResponse))]
public sealed partial class AppJson : JsonSerializerContext;
