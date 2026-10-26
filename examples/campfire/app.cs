#:project ../../framework/Reptar/Reptar.csproj
#:project ../../framework/Reptar.Kestrel/Reptar.Kestrel.csproj

using Reptar;
using Reptar.Hosting;
using Reptar.Realtime;

var database = Db.Sqlite("campfire.db");

var app = ReptarApp.Create(new ReptarConfig
{
    Database = database,
});

// Schema Definitions
var Rooms = ReptarApi.DefineTable("rooms",
    ReptarApi.Col.Int("id").PrimaryKey(),
    ReptarApi.Col.Text("name").NotNull(),
    ReptarApi.Col.Text("topic"),
    ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
);

var Messages = ReptarApi.DefineTable("messages",
    ReptarApi.Col.Int("id").PrimaryKey(),
    ReptarApi.Col.Int("room_id").NotNull().References(Rooms),
    ReptarApi.Col.Int("user_id").NotNull(),
    ReptarApi.Col.Text("user_name").NotNull(),
    ReptarApi.Col.Text("body").NotNull(),
    ReptarApi.Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
);

app.RegisterSchema(Rooms, Messages);
await app.EnsureCreatedAsync();

// Mount Action Cable / Campfire Real-Time Hub on /cable
var campfire = app.UseCampfire("/cable", hub =>
{
    hub.Server.AuthorizeSubscription = (conn, cmd) =>
    {
        // Allow all room and system streams
        return Task.FromResult(true);
    };
});

// Seed default room if empty
var existingRooms = await database.ListAsync<RoomRecord>(Rooms, limit: 1);
if (existingRooms.Count == 0)
{
    await database.InsertAsync(Rooms, new Dictionary<string, object?> { ["name"] = "Watercooler", ["topic"] = "General banter & watercooler chatter" });
}

// REST Routes for Campfire Chat & Action Cable Broadcasts
app.Get("/health", ctx => Task.FromResult(ctx.JsonRaw("{\"status\":\"ok\"}")));

app.Get("/rooms", async ctx =>
{
    var list = await ctx.Db.ListAsync<RoomRecord>(Rooms);
    return ctx.Json(list);
});

app.Post<CreateRoomInput>("/rooms", async (ctx, input) =>
{
    if (string.IsNullOrWhiteSpace(input?.Name))
    {
        return ctx.BadRequest("Room name is required.");
    }

    await ctx.Db.InsertAsync(Rooms, new Dictionary<string, object?> { ["name"] = input.Name, ["topic"] = input.Topic });
    return ctx.JsonRaw($"{{\"name\":\"{input.Name}\",\"topic\":\"{input.Topic}\"}}", status: 201);
});

app.Get("/rooms/:id/messages", async ctx =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    var list = await ctx.Db.ListAsync<MessageRecord>(
        Messages,
        clause: "WHERE room_id = @room_id ORDER BY id ASC LIMIT 100",
        param: new Dictionary<string, object?> { ["room_id"] = roomId }
    );
    return ctx.Json(list);
});

