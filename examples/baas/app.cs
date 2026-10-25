#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Threading.Channels;
using Reptar;
using Reptar.Hosting;
using static Reptar.ReptarApi;

// 1. Storage & Database Setup
var dbPath = Environment.GetEnvironmentVariable("BAAS_DB")
    ?? Environment.GetEnvironmentVariable("DATABASE_PATH")
    ?? "reptarbase.db";
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

IDbClient database = !string.IsNullOrWhiteSpace(dbUrl) &&
    (dbUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
     dbUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    ? Db.Postgres(dbUrl)
    : Db.Sqlite(dbPath);

var storageDir = Environment.GetEnvironmentVariable("STORAGE_DIR")
    ?? Environment.GetEnvironmentVariable("STORAGE_PATH")
    ?? "uploads";

var authOptions = new AuthOptions
{
    CookieName = "reptarbase_session",
    SessionDays = 30,
};
Auth.Configure(authOptions);

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
    Storage = Storage.Local(storageDir),
    Auth = authOptions,
});

// 2. Production Middlewares
app.Use(RequestId());
app.Use(Telemetry());
app.Use(Cors("*"));

// 3. Mount Built-in Auth Routes (/api/auth/signup, /api/auth/signin, /api/auth/session, /api/auth/api-keys)
app.Route("/api/auth", Auth.Routes());

// 4. Register Tables & Apply Schemas
app.RegisterSchema(Schema.All);
await app.EnsureCreatedAsync();

// 5. In-Memory Real-Time Event Hub for SSE Streams
var realtimeHub = new RealtimeHub();

// 6. Realtime SSE Change Feed
app.Get("/api/realtime", ctx =>
{
    return ctx.Sse(async (stream, ct) =>
    {
        var channel = Channel.CreateUnbounded<SseMessage>(new UnboundedChannelOptions { SingleWriter = false });
        var subId = realtimeHub.Subscribe(channel.Writer);

        try
        {
            await stream.SendAsync("connected", @event: "ready", cancellationToken: ct);

            while (!ct.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    var msg = await channel.Reader.ReadAsync(timeoutCts.Token);
                    await stream.SendAsync(msg, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Heartbeat ping
                    await stream.PingAsync("ping", ct);
                }
            }
        }
        finally
        {
            realtimeHub.Unsubscribe(subId);
        }
    });
});

// 7. Documents API
// GET /api/documents - List documents with optional status/owner filter and pagination
app.Get("/api/documents", async ctx =>
{
    var status = ctx.Req.QueryValue("status");
    var ownerId = ctx.Req.QueryValue("owner_id");
    var limit = Math.Clamp(int.TryParse(ctx.Req.QueryValue("limit"), out var l) ? l : 50, 1, 100);
    var offset = Math.Max(int.TryParse(ctx.Req.QueryValue("offset"), out var o) ? o : 0, 0);

    var query = ctx.Db.From<DocumentRecord>(Schema.Documents);

    if (!string.IsNullOrEmpty(status))
    {
        query = query.Where(Schema.DocStatus.Eq(status));
    }

    if (!string.IsNullOrEmpty(ownerId))
    {
        query = query.Where(Schema.DocOwnerId.Eq(ownerId));
    }

    query = query.OrderBy(Schema.DocCreatedAt.Desc()).Limit(limit).Offset(offset);
    var docs = await query.ToListAsync();
    return ctx.Json(docs);
});

// POST /api/documents - Create a new document (requires auth)
app.Post<CreateDocumentInput>("/api/documents", async (ctx, input) =>
{
    var user = await Auth.CurrentUser(ctx);
    if (user is null)
        return ctx.Unauthorized("Authentication required to create documents.");

    if (string.IsNullOrWhiteSpace(input.Title))
        return ctx.BadRequest("Document title is required.");

    if (string.IsNullOrWhiteSpace(input.Content))
        return ctx.BadRequest("Document content is required.");

    var now = ctx.Clock.IsoNow();
    var doc = new DocumentRecord(
        Id: Ids.New("doc"),
        OwnerId: user.Id,
        Title: input.Title.Trim(),
        Content: input.Content.Trim(),
        Status: input.Status ?? "draft",
        FileKey: input.FileKey,
        Views: 0,
        CreatedAt: now,
        UpdatedAt: now);

    await ctx.Db.InsertAsync(Schema.Documents, doc);
    await realtimeHub.BroadcastAsync("document_created", ctx.JsonCodec.Serialize(doc));

    return ctx.Json(doc, 201);
});

