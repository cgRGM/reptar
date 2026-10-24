using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Reptar;

public sealed class SqliteDbClient : IDbClient, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDbClient(string path)
    {
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await _gate.WaitAsync();

        try
        {
            await using var command = BuildCommand(sql, param);
            await using var reader = await command.ExecuteReaderAsync();
            var map = RowMapper.Resolve<T>(reader);

            var results = new List<T>();
            while (await reader.ReadAsync())
                results.Add(map(reader));

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        var results = await QueryAsync<T>(sql, param);
        return results.Count == 0 ? default : results[0];
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await _gate.WaitAsync();

        try
        {
            await using var command = BuildCommand(sql, param);
            return await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountAsync(
        TableDef table,
        string? where = null,
        object? param = null)
    {
        var clause = string.IsNullOrWhiteSpace(where)
            ? ""
            : where.TrimStart().StartsWith("where", StringComparison.OrdinalIgnoreCase)
                ? " " + where.Trim()
                : " where " + where.Trim();

        var row = await QuerySingleOrDefaultAsync<DbCount>(
            $"SELECT count(*) AS count FROM \"{table.Name}\"{clause}",
            param);

        return row?.Count ?? 0;
    }

    public async Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        await _gate.WaitAsync();

        try
        {
            await using var transaction = await _connection.BeginTransactionAsync();
            var changes = 0;

            try
            {
                foreach (var statement in statements)
                {
                    await using var command = BuildCommand(
                        statement.Sql,
                        statement.Param);
                    command.Transaction = (SqliteTransaction)transaction;
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
        finally
        {
            _gate.Release();
        }
    }

    public Task<T?> GetAsync<T>(TableDef table, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException(
                $"Table '{table.Name}' has no single primary key.");

        return QuerySingleOrDefaultAsync<T>(
            $"SELECT * FROM \"{table.Name}\" WHERE \"{primaryKey}\" = @id",
            new { id });
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
        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        var columns = table.Columns
            .Where(column => properties.ContainsKey(column.ClrName))
            .ToArray();

        var sql = new StringBuilder();
        sql.Append($"INSERT INTO \"{table.Name}\" (");
        sql.Append(string.Join(", ", columns.Select(column => $"\"{column.SqlName}\"")));
        sql.Append(") VALUES (");
        sql.Append(string.Join(", ", columns.Select(column => "@" + column.ClrName)));
        sql.Append(')');

        return ExecuteAsync(sql.ToString(), values);
    }

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class =>
        InsertAsync(table, (object)values);

    public Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException(
                $"Table '{table.Name}' has no single primary key.");

        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        var columns = table.Columns
            .Where(column =>
                properties.ContainsKey(column.ClrName) &&
                column.SqlName != primaryKey)
            .ToArray();

        var sql = new StringBuilder();
        sql.Append($"UPDATE \"{table.Name}\" SET ");
        sql.Append(string.Join(", ", columns.Select(
            column => $"\"{column.SqlName}\" = @{column.ClrName}")));
        sql.Append($" WHERE \"{primaryKey}\" = @__id");

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
            parameters[column.ClrName] = properties[column.ClrName].GetValue(values);

        parameters["__id"] = id;

        return ExecuteAsync(sql.ToString(), parameters);
    }

    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class =>
        UpdateAsync(table, (object)values, id);

    public Task<int> DeleteAsync(TableDef table, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException(
                $"Table '{table.Name}' has no single primary key.");

        return ExecuteAsync(
            $"DELETE FROM \"{table.Name}\" WHERE \"{primaryKey}\" = @id",
            new { id });
    }

    public Task<int> DeleteAsync<T>(TableDef table, object id) => DeleteAsync(table, id);

    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null) =>
        ExecuteAsync($"DELETE FROM \"{table.Name}\" WHERE {where}", param);

    public async Task<bool> ExistsAsync(TableDef table, string where, object? param = null)
    {
        await _gate.WaitAsync();

        try
        {
            await using var command = BuildCommand(
                $"SELECT EXISTS(SELECT 1 FROM \"{table.Name}\" WHERE {where})",
                param);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureCreatedAsync(params TableDef[] tables)
    {
        foreach (var table in tables)
        {
            await ExecuteAsync(SchemaDdl.CreateTable(table) + ";");

            foreach (var index in SchemaDdl.CreateIndexes(table))
                await ExecuteAsync(index + ";");
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    private SqliteCommand BuildCommand(string sql, object? param)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;

        if (param is null)
            return command;

        if (param is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                command.Parameters.AddWithValue(
                    "@" + entry.Key,
                    entry.Value ?? DBNull.Value);
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

internal static class RowMapper
{
    public static Func<SqliteDataReader, T> Resolve<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] T>(SqliteDataReader reader)
    {
        var constructors = typeof(T).GetConstructors();
        if (constructors.Length == 0)
        {
            return _ => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        }

        var constructor = constructors
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var parameters = constructor.GetParameters();
        var columns = new (int Ordinal, ParameterInfo Parameter)[parameters.Length];

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var ordinal = -1;

            for (var column = 0; column < reader.FieldCount; column++)
            {
                var name = reader.GetName(column);

                if (string.Equals(name, parameter.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ToPascal(name), parameter.Name, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = column;
                    break;
                }
            }

            columns[index] = (ordinal, parameter);
        }

        return readerInstance =>
        {
            var arguments = new object?[parameters.Length];

            for (var index = 0; index < parameters.Length; index++)
            {
                var (ordinal, parameter) = columns[index];

                if (ordinal < 0 || readerInstance.IsDBNull(ordinal))
                {
                    arguments[index] = parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
                    continue;
                }

                arguments[index] = ConvertValue(
                    readerInstance.GetValue(ordinal),
                    parameter.ParameterType);
            }

            return (T)constructor.Invoke(arguments)!;
        };
    }

    private static string ToPascal(string name) =>
        string.Join(
            "",
            name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static object? ConvertValue(object value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (type == typeof(string))
            return value.ToString();

        if (type == typeof(int))
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);

        if (type == typeof(long))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        if (type == typeof(short))
            return Convert.ToInt16(value, CultureInfo.InvariantCulture);

        if (type == typeof(double))
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);

        if (type == typeof(float))
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);

        if (type == typeof(decimal))
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (type == typeof(bool))
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);

        if (type == typeof(byte[]))
            return value;

        if (type == typeof(Guid))
            return Guid.Parse(value.ToString()!);

        if (type == typeof(DateTime))
        {
            return value is DateTime dateTime
                ? dateTime
                : DateTime.Parse(
                    value.ToString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
        }

        if (type == typeof(DateTimeOffset))
        {
            return value is DateTimeOffset offset
                ? offset
                : DateTimeOffset.Parse(
                    value.ToString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
        }

        if (type.IsEnum)
            return Enum.Parse(type, value.ToString()!, ignoreCase: true);

        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }
}
