using System.Collections;
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class PostgresDbClient : IDbClient
{
    private readonly string _connectionString;
    private readonly IJsonCodec _json;

    public PostgresDbClient(string connectionString, IJsonCodec? json = null)
    {
        _connectionString = connectionString;
        _json = json ?? new DefaultJsonCodec();
    }

    public string ConnectionString => _connectionString;

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        var (normSql, paramValues) = NormalizeSqlAndParams(sql, param);

        // Dispatches to the underlying Postgres driver or edge HTTP/Hyperdrive bridge
        return await ExecuteQueryAsync<T>(normSql, paramValues);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        var results = await QueryAsync<T>(sql, param);
        return results.Count == 0 ? default : results[0];
    }

    public Task<int> ExecuteAsync(string sql, object? param = null)
    {
        var (normSql, paramValues) = NormalizeSqlAndParams(sql, param);
        return ExecuteNonQueryAsync(normSql, paramValues);
    }

    public Task<T?> GetAsync<T>(TableDef table, object id)
    {
        var pk = table.SinglePrimaryKey
            ?? throw new InvalidOperationException($"Table '{table.Name}' has no single primary key.");

        var sql = $"SELECT * FROM \"{table.Name}\" WHERE \"{pk}\" = $1 LIMIT 1";
        return QuerySingleOrDefaultAsync<T>(sql, new[] { id });
    }

    public Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0)
    {
        var sql = new StringBuilder($"SELECT * FROM \"{table.Name}\"");

        if (!string.IsNullOrWhiteSpace(clause))
        {
            var trimmed = clause.Trim();
            if (!trimmed.StartsWith("where", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("order by", StringComparison.OrdinalIgnoreCase))
            {
                sql.Append(" WHERE ").Append(trimmed);
            }
            else
            {
                sql.Append(' ').Append(trimmed);
            }
        }

        if (limit > 0)
        {
            sql.Append($" LIMIT {limit}");

            if (offset > 0)
            {
                sql.Append($" OFFSET {offset}");
            }
        }

        return QueryAsync<T>(sql.ToString(), param);
    }

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class
    {
        var json = _json.Serialize(values);
        return InsertFromJsonAsync(table, json);
    }

    public Task<int> InsertAsync(TableDef table, object values)
    {
        var json = values is string s && s.TrimStart().StartsWith("{")
            ? s
            : _json.Serialize(values);
        return InsertFromJsonAsync(table, json);
    }

    private Task<int> InsertFromJsonAsync(TableDef table, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cols = new List<string>();
        var placeholders = new List<string>();
        var paramValues = new List<object?>();
        var index = 1;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var colName = ResolveColumnName(table, prop.Name);
            cols.Add($"\"{colName}\"");
            placeholders.Add($"${index}");
            paramValues.Add(JsonElementToValue(prop.Value));
            index += 1;
        }

        var sql = $"INSERT INTO \"{table.Name}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", placeholders)})";
        return ExecuteAsync(sql, paramValues.ToArray());
    }

    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class
    {
        var json = _json.Serialize(values);
        return UpdateFromJsonAsync(table, json, id);
    }

    public Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        var json = values is string s && s.TrimStart().StartsWith("{")
            ? s
            : _json.Serialize(values);
        return UpdateFromJsonAsync(table, json, id);
    }

    private Task<int> UpdateFromJsonAsync(TableDef table, string json, object id)
    {
        using var doc = JsonDocument.Parse(json);
        var pk = table.SinglePrimaryKey ?? "id";
        var sets = new List<string>();
        var paramValues = new List<object?>();
        var index = 1;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var colName = ResolveColumnName(table, prop.Name);
            if (string.Equals(colName, pk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(colName, "created_at", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sets.Add($"\"{colName}\" = ${index}");
            paramValues.Add(JsonElementToValue(prop.Value));
            index += 1;
        }

        if (sets.Count == 0)
        {
            return Task.FromResult(0);
        }

        if (table.Columns.Any(c => string.Equals(c.SqlName, "updated_at", StringComparison.OrdinalIgnoreCase)))
        {
            sets.Add("\"updated_at\" = CURRENT_TIMESTAMP");
        }

        var sql = $"UPDATE \"{table.Name}\" SET {string.Join(", ", sets)} WHERE \"{pk}\" = ${index}";
        paramValues.Add(id);

        return ExecuteAsync(sql, paramValues.ToArray());
    }

    public Task<int> DeleteAsync(TableDef table, object id)
    {
        var pk = table.SinglePrimaryKey ?? "id";
        var sql = $"DELETE FROM \"{table.Name}\" WHERE \"{pk}\" = $1";
        return ExecuteAsync(sql, new[] { id });
    }

    public Task<int> DeleteAsync<T>(TableDef table, object id) => DeleteAsync(table, id);

    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null)
    {
        var sql = $"DELETE FROM \"{table.Name}\" WHERE {where}";
        return ExecuteAsync(sql, param);
    }

    public async Task<bool> ExistsAsync(TableDef table, string where, object? param = null)
    {
        var sql = $"SELECT 1 as count FROM \"{table.Name}\" WHERE {where} LIMIT 1";
        var rows = await QueryAsync<CountRow>(sql, param);
        return rows.Count > 0;
    }

    public async Task EnsureCreatedAsync(params TableDef[] tables)
    {
        foreach (var table in tables)
        {
            var ddl = GenerateCreateTableSql(table);
            await ExecuteAsync(ddl);
        }
    }

    public static string GenerateCreateTableSql(TableDef table)
    {
        var sb = new StringBuilder($"CREATE TABLE IF NOT EXISTS \"{table.Name}\" (\n");
        var columnLines = new List<string>();

        foreach (var col in table.Columns)
        {
            var line = new StringBuilder($"  \"{col.SqlName}\" {MapPostgresType(col)}");

            if (col.IsPrimaryKey)
            {
                line.Append(" PRIMARY KEY");
            }

            if (col.IsNotNull && !col.IsPrimaryKey)
            {
                line.Append(" NOT NULL");
            }

            if (col.IsUnique && !col.IsPrimaryKey)
            {
                line.Append(" UNIQUE");
            }

            if (col.DefaultSql != null)
            {
                line.Append($" DEFAULT {col.DefaultSql}");
            }
            else if (col.DefaultValue != null)
            {
                line.Append($" DEFAULT {col.DefaultValue}");
            }

            if (col.ReferencesTable != null)
            {
                line.Append($" REFERENCES \"{col.ReferencesTable}\"(\"{col.ReferencesColumn ?? "id"}\") ON DELETE CASCADE");
            }

            columnLines.Add(line.ToString());
        }

        sb.Append(string.Join(",\n", columnLines));
        sb.Append("\n);");
        return sb.ToString();
    }

    public (string Sql, object?[] Params) NormalizeSqlAndParams(string sql, object? param) =>
        NormalizeSqlAndParams(sql, param, _json);

    internal static (string Sql, object?[] Params) NormalizeSqlAndParams(string sql, object? param, IJsonCodec jsonCodec)
    {
        var (rewrittenSql, paramNames) = SqlParamParser.ParsePostgresParameters(sql);

        if (param == null)
        {
            return (rewrittenSql, Array.Empty<object?>());
        }

        if (param is object?[] arr)
        {
            return (rewrittenSql, arr);
        }

        if (param is IDictionary dictionary)
        {
            var dictionaryValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in dictionary)
            {
                dictionaryValues[entry.Key?.ToString() ?? ""] = entry.Value;
            }

            var dictionaryParams = new List<object?>();

            foreach (var name in paramNames)
            {
                dictionaryParams.Add(
                    dictionaryValues.TryGetValue(name, out var value) ? value : null);
            }

            return (rewrittenSql, dictionaryParams.ToArray());
        }

        if (param is IEnumerable enumerable && param is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(item);
            }
            return (rewrittenSql, list.ToArray());
        }

        var json = param is string s && s.TrimStart().StartsWith("{")
            ? s
            : jsonCodec.Serialize(param);

        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToValue(prop.Value);
            var snake = ToSnakeCase(prop.Name);
            if (!dict.ContainsKey(snake))
            {
                dict[snake] = dict[prop.Name];
            }
        }

        var positional = new List<object?>();
        foreach (var name in paramNames)
        {
            if (dict.TryGetValue(name, out var val))
            {
                positional.Add(val);
            }
            else
            {
                positional.Add(null);
            }
        }

        return (rewrittenSql, positional.ToArray());
    }

    private static string MapPostgresType(ColumnDef col) => col.SqlType.ToUpperInvariant() switch
    {
        "TEXT" => "TEXT",
        "INTEGER" or "INT" => "INTEGER",
        "REAL" or "FLOAT" or "DOUBLE" => "DOUBLE PRECISION",
        "BLOB" => "BYTEA",
        _ => col.SqlType
    };

    private static string ResolveColumnName(TableDef table, string propName)
    {
        var normalized = propName.Replace("_", "");
        foreach (var col in table.Columns)
        {
            if (string.Equals(col.ClrName, propName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.SqlName, propName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.SqlName.Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return col.SqlName;
            }
        }

        return ToSnakeCase(propName);
    }

    private static string ToSnakeCase(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static object? JsonElementToValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        if (elem.ValueKind == JsonValueKind.Number)
        {
            if (elem.TryGetInt64(out var l))
            {
                return l;
            }
            return elem.GetDouble();
        }
        if (elem.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (elem.ValueKind == JsonValueKind.False)
        {
            return false;
        }
        if (elem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return elem.GetRawText();
    }

#if !NETWASM
    private Npgsql.NpgsqlDataSource? _dataSource;
    private readonly object _dsLock = new();

    public static string ConvertUrlToConnectionString(string connectionString)
    {
        if (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(connectionString);
            var builder = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = uri.AbsolutePath.TrimStart('/'),
            };

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                builder.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                {
                    builder.Password = Uri.UnescapeDataString(parts[1]);
                }
            }

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = uri.Query.TrimStart('?');
                foreach (var param in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = param.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<Npgsql.SslMode>(kv[1], true, out var sslMode))
                        {
                            builder.SslMode = sslMode;
                        }
                    }
                }
            }

            return builder.ConnectionString;
        }

        return connectionString;
    }

    private Npgsql.NpgsqlDataSource? GetDataSource()
    {
        if (string.IsNullOrEmpty(_connectionString) ||
            _connectionString.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_dataSource is not null)
        {
            return _dataSource;
        }

        lock (_dsLock)
        {
            return _dataSource ??= Npgsql.NpgsqlDataSource.Create(ConvertUrlToConnectionString(_connectionString));
        }
    }

    public async Task<int> TransactionAsync(Func<PostgresTransactionContext, Task> action)
    {
        var ds = GetDataSource();
        if (ds is null)
        {
            throw new InvalidOperationException("Cannot start transaction without a valid PostgreSQL connection string.");
        }

        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            var context = new PostgresTransactionContext(conn, tx, _json);
            await action(context);
            await tx.CommitAsync();
            return 1;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
#endif

#if NETWASM
    [System.Runtime.InteropServices.JavaScript.JSImport("pgQuery", "app.host")]
    private static extern Task<string> PgQueryAsync(string sql, string paramsJson);

    [System.Runtime.InteropServices.JavaScript.JSImport("pgExecute", "app.host")]
    private static extern Task<int> PgExecuteAsync(string sql, string paramsJson);
#endif

    private async Task<List<T>> ExecuteQueryAsync<T>(string sql, object?[] parameters)
    {
        LastExecutedSql = sql;
        LastExecutedParams = parameters;

#if !NETWASM
        var ds = GetDataSource();
        if (ds is not null)
        {
            await using var cmd = ds.CreateCommand(sql);
            for (var i = 0; i < parameters.Length; i++)
            {
                cmd.Parameters.AddWithValue(parameters[i] ?? DBNull.Value);
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<T>();
            Func<System.Data.Common.DbDataReader, T>? mapper = null;

            while (await reader.ReadAsync())
            {
                mapper ??= RowMapper.Resolve<T>(reader);
                results.Add(mapper(reader));
            }

            return results;
        }
#else
        if (!string.IsNullOrEmpty(_connectionString) &&
            !_connectionString.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            var paramsJson = _json.Serialize(parameters);
            var resultsJson = await PgQueryAsync(sql, paramsJson);

            if (!string.IsNullOrWhiteSpace(resultsJson) && resultsJson != "[]")
            {
                var list = _json.Deserialize<List<T>>(resultsJson);
                if (list is not null)
                {
                    return list;
                }
            }
            return new List<T>();
        }
#endif

        return new List<T>();
    }

    private async Task<int> ExecuteNonQueryAsync(string sql, object?[] parameters)
    {
        LastExecutedSql = sql;
        LastExecutedParams = parameters;

#if !NETWASM
        var ds = GetDataSource();
        if (ds is not null)
        {
            await using var cmd = ds.CreateCommand(sql);
            for (var i = 0; i < parameters.Length; i++)
            {
                cmd.Parameters.AddWithValue(parameters[i] ?? DBNull.Value);
            }

            return await cmd.ExecuteNonQueryAsync();
        }
#else
        if (!string.IsNullOrEmpty(_connectionString) &&
            !_connectionString.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            var paramsJson = _json.Serialize(parameters);
            return await PgExecuteAsync(sql, paramsJson);
        }
#endif

        return 1;
    }

    public string? LastExecutedSql { get; private set; }
    public object?[]? LastExecutedParams { get; private set; }

    private sealed record CountRow(int Count);
}

#if !NETWASM
public sealed class PostgresTransactionContext
{
    private readonly Npgsql.NpgsqlConnection _connection;
    private readonly Npgsql.NpgsqlTransaction _transaction;
    private readonly IJsonCodec _json;

    internal PostgresTransactionContext(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        IJsonCodec json)
    {
        _connection = connection;
        _transaction = transaction;
        _json = json;
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        var (normSql, paramValues) = PostgresDbClient.NormalizeSqlAndParams(sql, param, _json);
        await using var cmd = new Npgsql.NpgsqlCommand(normSql, _connection, _transaction);
        for (var i = 0; i < paramValues.Length; i++)
        {
            cmd.Parameters.AddWithValue(paramValues[i] ?? DBNull.Value);
        }
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        var (normSql, paramValues) = PostgresDbClient.NormalizeSqlAndParams(sql, param, _json);
        await using var cmd = new Npgsql.NpgsqlCommand(normSql, _connection, _transaction);
        for (var i = 0; i < paramValues.Length; i++)
        {
            cmd.Parameters.AddWithValue(paramValues[i] ?? DBNull.Value);
        }
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<T>();
        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }
            var json = _json.Serialize(dict);
            var item = _json.Deserialize<T>(json);
            if (item is not null)
            {
                results.Add(item);
            }
        }
        return results;
    }
}
#endif
