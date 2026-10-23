public static class Posts
{
    public sealed record CreatePost(string Title, string Content, string? Excerpt = null);

    public sealed record UpdatePost(
        string? Title = null,
        string? Content = null,
        string? Excerpt = null);

    public sealed record Post(
        string Id,
        string AuthorId,
        string Slug,
        string Title,
        string Content,
        string? Excerpt,
        int Published,
        string CreatedAt,
        string UpdatedAt);

    public static readonly Validator<CreatePost> CreateSchema = Rule<CreatePost>()
        .Prop(x => x.Title, rules => rules.Min(3).Max(120))
        .Prop(x => x.Content, rules => rules.Min(1))
        .Prop(x => x.Excerpt, rules => rules.Max(300).Optional());

    public static readonly Validator<UpdatePost> UpdateSchema = Rule<UpdatePost>()
        .Prop(x => x.Title, rules => rules.Min(3).Max(120).Optional())
        .Prop(x => x.Content, rules => rules.Min(1).Optional())
        .Prop(x => x.Excerpt, rules => rules.Max(300).Optional());

    public static ReptarApp Routes() => ReptarApp.Create()
        .Get("/", async ctx => ctx.Json(
            await ctx.Db.ListAsync<Post>(
                Schema.Posts,
                "where published = 1 order by created_at desc")))
            .Produces<Post[]>(200)

        .Get("/mine", Auth.RequireUser, async ctx =>
        {
            var posts = await ctx.Db.ListAsync<Post>(
                Schema.Posts,
                "where author_id = @authorId order by updated_at desc",
                new { authorId = ctx.User!.Id });

            return ctx.Json(posts);
        })
            .Produces<Post[]>(200)
            .Produces<ErrorBody>(401)

        .Get("/:id", async ctx =>
        {
            var post = await ctx.Db.GetAsync<Post>(Schema.Posts, ctx.Req.Param("id")!);

            return post is null
                ? ctx.NotFound("Post not found.")
                : ctx.Json(post);
        })
            .Produces<Post>(200)
            .Produces<ErrorBody>(404)

        .Post("/", Auth.RequireUser, CreateSchema, async (ctx, input) =>
        {
            var now = ctx.Clock.IsoNow();

            var post = new Post(
                Id: Ids.New("post"),
                AuthorId: ctx.User!.Id,
                Slug: Text.Slugify(input.Title),
                Title: input.Title.Trim(),
                Content: input.Content,
                Excerpt: input.Excerpt?.Trim(),
                Published: 0,
                CreatedAt: now,
                UpdatedAt: now);

            await ctx.Db.InsertAsync(Schema.Posts, post);

            return ctx.Json(post, 201);
        })
            .Produces<Post>(201)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(401)

        .Put("/:id", Auth.RequireUser, UpdateSchema, async (ctx, input) =>
        {
            var post = await ctx.Db.GetAsync<Post>(Schema.Posts, ctx.Req.Param("id")!);

            if (post is null)
                return ctx.NotFound("Post not found.");

            if (post.AuthorId != ctx.User!.Id)
                return ctx.Forbidden();

            var updated = post with
            {
                Title = input.Title?.Trim() ?? post.Title,
                Content = input.Content ?? post.Content,
                Excerpt = input.Excerpt?.Trim() ?? post.Excerpt,
                UpdatedAt = ctx.Clock.IsoNow(),
            };

            await ctx.Db.UpdateAsync(Schema.Posts, updated, updated.Id);

            return ctx.Json(updated);
        })
            .Produces<Post>(200)
            .Produces<ValidationError>(400)
            .Produces<ErrorBody>(401)
            .Produces<ErrorBody>(403)
            .Produces<ErrorBody>(404)

        .Delete("/:id", Auth.RequireUser, async ctx =>
        {
            var post = await ctx.Db.GetAsync<Post>(Schema.Posts, ctx.Req.Param("id")!);

            if (post is null)
                return ctx.NotFound("Post not found.");

            if (post.AuthorId != ctx.User!.Id)
                return ctx.Forbidden();

            await ctx.Db.DeleteAsync(Schema.Posts, post.Id);

            return ctx.Status(204);
        })
            .Produces(204)
            .Produces<ErrorBody>(401)
            .Produces<ErrorBody>(403)
            .Produces<ErrorBody>(404)
            .Build();
}
