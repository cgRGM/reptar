using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class D1DbClient : IDbClient
{
    private readonly IJsonCodec _json;

    public D1DbClient(IJsonCodec json) => _json = json;

    [JSImport("query", "app.host")]
    private static extern Task<string> Query(string sql, string paramsJson);

    [JSImport("execute", "app.host")]
    private static extern Task<int> Execute(string sql, string paramsJson);

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        var json = await Query(sql, Params(param));
        return _json.Deserialize<List<T>>(json) ?? [];
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? param = null)
    {
        var rows = await QueryAsync<T>(sql, param);
        return rows.Count == 0 ? default : rows[0];
    }

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        Execute(sql, Params(param));

    public Task<T?> GetAsync<T>(TableDef table, object id) =>
        throw Unsupported("GetAsync");

    public Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0) =>
        throw Unsupported("ListAsync");

    public Task<int> InsertAsync(TableDef table, object values) =>
        throw Unsupported("InsertAsync");

    public Task<int> UpdateAsync(TableDef table, object values, object id) =>
        throw Unsupported("UpdateAsync");

    public Task<int> DeleteAsync(TableDef table, object id) =>
        throw Unsupported("DeleteAsync");

    public Task<int> DeleteAsync<T>(TableDef table, object id) =>
        throw Unsupported("DeleteAsync");

    public Task<int> DeleteWhereAsync(
        TableDef table,
        string where,
        object? param = null) =>
        throw Unsupported("DeleteWhereAsync");

    public Task<bool> ExistsAsync(
        TableDef table,
        string where,
        object? param = null) =>
        throw Unsupported("ExistsAsync");

    public Task EnsureCreatedAsync(params TableDef[] tables) =>
        Task.CompletedTask;

    private static string Params(object? param) => param as string ?? "[]";

    private static NotSupportedException Unsupported(string operation) =>
        new($"{operation} requires source-generated table metadata on the Wasm target.");
}
