namespace ReptarApp.Features;

using Reptar;
using ReptarApp.Schema;

public static class ItemsFeature
{
    public static void Configure(ReptarApp app)
    {
        // Public list of items
        app.Get("/api/items", async ctx =>
        {
            var rows = await ctx.Db.QueryMappedAsync(
                "SELECT id, user_id, title, content, created_at FROM items ORDER BY created_at DESC LIMIT 50");

            var items = rows.Select(r => new ItemRecord(
                (string)r["id"],
                (string)r["user_id"],
                (string)r["title"],
                (string)r["content"],
                (string)r["created_at"]
            )).ToList();

            return ctx.Json(items);
        });

        // Protected create item
        app.Post<CreateItemInput>("/api/items", async (ctx, input) =>
        {
            var user = await ctx.UserAsync();
            if (user is null)
            {
                return ctx.Json(new { error = "Unauthorized" }, 401);
            }

            var id = Ids.New("item");
            var now = DateTime.UtcNow.ToString("O");

            await ctx.Db.ExecuteAsync(
                "INSERT INTO items (id, user_id, title, content, created_at) VALUES (@id, @user_id, @title, @content, @created_at)",
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["user_id"] = user.Id,
                    ["title"] = input.Title,
                    ["content"] = input.Content,
                    ["created_at"] = now,
                });

            var created = new ItemRecord(id, user.Id, input.Title, input.Content, now);
            return ctx.Json(created, 201);
        });

        // Protected delete item
        app.Delete("/api/items/:id", async ctx =>
        {
            var user = await ctx.UserAsync();
            if (user is null)
            {
                return ctx.Json(new { error = "Unauthorized" }, 401);
            }

            var id = ctx.Param("id");
            if (string.IsNullOrEmpty(id))
            {
                return ctx.Json(new { error = "Item ID required" }, 400);
            }

            await ctx.Db.ExecuteAsync(
                "DELETE FROM items WHERE id = @id AND user_id = @user_id",
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["user_id"] = user.Id,
                });

            return ctx.Json(new { success = true });
        });
    }
}
