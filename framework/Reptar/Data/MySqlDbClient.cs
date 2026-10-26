#if !REPTAR_SQLITE_ONLY
using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace Reptar;

public sealed class MySqlDbClient : IDbClient, IDisposable, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly MySqlDataSource? _dataSource;
    private readonly bool _isMock;
    private bool _disposed;

    private sealed record TransactionScope(
        MySqlConnection Connection,
        MySqlTransaction Transaction);

    private static readonly AsyncLocal<TransactionScope?> AmbientTransaction = new();

    private readonly struct ConnectionLease : IAsyncDisposable
    {
        public MySqlConnection Connection { get; }
        private readonly bool _owned;

        public ConnectionLease(MySqlConnection connection, bool owned)
        {
            Connection = connection;
            _owned = owned;
        }

        public ValueTask DisposeAsync() =>
            _owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }

    private async ValueTask<ConnectionLease> RentConnectionAsync()
    {
        var ambient = AmbientTransaction.Value;
        if (ambient is not null)
            return new ConnectionLease(ambient.Connection, owned: false);

        return new ConnectionLease(
            await _dataSource!.OpenConnectionAsync(),
            owned: true);
    }

    public MySqlDbClient(string connectionString)
    {
        _connectionString = ConvertUrlToConnectionString(connectionString);
        _isMock = string.IsNullOrWhiteSpace(_connectionString) ||
                  _connectionString.StartsWith("mock:", StringComparison.OrdinalIgnoreCase);

        if (!_isMock)
        {
            _dataSource = new MySqlDataSource(_connectionString);
        }
    }

    public string ConnectionString => _connectionString;
    public string? LastExecutedSql { get; private set; }
    public object? LastExecutedParams { get; private set; }

    public static string ConvertUrlToConnectionString(string connectionString)
    {
        if (connectionString.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("mariadb://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(connectionString);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = uri.Host,
                Port = uri.Port > 0 ? (uint)uri.Port : 3306,
                Database = uri.AbsolutePath.TrimStart('/'),
            };

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                builder.UserID = Uri.UnescapeDataString(parts[0]);
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
                        if (Enum.TryParse<MySqlSslMode>(kv[1], true, out var sslMode))
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

    public async Task<List<T>> QueryAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param = null)
    {
        LastExecutedSql = sql;
        LastExecutedParams = param;

        if (_isMock || _dataSource is null)
            return [];

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        await using var command = BuildCommand(connection, sql, param);
        await using var reader = await command.ExecuteReaderAsync();

        if (EntityMapper<T>.Factory is { } factory)
        {
            var map = factory(reader);
            var results = new List<T>();
            while (await reader.ReadAsync())
                results.Add(map(reader));
            return results;
        }

        var fallbackMap = RowMapper.Resolve<T>(reader);
        var fallbackResults = new List<T>();
        while (await reader.ReadAsync())
            fallbackResults.Add(fallbackMap(reader));

        return fallbackResults;
    }

    public async Task<T?> QuerySingleOrDefaultAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param = null)
    {
        var list = await QueryAsync<T>(sql, param);
        return list.Count == 0 ? default : list[0];
    }

    public async Task<List<T>> QueryMappedAsync<T>(
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map)
    {
        LastExecutedSql = sql;
        LastExecutedParams = param;

        if (_isMock || _dataSource is null)
            return [];

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        await using var command = BuildCommand(connection, sql, param);
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<T>();

        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(map(dict));
        }

        return results;
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        LastExecutedSql = sql;
        LastExecutedParams = param;

        if (_isMock || _dataSource is null)
            return 1;

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        await using var command = BuildCommand(connection, sql, param);
        return await command.ExecuteNonQueryAsync();
    }

    public Task TransactionAsync(Func<IDbClient, Task> action) =>
        RunTransactionAsync(async () =>
        {
            await action(this);
            return 0;
        });

    public Task<T> TransactionAsync<T>(Func<IDbClient, Task<T>> action) =>
        RunTransactionAsync(() => action(this));

    private async Task<T> RunTransactionAsync<T>(Func<Task<T>> action)
    {
        if (AmbientTransaction.Value is not null)
            throw new NotSupportedException("Nested MySQL transactions are not supported.");

        if (_isMock || _dataSource is null)
            return await action();

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction =
            (MySqlTransaction)await connection.BeginTransactionAsync();

        AmbientTransaction.Value = new TransactionScope(connection, transaction);

        try
        {
            var result = await action();
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // Preserve the original failure.
            }

            throw;
        }
        finally
        {
            AmbientTransaction.Value = null;
        }
    }

    public async Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        if (_isMock || _dataSource is null)
            return statements.Count;

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        var ambient = AmbientTransaction.Value;

        if (ambient is not null)
        {
            var ambientChanges = 0;
            foreach (var statement in statements)
            {
                await using var command = BuildCommand(connection, statement.Sql, statement.Param);
                command.Transaction = ambient.Transaction;
                ambientChanges += await command.ExecuteNonQueryAsync();
            }
            return ambientChanges;
        }

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var changes = 0;
            foreach (var statement in statements)
            {
                await using var command = BuildCommand(connection, statement.Sql, statement.Param);
                command.Transaction = transaction;
                changes += await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return changes;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public Task<T?> GetAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        TableDef table,
        object id)
    {
        var pk = table.SinglePrimaryKey
            ?? throw new InvalidOperationException($"Table '{table.Name}' has no single primary key.");

        var sql = $"SELECT * FROM `{table.Name}` WHERE `{pk}` = @id LIMIT 1";
        return QuerySingleOrDefaultAsync<T>(sql, ("id", id));
    }

    public Task<List<T>> ListAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0)
    {
        var sql = new StringBuilder($"SELECT * FROM `{table.Name}`");

        if (!string.IsNullOrWhiteSpace(clause))
            sql.Append(' ').Append(clause);

        if (limit > 0)
        {
            sql.Append($" LIMIT {limit}");
            if (offset > 0)
                sql.Append($" OFFSET {offset}");
        }

        return QueryAsync<T>(sql.ToString(), param);
    }

    public Task<int> InsertAsync(TableDef table, object values)
    {
        if (values is IReptarEntity entity)
        {
            var rawParams = entity.ToDbParameters();
            var cols = new List<string>(rawParams.Length);
            var placeholders = new List<string>(rawParams.Length);
            var paramDict = new Dictionary<string, object?>(rawParams.Length, StringComparer.OrdinalIgnoreCase);

            foreach (var col in table.Columns)
            {
                for (var i = 0; i < rawParams.Length; i++)
                {
                    if (string.Equals(rawParams[i].Name, col.ClrName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawParams[i].Name, col.SqlName, StringComparison.OrdinalIgnoreCase))
                    {
                        cols.Add($"`{col.SqlName}`");
                        placeholders.Add($"@{col.ClrName}");
                        paramDict[col.ClrName] = rawParams[i].Value;
                        break;
                    }
                }
            }

            var entitySql = $"INSERT INTO `{table.Name}` ({string.Join(", ", cols)}) VALUES ({string.Join(", ", placeholders)})";
            return ExecuteAsync(entitySql, paramDict);
        }

        if (values is IDictionary dictionary)
        {
            var cols = new List<string>();
            var placeholders = new List<string>();
            var paramDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "";
                var col = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.ClrName, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.SqlName, key, StringComparison.OrdinalIgnoreCase));

                var sqlName = col?.SqlName ?? key;
                cols.Add($"`{sqlName}`");
                placeholders.Add($"@{key}");
                paramDict[key] = entry.Value;
            }

            var sql = $"INSERT INTO `{table.Name}` ({string.Join(", ", cols)}) VALUES ({string.Join(", ", placeholders)})";
            return ExecuteAsync(sql, paramDict);
        }

        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        if (properties.Count == 0)
        {
            try
            {
                var json = JsonSerializer.Serialize(values);
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.True => 1,
                        JsonValueKind.False => 0,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
                return InsertAsync(table, dict);
            }
            catch
            {
                // Fallback to property mapping
            }
        }

        var activeCols = table.Columns
            .Where(c => properties.ContainsKey(c.ClrName))
            .ToArray();

        var insertCols = activeCols.Select(c => $"`{c.SqlName}`");
        var insertParams = activeCols.Select(c => $"@{c.ClrName}");
        var insertSql = $"INSERT INTO `{table.Name}` ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertParams)})";

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in activeCols)
            parameters[col.ClrName] = properties[col.ClrName].GetValue(values);

        return ExecuteAsync(insertSql, parameters);
    }

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class =>
        InsertAsync(table, (object)values);

    public Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException($"Table '{table.Name}' has no single primary key.");

        if (values is IReptarEntity entity)
        {
            var rawParams = entity.ToDbParameters();
            var sets = new List<string>(rawParams.Length);
            var paramDict = new Dictionary<string, object?>(rawParams.Length + 1, StringComparer.OrdinalIgnoreCase);

            foreach (var col in table.Columns)
            {
                if (string.Equals(col.SqlName, primaryKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                for (var i = 0; i < rawParams.Length; i++)
                {
                    if (string.Equals(rawParams[i].Name, col.ClrName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawParams[i].Name, col.SqlName, StringComparison.OrdinalIgnoreCase))
                    {
                        sets.Add($"`{col.SqlName}` = @{col.ClrName}");
                        paramDict[col.ClrName] = rawParams[i].Value;
                        break;
                    }
                }
            }

            paramDict["__id"] = id;
            var entityUpdateSql = $"UPDATE `{table.Name}` SET {string.Join(", ", sets)} WHERE `{primaryKey}` = @__id";
            return ExecuteAsync(entityUpdateSql, paramDict);
        }

        if (values is IDictionary dictionary)
        {
            var sets = new List<string>();
            var paramDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "";
                var col = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.ClrName, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.SqlName, key, StringComparison.OrdinalIgnoreCase));

                var sqlName = col?.SqlName ?? key;
                if (string.Equals(sqlName, primaryKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                sets.Add($"`{sqlName}` = @{key}");
                paramDict[key] = entry.Value;
            }

            paramDict["__id"] = id;
            var updateSql = $"UPDATE `{table.Name}` SET {string.Join(", ", sets)} WHERE `{primaryKey}` = @__id";
            return ExecuteAsync(updateSql, paramDict);
        }

        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var columns = table.Columns
            .Where(c => properties.ContainsKey(c.ClrName) && c.SqlName != primaryKey)
            .ToArray();

        var setsList = columns.Select(c => $"`{c.SqlName}` = @{c.ClrName}");
        var sql = $"UPDATE `{table.Name}` SET {string.Join(", ", setsList)} WHERE `{primaryKey}` = @__id";

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            parameters[col.ClrName] = properties[col.ClrName].GetValue(values);

        parameters["__id"] = id;
        return ExecuteAsync(sql, parameters);
    }

    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class =>
        UpdateAsync(table, (object)values, id);

    public Task<int> DeleteAsync(TableDef table, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException($"Table '{table.Name}' has no single primary key.");

        var sql = $"DELETE FROM `{table.Name}` WHERE `{primaryKey}` = @id";
        return ExecuteAsync(sql, ("id", id));
    }

    public Task<int> DeleteAsync<T>(TableDef table, object id) => DeleteAsync(table, id);

    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null) =>
        ExecuteAsync($"DELETE FROM `{table.Name}` WHERE {where}", param);

    public async Task<bool> ExistsAsync(TableDef table, string where, object? param = null)
    {
        var sql = $"SELECT EXISTS(SELECT 1 FROM `{table.Name}` WHERE {where})";
        LastExecutedSql = sql;
        LastExecutedParams = param;

        if (_isMock || _dataSource is null)
            return false;

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        await using var command = BuildCommand(connection, sql, param);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    public async Task<int> CountAsync(TableDef table, string? where = null, object? param = null)
    {
        var clause = string.IsNullOrWhiteSpace(where)
            ? ""
            : where.TrimStart().StartsWith("where", StringComparison.OrdinalIgnoreCase)
                ? " " + where.Trim()
                : " WHERE " + where.Trim();

        var sql = $"SELECT COUNT(*) AS count FROM `{table.Name}`{clause}";
        var row = await QuerySingleOrDefaultAsync<DbCount>(sql, param);
        return row?.Count ?? 0;
    }

    public async Task EnsureCreatedAsync(params TableDef[] tables)
    {
        if (_isMock || _dataSource is null)
            return;

        await using var lease = await RentConnectionAsync();
        var connection = lease.Connection;
        foreach (var table in tables)
        {
            var ddl = SqlDialects.MySql.CreateTable(table);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = ddl;
                command.Transaction = AmbientTransaction.Value?.Transaction;
                await command.ExecuteNonQueryAsync();
            }

            foreach (var indexSql in SqlDialects.MySql.CreateIndexes(table))
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = indexSql;
                    command.Transaction = AmbientTransaction.Value?.Transaction;
                    await command.ExecuteNonQueryAsync();
                }
                catch (MySqlException)
                {
                    // Ignore duplicate index errors
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dataSource?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private static MySqlCommand BuildCommand(MySqlConnection connection, string sql, object? param)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value?.Transaction;

        if (param is null)
            return command;

        if (param is IReptarEntity entity)
        {
            var entityParams = entity.ToDbParameters();
            for (var i = 0; i < entityParams.Length; i++)
            {
                var pName = entityParams[i].Name;
                var name = pName.StartsWith('@') ? pName : "@" + pName;
                command.Parameters.AddWithValue(name, entityParams[i].Value ?? DBNull.Value);
            }
            return command;
        }

        if (param is System.Runtime.CompilerServices.ITuple singleTuple && singleTuple.Length == 2)
        {
            var pName = singleTuple[0]?.ToString() ?? "";
            var name = pName.StartsWith('@') ? pName : "@" + pName;
            command.Parameters.AddWithValue(name, singleTuple[1] ?? DBNull.Value);
            return command;
        }

        if (param is IEnumerable tupleEnum && param is not string && param is not IDictionary)
        {
            var isTupleSeq = false;
            foreach (var item in tupleEnum)
            {
                if (item is System.Runtime.CompilerServices.ITuple t && t.Length == 2)
                {
                    isTupleSeq = true;
                    var pName = t[0]?.ToString() ?? "";
                    var name = pName.StartsWith('@') ? pName : "@" + pName;
                    command.Parameters.AddWithValue(name, t[1] ?? DBNull.Value);
                }
                else
                {
                    isTupleSeq = false;
                    break;
                }
            }

            if (isTupleSeq)
                return command;
        }

        if (param is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var name = entry.Key?.ToString() ?? "";
                if (!name.StartsWith('@'))
                    name = "@" + name;

                command.Parameters.AddWithValue(name, entry.Value ?? DBNull.Value);
            }
            return command;
        }

        if (param is IEnumerable enumerable && param is not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                command.Parameters.AddWithValue("@p" + index, item ?? DBNull.Value);
                index++;
            }
            return command;
        }

        foreach (var property in param.GetType().GetProperties())
        {
            command.Parameters.AddWithValue(
                "@" + property.Name,
                property.GetValue(param) ?? DBNull.Value);
        }

        return command;
    }
}
#else
namespace Reptar;

