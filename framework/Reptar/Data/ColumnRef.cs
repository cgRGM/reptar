using System.Collections;
using System.Text;

namespace Reptar;

public readonly struct ColumnRef<T>
{
    public string Table { get; }
    public string Name { get; }

    public ColumnRef(string table, string name)
    {
        Table = table;
        Name = name;
    }

    public SqlFilter Eq(T value) => new($"\"{Table}\".\"{Name}\" = @", value);
    public SqlFilter NotEq(T value) => new($"\"{Table}\".\"{Name}\" != @", value);
    public SqlFilter Gt(T value) => new($"\"{Table}\".\"{Name}\" > @", value);
    public SqlFilter Gte(T value) => new($"\"{Table}\".\"{Name}\" >= @", value);
    public SqlFilter Lt(T value) => new($"\"{Table}\".\"{Name}\" < @", value);
    public SqlFilter Lte(T value) => new($"\"{Table}\".\"{Name}\" <= @", value);
    public SqlFilter Like(string pattern) => new($"\"{Table}\".\"{Name}\" LIKE @", pattern);

    public SqlFilter In(IEnumerable<T> values) => SqlFilter.InValues(Table, Name, values);
    public SqlFilter In(params T[] values) => In((IEnumerable<T>)values);

    public SqlFilter NotIn(IEnumerable<T> values) => SqlFilter.NotInValues(Table, Name, values);
    public SqlFilter NotIn(params T[] values) => NotIn((IEnumerable<T>)values);

    public SqlFilter IsNull() => new($"\"{Table}\".\"{Name}\" IS NULL");
    public SqlFilter IsNotNull() => new($"\"{Table}\".\"{Name}\" IS NOT NULL");

    public SqlOrderBy Asc() => new($"\"{Table}\".\"{Name}\" ASC");
    public SqlOrderBy Desc() => new($"\"{Table}\".\"{Name}\" DESC");
}

public sealed class SqlOrderBy
{
    public string Sql { get; }

    public SqlOrderBy(string sql) => Sql = sql;

    public static implicit operator SqlOrderBy(string sql) => new(sql);

    public static SqlOrderBy Combine(params SqlOrderBy[] orders) =>
        new(string.Join(", ", orders.Select(o => o.Sql)));
}

public sealed class SqlFilter
{
    public string SqlTemplate { get; }
    public IReadOnlyList<object?> Values { get; }

    public SqlFilter(string sqlTemplate, params object?[] values)
    {
        SqlTemplate = sqlTemplate;
        Values = values;
    }

    public static SqlFilter Raw(string sql, params object?[] values) =>
        new(sql, values);

    public static SqlFilter InValues<T>(string table, string column, IEnumerable<T> values)
    {
        var items = values.Cast<object?>().ToArray();
        if (items.Length == 0)
        {
            return new("1 = 0");
        }

        var placeholders = string.Join(", ", Enumerable.Repeat("@", items.Length));
        return new($"\"{table}\".\"{column}\" IN ({placeholders})", items);
    }

    public static SqlFilter NotInValues<T>(string table, string column, IEnumerable<T> values)
    {
        var items = values.Cast<object?>().ToArray();
        if (items.Length == 0)
        {
            return new("1 = 1");
        }

        var placeholders = string.Join(", ", Enumerable.Repeat("@", items.Length));
        return new($"\"{table}\".\"{column}\" NOT IN ({placeholders})", items);
    }

    public static SqlFilter operator &(SqlFilter left, SqlFilter right) =>
        Combine(" AND ", left, right);

    public static SqlFilter operator |(SqlFilter left, SqlFilter right) =>
        Combine(" OR ", left, right);

    public static SqlFilter operator !(SqlFilter filter) =>
        new($"NOT ({filter.SqlTemplate})", filter.Values.ToArray());

    private static SqlFilter Combine(string op, SqlFilter left, SqlFilter right)
    {
        var combinedSql = $"({left.SqlTemplate}{op}{right.SqlTemplate})";
        var combinedValues = new object?[left.Values.Count + right.Values.Count];
        for (var i = 0; i < left.Values.Count; i++)
        {
            combinedValues[i] = left.Values[i];
        }
        for (var i = 0; i < right.Values.Count; i++)
        {
            combinedValues[left.Values.Count + i] = right.Values[i];
        }

        return new SqlFilter(combinedSql, combinedValues);
    }

