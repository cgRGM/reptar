namespace Reptar;

public sealed class ColApi
{
    public ColumnBuilder Text(string sqlName) => new(sqlName, "TEXT");
    public ColumnBuilder Int(string sqlName) => new(sqlName, "INTEGER");
    public ColumnBuilder Real(string sqlName) => new(sqlName, "REAL");
    public ColumnBuilder Blob(string sqlName) => new(sqlName, "BLOB");
}

public sealed class ColumnBuilder
{
    private readonly ColumnDef _definition;

    public ColumnBuilder(string sqlName, string sqlType) =>
        _definition = new ColumnDef { SqlName = sqlName, SqlType = sqlType };

    public ColumnBuilder PrimaryKey() { _definition.IsPrimaryKey = true; return this; }
    public ColumnBuilder NotNull() { _definition.IsNotNull = true; return this; }
    public ColumnBuilder Unique() { _definition.IsUnique = true; return this; }
    public ColumnBuilder Default(object value) { _definition.DefaultValue = value; return this; }
    public ColumnBuilder DefaultSql(string sql) { _definition.DefaultSql = sql; return this; }
    public ColumnBuilder Indexed() { _definition.IsIndexed = true; return this; }

    public ColumnBuilder References(string table, string column, string? onDelete = null)
    {
        _definition.ReferencesTable = table;
        _definition.ReferencesColumn = column;
        _definition.OnDelete = onDelete;
        return this;
    }

    internal ColumnDef Build(string clrName)
    {
        _definition.ClrName = clrName;
        return _definition;
    }
}

public sealed class ColumnDef
{
    public string ClrName { get; internal set; } = "";
    public required string SqlName { get; init; }
    public required string SqlType { get; init; }
    public bool IsNotNull { get; internal set; }
    public bool IsPrimaryKey { get; internal set; }
    public bool IsUnique { get; internal set; }
    public string? DefaultSql { get; internal set; }
    public object? DefaultValue { get; internal set; }
    public string? ReferencesTable { get; internal set; }
    public string? ReferencesColumn { get; internal set; }
    public string? OnDelete { get; internal set; }
    public bool IsIndexed { get; internal set; }
}

public sealed class TableDef
{
    private TableDef(string name, ColumnDef[] columns)
    {
        Name = name;
        Columns = columns;
    }

    public string Name { get; }
    public ColumnDef[] Columns { get; }
    public string[] PrimaryKeySql { get; private set; } = [];

    public static TableDef Create(string name, object columns)
    {
        var properties = columns.GetType().GetProperties();
        var definitions = properties
            .Select(property => ((ColumnBuilder)property.GetValue(columns)!).Build(property.Name))
            .ToArray();

        return new TableDef(name, definitions);
    }

    public static TableDef Define(string name, params ColumnBuilder[] columns) =>
        new(name, columns.Select(c => c.Build(c.GetType().Name)).ToArray());

    public static TableDef Define(string name, params ColumnDef[] columns) =>
        new(name, columns);

    public TableDef PrimaryKey(params string[] sqlNames)
    {
        PrimaryKeySql = sqlNames;
        return this;
    }

    internal string? SinglePrimaryKey =>
        PrimaryKeySql.Length == 1
            ? PrimaryKeySql[0]
            : Columns.FirstOrDefault(column => column.IsPrimaryKey)?.SqlName;
}

internal static class SchemaDdl
{
    public static string CreateTable(TableDef table)
    {
        var lines = new List<string>();

        foreach (var column in table.Columns)
        {
            var line = $"\"{column.SqlName}\" {column.SqlType}";

            if (column.IsNotNull)
                line += " NOT NULL";

            if (column.DefaultSql is not null)
                line += $" DEFAULT {column.DefaultSql}";
            else if (column.DefaultValue is not null)
                line += $" DEFAULT {FormatDefault(column.DefaultValue)}";

            if (column.ReferencesTable is not null)
            {
                line += $" REFERENCES \"{column.ReferencesTable}\"(\"{column.ReferencesColumn}\")";

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
                string.Join(", ", table.PrimaryKeySql.Select(name => $"\"{name}\"")) + ")");
        }

        return $"CREATE TABLE IF NOT EXISTS \"{table.Name}\"\n(\n  {string.Join(",\n  ", lines)}\n)";
    }

    public static IEnumerable<string> CreateIndexes(TableDef table)
    {
        foreach (var column in table.Columns)
        {
            if (column.IsUnique && !column.IsPrimaryKey)
            {
                yield return
                    $"CREATE UNIQUE INDEX IF NOT EXISTS \"{table.Name}_{column.SqlName}_uq\" " +
                    $"ON \"{table.Name}\"(\"{column.SqlName}\")";
            }

            if (column.IsIndexed && !column.IsUnique)
            {
                yield return
                    $"CREATE INDEX IF NOT EXISTS \"{table.Name}_{column.SqlName}_idx\" " +
                    $"ON \"{table.Name}\"(\"{column.SqlName}\")";
            }
        }
    }

    private static string FormatDefault(object value) => value switch
    {
        string text => $"'{text.Replace("'", "''")}'",
        bool flag => flag ? "1" : "0",
        _ => value.ToString() ?? "NULL",
    };
}
