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
        _definition = new ColumnDef { SqlName = sqlName, SqlType = sqlType, ClrName = sqlName };

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

    public ColumnDef Build(string? clrName = null)
    {
        if (clrName is not null)
        {
            _definition.ClrName = clrName;
        }
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
    public TableDef(string name, ColumnDef[] columns)
    {
        Name = name;
        Columns = columns;
    }

    public TableDef(string name, params ColumnBuilder[] builders)
    {
        Name = name;
        var cols = new ColumnDef[builders.Length];
        for (var i = 0; i < builders.Length; i++)
        {
            cols[i] = builders[i].Build();
        }
        Columns = cols;
    }

    public string Name { get; }
    public ColumnDef[] Columns { get; }
    public string[] PrimaryKeySql { get; private set; } = [];

    public static TableDef Create(string name, params ColumnBuilder[] columns) =>
        new(name, columns);

    public static TableDef Create(string name, object? columns = null) =>
        throw new NotSupportedException(
            "Use TableDef.Define(...) with explicit columns on the Wasm target.");

    public static TableDef Define(string name, params ColumnBuilder[] columns) =>
        new(name, columns);

    public static TableDef Define(string name, params ColumnDef[] columns) =>
        new(name, columns);

    public TableDef PrimaryKey(params string[] sqlNames)
    {
        PrimaryKeySql = sqlNames;
        return this;
    }

    public string SinglePrimaryKey =>
        PrimaryKeySql.Length == 1
            ? PrimaryKeySql[0]
            : Columns.FirstOrDefault(column => column.IsPrimaryKey)?.SqlName ?? "id";
}
