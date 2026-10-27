using System.Collections;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class D1DbClient : IDbClient
{
    private readonly IJsonCodec _json;

    public D1DbClient(IJsonCodec json) => _json = json;

    [JSImport("query", "app.host")]
    private static extern Task<string> Query(string sql, string paramsJson);

    [JSImport("execute", "app.host")]
    private static extern Task<int> Execute(string sql, string paramsJson);

    [JSImport("batch", "app.host")]
    private static extern Task<int> Batch(string batchJson);

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        var (normSql, paramsJson) = NormalizeSqlAndParams(sql, param);
        var json = await Query(normSql, paramsJson);
        return _json.Deserialize<List<T>>(json) ?? [];
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? param = null)
    {
        var rows = await QueryAsync<T>(sql, param);
        return rows.Count == 0 ? default : rows[0];
    }

    public Task<int> ExecuteAsync(string sql, object? param = null)
    {
        var (normSql, paramsJson) = NormalizeSqlAndParams(sql, param);
        return Execute(normSql, paramsJson);
    }

    public Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        var parts = new List<string>();

        foreach (var statement in statements)
        {
            var (sql, paramsJson) = NormalizeSqlAndParams(
                statement.Sql,
                statement.Param);
            parts.Add(
                $"{{\"sql\":{JsonSerializer.Serialize(sql, ReptarJson.Options)},\"params\":{JsonSerializer.Serialize(paramsJson, ReptarJson.Options)}}}");
        }

        return Batch($"[{string.Join(",", parts)}]");
    }

    public Task<T?> GetAsync<T>(TableDef table, object id)
    {
        var pk = table.SinglePrimaryKey;
        var sql = $"select * from \"{table.Name}\" where \"{pk}\" = ? limit 1";
        var paramsJson = SerializeParams([id]);
        return QuerySingleOrDefaultAsync<T>(sql, paramsJson);
    }

    public Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0)
    {
        var sql = new StringBuilder($"select * from \"{table.Name}\"");

        if (!string.IsNullOrWhiteSpace(clause))
        {
            sql.Append(' ').Append(clause);
        }

        if (limit <= 0) return QueryAsync<T>(sql.ToString(), param);
        var (normalizedSql, paramsJson) = NormalizeSqlAndParams(sql.ToString(), param);
        var page = SqlPagination.AppendPositionalJson(normalizedSql, paramsJson, limit, offset);
        return QueryAsync<T>(page.Sql, page.ParamsJson);
    }

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class =>
        // Dictionaries bypass JSON entirely (see the object overload). Keep T
        // in the generic call for DTOs so their source-generated metadata is
        // used; casting those to object would ask the resolver for `object`
        // and fail under NetWasm.
        values is IDictionary
            ? InsertAsync(table, (object)values)
            : InsertFromJsonAsync(table, _json.Serialize(values));

    public Task<int> InsertAsync(TableDef table, object values)
    {
        // Dictionaries (and any IDictionary) map straight onto columns:
        // System.Text.Json source generation under NetWasm has no metadata
        // for Dictionary<string, object?> and would otherwise throw
        // NotSupportedException at runtime.
        if (values is IDictionary dictionary)
        {
            var cols = new List<string>();
            var paramValues = new List<object?>();

            foreach (DictionaryEntry entry in dictionary)
            {
                cols.Add($"\"{ResolveColumnName(table, entry.Key?.ToString() ?? "")}\"");
                paramValues.Add(entry.Value);
            }

            var placeholders = string.Join(", ", System.Linq.Enumerable.Repeat("?", cols.Count));
            var sql = $"insert into \"{table.Name}\" ({string.Join(", ", cols)}) values ({placeholders})";
            return ExecuteAsync(sql, SerializeParams(paramValues));
        }

        var json = values is string s && s.TrimStart().StartsWith("{")
            ? s
            : SerializeForInsert(values);
        return InsertFromJsonAsync(table, json);
    }

    private string SerializeForInsert(object values)
    {
        try
        {
            return _json.Serialize(values);
        }
        catch (System.NotSupportedException exception)
        {
            throw new System.NotSupportedException(
                $"Serializing {values.GetType()} for a D1 insert requires the type in the app's JsonSerializerContext " +
                $"(set ReptarJson.Options.TypeInfoResolver in your worker Program.cs and add [JsonSerializable(typeof({values.GetType().Name}))]), " +
                "or pass an IDictionary / raw JSON string / raw ExecuteAsync parameters instead.",
                exception);
        }
    }

    private Task<int> InsertFromJsonAsync(TableDef table, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cols = new List<string>();
        var placeholders = new List<string>();
        var paramValues = new List<object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var colName = ResolveColumnName(table, prop.Name);
            cols.Add($"\"{colName}\"");
            placeholders.Add("?");
            paramValues.Add(JsonElementToValue(prop.Value));
        }

        var sql = $"insert into \"{table.Name}\" ({string.Join(", ", cols)}) values ({string.Join(", ", placeholders)})";
        return ExecuteAsync(sql, SerializeParams(paramValues));
    }

    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class =>
        values is IDictionary
            ? UpdateAsync(table, (object)values, id)
            : UpdateFromJsonAsync(table, _json.Serialize(values), id);

    public Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        if (values is IDictionary dictionary)
        {
            var pk = table.SinglePrimaryKey;
            var setClauses = new List<string>();
            var paramValues = new List<object?>();

            foreach (DictionaryEntry entry in dictionary)
            {
                var column = ResolveColumnName(table, entry.Key?.ToString() ?? "");
                if (string.Equals(column, pk, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(column, "created_at", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                setClauses.Add($"\"{column}\" = ?");
                paramValues.Add(entry.Value);
            }

            paramValues.Add(id);
            var sql = $"update \"{table.Name}\" set {string.Join(", ", setClauses)} where \"{pk}\" = ?";
            return ExecuteAsync(sql, SerializeParams(paramValues));
        }

        var json = values is string s && s.TrimStart().StartsWith("{")
            ? s
            : _json.Serialize(values);
        return UpdateFromJsonAsync(table, json, id);
    }

    private Task<int> UpdateFromJsonAsync(TableDef table, string json, object id)
    {
        var pk = table.SinglePrimaryKey;
        using var doc = JsonDocument.Parse(json);
        var setClauses = new List<string>();
        var paramValues = new List<object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var colName = ResolveColumnName(table, prop.Name);
            if (string.Equals(colName, pk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(colName, "created_at", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            setClauses.Add($"\"{colName}\" = ?");
            paramValues.Add(JsonElementToValue(prop.Value));
        }

        paramValues.Add(id);
        var sql = $"update \"{table.Name}\" set {string.Join(", ", setClauses)} where \"{pk}\" = ?";
        return ExecuteAsync(sql, SerializeParams(paramValues));
    }

    public Task<int> DeleteAsync(TableDef table, object id)
    {
        var pk = table.SinglePrimaryKey;
        var sql = $"delete from \"{table.Name}\" where \"{pk}\" = ?";
        return ExecuteAsync(sql, SerializeParams([id]));
    }

    public Task<int> DeleteAsync<T>(TableDef table, object id) =>
        DeleteAsync(table, id);

    public Task<int> DeleteWhereAsync(
        TableDef table,
        string where,
        object? param = null)
    {
        var sql = $"delete from \"{table.Name}\" where {where}";
        return ExecuteAsync(sql, param);
    }

    public async Task<bool> ExistsAsync(
        TableDef table,
        string where,
        object? param = null)
    {
        var sql = $"select 1 as count from \"{table.Name}\" where {where} limit 1";
        var rows = await QueryAsync<DbCount>(sql, param);
        return rows.Count > 0;
    }

    public Task EnsureCreatedAsync(params TableDef[] tables) =>
        Task.CompletedTask;

    private (string Sql, string ParamsJson) NormalizeSqlAndParams(string sql, object? param)
    {
        if (param is null)
        {
            return (sql, "[]");
        }

        if (param is string s && s.TrimStart().StartsWith("["))
        {
            return (sql, s);
        }

        if (param is string or int or long or double or float or decimal or bool or Guid)
        {
            return (sql, SerializeParams([param]));
        }

        if (param is IDictionary dictionary)
        {
            var dictFromMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in dictionary)
            {
                dictFromMap[entry.Key?.ToString() ?? ""] = entry.Value;
            }

            if (sql.Contains('@') || sql.Contains('$'))
            {
                var (rewrittenSql, paramNames) =
                    SqlParamParser.ParseNamedParameters(sql);
                var positionalValues = new List<object?>();

                foreach (var name in paramNames)
                {
                    positionalValues.Add(
                        dictFromMap.TryGetValue(name, out var value) ? value : null);
                }

                return (rewrittenSql, SerializeParams(positionalValues));
            }

            return (sql, SerializeParams(dictFromMap.Values));
        }

        if (param is System.Collections.IEnumerable list and not string and not System.Collections.IDictionary)
        {
            var items = new List<object?>();
            foreach (var item in list)
            {
                items.Add(item);
            }
            return (sql, SerializeParams(items));
        }

        var json = _json.Serialize(param);
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

        if (sql.Contains('@') || sql.Contains('$'))
        {
            var (rewrittenSql, paramNames) =
                SqlParamParser.ParseNamedParameters(sql);
            var positionalValues = new List<object?>();

            foreach (var name in paramNames)
            {
                if (dict.TryGetValue(name, out var val))
                {
                    positionalValues.Add(val);
                }
                else
                {
                    positionalValues.Add(null);
                }
            }

            return (rewrittenSql, SerializeParams(positionalValues));
        }

        var valuesList = new List<object?>();
        foreach (var val in dict.Values)
        {
            valuesList.Add(val);
        }
        return (sql, SerializeParams(valuesList));
    }

    private static string ResolveColumnName(TableDef table, string propName)
    {
        var normalizedProp = propName.Replace("_", "");
        foreach (var col in table.Columns)
        {
            if (string.Equals(col.ClrName, propName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.SqlName, propName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.SqlName.Replace("_", ""), normalizedProp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.ClrName.Replace("_", ""), normalizedProp, StringComparison.OrdinalIgnoreCase))
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

    private static object? JsonElementToValue(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.String => elem.GetString(),
        JsonValueKind.Number => elem.TryGetInt64(out var l) ? l : elem.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => elem.GetRawText(),
    };

    private static string SerializeParams(IEnumerable<object?> values)
    {
        var parts = new List<string>();

        foreach (var v in values)
        {
            if (v is null)
            {
                parts.Add("null");
            }
            else if (v is string s)
            {
                parts.Add(JsonSerializer.Serialize(s, ReptarJson.Options));
            }
            else if (v is bool b)
            {
                parts.Add(b ? "1" : "0");
            }
            else if (v is int or long or short or byte or float or double or decimal)
            {
                parts.Add(v.ToString() ?? "0");
            }
            else
            {
                parts.Add(JsonSerializer.Serialize(v.ToString() ?? "", ReptarJson.Options));
            }
        }

        return $"[{string.Join(",", parts)}]";
    }

}
