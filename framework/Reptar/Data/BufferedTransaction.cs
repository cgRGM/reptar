namespace Reptar;

public static class DbTransactionExtensions
{
    /// <summary>
    /// Convenience overload for concrete clients (SQLite/MySQL/D1) that expose
    /// the transaction contract through IDbClient's default implementation.
    /// </summary>
    public static Task TransactionAsync(
        this IDbClient db,
        Func<IDbClient, Task> action) =>
        ((IDbClient)db).TransactionAsync(action);

    public static Task<T> TransactionAsync<T>(
        this IDbClient db,
        Func<IDbClient, Task<T>> action) =>
        ((IDbClient)db).TransactionAsync(action);
}

/// <summary>
/// A small write-only transaction buffer. Statements are collected until the
/// action completes, then committed through <see cref="IDbClient.BatchAsync"/>
/// (which is transactional for SQLite/PostgreSQL/MySQL and atomic for D1).
///
/// Reads inside a buffered transaction are rejected instead of silently
/// escaping the transaction. Use the provider-specific transaction context
/// (for example <c>PostgresDbClient.TransactionAsync</c>) when you need reads
/// inside the transaction.
/// </summary>
public sealed class BufferedTransactionClient(IDbClient inner) : IDbClient
{
    private readonly List<DbStatement> _statements = [];

    public static async Task RunAsync(
        IDbClient inner,
        Func<IDbClient, Task> action)
    {
        var transaction = new BufferedTransactionClient(inner);
        await action(transaction);
        await transaction.CommitAsync();
    }

    public static async Task<T> RunAsync<T>(
        IDbClient inner,
        Func<IDbClient, Task<T>> action)
    {
        var transaction = new BufferedTransactionClient(inner);
        var result = await action(transaction);
        await transaction.CommitAsync();
        return result;
    }

    private async Task CommitAsync()
    {
        if (_statements.Count > 0)
            await inner.BatchAsync(_statements);
    }

    public Task<int> ExecuteAsync(string sql, object? param = null)
    {
        _statements.Add(new DbStatement(sql, param));
        return Task.FromResult(1);
    }

    public Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        _statements.AddRange(statements);
        return Task.FromResult(statements.Count);
    }

    public Task<List<T>> QueryAsync<T>(string sql, object? param = null) =>
        throw ReadNotSupported();

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
        throw ReadNotSupported();

    public Task<T?> GetAsync<T>(TableDef table, object id) =>
        throw ReadNotSupported();

    public Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0) =>
        throw ReadNotSupported();

    public Task<int> InsertAsync(TableDef table, object values) =>
        throw WriteShapeNotSupported();

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class =>
        throw WriteShapeNotSupported();

    public Task<int> UpdateAsync(TableDef table, object values, object id) =>
        throw WriteShapeNotSupported();

    public Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class =>
        throw WriteShapeNotSupported();

    public Task<int> DeleteAsync(TableDef table, object id) =>
        throw WriteShapeNotSupported();

    public Task<int> DeleteAsync<T>(TableDef table, object id) =>
        throw WriteShapeNotSupported();

    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null) =>
        throw WriteShapeNotSupported();

    public Task<bool> ExistsAsync(TableDef table, string where, object? param = null) =>
        throw ReadNotSupported();

    public Task<List<T>> QueryMappedAsync<T>(
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map) =>
        throw ReadNotSupported();

    public Task EnsureCreatedAsync(params TableDef[] tables) =>
        throw WriteShapeNotSupported();

    public Task TransactionAsync(Func<IDbClient, Task> action) =>
        throw new NotSupportedException("Nested transactions are not supported.");

    public Task<T> TransactionAsync<T>(Func<IDbClient, Task<T>> action) =>
        throw new NotSupportedException("Nested transactions are not supported.");

    private static NotSupportedException ReadNotSupported() =>
        new(
            "Reads inside a buffered transaction are not supported. " +
            "Use the provider-specific transaction context for read/write transactions.");

    private static NotSupportedException WriteShapeNotSupported() =>
        new(
            "Buffered transactions support ExecuteAsync and BatchAsync only. " +
            "Use ExecuteAsync for writes, or the provider-specific transaction context.");
}