    public (string Condition, Dictionary<string, object?> Parameters) ToCondition(int paramStartIndex = 0)
    {
        var sb = new StringBuilder();
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var paramIndex = paramStartIndex;

        for (var i = 0; i < SqlTemplate.Length; i++)
        {
            if (SqlTemplate[i] == '@')
            {
                var paramName = $"p{paramIndex}";
                sb.Append('@').Append(paramName);
                dict[paramName] = paramIndex < Values.Count ? Values[paramIndex] : null;
                paramIndex++;
            }
            else
            {
                sb.Append(SqlTemplate[i]);
            }
        }

        return (sb.ToString(), dict);
    }

    public (string WhereClause, Dictionary<string, object?> Parameters) ToWhereClause(int paramStartIndex = 0)
    {
        var (condition, parameters) = ToCondition(paramStartIndex);
        return ("WHERE " + condition, parameters);
    }
}

public sealed class TableQuery<T>
{
    private readonly IDbClient _db;
    private readonly TableDef _table;
    private SqlFilter? _filter;
    private SqlOrderBy? _orderBy;
    private int _limit;
    private int _offset;

    public TableQuery(IDbClient db, TableDef table)
    {
        _db = db;
        _table = table;
    }

    public TableQuery<T> Where(SqlFilter filter)
    {
        _filter = _filter is null ? filter : _filter & filter;
        return this;
    }

    public TableQuery<T> OrderBy(SqlOrderBy orderBy)
    {
        _orderBy = orderBy;
        return this;
    }

    public TableQuery<T> OrderBy(params SqlOrderBy[] orderBys)
    {
        _orderBy = SqlOrderBy.Combine(orderBys);
        return this;
    }

    public TableQuery<T> Limit(int limit)
    {
        _limit = limit;
        return this;
    }

    public TableQuery<T> Offset(int offset)
    {
        _offset = offset;
        return this;
    }

    public Task<List<T>> ToListAsync()
    {
        if (_filter is null)
        {
            var clause = _orderBy is not null ? $"ORDER BY {_orderBy.Sql}" : null;
            return _db.ListAsync<T>(_table, clause, null, _limit, _offset);
        }

        return _db.ListAsync<T>(_table, _filter, _orderBy, _limit, _offset);
    }

    public async Task<T?> FirstOrDefaultAsync()
    {
        var results = await Limit(1).ToListAsync();
        return results.Count > 0 ? results[0] : default;
    }

    public Task<int> CountAsync()
    {
        if (_filter is null)
        {
            return _db.CountAsync(_table);
        }

        return _db.CountAsync(_table, _filter);
    }

    public Task<bool> ExistsAsync()
    {
        if (_filter is null)
        {
            return _db.ExistsAsync(_table, "1=1");
        }

        return _db.ExistsAsync(_table, _filter);
    }

    public Task<int> DeleteAsync()
    {
        if (_filter is null)
        {
            return _db.DeleteWhereAsync(_table, "1=1");
        }

        return _db.DeleteWhereAsync(_table, _filter);
    }
}

public static class DbFilterExtensions
{
    public static Task<List<T>> ListAsync<T>(
        this IDbClient db,
        TableDef table,
        SqlFilter filter,
        SqlOrderBy? orderBy = null,
        int limit = 0,
        int offset = 0)
    {
        var (clause, param) = filter.ToWhereClause();
        if (orderBy is not null && !string.IsNullOrWhiteSpace(orderBy.Sql))
        {
            clause += " ORDER BY " + orderBy.Sql;
        }
        return db.ListAsync<T>(table, clause, param, limit, offset);
    }

    public static TableQuery<T> From<T>(this IDbClient db, TableDef table) =>
        new(db, table);

    public static Task<int> DeleteWhereAsync(this IDbClient db, TableDef table, SqlFilter filter)
    {
        var (condition, param) = filter.ToCondition();
        return db.DeleteWhereAsync(table, condition, param);
    }

    public static Task<bool> ExistsAsync(this IDbClient db, TableDef table, SqlFilter filter)
    {
        var (condition, param) = filter.ToCondition();
        return db.ExistsAsync(table, condition, param);
    }

    public static Task<int> CountAsync(this IDbClient db, TableDef table, SqlFilter filter)
    {
        var (condition, param) = filter.ToCondition();
        return db.CountAsync(table, condition, param);
    }
}

