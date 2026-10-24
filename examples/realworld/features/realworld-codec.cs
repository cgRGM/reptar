using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Reptar;

public sealed class RealWorldJsonCodec : IJsonCodec
{
    public string Serialize<T>(T value)
    {
        if (value is RwUser user)
            return User(user);
        if (value is RwArticleInsert article)
            return Article(article);
        if (value is RwTagInsert tag)
            return Tag(tag);
        if (value is RwArticleTagInsert articleTag)
            return ArticleTag(articleTag);
        if (value is RwCommentInsert comment)
            return Comment(comment);
        if (value is RwFavoriteInsert favorite)
            return Favorite(favorite);
        if (value is RwFollowInsert follow)
            return Follow(follow);
        if (value is ErrorBody error)
            return $"{{\"code\":{Q(error.Code)},\"message\":{Q(error.Message)},\"requestId\":{Q(error.RequestId)}}}";

        throw new NotSupportedException(
            $"RealWorldJsonCodec cannot serialize {typeof(T).Name}.");
    }

    public T? Deserialize<T>(string json)
    {
        if (typeof(T) == typeof(List<RwUser>))
            return (T)(object)Users(json);
        if (typeof(T) == typeof(List<RwProfileRow>))
            return (T)(object)Profiles(json);
        if (typeof(T) == typeof(List<RwArticleRow>))
            return (T)(object)Articles(json);
        if (typeof(T) == typeof(List<RwTagRow>))
            return (T)(object)Tags(json);
        if (typeof(T) == typeof(List<RwTagIdRow>))
            return (T)(object)TagIds(json);
        if (typeof(T) == typeof(List<RwArticleTagRow>))
            return (T)(object)ArticleTags(json);
        if (typeof(T) == typeof(List<RwCommentRow>))
            return (T)(object)Comments(json);
        if (typeof(T) == typeof(List<DbCount>))
            return (T)(object)Counts(json);

        throw new NotSupportedException(
            $"RealWorldJsonCodec cannot deserialize {typeof(T).Name}.");
    }

    private static List<RwUser> Users(string json)
    {
        var rows = new List<RwUser>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            rows.Add(new RwUser(
                Str(item, "id"),
                Str(item, "username"),
                Str(item, "email"),
                Str(item, "passwordHash"),
                NullableStr(item, "bio"),
                NullableStr(item, "image"),
                Str(item, "createdAt")));
        }

        return rows;
    }

    private static List<RwProfileRow> Profiles(string json)
    {
        var rows = new List<RwProfileRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            rows.Add(new RwProfileRow(
                Str(item, "id"),
                Str(item, "username"),
                NullableStr(item, "bio"),
                NullableStr(item, "image")));
        }

        return rows;
    }

    private static List<RwArticleRow> Articles(string json)
    {
        var rows = new List<RwArticleRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            rows.Add(new RwArticleRow(
                Str(item, "id"),
                Str(item, "slug"),
                Str(item, "title"),
                Str(item, "description"),
                Str(item, "body"),
                Str(item, "createdAt"),
                Str(item, "updatedAt"),
                Str(item, "authorId"),
                Str(item, "username"),
                NullableStr(item, "bio"),
                NullableStr(item, "image"),
                Int(item, "favoritesCount"),
                Int(item, "favorited"),
                Int(item, "following")));
        }

        return rows;
    }

    private static List<RwTagRow> Tags(string json)
    {
        var rows = new List<RwTagRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
            rows.Add(new RwTagRow(Str(item, "name")));

        return rows;
    }

    private static List<RwTagIdRow> TagIds(string json)
    {
        var rows = new List<RwTagIdRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
            rows.Add(new RwTagIdRow(Str(item, "id")));

        return rows;
    }

    private static List<RwArticleTagRow> ArticleTags(string json)
    {
        var rows = new List<RwArticleTagRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
            rows.Add(new RwArticleTagRow(
                Str(item, "articleId"),
                Str(item, "name")));

        return rows;
    }

    private static List<RwCommentRow> Comments(string json)
    {
        var rows = new List<RwCommentRow>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            rows.Add(new RwCommentRow(
                Str(item, "id"),
                Str(item, "body"),
                Str(item, "createdAt"),
                Str(item, "updatedAt"),
                Str(item, "authorId"),
                Str(item, "username"),
                NullableStr(item, "bio"),
                NullableStr(item, "image"),
                Int(item, "following")));
        }

        return rows;
    }

    private static List<DbCount> Counts(string json)
    {
        var rows = new List<DbCount>();

        using var document = JsonDocument.Parse(json);

        foreach (var item in document.RootElement.EnumerateArray())
            rows.Add(new DbCount(Int(item, "count")));

        return rows;
    }

    private static string User(RwUser user) =>
        $"{{\"id\":{Q(user.Id)},\"username\":{Q(user.Username)},\"email\":{Q(user.Email)},\"password_hash\":{Q(user.PasswordHash)},\"bio\":{Q(user.Bio)},\"image\":{Q(user.Image)},\"created_at\":{Q(user.CreatedAt)}}}";

    private static string Article(RwArticleInsert article) =>
        $"{{\"id\":{Q(article.Id)},\"author_id\":{Q(article.AuthorId)},\"slug\":{Q(article.Slug)},\"title\":{Q(article.Title)},\"description\":{Q(article.Description)},\"body\":{Q(article.Body)},\"created_at\":{Q(article.CreatedAt)},\"updated_at\":{Q(article.UpdatedAt)}}}";

    private static string Tag(RwTagInsert tag) =>
        $"{{\"id\":{Q(tag.Id)},\"name\":{Q(tag.Name)}}}";

    private static string ArticleTag(RwArticleTagInsert articleTag) =>
        $"{{\"id\":{Q(articleTag.Id)},\"article_id\":{Q(articleTag.ArticleId)},\"tag_id\":{Q(articleTag.TagId)}}}";

    private static string Comment(RwCommentInsert comment) =>
        $"{{\"id\":{Q(comment.Id)},\"article_id\":{Q(comment.ArticleId)},\"author_id\":{Q(comment.AuthorId)},\"body\":{Q(comment.Body)},\"created_at\":{Q(comment.CreatedAt)},\"updated_at\":{Q(comment.UpdatedAt)}}}";

    private static string Favorite(RwFavoriteInsert favorite) =>
        $"{{\"id\":{Q(favorite.Id)},\"article_id\":{Q(favorite.ArticleId)},\"user_id\":{Q(favorite.UserId)},\"created_at\":{Q(favorite.CreatedAt)}}}";

    private static string Follow(RwFollowInsert follow) =>
        $"{{\"id\":{Q(follow.Id)},\"follower_id\":{Q(follow.FollowerId)},\"followed_id\":{Q(follow.FollowedId)},\"created_at\":{Q(follow.CreatedAt)}}}";

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? NullableStr(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static int Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean() ? 1 : 0;

        return 0;
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
}