// GET /api/documents/:id - Retrieve single document and atomically increment view count
app.Get("/api/documents/:id", async ctx =>
{
    var id = ctx.Req.Param("id")!;
    var doc = await ctx.Db.QuerySingleOrDefaultAsync<DocumentRecord>(
        """
        SELECT id as Id, owner_id as OwnerId, title as Title, content as Content,
               status as Status, file_key as FileKey, views as Views,
               created_at as CreatedAt, updated_at as UpdatedAt
        FROM documents
        WHERE id = @id
        """,
        new { id });

    if (doc is null)
        return ctx.NotFound($"Document '{id}' not found.");

    await ctx.Db.ExecuteAsync("UPDATE documents SET views = views + 1 WHERE id = @id", new { id });
    return ctx.Json(doc with { Views = doc.Views + 1 });
});

// PATCH /api/documents/:id - Partially update an existing document (owner or admin)
app.Patch<UpdateDocumentInput>("/api/documents/:id", async (ctx, input) =>
{
    var user = await Auth.CurrentUser(ctx);
    if (user is null)
        return ctx.Unauthorized("Authentication required to modify documents.");

    var id = ctx.Req.Param("id")!;
    var existing = await ctx.Db.QuerySingleOrDefaultAsync<DocumentRecord>(
        """
        SELECT id as Id, owner_id as OwnerId, title as Title, content as Content,
               status as Status, file_key as FileKey, views as Views,
               created_at as CreatedAt, updated_at as UpdatedAt
        FROM documents
        WHERE id = @id
        """,
        new { id });

    if (existing is null)
        return ctx.NotFound($"Document '{id}' not found.");

    if (existing.OwnerId != user.Id && user.Role != "admin")
        return ctx.Forbidden("You can only edit documents you own.");

    var newTitle = input.Title?.Trim() ?? existing.Title;
    var newContent = input.Content?.Trim() ?? existing.Content;
    var newStatus = input.Status ?? existing.Status;
    var newFileKey = input.FileKey ?? existing.FileKey;
    var now = ctx.Clock.IsoNow();

    await ctx.Db.ExecuteAsync(
        """
        UPDATE documents
        SET title = @title, content = @content, status = @status, file_key = @file_key, updated_at = @now
        WHERE id = @id
        """,
        new { title = newTitle, content = newContent, status = newStatus, file_key = newFileKey, now, id });

    var updated = existing with
    {
        Title = newTitle,
        Content = newContent,
        Status = newStatus,
        FileKey = newFileKey,
        UpdatedAt = now
    };

    await realtimeHub.BroadcastAsync("document_updated", ctx.JsonCodec.Serialize(updated));
    return ctx.Json(updated);
});

// DELETE /api/documents/:id - Delete document (owner or admin)
app.Delete("/api/documents/:id", async ctx =>
{
    var user = await Auth.CurrentUser(ctx);
    if (user is null)
        return ctx.Unauthorized("Authentication required to delete documents.");

    var id = ctx.Req.Param("id")!;
    var existing = await ctx.Db.QuerySingleOrDefaultAsync<DocumentRecord>(
        "SELECT id as Id, owner_id as OwnerId, title as Title, content as Content, status as Status, file_key as FileKey, views as Views, created_at as CreatedAt, updated_at as UpdatedAt FROM documents WHERE id = @id",
        new { id });

    if (existing is null)
        return ctx.NotFound($"Document '{id}' not found.");

    if (existing.OwnerId != user.Id && user.Role != "admin")
        return ctx.Forbidden("You can only delete documents you own.");

    await ctx.Db.ExecuteAsync("DELETE FROM documents WHERE id = @id", new { id });
    await realtimeHub.BroadcastAsync("document_deleted", ctx.JsonCodec.Serialize(new { id }));

    return ctx.Json(new { deleted = true, id });
});

