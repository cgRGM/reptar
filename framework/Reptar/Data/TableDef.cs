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

    public ColumnBuilder References(TableDef table, string column = "id", string? onDelete = null) =>
        References(table.Name, column, onDelete);

    internal ColumnDef Build(string? clrName = null)
    {
        _definition.ClrName = clrName ?? ToPascalCase(_definition.SqlName);
        return _definition;
    }

    private static string ToPascalCase(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();

        foreach (var part in parts)
        {
            result.Append(char.ToUpperInvariant(part[0]));

            if (part.Length > 1)
                result.Append(part[1..]);
        }

        return result.ToString();
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

public static class SchemaRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TableDef> _tables =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _order = new();
    private static readonly object _sync = new();

    public static IReadOnlyList<TableDef> All
    {
        get
        {
            lock (_sync)
            {
                var list = new List<TableDef>(_order.Count);
                foreach (var name in _order)
                {
                    if (_tables.TryGetValue(name, out var table))
                        list.Add(table);
                }
                return list;
            }
        }
    }

    public static TableDef Register(TableDef table)
    {
        lock (_sync)
        {
            if (!_tables.ContainsKey(table.Name))
                _order.Add(table.Name);
            _tables[table.Name] = table;
        }
        return table;
    }

    public static string ExportJson(IEnumerable<TableDef>? appTables = null)
    {
        var merged = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        if (appTables is not null)
        {
            foreach (var t in appTables)
            {
                if (merged.TryAdd(t.Name, t))
                    names.Add(t.Name);
            }
        }

        foreach (var t in All)
        {
            if (merged.TryAdd(t.Name, t))
                names.Add(t.Name);
        }

        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tables");

            foreach (var name in names)
            {
                var table = merged[name];
                writer.WriteStartObject();
                writer.WriteString("name", table.Name);

                writer.WriteStartArray("primaryKey");
                if (table.PrimaryKeySql.Length > 0)
                {
                    foreach (var pk in table.PrimaryKeySql)
                        writer.WriteStringValue(pk);
                }
                else
                {
                    foreach (var col in table.Columns)
                    {
                        if (col.IsPrimaryKey)
                            writer.WriteStringValue(col.SqlName);
                    }
                }
                writer.WriteEndArray();

                writer.WriteStartArray("columns");
                foreach (var col in table.Columns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", col.SqlName);
                    writer.WriteString("clrName", col.ClrName);
                    writer.WriteString("type", col.SqlType);
                    writer.WriteString("clrType", MapSqlToClrTypeName(col.SqlType));
                    writer.WriteBoolean("primaryKey", col.IsPrimaryKey || table.PrimaryKeySql.Contains(col.SqlName, StringComparer.OrdinalIgnoreCase));
                    writer.WriteBoolean("notNull", col.IsNotNull || col.IsPrimaryKey);
                    writer.WriteBoolean("unique", col.IsUnique);
                    writer.WriteBoolean("indexed", col.IsIndexed);

                    if (col.DefaultValue is null)
                        writer.WriteNull("defaultValue");
                    else
                        writer.WriteString("defaultValue", FormatDefaultLiteral(col.DefaultValue));

                    if (col.DefaultSql is null)
                        writer.WriteNull("defaultSql");
                    else
                        writer.WriteString("defaultSql", col.DefaultSql);

                    if (col.ReferencesTable is null)
                        writer.WriteNull("referencesTable");
                    else
                        writer.WriteString("referencesTable", col.ReferencesTable);

                    if (col.ReferencesColumn is null)
                        writer.WriteNull("referencesColumn");
                    else
                        writer.WriteString("referencesColumn", col.ReferencesColumn);

                    if (col.OnDelete is null)
                        writer.WriteNull("onDelete");
                    else
                        writer.WriteString("onDelete", col.OnDelete);

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("indexes");
                foreach (var col in table.Columns)
                {
                    if (col.IsUnique && !col.IsPrimaryKey)
                        writer.WriteStringValue($"{table.Name}_{col.SqlName}_uq");
                    if (col.IsIndexed && !col.IsUnique)
                        writer.WriteStringValue($"{table.Name}_{col.SqlName}_idx");
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MapSqlToClrTypeName(string sqlType) =>
        sqlType.ToUpperInvariant() switch
        {
            "INTEGER" or "INT" => "int",
            "REAL" or "DOUBLE" or "FLOAT" => "double",
            "BLOB" => "byte[]",
            _ => "string"
        };

    private static string FormatDefaultLiteral(object value) => value switch
    {
        string text => $"'{text.Replace("'", "''")}'",
        bool flag => flag ? "1" : "0",
        _ => value.ToString() ?? "NULL"
    };
}

public sealed class TableDef
{
    private TableDef(string name, ColumnDef[] columns)
    {
        Name = name;
        Columns = columns;
        SchemaRegistry.Register(this);
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
        new(name, columns.Select(c => c.Build()).ToArray());

    public static TableDef Define(string name, params ColumnDef[] columns) =>
        new(name, columns);

    public TableDef PrimaryKey(params string[] sqlNames)
    {
        PrimaryKeySql = sqlNames;
        return this;
    }

    public ColumnRef<T> Col<T>(string name) => new(Name, name);

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