public sealed class MySqlDbClient : IDbClient, IDisposable, IAsyncDisposable
{
    public MySqlDbClient(string connectionString)
    {
        throw new PlatformNotSupportedException("MySQL support was excluded at compile time (ReptarSqliteOnly=true).");
    }

    public string ConnectionString => throw new PlatformNotSupportedException();
    public string? LastExecutedSql => null;
    public object? LastExecutedParams => null;
    public Task<List<T>> QueryAsync<T>(string sql, object? param = null) => throw new PlatformNotSupportedException();
    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) => throw new PlatformNotSupportedException();
    public Task<int> ExecuteAsync(string sql, object? param = null) => throw new PlatformNotSupportedException();
    public Task<int> BatchAsync(IReadOnlyList<DbStatement> statements) => throw new PlatformNotSupportedException();
    public Task<T?> GetAsync<T>(TableDef table, object id) => throw new PlatformNotSupportedException();
    public Task<List<T>> ListAsync<T>(TableDef table, string? clause = null, object? param = null, int limit = 0, int offset = 0) => throw new PlatformNotSupportedException();
    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class => throw new PlatformNotSupportedException();
    public Task<int> InsertAsync(TableDef table, object values) => throw new PlatformNotSupportedException();
    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class => throw new PlatformNotSupportedException();
    public Task<int> UpdateAsync(TableDef table, object values, object id) => throw new PlatformNotSupportedException();
    public Task<int> DeleteAsync(TableDef table, object id) => throw new PlatformNotSupportedException();
    public Task<int> DeleteAsync<T>(TableDef table, object id) => throw new PlatformNotSupportedException();
    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null) => throw new PlatformNotSupportedException();
    public Task<bool> ExistsAsync(TableDef table, string where, object? param = null) => throw new PlatformNotSupportedException();
    public Task EnsureCreatedAsync(params TableDef[] tables) => throw new PlatformNotSupportedException();
    public void Dispose() {}
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
