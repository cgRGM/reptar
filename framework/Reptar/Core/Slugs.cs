namespace Reptar;

public static class Slugs
{
    public static string From(string value)
    {
        var slug = Text.Slugify(value);
        return slug.Length == 0 ? "item" : slug;
    }

    public static async Task<string> UniqueAsync(
        IDbClient db,
        TableDef table,
        string column,
        string source,
        string? excludeId = null,
        string idColumn = "id")
    {
        var slugColumn = ResolveColumn(table, column);
        var primaryKey = ResolveColumn(table, idColumn);
        var baseSlug = From(source);
        var candidate = baseSlug;

        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            var where = excludeId is null
                ? $"{slugColumn} = @slug"
                : $"{slugColumn} = @slug and {primaryKey} <> @id";
            var param = excludeId is null
                ? new { slug = candidate }
                : (object)new { slug = candidate, id = excludeId };

            if (!await db.ExistsAsync(table, where, param))
                return candidate;

            candidate = $"{baseSlug}-{suffix}";
        }

        return $"{baseSlug}-{Ids.New("s").ToLowerInvariant()}";
    }

    private static string ResolveColumn(TableDef table, string name)
    {
        foreach (var column in table.Columns)
        {
            if (string.Equals(column.SqlName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.ClrName, name, StringComparison.OrdinalIgnoreCase))
            {
                return column.SqlName;
            }
        }

        throw new InvalidOperationException(
            $"Column '{name}' does not exist on table '{table.Name}'.");
    }
}
