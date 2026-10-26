namespace Reptar;

/// <summary>
/// A typed relationship that can be eager-loaded by <see cref="TableQuery{T}"/>.
/// Relations are explicit: the caller supplies the key selectors and the
/// immutable attach expression, so preloading stays reflection-free.
/// </summary>
public abstract class Relation<TParent, TRelated>
{
    internal abstract Task<List<TParent>> ApplyAsync(
        IDbClient db,
        List<TParent> parents);
}

public static class Relation
{
    /// <summary>
    /// Parent belongs to one related row (e.g. Post.AuthorId -> User.Id).
    /// </summary>
    public static Relation<TParent, TRelated> BelongsTo<TParent, TRelated, TKey>(
        Func<TParent, TKey> parentKey,
        TableDef relatedTable,
        ColumnRef<TKey> relatedColumn,
        Func<TRelated, TKey> relatedKey,
        Func<TParent, TRelated?, TParent> attach)
        where TKey : notnull =>
        new BelongsToRelation<TParent, TRelated, TKey>(
            parentKey,
            relatedTable,
            relatedColumn,
            relatedKey,
            attach);

    /// <summary>
    /// Parent has many related rows (e.g. Post.Id -> Comment.PostId).
    /// </summary>
    public static Relation<TParent, TRelated> HasMany<TParent, TRelated, TKey>(
        Func<TParent, TKey> parentKey,
        TableDef relatedTable,
        ColumnRef<TKey> foreignKeyColumn,
        Func<TRelated, TKey> foreignKey,
        Func<TParent, IReadOnlyList<TRelated>, TParent> attach)
        where TKey : notnull =>
        new HasManyRelation<TParent, TRelated, TKey>(
            parentKey,
            relatedTable,
            foreignKeyColumn,
            foreignKey,
            attach);

    internal static IEnumerable<TKey[]> Chunk<TKey>(
        IEnumerable<TKey> values,
        // D1 caps bound parameters per statement; 100 is safe across D1,
        // SQLite, PostgreSQL, and MySQL.
        int size = 100)
    {
        var buffer = new List<TKey>(size);

        foreach (var value in values)
        {
            buffer.Add(value);

            if (buffer.Count == size)
            {
                yield return buffer.ToArray();
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
            yield return buffer.ToArray();
    }
}

internal sealed class BelongsToRelation<TParent, TRelated, TKey>(
    Func<TParent, TKey> parentKey,
    TableDef relatedTable,
    ColumnRef<TKey> relatedColumn,
    Func<TRelated, TKey> relatedKey,
    Func<TParent, TRelated?, TParent> attach) : Relation<TParent, TRelated>
    where TKey : notnull
{
    internal override async Task<List<TParent>> ApplyAsync(
        IDbClient db,
        List<TParent> parents)
    {
        if (parents.Count == 0)
            return parents;

        var keys = parents
            .Select(parentKey)
            .Distinct()
            .ToArray();

        var related = new Dictionary<TKey, TRelated>(keys.Length);

        foreach (var chunk in Relation.Chunk(keys))
        {
            var rows = await db.ListAsync<TRelated>(
                relatedTable,
                relatedColumn.In(chunk));

            foreach (var row in rows)
                related[relatedKey(row)] = row;
        }

        var result = new List<TParent>(parents.Count);

        foreach (var parent in parents)
        {
            var key = parentKey(parent);
            result.Add(related.TryGetValue(key, out var row)
                ? attach(parent, row)
                : attach(parent, default));
        }

        return result;
    }
}

internal sealed class HasManyRelation<TParent, TRelated, TKey>(
    Func<TParent, TKey> parentKey,
    TableDef relatedTable,
    ColumnRef<TKey> foreignKeyColumn,
    Func<TRelated, TKey> foreignKey,
    Func<TParent, IReadOnlyList<TRelated>, TParent> attach) : Relation<TParent, TRelated>
    where TKey : notnull
{
    internal override async Task<List<TParent>> ApplyAsync(
        IDbClient db,
        List<TParent> parents)
    {
        if (parents.Count == 0)
            return parents;

        var keys = parents
            .Select(parentKey)
            .Distinct()
            .ToArray();

        var groups = new Dictionary<TKey, List<TRelated>>(keys.Length);

        foreach (var chunk in Relation.Chunk(keys))
        {
            var rows = await db.ListAsync<TRelated>(
                relatedTable,
                foreignKeyColumn.In(chunk));

            foreach (var row in rows)
            {
                var key = foreignKey(row);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add(row);
            }
        }

        var result = new List<TParent>(parents.Count);

        foreach (var parent in parents)
        {
            var key = parentKey(parent);
            result.Add(attach(
                parent,
                groups.TryGetValue(key, out var rows)
                    ? rows
                    : Array.Empty<TRelated>()));
        }

        return result;
    }
}
