namespace Reptar;

// Wasm-target table metadata. The reflection-based DDL/mapping path is not
// available under NetWasm; D1 migrations own the schema. This stub keeps the
// shared framework types compiling until source-generated table metadata lands.

public sealed class ColApi
{
    public ColumnBuilder Text(string sqlName) => new();
    public ColumnBuilder Int(string sqlName) => new();
    public ColumnBuilder Real(string sqlName) => new();
    public ColumnBuilder Blob(string sqlName) => new();
}

public sealed class ColumnBuilder
{
    public ColumnBuilder PrimaryKey() => this;
    public ColumnBuilder NotNull() => this;
    public ColumnBuilder Unique() => this;
    public ColumnBuilder Default(object value) => this;
    public ColumnBuilder DefaultSql(string sql) => this;
    public ColumnBuilder Indexed() => this;

    public ColumnBuilder References(
        string table,
        string column,
        string? onDelete = null) => this;
}

public sealed class ColumnDef
{
    public string ClrName { get; set; } = "";
    public string SqlName { get; set; } = "";
    public string SqlType { get; set; } = "";
}

public sealed class TableDef
{
    private TableDef(string name) => Name = name;

    public string Name { get; }
    public ColumnDef[] Columns { get; } = [];
    public string[] PrimaryKeySql { get; } = [];

    public static TableDef Create(string name, object columns) => new(name);

    public TableDef PrimaryKey(params string[] sqlNames) => this;
}
