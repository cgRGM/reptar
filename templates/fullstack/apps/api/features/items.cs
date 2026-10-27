namespace {{PROJECT_PASCAL}}.Features;

using Reptar;
using {{PROJECT_PASCAL}}.Schema;

public static class ItemsFeature
{
    public static void Configure(ReptarApp app)
    {
        // Public feed — fluent query over the TableDef with generated
        // ColumnRef tokens (no strings, no reflection).
        app.Get("/api/items", async ctx =>
        {
            var items = await ctx.Db
                .From<Item>(AppSchema.Items)
                .OrderBy(AppSchema.ItemsCols.CreatedAt.Desc())
                .Limit(50)
                .ToListAsync();

            return ctx.Json(items);
        })
        .Produces<Item[]>(200);

        // Protected write with Rule<T> validation — the same validators that
        // regenerate the frontend schemas.
        app.Post("/api/items",
            ReptarApi.Rule<CreateItemInput>()
                .Prop(x => x.Title, r => r.NotNull().Min(1).Max(120))
                .Prop(x => x.Content, r => r.NotNull().Min(1).Max(2000)),
            async (ctx, input) =>
            {
                var user = await ctx.UserAsync();
                if (user is null)
                {
                    return ctx.Unauthorized();
                }

                var item = new Item(
                    Ids.New("item"),
                    user.Id,
                    input.Title,
                    input.Content,
                    DateTimeOffset.UtcNow.ToString("O"));

                await ctx.Db.InsertAsync(AppSchema.Items, item);
                return ctx.Json(item, 201);
            })
            .Produces<Item>(201)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(401)
            // One line exports this route to OpenAI/Anthropic/Gemini tool schemas.
            .AsAiTool("create_item", "Create an item for the signed-in user");

        // Protected delete, scoped to the owner via typed filters.
        app.Delete("/api/items/:id", async ctx =>
        {
            var user = await ctx.UserAsync();
            if (user is null)
            {
                return ctx.Unauthorized();
            }

            var deleted = await ctx.Db.DeleteWhereAsync(
                AppSchema.Items,
                AppSchema.ItemsCols.Id.Eq(ctx.Param("id")!) &
                AppSchema.ItemsCols.UserId.Eq(user.Id));

            return deleted > 0
                ? ctx.Json(new ActionSuccessResponse(true))
                : ctx.NotFound("Item not found.");
        })
        .Produces<ActionSuccessResponse>(200)
        .Produces<ErrorBody>(401)
        .Produces<ErrorBody>(404);
    }
}