// 8. Files API
// POST /api/files/upload - Upload file with magic-byte verification (requires auth)
app.Post("/api/files/upload", async ctx =>
{
    var user = await Auth.CurrentUser(ctx);
    if (user is null)
        return ctx.Unauthorized("Authentication required to upload files.");

    var form = ctx.Req.Form;
    var file = form?.File("file");
    if (file is null)
        return ctx.BadRequest("Missing 'file' field in multipart form-data.");

    // Validate size and format using stack-allocated magic-byte detection
    var check = Files.Check(file, maxBytes: 25 * 1024 * 1024, "image/*", "application/pdf", "text/*");
    if (!check.Ok)
        return check.ToError(ctx);

    var fileId = Ids.New("fil");
    var extension = Path.GetExtension(file.FileName) ?? "";
    var storageKey = $"{fileId}{extension}";
    var contentType = file.SniffedContentType ?? file.ContentType ?? "application/octet-stream";

    await ctx.Storage.PutAsync(storageKey, file.Data, contentType);

    var record = new FileRecord(
        Id: fileId,
        OwnerId: user.Id,
        Filename: file.FileName ?? "unnamed",
        MimeType: contentType,
        SizeBytes: file.Length,
        StorageKey: storageKey,
        CreatedAt: ctx.Clock.IsoNow());

    await ctx.Db.InsertAsync(Schema.Files, record);
    await realtimeHub.BroadcastAsync("file_uploaded", ctx.JsonCodec.Serialize(record));

    return ctx.Json(record, 201);
});

// GET /api/files/:key - Serve stored file with ETag caching
app.Get("/api/files/:key", async ctx =>
{
    var key = ctx.Req.Param("key")!;
    var bytes = await ctx.Storage.GetAsync(key);
    if (bytes is null)
        return ctx.NotFound("File not found.");

    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var etag = $"\"{hash}\"";

    if (ctx.Req.Header("if-none-match") == etag)
    {
        return new ReptarResponse { Status = 304 };
    }

    var sniffedType = Files.Sniff(bytes)?.ContentType ?? "application/octet-stream";
    var res = ctx.Stream(bytes, contentType: sniffedType);
    res.Headers["ETag"] = etag;
    res.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
    return res;
});

// DELETE /api/files/:id - Remove file from storage and database (owner or admin)
app.Delete("/api/files/:id", async ctx =>
{
    var user = await Auth.CurrentUser(ctx);
    if (user is null)
        return ctx.Unauthorized("Authentication required to delete files.");

    var id = ctx.Req.Param("id")!;
    var record = await ctx.Db.QuerySingleOrDefaultAsync<FileRecord>(
        "SELECT id as Id, owner_id as OwnerId, filename as Filename, mime_type as MimeType, size_bytes as SizeBytes, storage_key as StorageKey, created_at as CreatedAt FROM files WHERE id = @id",
        new { id });

    if (record is null)
        return ctx.NotFound($"File '{id}' not found.");

    if (record.OwnerId != user.Id && user.Role != "admin")
        return ctx.Forbidden("You can only delete files you own.");

    await ctx.Storage.DeleteAsync(record.StorageKey);
    await ctx.Db.ExecuteAsync("DELETE FROM files WHERE id = @id", new { id });
    await realtimeHub.BroadcastAsync("file_deleted", ctx.JsonCodec.Serialize(new { id }));

    return ctx.Json(new { deleted = true, id });
});

// 9. Studio Admin Console / Web GUI (OpenShip & PocketBase style)
string GetDashboardHtml()
{
    var candidates = new[]
    {
        "dashboard.html",
        Path.Combine(AppContext.BaseDirectory, "dashboard.html"),
        Path.Combine(Directory.GetCurrentDirectory(), "dashboard.html"),
        Path.Combine(Directory.GetCurrentDirectory(), "examples", "baas", "dashboard.html"),
    };
    foreach (var path in candidates)
    {
        if (File.Exists(path))
            return File.ReadAllText(path);
    }
    return "<!DOCTYPE html><html><body style='background:#050811;color:#fff;font-family:sans-serif;padding:40px;'><h1>ReptarBase Studio</h1><p>dashboard.html not found.</p></body></html>";
}

