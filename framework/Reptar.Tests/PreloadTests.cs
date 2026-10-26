using static Reptar.ReptarApi;

namespace Reptar.Tests;

[ReptarEntity("preload_users")]
public sealed partial record PreloadUser(string Id, string Name);

[ReptarEntity("preload_posts")]
public sealed partial record PreloadPost(string Id, string AuthorId, string Title)
{
    public PreloadUser? Author { get; init; }
    public List<PreloadComment> Comments { get; init; } = [];
}

[ReptarEntity("preload_comments")]
public sealed partial record PreloadComment(string Id, string PostId, string Body);

public sealed class PreloadTests
{
    private static readonly TableDef Users = TableDef.Define("preload_users",
        Col.Text("id").PrimaryKey(),
        Col.Text("name").NotNull());

    private static readonly TableDef Posts = TableDef.Define("preload_posts",
        Col.Text("id").PrimaryKey(),
        Col.Text("author_id").NotNull().Indexed(),
        Col.Text("title").NotNull());

    private static readonly TableDef Comments = TableDef.Define("preload_comments",
        Col.Text("id").PrimaryKey(),
        Col.Text("post_id").NotNull().Indexed(),
        Col.Text("body").NotNull());

    [Fact]
    public async Task Preload_belongs_to_and_has_many_batch_relations()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Users, Posts, Comments);

        await db.InsertAsync(Users, new PreloadUser("u1", "Alice"));
        await db.InsertAsync(Users, new PreloadUser("u2", "Bob"));
        await db.InsertAsync(Posts, new PreloadPost("p1", "u1", "First"));
        await db.InsertAsync(Posts, new PreloadPost("p2", "u2", "Second"));
        await db.InsertAsync(Comments, new PreloadComment("c1", "p1", "Nice"));
        await db.InsertAsync(Comments, new PreloadComment("c2", "p1", "Agreed"));

        var author = Relation.BelongsTo<PreloadPost, PreloadUser, string>(
            post => post.AuthorId,
            Users,
            Users.Col<string>("id"),
            user => user.Id,
            (post, user) => post with { Author = user });

        var comments = Relation.HasMany<PreloadPost, PreloadComment, string>(
            post => post.Id,
            Comments,
            Comments.Col<string>("post_id"),
            comment => comment.PostId,
            (post, rows) => post with { Comments = rows.ToList() });

        var posts = await db.From<PreloadPost>(Posts)
            .OrderBy(Posts.Col<string>("id").Asc())
            .Preload(author)
            .Preload(comments)
            .ToListAsync();

        Assert.Equal(2, posts.Count);
        Assert.Equal("Alice", posts[0].Author?.Name);
        Assert.Equal("Bob", posts[1].Author?.Name);
        Assert.Equal(2, posts[0].Comments.Count);
        Assert.Empty(posts[1].Comments);
    }

    [Fact]
    public async Task Preload_handles_missing_related_rows()
    {
        using var db = new SqliteDbClient(":memory:");
        await db.EnsureCreatedAsync(Users, Posts);

        await db.InsertAsync(Posts, new PreloadPost("p1", "missing", "Orphan"));

        var author = Relation.BelongsTo<PreloadPost, PreloadUser, string>(
            post => post.AuthorId,
            Users,
            Users.Col<string>("id"),
            user => user.Id,
            (post, user) => post with { Author = user });

        var post = await db.From<PreloadPost>(Posts)
            .Preload(author)
            .FirstOrDefaultAsync();

        Assert.NotNull(post);
        Assert.Null(post!.Author);
    }
}
