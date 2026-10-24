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
var Rooms = ReptarApi.DefineTable("rooms", new
{
    Id = ReptarApi.Col.Int("id").PrimaryKey(),
    Name = ReptarApi.Col.Text("name").NotNull(),
    Topic = ReptarApi.Col.Text("topic"),
    CreatedAt = ReptarApi.Col.Text("created_at").Default("CURRENT_TIMESTAMP"),
});

var Messages = ReptarApi.DefineTable("messages", new
{
    Id = ReptarApi.Col.Int("id").PrimaryKey(),
    RoomId = ReptarApi.Col.Int("room_id").NotNull().References(Rooms),
    UserId = ReptarApi.Col.Int("user_id").NotNull(),
    UserName = ReptarApi.Col.Text("user_name").NotNull(),
    Body = ReptarApi.Col.Text("body").NotNull(),
    CreatedAt = ReptarApi.Col.Text("created_at").Default("CURRENT_TIMESTAMP"),
});

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
    await database.InsertAsync(Rooms, new { Name = "Watercooler", Topic = "General banter & watercooler chatter" });
}

// REST Routes for Campfire Chat & Action Cable Broadcasts
app.Get("/health", ctx => Task.FromResult(ctx.Json(new { status = "ok" })));

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

    await ctx.Db.InsertAsync(Rooms, new { Name = input.Name, Topic = input.Topic });
    return ctx.Json(new { name = input.Name, topic = input.Topic }, status: 201);
});

app.Get("/rooms/:id/messages", async ctx =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    var list = await ctx.Db.ListAsync<MessageRecord>(
        Messages,
        clause: "WHERE room_id = @room_id ORDER BY id ASC LIMIT 100",
        param: new { room_id = roomId }
    );
    return ctx.Json(list);
});

app.Post<CreateMessageInput>("/rooms/:id/messages", async (ctx, input) =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);

    if (string.IsNullOrWhiteSpace(input?.Body))
    {
        return ctx.BadRequest("Message body cannot be empty.");
    }

    var userName = input.UserName ?? "Anonymous";
    var userId = input.UserId > 0 ? input.UserId : 1;
    var now = DateTimeOffset.UtcNow.ToString("O");

    await ctx.Db.InsertAsync(Messages, new
    {
        RoomId = roomId,
        UserId = userId,
        UserName = userName,
        Body = input.Body,
        CreatedAt = now,
    });

    var inserted = await ctx.Db.QuerySingleOrDefaultAsync<MessageRecord>(
        "SELECT * FROM \"messages\" WHERE room_id = @room_id ORDER BY id DESC LIMIT 1",
        new { room_id = roomId }
    );

    var messageObj = new CampfireMessage(
        Id: inserted?.Id ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        RoomId: roomId,
        UserId: userId,
        UserName: userName,
        Body: input.Body,
        CreatedAt: now
    );

    var subscriberCount = await campfire.BroadcastMessageCreatedAsync(messageObj);

    return ctx.Json(new
    {
        message = messageObj,
        subscribersMessaged = subscriberCount
    }, status: 201);
});

app.Put<UpdateMessageInput>("/rooms/:id/messages/:messageId", async (ctx, input) =>
{
    var roomId = long.Parse(ctx.Req.Param("id")!);
    var messageId = long.Parse(ctx.Req.Param("messageId")!);

    if (string.IsNullOrWhiteSpace(input?.Body))
    {
        return ctx.BadRequest("Updated body cannot be empty.");
    }

    await ctx.Db.UpdateAsync(Messages, new { Body = input.Body }, messageId);

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
    return ctx.Json(new { status = "broadcasted" });
});

await app.RunAsync(args);

// DTO Records
public sealed record RoomRecord(long Id, string Name, string? Topic, string CreatedAt);
public sealed record MessageRecord(long Id, long RoomId, long UserId, string UserName, string Body, string CreatedAt);
public sealed record CreateRoomInput(string Name, string? Topic);
public sealed record CreateMessageInput(string Body, string? UserName, long UserId);
public sealed record UpdateMessageInput(string Body);
public sealed record TypingInput(long UserId, string? UserName, bool IsTyping);
