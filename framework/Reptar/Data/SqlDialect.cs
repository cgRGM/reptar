namespace Reptar;

public interface ISqlDialect
{
    string Name { get; }
    string QuoteIdentifier(string name);
    string FormatParameter(string name, int ordinal);
    string LimitOffset(int limit, int offset);
    string CreateTable(TableDef table);
    IEnumerable<string> CreateIndexes(TableDef table);
}

public static class SqlDialects
{
    public static readonly ISqlDialect Sqlite = new SqliteDialect();
    public static readonly ISqlDialect Postgres = new PostgresDialect();
    public static readonly ISqlDialect MySql = new MySqlDialect();
}

public sealed class SqliteDialect : ISqlDialect
{
    public string Name => "sqlite";

    public string QuoteIdentifier(string name) => $"\"{name}\"";

    public string FormatParameter(string name, int ordinal) => "@" + name;

    public string LimitOffset(int limit, int offset)
    {
        if (limit <= 0)
            return "";
        return offset > 0 ? $" LIMIT {limit} OFFSET {offset}" : $" LIMIT {limit}";
    }

    public string CreateTable(TableDef table) => SchemaDdl.CreateTable(table);

    public IEnumerable<string> CreateIndexes(TableDef table) => SchemaDdl.CreateIndexes(table);
}

public sealed class PostgresDialect : ISqlDialect
{
    public string Name => "postgres";

    public string QuoteIdentifier(string name) => $"\"{name}\"";

    public string FormatParameter(string name, int ordinal) => $"${ordinal}";

    public string LimitOffset(int limit, int offset)
    {
        if (limit <= 0)
            return "";
        return offset > 0 ? $" LIMIT {limit} OFFSET {offset}" : $" LIMIT {limit}";
    }

    public string CreateTable(TableDef table) => SchemaDdl.CreateTable(table);

    public IEnumerable<string> CreateIndexes(TableDef table) => SchemaDdl.CreateIndexes(table);
}

public sealed class MySqlDialect : ISqlDialect
{
    public string Name => "mysql";

    public string QuoteIdentifier(string name) => $"`{name}`";

    public string FormatParameter(string name, int ordinal) => "@" + name;

    public string LimitOffset(int limit, int offset)
    {
        if (limit <= 0)
            return "";
        return offset > 0 ? $" LIMIT {limit} OFFSET {offset}" : $" LIMIT {limit}";
    }

    public string CreateTable(TableDef table)
    {
        var lines = new List<string>();

        foreach (var column in table.Columns)
        {
            var line = $"`{column.SqlName}` {ToMySqlType(column.SqlType)}";

            if (column.IsNotNull)
                line += " NOT NULL";

            if (column.DefaultSql is not null)
                line += $" DEFAULT {column.DefaultSql}";
            else if (column.DefaultValue is not null)
                line += $" DEFAULT {FormatDefault(column.DefaultValue)}";

            if (column.ReferencesTable is not null)
            {
                line += $" REFERENCES `{column.ReferencesTable}`(`{column.ReferencesColumn}`)";

                if (column.OnDelete is not null)
                    line += $" ON DELETE {column.OnDelete.ToUpperInvariant()}";
            }

            if (column.IsPrimaryKey && table.PrimaryKeySql.Length == 0)
                line += " PRIMARY KEY";

            lines.Add(line);
        }

        if (table.PrimaryKeySql.Length > 0)
        {
            lines.Add("PRIMARY KEY (" +
                string.Join(", ", table.PrimaryKeySql.Select(name => $"`{name}`")) + ")");
        }

        return $"CREATE TABLE IF NOT EXISTS `{table.Name}`\n(\n  {string.Join(",\n  ", lines)}\n)";
    }

    public IEnumerable<string> CreateIndexes(TableDef table)
    {
        foreach (var column in table.Columns)
        {
            if (column.IsUnique && !column.IsPrimaryKey)
            {
                yield return
                    $"CREATE UNIQUE INDEX `{table.Name}_{column.SqlName}_uq` " +
                    $"ON `{table.Name}`(`{column.SqlName}`)";
            }

            if (column.IsIndexed && !column.IsUnique)
            {
                yield return
                    $"CREATE INDEX `{table.Name}_{column.SqlName}_idx` " +
                    $"ON `{table.Name}`(`{column.SqlName}`)";
            }
        }
    }

    private static string ToMySqlType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        "TEXT" => "VARCHAR(255)",
        "INTEGER" => "INT",
        "REAL" => "DOUBLE",
        "BLOB" => "BLOB",
        _ => sqlType,
    };

    private static string FormatDefault(object value) => value switch
    {
        string text => $"'{text.Replace("'", "''")}'",
        bool flag => flag ? "1" : "0",
        _ => value.ToString() ?? "NULL",
    };
}
