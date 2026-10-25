using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Reptar;
using static Reptar.ReptarApi;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwUser(
    string Id,
    string Username,
    string Email,
    string PasswordHash,
    string? Bio,
    string? Image,
    string CreatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwProfileRow(
    string Id,
    string Username,
    string? Bio,
    string? Image);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwArticleRow(
    string Id,
    string Slug,
    string Title,
    string Description,
    string Body,
    string CreatedAt,
    string UpdatedAt,
    string AuthorId,
    string Username,
    string? Bio,
    string? Image,
    int FavoritesCount,
    int Favorited,
    int Following);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwTagRow(string Name);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwSlugRow(string Slug);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwTagIdRow(string Id);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwArticleTagRow(string ArticleId, string Name);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwArticleFavoriteRow(
    string ArticleId,
    int FavoritesCount,
    int Favorited);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwTagNameIdRow(string Id, string Name);

public sealed record RwArticleInsert(
    string Id,
    string AuthorId,
    string Slug,
    string Title,
    string Description,
    string Body,
    string CreatedAt,
    string UpdatedAt);

public sealed record RwTagInsert(string Id, string Name);

public sealed record RwArticleTagInsert(
    string Id,
    string ArticleId,
    string TagId);

public sealed record RwCommentInsert(
    string Id,
    string ArticleId,
    string AuthorId,
    string Body,
    string CreatedAt,
    string UpdatedAt);

public sealed record RwFavoriteInsert(
    string Id,
    string ArticleId,
    string UserId,
    string CreatedAt);

public sealed record RwFollowInsert(
    string Id,
    string FollowerId,
    string FollowedId,
    string CreatedAt);

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed record RwCommentRow(
    string Id,
    string Body,
    string CreatedAt,
    string UpdatedAt,
    string AuthorId,
    string Username,
    string? Bio,
    string? Image,
    int Following);

public static class RealWorld
{
    private sealed record TagPlan(string Name, string Id, bool IsNew);

    private static long _commentSequence =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Configure(ReptarApp app)
    {
        app.RegisterSchema(RealWorldSchema.All);
        app.Route("/api", Routes());
    }

    public static async Task EnsureIndexesAsync(ReptarApp app)
    {
        await app.ExecuteAsync(
            "create index if not exists rw_article_tags_article_tag_idx on rw_article_tags(article_id, tag_id)");
        await app.ExecuteAsync(
            "create index if not exists rw_favorites_article_user_idx on rw_favorites(article_id, user_id)");
        await app.ExecuteAsync(
            "create index if not exists rw_follows_follower_followed_idx on rw_follows(follower_id, followed_id)");
        await app.ExecuteAsync(
            "create index if not exists rw_articles_author_created_idx on rw_articles(author_id, created_at)");
    }

    public static ReptarApp Routes() => ReptarApp.Create()
        .Post("/users", Register)
        .Post("/users/login", Login)
        .Get("/user", RealWorldAuth.Require, CurrentUser)
        .Put("/user", RealWorldAuth.Require, UpdateUser)
        .Get("/profiles/:username", RealWorldAuth.Optional, GetProfile)
        .Post("/profiles/:username/follow", RealWorldAuth.Require, FollowProfile)
        .Delete("/profiles/:username/follow", RealWorldAuth.Require, UnfollowProfile)
        .Get("/articles", RealWorldAuth.Optional, ListArticles)
        .Get("/articles/feed", RealWorldAuth.Require, Feed)
        .Get("/articles/:slug", RealWorldAuth.Optional, GetArticle)
        .Post("/articles", RealWorldAuth.Require, CreateArticle)
        .Put("/articles/:slug", RealWorldAuth.Require, UpdateArticle)
        .Delete("/articles/:slug", RealWorldAuth.Require, DeleteArticle)
        .Post("/articles/:slug/favorite", RealWorldAuth.Require, FavoriteArticle)
        .Delete("/articles/:slug/favorite", RealWorldAuth.Require, UnfavoriteArticle)
        .Get("/articles/:slug/comments", RealWorldAuth.Optional, ListComments)
        .Post("/articles/:slug/comments", RealWorldAuth.Require, CreateComment)
        .Delete("/articles/:slug/comments/:id", RealWorldAuth.Require, DeleteComment)
        .Get("/tags", ListTags)
        .Build();

    private static async Task<ReptarResponse> Register(Ctx ctx)
    {
        using var document = TryBody(ctx);
        if (document is null || !TryUser(document, out var user))
            return Error(ctx, 422, "email", "can't be blank");

        var username = TextValue(user, "username");
        var email = TextValue(user, "email");
        var password = TextValue(user, "password");

        if (string.IsNullOrWhiteSpace(username))
            return Error(ctx, 422, "username", "can't be blank");
        if (string.IsNullOrWhiteSpace(email))
            return Error(ctx, 422, "email", "can't be blank");
        if (string.IsNullOrWhiteSpace(password))
            return Error(ctx, 422, "password", "can't be blank");
        if (password.Length < 8)
            return Error(ctx, 422, "password", "is too short");

        username = username.Trim();
        email = email.Trim().ToLowerInvariant();

        if (await FindByUsername(ctx, username) is not null)
            return Error(ctx, 409, "username", "has already been taken");
        if (await FindByEmail(ctx, email) is not null)
            return Error(ctx, 409, "email", "has already been taken");

        var record = new RwUser(
            Ids.New("rwu"),
            username,
            email,
            await ctx.Crypto.HashPassword(password),
            null,
            null,
            ctx.Clock.IsoNow());

        await ctx.Db.ExecuteAsync(
            """
            insert into rw_users
              (id, username, email, password_hash, bio, image, created_at)
            values
              (@id, @username, @email, @passwordHash, @bio, @image, @createdAt)
            """,
            Args(
                ("id", record.Id),
                ("username", record.Username),
                ("email", record.Email),
                ("passwordHash", record.PasswordHash),
                ("bio", record.Bio),
                ("image", record.Image),
                ("createdAt", record.CreatedAt)));

        var token = await RealWorldAuth.IssueToken(ctx, record);
        return ReptarResponse.Json(UserJson(record, token), 201);
    }

    private static async Task<ReptarResponse> Login(Ctx ctx)
    {
        using var document = TryBody(ctx);
        if (document is null || !TryUser(document, out var user))
            return Error(ctx, 422, "email", "can't be blank");

        var email = TextValue(user, "email");
        var password = TextValue(user, "password");

        if (string.IsNullOrWhiteSpace(email))
            return Error(ctx, 422, "email", "can't be blank");
        if (string.IsNullOrWhiteSpace(password))
            return Error(ctx, 422, "password", "can't be blank");

        var record = await FindByEmail(ctx, email.Trim().ToLowerInvariant());

        if (record is null ||
            !await ctx.Crypto.VerifyPassword(password, record.PasswordHash))
        {
            return Error(ctx, 401, "credentials", "invalid");
        }

        var token = await RealWorldAuth.IssueToken(ctx, record);
        return ReptarResponse.Json(UserJson(record, token), 200);
    }

    private static async Task<ReptarResponse> CurrentUser(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var token = await RealWorldAuth.IssueToken(ctx, user);
        return ReptarResponse.Json(UserJson(user, token), 200);
    }

    private static async Task<ReptarResponse> UpdateUser(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        using var document = TryBody(ctx);
        if (document is null || !TryUser(document, out var input))
            return Error(ctx, 422, "user", "is invalid");

        var username = TextValue(input, "username");
        var email = TextValue(input, "email");
        var password = TextValue(input, "password");
        var (hasBio, bio) = NullableText(input, "bio");
        var (hasImage, image) = NullableText(input, "image");

        if (Has(input, "username") && string.IsNullOrWhiteSpace(username))
            return Error(ctx, 422, "username", "can't be blank");
        if (Has(input, "email") && string.IsNullOrWhiteSpace(email))
            return Error(ctx, 422, "email", "can't be blank");
        if (Has(input, "password") &&
            (string.IsNullOrWhiteSpace(password) || password.Length < 8))
        {
            return Error(ctx, 422, "password", "is too short");
        }

        if (Has(input, "username") && !string.Equals(username, user.Username, StringComparison.Ordinal))
        {
            username = username!.Trim();
            var existing = await FindByUsername(ctx, username);
            if (existing is not null && existing.Id != user.Id)
                return Error(ctx, 409, "username", "has already been taken");
        }
        else
        {
            username = user.Username;
        }

        if (Has(input, "email") && !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            email = email!.Trim().ToLowerInvariant();
            var existing = await FindByEmail(ctx, email);
            if (existing is not null && existing.Id != user.Id)
                return Error(ctx, 409, "email", "has already been taken");
        }
        else
        {
            email = user.Email;
        }

        var updated = user with
        {
            Username = username!,
            Email = email!,
            PasswordHash = Has(input, "password")
                ? await ctx.Crypto.HashPassword(password!)
                : user.PasswordHash,
            Bio = hasBio ? Normalize(bio) : user.Bio,
            Image = hasImage ? Normalize(image) : user.Image,
        };

        await ctx.Db.ExecuteAsync(
            """
            update rw_users
            set username = @username,
                email = @email,
                password_hash = @passwordHash,
                bio = @bio,
                image = @image
            where id = @id
            """,
            Args(
                ("id", updated.Id),
                ("username", updated.Username),
                ("email", updated.Email),
                ("passwordHash", updated.PasswordHash),
                ("bio", updated.Bio),
                ("image", updated.Image)));

        var token = await RealWorldAuth.IssueToken(ctx, updated);
        return ReptarResponse.Json(UserJson(updated, token), 200);
    }

    private static async Task<ReptarResponse> GetProfile(Ctx ctx)
    {
        var profile = await FindProfile(ctx, ctx.Req.Param("username")!);

        if (profile is null)
            return Error(ctx, 404, "profile", "not found");

        var following = await IsFollowing(ctx, profile.Id);
        return ReptarResponse.Json(
            $"{{\"profile\":{ProfileJson(profile, following)}}}",
            200);
    }

    private static async Task<ReptarResponse> FollowProfile(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var profile = await FindProfile(ctx, ctx.Req.Param("username")!);
        if (profile is null)
            return Error(ctx, 404, "profile", "not found");

        if (profile.Id == user.Id)
            return ReptarResponse.Json(
                $"{{\"profile\":{ProfileJson(profile, false)}}}",
                200);

        var id = $"rwf:{user.Id}:{profile.Id}";

        if (!await ctx.Db.ExistsAsync(
            RealWorldSchema.Follows,
            "follower_id = @follower and followed_id = @followed",
            Args(("follower", user.Id), ("followed", profile.Id))))
        {
            await ctx.Db.ExecuteAsync(
                """
                insert into rw_follows (id, follower_id, followed_id, created_at)
                values (@id, @follower, @followed, @createdAt)
                """,
                Args(
                    ("id", id),
                    ("follower", user.Id),
                    ("followed", profile.Id),
                    ("createdAt", ctx.Clock.IsoNow())));
        }

        return ReptarResponse.Json(
            $"{{\"profile\":{ProfileJson(profile, true)}}}",
            200);
    }

    private static async Task<ReptarResponse> UnfollowProfile(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var profile = await FindProfile(ctx, ctx.Req.Param("username")!);
        if (profile is null)
            return Error(ctx, 404, "profile", "not found");

        await ctx.Db.DeleteWhereAsync(
            RealWorldSchema.Follows,
            "follower_id = @follower and followed_id = @followed",
            Args(("follower", user.Id), ("followed", profile.Id)));

        return ReptarResponse.Json(
            $"{{\"profile\":{ProfileJson(profile, false)}}}",
            200);
    }

    private static Task<ReptarResponse> ListArticles(Ctx ctx) =>
        ArticleList(ctx, feed: false);

    private static Task<ReptarResponse> Feed(Ctx ctx) =>
        ArticleList(ctx, feed: true);

    private static async Task<ReptarResponse> ArticleList(Ctx ctx, bool feed)
    {
        var viewer = await RealWorldAuth.CurrentUser(ctx);
        var viewerId = viewer?.Id ?? "";

        var tag = ctx.Req.QueryValue("tag");
        var author = ctx.Req.QueryValue("author");
        var favorited = ctx.Req.QueryValue("favorited");
        var limit = QueryInt(ctx, "limit", 20, 1, 100);
        var offset = QueryInt(ctx, "offset", 0, 0, 100_000);

        var where = new List<string>();
        var parameters = new Dictionary<string, object?>
        {
            ["viewerId"] = viewerId,
            ["limit"] = limit,
            ["offset"] = offset,
        };

        if (!string.IsNullOrWhiteSpace(tag))
        {
            where.Add(
                "exists (select 1 from rw_article_tags at join rw_tags t on t.id = at.tag_id where at.article_id = a.id and t.name = @tag)");
            parameters["tag"] = tag;
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            where.Add("u.username = @author");
            parameters["author"] = author;
        }

        if (!string.IsNullOrWhiteSpace(favorited))
        {
            where.Add(
                "a.id in (select f.article_id from rw_favorites f join rw_users fu on fu.id = f.user_id where fu.username = @favorited)");
            parameters["favorited"] = favorited;
        }

        if (feed)
        {
            if (viewer is null)
                return TokenMissing(ctx);

            where.Add(
                "a.author_id in (select fo.followed_id from rw_follows fo where fo.follower_id = @viewerId)");
        }

        var whereSql = where.Count == 0
            ? ""
            : " where " + string.Join(" and ", where);

        var countParameters = new Dictionary<string, object?>(parameters);
        countParameters.Remove("limit");
        countParameters.Remove("offset");
        if (!feed)
            countParameters.Remove("viewerId");
        if (where.Count == 0)
            countParameters.Clear();

        var countRow = await SingleMapped(
            ctx,
            $"select count(*) as count from rw_articles a join rw_users u on u.id = a.author_id{whereSql}",
            countParameters,
            MapCount);

        var listParameters = new Dictionary<string, object?>(parameters);
        if (!whereSql.Contains("@viewerId", StringComparison.Ordinal))
            listParameters.Remove("viewerId");

        var rows = await Mapped(
            ctx,
            $"{ArticleSummarySelect}{whereSql} order by a.created_at desc limit @limit offset @offset",
            listParameters,
            MapArticle);

        var favoriteStats = await FavoriteStats(ctx, rows, viewerId);
        var following = await FollowingSet(ctx, rows, viewerId);
        var tagsByArticle = await TagMap(ctx, rows);

        var articles = new StringBuilder("[");

        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0)
                articles.Append(',');

            var article = rows[index];

            if (favoriteStats.TryGetValue(article.Id, out var stats))
            {
                article = article with
                {
                    FavoritesCount = stats.FavoritesCount,
                    Favorited = stats.Favorited,
                };
            }

            if (following.Contains(article.AuthorId))
                article = article with { Following = 1 };

            var tags = tagsByArticle.TryGetValue(article.Id, out var value)
                ? value
                : [];
            articles.Append(ArticleJson(article, tags, includeBody: false));
        }

        articles.Append(']');

        return ReptarResponse.Json(
            $"{{\"articles\":{articles},\"articlesCount\":{countRow?.Count ?? 0}}}",
            200);
    }

    private static async Task<ReptarResponse> GetArticle(Ctx ctx)
    {
        var viewer = await RealWorldAuth.CurrentUser(ctx);
        var row = await FindArticle(ctx, ctx.Req.Param("slug")!, viewer?.Id ?? "");

        if (row is null)
            return Error(ctx, 404, "article", "not found");

        var tags = await TagList(ctx, row.Id);
        return ReptarResponse.Json(
            $"{{\"article\":{ArticleJson(row, tags, includeBody: true)}}}",
            200);
    }

    private static async Task<ReptarResponse> CreateArticle(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        using var document = TryBody(ctx);
        if (document is null || !TryArticle(document, out var input))
            return Error(ctx, 422, "title", "can't be blank");

        var title = TextValue(input, "title");
        var description = TextValue(input, "description");
        var body = TextValue(input, "body");

        if (string.IsNullOrWhiteSpace(title))
            return Error(ctx, 422, "title", "can't be blank");
        if (string.IsNullOrWhiteSpace(description))
            return Error(ctx, 422, "description", "can't be blank");
        if (string.IsNullOrWhiteSpace(body))
            return Error(ctx, 422, "body", "can't be blank");

        if (input.TryGetProperty("tagList", out var tagListElement) &&
            tagListElement.ValueKind == JsonValueKind.Null)
        {
            return Error(ctx, 422, "tagList", "can't be null");
        }

        var now = ctx.Clock.IsoNow();
        var slug = await UniqueSlug(ctx, title.Trim());

        var article = new RwArticleInsert(
            Ids.New("rwa"),
            user.Id,
            slug,
            title.Trim(),
            description.Trim(),
            body,
            now,
            now);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var tagPlans = await PlanTags(ctx, TagValues(input));
            var statements = new List<DbStatement>
            {
                new(
                    """
                    insert into rw_articles
                      (id, author_id, slug, title, description, body, created_at, updated_at)
                    values
                      (@id, @authorId, @slug, @title, @description, @body, @createdAt, @updatedAt)
                    """,
                    Args(
                        ("id", article.Id),
                        ("authorId", article.AuthorId),
                        ("slug", article.Slug),
                        ("title", article.Title),
                        ("description", article.Description),
                        ("body", article.Body),
                        ("createdAt", article.CreatedAt),
                        ("updatedAt", article.UpdatedAt))),
            };

            foreach (var tag in tagPlans)
            {
                if (tag.IsNew)
                {
                    statements.Add(new DbStatement(
                        "insert into rw_tags (id, name) values (@id, @name)",
                        Args(("id", tag.Id), ("name", tag.Name))));
                }
            }

            foreach (var tag in tagPlans)
            {
                statements.Add(new DbStatement(
                    """
                    insert into rw_article_tags (id, article_id, tag_id)
                    values (@id, @article, @tag)
                    """,
                    Args(
                        ("id", $"{article.Id}:{tag.Name}"),
                        ("article", article.Id),
                        ("tag", tag.Id))));
            }

            try
            {
                await ctx.Db.BatchAsync(statements);
                break;
            }
            catch when (attempt < 2)
            {
                // Another concurrent request took the slug or created a tag.
                slug = $"{slug}-{Ids.New("rwa").ToLowerInvariant()}";
                article = article with { Slug = slug };
            }
        }

        var row = await FindArticle(ctx, slug, user.Id);
        var tags = await TagList(ctx, article.Id);

        return ReptarResponse.Json(
            $"{{\"article\":{ArticleJson(row!, tags, includeBody: true)}}}",
            201);
    }

    private static async Task<ReptarResponse> UpdateArticle(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var row = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (row is null)
            return Error(ctx, 404, "article", "not found");
        if (row.AuthorId != user.Id)
            return Error(ctx, 403, "article", "forbidden");

        using var document = TryBody(ctx);
        if (document is null || !TryArticle(document, out var input))
            return Error(ctx, 422, "article", "is invalid");

        var title = Has(input, "title") ? TextValue(input, "title") : row.Title;
        var description = Has(input, "description") ? TextValue(input, "description") : row.Description;
        var body = Has(input, "body") ? TextValue(input, "body") : row.Body;

        if (Has(input, "title") && string.IsNullOrWhiteSpace(title))
            return Error(ctx, 422, "title", "can't be blank");
        if (Has(input, "description") && string.IsNullOrWhiteSpace(description))
            return Error(ctx, 422, "description", "can't be blank");
        if (Has(input, "body") && string.IsNullOrWhiteSpace(body))
            return Error(ctx, 422, "body", "can't be blank");

        var slug = row.Slug;

        if (Has(input, "title") && !string.Equals(title, row.Title, StringComparison.Ordinal))
        {
            slug = await UniqueSlug(ctx, title!, row.Id);
        }

        await ctx.Db.ExecuteAsync(
            """
            update rw_articles
            set slug = @slug, title = @title, description = @description,
                body = @body, updated_at = @updatedAt
            where id = @id
            """,
            Args(
                ("id", row.Id),
                ("slug", slug),
                ("title", title!.Trim()),
                ("description", description!.Trim()),
                ("body", body!),
                ("updatedAt", ctx.Clock.IsoNow())));

        if (Has(input, "tagList"))
        {
            if (input.GetProperty("tagList").ValueKind == JsonValueKind.Null)
                return Error(ctx, 422, "tagList", "can't be null");

            await ReplaceTags(ctx, row.Id, TagValues(input));
        }

        var updated = await FindArticle(ctx, slug, user.Id);
        var tags = await TagList(ctx, row.Id);

        return ReptarResponse.Json(
            $"{{\"article\":{ArticleJson(updated!, tags, includeBody: true)}}}",
            200);
    }

    private static async Task<ReptarResponse> DeleteArticle(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var row = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (row is null)
            return Error(ctx, 404, "article", "not found");
        if (row.AuthorId != user.Id)
            return Error(ctx, 403, "article", "forbidden");

        await ctx.Db.DeleteWhereAsync(RealWorldSchema.Comments, "article_id = @id", Args(("id", row.Id)));
        await ctx.Db.DeleteWhereAsync(RealWorldSchema.Favorites, "article_id = @id", Args(("id", row.Id)));
        await ctx.Db.DeleteWhereAsync(RealWorldSchema.ArticleTags, "article_id = @id", Args(("id", row.Id)));
        await ctx.Db.DeleteAsync(RealWorldSchema.Articles, row.Id);

        return ctx.Status(204);
    }

    private static async Task<ReptarResponse> FavoriteArticle(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var row = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (row is null)
            return Error(ctx, 404, "article", "not found");

        if (!await ctx.Db.ExistsAsync(
            RealWorldSchema.Favorites,
            "article_id = @article and user_id = @user",
            Args(("article", row.Id), ("user", user.Id))))
        {
            await ctx.Db.ExecuteAsync(
                """
                insert into rw_favorites (id, article_id, user_id, created_at)
                values (@id, @article, @user, @createdAt)
                """,
                Args(
                    ("id", $"rwv:{user.Id}:{row.Id}"),
                    ("article", row.Id),
                    ("user", user.Id),
                    ("createdAt", ctx.Clock.IsoNow())));
        }

        var updated = await FindArticle(ctx, row.Slug, user.Id);
        var tags = await TagList(ctx, row.Id);
        return ReptarResponse.Json(
            $"{{\"article\":{ArticleJson(updated!, tags, includeBody: true)}}}",
            200);
    }

    private static async Task<ReptarResponse> UnfavoriteArticle(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var row = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (row is null)
            return Error(ctx, 404, "article", "not found");

        await ctx.Db.DeleteWhereAsync(
            RealWorldSchema.Favorites,
            "article_id = @article and user_id = @user",
            Args(("article", row.Id), ("user", user.Id)));

        var updated = await FindArticle(ctx, row.Slug, user.Id);
        var tags = await TagList(ctx, row.Id);
        return ReptarResponse.Json(
            $"{{\"article\":{ArticleJson(updated!, tags, includeBody: true)}}}",
            200);
    }

    private static async Task<ReptarResponse> ListComments(Ctx ctx)
    {
        var viewer = await RealWorldAuth.CurrentUser(ctx);
        var article = await FindArticle(ctx, ctx.Req.Param("slug")!, viewer?.Id ?? "");
        if (article is null)
            return Error(ctx, 404, "article", "not found");

        var comments = await CommentRows(ctx, article.Id, viewer?.Id ?? "");
        var builder = new StringBuilder("[");

        for (var index = 0; index < comments.Count; index++)
        {
            if (index > 0)
                builder.Append(',');

            builder.Append(CommentJson(comments[index]));
        }

        builder.Append(']');
        return ReptarResponse.Json($"{{\"comments\":{builder}}}", 200);
    }

    private static async Task<ReptarResponse> CreateComment(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var article = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (article is null)
            return Error(ctx, 404, "article", "not found");

        using var document = TryBody(ctx);
        if (document is null ||
            !document.RootElement.TryGetProperty("comment", out var input))
        {
            return Error(ctx, 422, "body", "can't be blank");
        }

        var body = TextValue(input, "body");
        if (string.IsNullOrWhiteSpace(body))
            return Error(ctx, 422, "body", "can't be blank");

        var now = ctx.Clock.IsoNow();
        var comment = new RwCommentInsert(
            Interlocked.Increment(ref _commentSequence).ToString(),
            article.Id,
            user.Id,
            body,
            now,
            now);

        await ctx.Db.BatchAsync(
            [
                new DbStatement(
                    """
                    insert into rw_comments
                      (id, article_id, author_id, body, created_at, updated_at)
                    values
                      (@id, @article, @author, @body, @createdAt, @updatedAt)
                    """,
                    Args(
                        ("id", comment.Id),
                        ("article", comment.ArticleId),
                        ("author", comment.AuthorId),
                        ("body", comment.Body),
                        ("createdAt", comment.CreatedAt),
                        ("updatedAt", comment.UpdatedAt))),
            ]);

        var row = await CommentRow(ctx, comment.Id, user.Id);
        return ReptarResponse.Json(
            $"{{\"comment\":{CommentJson(row!)}}}",
            201);
    }

    private static async Task<ReptarResponse> DeleteComment(Ctx ctx)
    {
        var user = await RealWorldAuth.RequireUser(ctx);
        if (user is null)
            return TokenMissing(ctx);

        var article = await FindArticle(ctx, ctx.Req.Param("slug")!, user.Id);
        if (article is null)
            return Error(ctx, 404, "article", "not found");

        var comment = await SingleMapped(
            ctx,
            $"{CommentSelect} where c.id = @id and c.article_id = @article",
            Args(
                ("id", ctx.Req.Param("id")!),
                ("article", article.Id),
                ("viewerId", user.Id)),
            MapComment);

        if (comment is null)
            return Error(ctx, 404, "comment", "not found");
        if (comment.AuthorId != user.Id)
            return Error(ctx, 403, "comment", "forbidden");

        await ctx.Db.DeleteAsync(RealWorldSchema.Comments, comment.Id);
        return ctx.Status(204);
    }

    private static async Task<ReptarResponse> ListTags(Ctx ctx)
    {
        var rows = await Mapped(
            ctx,
            "select name from rw_tags order by name",
            null,
            MapTag);

        var builder = new StringBuilder("[");

        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0)
                builder.Append(',');

            builder.Append(Q(rows[index].Name));
        }

        builder.Append(']');
        return ReptarResponse.Json($"{{\"tags\":{builder}}}", 200);
    }

    private const string ArticleSelect = """
        select a.id as "id",
               a.slug as "slug",
               a.title as "title",
               a.description as "description",
               a.body as "body",
               a.created_at as "createdAt",
               a.updated_at as "updatedAt",
               a.author_id as "authorId",
               u.username as "username",
               u.bio as "bio",
               u.image as "image",
               (select count(*) from rw_favorites f where f.article_id = a.id) as "favoritesCount",
               case when exists(select 1 from rw_favorites f where f.article_id = a.id and f.user_id = @viewerId) then 1 else 0 end as "favorited",
               case when exists(select 1 from rw_follows fo where fo.follower_id = @viewerId and fo.followed_id = a.author_id) then 1 else 0 end as "following"
        from rw_articles a
        join rw_users u on u.id = a.author_id
        """;

    // List responses do not include body, favorite counts, or follow state.
    // Those values are fetched in batches for the whole page.
    private const string ArticleSummarySelect = """
        select a.id as "id",
               a.slug as "slug",
               a.title as "title",
               a.description as "description",
               a.created_at as "createdAt",
               a.updated_at as "updatedAt",
               a.author_id as "authorId",
               u.username as "username",
               u.bio as "bio",
               u.image as "image"
        from rw_articles a
        join rw_users u on u.id = a.author_id
        """;

    private const string CommentSelect = """
        select c.id as "id",
               c.body as "body",
               c.created_at as "createdAt",
               c.updated_at as "updatedAt",
               c.author_id as "authorId",
               u.username as "username",
               u.bio as "bio",
               u.image as "image",
               case when exists(select 1 from rw_follows fo where fo.follower_id = @viewerId and fo.followed_id = c.author_id) then 1 else 0 end as "following"
        from rw_comments c
        join rw_users u on u.id = c.author_id
        """;

    private static async Task<RwArticleRow?> FindArticle(
        Ctx ctx,
        string slug,
        string viewerId) =>
        await SingleMapped(
            ctx,
            $"{ArticleSelect} where a.slug = @slug",
            Args(("slug", slug), ("viewerId", viewerId)),
            MapArticle);

    private static async Task<RwCommentRow?> CommentRow(
        Ctx ctx,
        string id,
        string viewerId) =>
        await SingleMapped(
            ctx,
            $"{CommentSelect} where c.id = @id",
            Args(("id", id), ("viewerId", viewerId)),
            MapComment);

    private static async Task<List<RwCommentRow>> CommentRows(
        Ctx ctx,
        string articleId,
        string viewerId) =>
        await Mapped(
            ctx,
            $"{CommentSelect} where c.article_id = @article order by c.created_at asc",
            Args(("article", articleId), ("viewerId", viewerId)),
            MapComment);

    private static async Task<Dictionary<string, RwArticleFavoriteRow>>
        FavoriteStats(
            Ctx ctx,
            IReadOnlyList<RwArticleRow> articles,
            string viewerId)
    {
        var result = new Dictionary<string, RwArticleFavoriteRow>(
            StringComparer.Ordinal);

        if (articles.Count == 0)
            return result;

        const int chunkSize = 50;

        for (var start = 0; start < articles.Count; start += chunkSize)
        {
            var count = Math.Min(chunkSize, articles.Count - start);
            var args = new Dictionary<string, object?>(count + 1)
            {
                ["viewerId"] = viewerId,
            };
            var placeholders = new string[count];

            for (var index = 0; index < count; index++)
            {
                var name = $"article{index}";
                args[name] = articles[start + index].Id;
                placeholders[index] = $"@{name}";
            }

            var rows = await Mapped(
                ctx,
                $"""
                select f.article_id as "articleId",
                       count(*) as "favoritesCount",
                       sum(case when f.user_id = @viewerId then 1 else 0 end) as "favorited"
                from rw_favorites f
                where f.article_id in ({string.Join(", ", placeholders)})
                group by f.article_id
                """,
                args,
                MapArticleFavorite);

            foreach (var row in rows)
                result[row.ArticleId] = row;
        }

        return result;
    }

    private static async Task<HashSet<string>> FollowingSet(
        Ctx ctx,
        IReadOnlyList<RwArticleRow> articles,
        string viewerId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (articles.Count == 0 || string.IsNullOrEmpty(viewerId))
            return result;

        var authors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var article in articles)
        {
            if (seen.Add(article.AuthorId))
                authors.Add(article.AuthorId);
        }

        const int chunkSize = 50;

        for (var start = 0; start < authors.Count; start += chunkSize)
        {
            var count = Math.Min(chunkSize, authors.Count - start);
            var args = new Dictionary<string, object?>(count + 1)
            {
                ["viewerId"] = viewerId,
            };
            var placeholders = new string[count];

            for (var index = 0; index < count; index++)
            {
                var name = $"author{index}";
                args[name] = authors[start + index];
                placeholders[index] = $"@{name}";
            }

            var rows = await Mapped(
                ctx,
                $"""
                select followed_id as "id"
                from rw_follows
                where follower_id = @viewerId
                  and followed_id in ({string.Join(", ", placeholders)})
                """,
                args,
                MapTagId);

            foreach (var row in rows)
                result.Add(row.Id);
        }

        return result;
    }

    private static async Task<string> UniqueSlug(
        Ctx ctx,
        string source,
        string? excludeId = null)
    {
        var baseSlug = Slugs.From(source);
        var prefix = $"{baseSlug}-%";
        var where = excludeId is null
            ? "(slug = @slug or slug like @prefix)"
            : "(slug = @slug or slug like @prefix) and id <> @id";
        var arguments = Args(("slug", baseSlug), ("prefix", prefix));

        if (excludeId is not null)
            arguments["id"] = excludeId;

        var rows = await Mapped(
            ctx,
            $"""select slug as "slug" from rw_articles where {where}""",
            arguments,
            row => new RwSlugRow(Text(row, "slug")));

        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
            used.Add(row.Slug);

        var candidate = baseSlug;

        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            if (!used.Contains(candidate))
                return candidate;

            candidate = $"{baseSlug}-{suffix}";
        }

        return $"{baseSlug}-{Ids.New("s").ToLowerInvariant()}";
    }

    private static async Task<List<TagPlan>> PlanTags(
        Ctx ctx,
        IReadOnlyList<string> tags)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var trimmed = tag.Trim();
            if (seen.Add(trimmed))
                names.Add(trimmed);
        }

        if (names.Count == 0)
            return [];

        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        const int chunkSize = 50;

        for (var start = 0; start < names.Count; start += chunkSize)
        {
            var count = Math.Min(chunkSize, names.Count - start);
            var args = new Dictionary<string, object?>(count);
            var placeholders = new string[count];

            for (var index = 0; index < count; index++)
            {
                var name = $"tag{index}";
                args[name] = names[start + index];
                placeholders[index] = $"@{name}";
            }

            var rows = await Mapped(
                ctx,
                $"""
                select id as "id", name as "name"
                from rw_tags
                where name in ({string.Join(", ", placeholders)})
                """,
                args,
                MapTagNameId);

            foreach (var row in rows)
                existing[row.Name] = row.Id;
        }

        var plans = new List<TagPlan>(names.Count);

        foreach (var name in names)
        {
            plans.Add(existing.TryGetValue(name, out var id)
                ? new TagPlan(name, id, false)
                : new TagPlan(name, Ids.New("rwt"), true));
        }

        return plans;
    }

    private static async Task<Dictionary<string, List<string>>> TagMap(
        Ctx ctx,
        IReadOnlyList<RwArticleRow> articles)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (articles.Count == 0)
            return result;

        const int chunkSize = 50;

        for (var start = 0; start < articles.Count; start += chunkSize)
        {
            var count = Math.Min(chunkSize, articles.Count - start);
            var args = new Dictionary<string, object?>(count);
            var placeholders = new string[count];

            for (var index = 0; index < count; index++)
            {
                var name = $"article{index}";
                args[name] = articles[start + index].Id;
                placeholders[index] = $"@{name}";
            }

            var rows = await Mapped(
                ctx,
                $"""
                select at.article_id as "articleId", t.name as "name"
                from rw_article_tags at
                join rw_tags t on t.id = at.tag_id
                where at.article_id in ({string.Join(", ", placeholders)})
                order by t.name
                """,
                args,
                MapArticleTag);

            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.ArticleId, out var tags))
                {
                    tags = [];
                    result[row.ArticleId] = tags;
                }

                tags.Add(row.Name);
            }
        }

        return result;
    }

    private static async Task<List<string>> TagList(Ctx ctx, string articleId)
    {
        var rows = await Mapped(
            ctx,
            """
            select t.name as "name"
            from rw_article_tags at
            join rw_tags t on t.id = at.tag_id
            where at.article_id = @article
            order by t.name
            """,
            Args(("article", articleId)),
            MapTag);

        var tags = new List<string>();

        foreach (var row in rows)
            tags.Add(row.Name);

        return tags;
    }

    private static async Task ReplaceTags(
        Ctx ctx,
        string articleId,
        IReadOnlyList<string> tags)
    {
        await ctx.Db.DeleteWhereAsync(
            RealWorldSchema.ArticleTags,
            "article_id = @article",
            Args(("article", articleId)));

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in tags)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var trimmed = name.Trim();
            if (!seen.Add(trimmed))
                continue;

            var tag = await SingleMapped(
                ctx,
                "select name as \"name\" from rw_tags where name = @name",
                Args(("name", trimmed)),
                MapTag);

            if (tag is null)
            {
                try
                {
                    await ctx.Db.ExecuteAsync(
                        "insert into rw_tags (id, name) values (@id, @name)",
                        Args(("id", Ids.New("rwt")), ("name", trimmed)));
                }
                catch
                {
                    // Another request may have inserted the same tag.
                }
            }

            await ctx.Db.ExecuteAsync(
                """
                insert into rw_article_tags (id, article_id, tag_id)
                values (@id, @article, @tag)
                """,
                Args(
                    ("id", $"{articleId}:{trimmed}"),
                    ("article", articleId),
                    ("tag", await TagId(ctx, trimmed))));
        }
    }

    private static async Task<string> TagId(Ctx ctx, string name)
    {
        var row = await SingleMapped(
            ctx,
            "select id as \"id\" from rw_tags where name = @name",
            Args(("name", name)),
            MapTagId);

        return row?.Id ?? "";
    }

    private static async Task<RwUser?> FindByUsername(Ctx ctx, string username) =>
        await SingleMapped(
            ctx,
            """
            select id, username, email, password_hash as "passwordHash",
                   bio, image, created_at as "createdAt"
            from rw_users where username = @username
            """,
            Args(("username", username)),
            MapUser);

    private static async Task<RwUser?> FindByEmail(Ctx ctx, string email) =>
        await SingleMapped(
            ctx,
            """
            select id, username, email, password_hash as "passwordHash",
                   bio, image, created_at as "createdAt"
            from rw_users where email = @email
            """,
            Args(("email", email)),
            MapUser);

    private static async Task<RwProfileRow?> FindProfile(Ctx ctx, string username) =>
        await SingleMapped(
            ctx,
            """
            select id, username, bio, image from rw_users where username = @username
            """,
            Args(("username", username)),
            MapProfile);

    private static async Task<bool> IsFollowing(Ctx ctx, string followedId)
    {
        var viewer = await RealWorldAuth.CurrentUser(ctx);

        if (viewer is null)
            return false;

        return await ctx.Db.ExistsAsync(
            RealWorldSchema.Follows,
            "follower_id = @follower and followed_id = @followed",
            Args(("follower", viewer.Id), ("followed", followedId)));
    }

    private static string UserJson(RwUser user, string token) =>
        $"{{\"user\":{{\"email\":{Q(user.Email)},\"token\":{Q(token)},\"username\":{Q(user.Username)},\"bio\":{Q(user.Bio)},\"image\":{Q(user.Image)}}}}}";

    private static string ProfileJson(RwProfileRow profile, bool following) =>
        $"{{\"username\":{Q(profile.Username)},\"bio\":{Q(profile.Bio)},\"image\":{Q(profile.Image)},\"following\":{Bool(following)}}}";

    private static string ArticleJson(
        RwArticleRow article,
        IReadOnlyList<string> tags,
        bool includeBody)
    {
        var body = includeBody
            ? $",\"body\":{Q(article.Body)}"
            : "";
        var tagJson = new StringBuilder("[");

        for (var index = 0; index < tags.Count; index++)
        {
            if (index > 0)
                tagJson.Append(',');

            tagJson.Append(Q(tags[index]));
        }

        tagJson.Append(']');

        return $"{{\"slug\":{Q(article.Slug)},\"title\":{Q(article.Title)},\"description\":{Q(article.Description)}{body},\"tagList\":{tagJson},\"createdAt\":{Q(article.CreatedAt)},\"updatedAt\":{Q(article.UpdatedAt)},\"favorited\":{Bool(article.Favorited != 0)},\"favoritesCount\":{article.FavoritesCount},\"author\":{{\"username\":{Q(article.Username)},\"bio\":{Q(article.Bio)},\"image\":{Q(article.Image)},\"following\":{Bool(article.Following != 0)}}}}}";
    }

    private static string CommentJson(RwCommentRow comment) =>
        $"{{\"id\":{IdJson(comment.Id)},\"createdAt\":{Q(comment.CreatedAt)},\"updatedAt\":{Q(comment.UpdatedAt)},\"body\":{Q(comment.Body)},\"author\":{{\"username\":{Q(comment.Username)},\"bio\":{Q(comment.Bio)},\"image\":{Q(comment.Image)},\"following\":{Bool(comment.Following != 0)}}}}}";

    private static string IdJson(string id) =>
        long.TryParse(id, out _) ? id : Q(id);

    internal static ReptarResponse Error(
        Ctx ctx,
        int status,
        string field,
        string message) =>
        ReptarResponse.Json(
            $"{{\"errors\":{{{Q(field)}:[{Q(message)}]}}}}",
            status);

    private static ReptarResponse TokenMissing(Ctx ctx) =>
        Error(ctx, 401, "token", "is missing");

    private static JsonDocument? TryBody(Ctx ctx)
    {
        try
        {
            return JsonDocument.Parse(ctx.Req.BodyText ?? "{}");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryUser(JsonDocument document, out JsonElement user) =>
        document.RootElement.TryGetProperty("user", out user) &&
        user.ValueKind == JsonValueKind.Object;

    private static bool TryArticle(JsonDocument document, out JsonElement article) =>
        document.RootElement.TryGetProperty("article", out article) &&
        article.ValueKind == JsonValueKind.Object;

    private static bool Has(JsonElement element, string name) =>
        element.TryGetProperty(name, out _);

    private static string? TextValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static (bool HasValue, string? Value) NullableText(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return (false, null);

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (true, null);

        return (true, value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString());
    }

    private static IReadOnlyList<string> TagValues(JsonElement element)
    {
        var values = new List<string>();

        if (!element.TryGetProperty("tagList", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? "");
        }

        return values;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    internal static Dictionary<string, object?> Args(
        params (string Name, object? Value)[] values)
    {
        var arguments = new Dictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in values)
            arguments[name] = value;

        return arguments;
    }

    internal static async Task<T?> SingleMapped<T>(
        Ctx ctx,
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map)
    {
        var rows = await ctx.Db.QueryMappedAsync(sql, param, map);
        return rows.Count == 0 ? default : rows[0];
    }

    internal static Task<List<T>> Mapped<T>(
        Ctx ctx,
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map) =>
        ctx.Db.QueryMappedAsync(sql, param, map);

    internal static RwUser MapUser(IReadOnlyDictionary<string, object?> row) =>
        new(
            Text(row, "id"),
            Text(row, "username"),
            Text(row, "email"),
            Text(row, "passwordHash"),
            Nullable(row, "bio"),
            Nullable(row, "image"),
            Text(row, "createdAt"));

    internal static RwProfileRow MapProfile(
        IReadOnlyDictionary<string, object?> row) =>
        new(
            Text(row, "id"),
            Text(row, "username"),
            Nullable(row, "bio"),
            Nullable(row, "image"));

    internal static RwArticleRow MapArticle(
        IReadOnlyDictionary<string, object?> row) =>
        new(
            Text(row, "id"),
            Text(row, "slug"),
            Text(row, "title"),
            Text(row, "description"),
            Text(row, "body"),
            Text(row, "createdAt"),
            Text(row, "updatedAt"),
            Text(row, "authorId"),
            Text(row, "username"),
            Nullable(row, "bio"),
            Nullable(row, "image"),
            Number(row, "favoritesCount"),
            Number(row, "favorited"),
            Number(row, "following"));

    internal static RwCommentRow MapComment(
        IReadOnlyDictionary<string, object?> row) =>
        new(
            Text(row, "id"),
            Text(row, "body"),
            Text(row, "createdAt"),
            Text(row, "updatedAt"),
            Text(row, "authorId"),
            Text(row, "username"),
            Nullable(row, "bio"),
            Nullable(row, "image"),
            Number(row, "following"));

    internal static RwTagRow MapTag(IReadOnlyDictionary<string, object?> row) =>
        new(Text(row, "name"));

    internal static RwTagIdRow MapTagId(
        IReadOnlyDictionary<string, object?> row) =>
        new(Text(row, "id"));

    internal static RwArticleTagRow MapArticleTag(
        IReadOnlyDictionary<string, object?> row) =>
        new(Text(row, "articleId"), Text(row, "name"));

    internal static RwArticleFavoriteRow MapArticleFavorite(
        IReadOnlyDictionary<string, object?> row) =>
        new(
            Text(row, "articleId"),
            Number(row, "favoritesCount"),
            Number(row, "favorited"));

    internal static RwTagNameIdRow MapTagNameId(
        IReadOnlyDictionary<string, object?> row) =>
        new(Text(row, "id"), Text(row, "name"));

    internal static DbCount MapCount(IReadOnlyDictionary<string, object?> row) =>
        new(Number(row, "count"));

    private static string Text(
        IReadOnlyDictionary<string, object?> row,
        string name) =>
        row.TryGetValue(name, out var value) && value is not null
            ? value.ToString() ?? ""
            : "";

    private static string? Nullable(
        IReadOnlyDictionary<string, object?> row,
        string name) =>
        row.TryGetValue(name, out var value) && value is not null
            ? value.ToString()
            : null;

    private static int Number(
        IReadOnlyDictionary<string, object?> row,
        string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
            return 0;

        return value switch
        {
            int number => number,
            long number => (int)number,
            bool boolean => boolean ? 1 : 0,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : 0,
        };
    }

    private static string Q(string? value)
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
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
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
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static int QueryInt(
        Ctx ctx,
        string name,
        int fallback,
        int min,
        int max)
    {
        var raw = ctx.Req.QueryValue(name);

        if (!int.TryParse(raw, out var value))
            return fallback;

        return Math.Clamp(value, min, max);
    }
}

public static class RealWorldAuth
{
    private static JwtOptions Options(Ctx ctx) => new()
    {
        Audience = "reptar-realworld",
        Issuer = "reptar-realworld",
        LifetimeMinutes = 60 * 24 * 7,
        Secret = ctx.Environment.Get("REALWORLD_JWT_SECRET")
            ?? "reptar-realworld-dev-secret",
    };

    public static readonly ReptarMiddleware Optional = async (ctx, next) =>
    {
        await CurrentUser(ctx);
        return await next();
    };

    public static readonly ReptarMiddleware Require = async (ctx, next) =>
    {
        var user = await CurrentUser(ctx);

        if (user is null)
            return RealWorld.Error(ctx, 401, "token", "is missing");

        return await next();
    };

    public static async Task<RwUser?> CurrentUser(Ctx ctx)
    {
        var token = Jwt.ExtractToken(ctx.Req);

        if (token is null)
            return null;

        var payload = await Jwt.VerifyAsync(ctx.Crypto, Options(ctx), token);

        if (payload is null)
            return null;

        var user = await RealWorld.SingleMapped(
            ctx,
            """
            select id, username, email, password_hash as "passwordHash",
                   bio, image, created_at as "createdAt"
            from rw_users where id = @id
            """,
            RealWorld.Args(("id", payload.Subject)),
            RealWorld.MapUser);

        if (user is null)
            return null;

        ctx.User = new SessionUser(user.Id, user.Email, user.Username);
        ctx.Claims = payload;
        return user;
    }

    public static async Task<RwUser?> RequireUser(Ctx ctx)
    {
        var user = await CurrentUser(ctx);

        if (user is null)
            ctx.SetHeader("WWW-Authenticate", "Token");

        return user;
    }

    public static Task<string> IssueToken(Ctx ctx, RwUser user)
    {
        var options = Options(ctx);
        var payload = Jwt.CreatePayload(
            new SessionUser(user.Id, user.Email, user.Username),
            lifetime: TimeSpan.FromMinutes(options.LifetimeMinutes));

        return Jwt.SignAsync(ctx.Crypto, options, payload);
    }
}