app.Post("/rooms/:id/messages", async ctx =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    string? body = null;
    string? userName = null;
    long userId = 1;

    var contentType = ctx.Req.Headers.TryGetValue("content-type", out var ct) ? ct : "";
    if (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
    {
        var form = ParseFormUrlEncoded(ctx.Req.BodyText);
        body = form.TryGetValue("message[body]", out var b1) ? b1
             : form.TryGetValue("body", out var b2) ? b2 : null;
        userName = form.TryGetValue("user_name", out var u) ? u : "Anonymous";
    }
    else
    {
        try
        {
            var jsonInput = !string.IsNullOrWhiteSpace(ctx.Req.BodyText)
                ? ctx.JsonCodec.Deserialize<CreateMessageInput>(ctx.Req.BodyText)
                : null;
            body = jsonInput?.Body;
            userName = jsonInput?.UserName;
            userId = jsonInput?.UserId > 0 ? jsonInput.UserId : 1;
        }
        catch
        {
            body = ctx.Req.BodyText;
        }
    }

    if (string.IsNullOrWhiteSpace(body))
    {
        return ctx.BadRequest("Message body cannot be empty.");
    }

    userName ??= "Anonymous";
    var now = DateTimeOffset.UtcNow.ToString("O");

    await ctx.Db.InsertAsync(Messages, new Dictionary<string, object?>
    {
        ["room_id"] = roomId,
        ["user_id"] = userId,
        ["user_name"] = userName,
        ["body"] = body,
        ["created_at"] = now,
    });

    var messageId = await ctx.Db.QuerySingleOrDefaultAsync<long>("SELECT last_insert_rowid()");
    if (messageId <= 0)
    {
        var inserted = await ctx.Db.QuerySingleOrDefaultAsync<MessageRecord>(
            "SELECT * FROM \"messages\" WHERE room_id = @room_id ORDER BY id DESC LIMIT 1",
            new Dictionary<string, object?> { ["room_id"] = roomId }
        );
        messageId = inserted?.Id ?? 0;
        if (messageId <= 0)
        {
            messageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    var messageObj = new CampfireMessage(
        Id: messageId,
        RoomId: roomId,
        UserId: userId,
        UserName: userName,
        Body: body,
        CreatedAt: now
    );

    var subscriberCount = await campfire.BroadcastMessageCreatedAsync(messageObj);

    var accept = ctx.Req.Headers.TryGetValue("accept", out var acc) ? acc : "";
    if (accept.Contains("turbo-stream", StringComparison.OrdinalIgnoreCase))
    {
        var turboHtml = $"<turbo-stream action=\"append\" target=\"messages\"><template><div id=\"message_{messageId}\" data-message-id=\"{messageId}\" class=\"message\"><div class=\"message__content\">{body}</div></div></template></turbo-stream>";
        return ctx.Text(turboHtml, status: 200, contentType: "text/vnd.turbo-stream.html; charset=utf-8");
    }

    var messageJson = ctx.JsonCodec.Serialize(messageObj);
    return ctx.JsonRaw($"{{\"message\":{messageJson},\"subscribersMessaged\":{subscriberCount}}}", status: 201);
});

app.Put<UpdateMessageInput>("/rooms/:id/messages/:messageId", async (ctx, input) =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    var messageId = long.Parse(ctx.Req.Param("messageId")!);

    if (string.IsNullOrWhiteSpace(input?.Body))
    {
        return ctx.BadRequest("Updated body cannot be empty.");
    }

    await ctx.Db.UpdateAsync(Messages, new Dictionary<string, object?> { ["body"] = input.Body }, messageId);

    var existing = await ctx.Db.GetAsync<MessageRecord>(Messages, messageId);
    if (existing is null)
    {
        return ctx.NotFound("Message not found.");
    }

    var messageObj = new CampfireMessage(
        Id: messageId,
        RoomId: roomId,
        UserId: existing.UserId,
        UserName: existing.UserName,
        Body: input.Body,
        CreatedAt: existing.CreatedAt
    );

    await campfire.BroadcastMessageUpdatedAsync(messageObj);
    return ctx.Json(messageObj);
});

app.Delete("/rooms/:id/messages/:messageId", async ctx =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    var messageId = long.Parse(ctx.Req.Param("messageId")!);

    await ctx.Db.DeleteAsync(Messages, messageId);
    await campfire.BroadcastMessageDeletedAsync(roomId, messageId);

    return ctx.Status(204);
});

app.Post<TypingInput>("/rooms/:id/typing", async (ctx, input) =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);

    var status = new CampfireTypingStatus(
        RoomId: roomId,
        UserId: input?.UserId ?? 1,
        UserName: input?.UserName ?? "Anonymous",
        IsTyping: input?.IsTyping ?? true
    );

    await campfire.BroadcastTypingAsync(status);
    return ctx.Json(new Dictionary<string, string> { ["status"] = "broadcasted" });
});

static Dictionary<string, string> ParseFormUrlEncoded(string? body)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(body)) return dict;
    foreach (var pair in body.Split('&'))
    {
        var idx = pair.IndexOf('=');
        if (idx > 0)
        {
            var key = System.Net.WebUtility.UrlDecode(pair[..idx]);
            var val = System.Net.WebUtility.UrlDecode(pair[(idx + 1)..]);
            dict[key] = val;
        }
    }
    return dict;
}

await app.RunAsync(args);

// DTO Records
public sealed record RoomRecord(long Id, string Name, string? Topic, string CreatedAt);
public sealed record MessageRecord(long Id, long RoomId, long UserId, string UserName, string Body, string CreatedAt);
public sealed record CreateRoomInput(string Name, string? Topic);
public sealed record CreateMessageInput(string Body, string? UserName, long UserId);
public sealed record UpdateMessageInput(string Body);
public sealed record TypingInput(long UserId, string? UserName, bool IsTyping);