app.Get("/", ctx => ctx.Html(GetDashboardHtml()));
app.Get("/dashboard", ctx => ctx.Html(GetDashboardHtml()));
app.Get("/_", ctx => ctx.Html(GetDashboardHtml()));

// 10. Health & Service Metadata Probe
app.Get("/health", ctx => Task.FromResult(ctx.Json(new
{
    status = "ok",
    service = "reptarbase",
    version = "1.0.0",
    collections = new[] { "documents", "files", "users", "sessions", "api_keys" }
})));

// 11. Start Server
await app.RunAsync(args);

// ---------------------------------------------------------
// Types & Schema Definitions (Zero-reflection Native AOT)
// ---------------------------------------------------------

public static partial class Schema
{
    // 1. Documents / Records Collection
    public static readonly TableDef Documents = DefineTable("documents",
        Col.Text("id").PrimaryKey(),
        Col.Text("owner_id").NotNull().References(Auth.Users, "id", onDelete: "cascade"),
        Col.Text("title").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("status").NotNull().Default("draft").Indexed(),
        Col.Text("file_key"),
        Col.Int("views").NotNull().Default(0),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").NotNull().DefaultSql("CURRENT_TIMESTAMP")
    );

    // 2. File Upload Metadata Collection
    public static readonly TableDef Files = DefineTable("files",
        Col.Text("id").PrimaryKey(),
        Col.Text("owner_id").NotNull().References(Auth.Users, "id", onDelete: "cascade"),
        Col.Text("filename").NotNull(),
        Col.Text("mime_type").NotNull(),
        Col.Int("size_bytes").NotNull(),
        Col.Text("storage_key").NotNull().Unique(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP")
    );

    // Typed ColumnRef tokens for zero-reflection, typo-free querying
    public static readonly ColumnRef<string> DocId = Documents.Col<string>("id");
    public static readonly ColumnRef<string> DocOwnerId = Documents.Col<string>("owner_id");
    public static readonly ColumnRef<string> DocTitle = Documents.Col<string>("title");
    public static readonly ColumnRef<string> DocStatus = Documents.Col<string>("status");
    public static readonly ColumnRef<string> DocCreatedAt = Documents.Col<string>("created_at");

    public static readonly ColumnRef<string> FileId = Files.Col<string>("id");
    public static readonly ColumnRef<string> FileOwnerId = Files.Col<string>("owner_id");
    public static readonly ColumnRef<string> FileStorageKey = Files.Col<string>("storage_key");

    public static readonly TableDef[] All = [Auth.Users, Auth.Sessions, Auth.ApiKeys, Documents, Files];
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record DocumentRecord(
    string Id,
    string OwnerId,
    string Title,
    string Content,
    string Status,
    string? FileKey,
    int Views,
    string CreatedAt,
    string UpdatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record FileRecord(
    string Id,
    string OwnerId,
    string Filename,
    string MimeType,
    int SizeBytes,
    string StorageKey,
    string CreatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record CreateDocumentInput(
    string Title,
    string Content,
    string? Status = "draft",
    string? FileKey = null);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record UpdateDocumentInput(
    string? Title = null,
    string? Content = null,
    string? Status = null,
    string? FileKey = null);

public sealed class RealtimeHub
{
    private readonly ConcurrentDictionary<string, ChannelWriter<SseMessage>> _subscribers = new();

    public string Subscribe(ChannelWriter<SseMessage> writer)
    {
        var id = Guid.NewGuid().ToString("N");
        _subscribers[id] = writer;
        return id;
    }

    public void Unsubscribe(string id) => _subscribers.TryRemove(id, out _);

    public async ValueTask BroadcastAsync(string eventName, string jsonPayload)
    {
        var msg = new SseMessage(jsonPayload, @event: eventName);
        foreach (var writer in _subscribers.Values)
        {
            try
            {
                await writer.WriteAsync(msg);
            }
            catch
            {
                // Channel closed by subscriber
            }
        }
    }
}
