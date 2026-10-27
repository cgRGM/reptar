using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Reptar;

public readonly record struct DbWriteTiming(long GateWaitTicks, long ExecuteTicks);

public sealed class SqliteDbClient : IDbClient, IDisposable
{
    private readonly string _connectionString;
    private readonly bool _isMemory;
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readSemaphore;
    private readonly ConcurrentQueue<SqliteConnection> _readConnections = new();
    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
    private readonly List<PendingWrite> _batchBuffer = new(64);
    private readonly List<SqliteConnection> _allConnections = new();
    private readonly string _readConnectionString;
    private int _drainerRunning;
    private bool _disposed;

    // Flows with the caller's async context. When set, all commands use the
    // single writer connection and the active transaction; reads see
    // uncommitted writes, which is what a read/write transaction needs.
    private static readonly AsyncLocal<SqliteTransaction?> AmbientTransaction = new();

    private const int MaxBatchSize = 64;
    private const int MaxTransientRetries = 3;

    private readonly record struct PendingWrite(
        SqliteCommand Command,
        TaskCompletionSource<int> Completion,
        long GateStartedTimestamp);

    /// <summary>
    /// Optional write-path observer used by benchmarks/telemetry. Receives the
    /// time spent waiting for the write gate and the time spent executing the
    /// statement. Null by default, so the hot path only pays a null check.
    /// </summary>
    public Action<DbWriteTiming>? WriteTimingObserver { get; set; }

    public SqliteDbClient(string path, int maxReadConnections = 0)
    {
        _isMemory = string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

        _connectionString = path.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"Data Source={path}";

        if (!_isMemory && !_connectionString.Contains("Mode=", StringComparison.OrdinalIgnoreCase))
        {
            _readConnectionString = _connectionString.TrimEnd(';') + ";Mode=ReadOnly";
        }
        else
        {
            _readConnectionString = _connectionString;
        }

        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();

        lock (_allConnections)
        {
            _allConnections.Add(_writeConnection);
        }

        ExecutePragma(_writeConnection, "PRAGMA foreign_keys = ON");
        ExecutePragma(_writeConnection, "PRAGMA busy_timeout = 5000");

        var journalMode = AllowedPragma(
            Environment.GetEnvironmentVariable("REPTAR_SQLITE_JOURNAL_MODE"),
            "WAL",
            "DELETE",
            "TRUNCATE",
            "PERSIST",
            "MEMORY",
            "WAL");
        ExecutePragma(_writeConnection, $"PRAGMA journal_mode = {journalMode}");

        var synchronous = AllowedPragma(
            Environment.GetEnvironmentVariable("REPTAR_SQLITE_SYNCHRONOUS"),
            "NORMAL",
            "OFF",
            "NORMAL",
            "FULL",
            "EXTRA");
        ExecutePragma(_writeConnection, $"PRAGMA synchronous = {synchronous}");

        if (_isMemory)
        {
            _readSemaphore = new SemaphoreSlim(1, 1);
        }
        else
        {
            if (maxReadConnections <= 0)
            {
                var envPool = Environment.GetEnvironmentVariable("REPTAR_SQLITE_READ_POOL_SIZE");
                if (int.TryParse(envPool, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    maxReadConnections = parsed;
                else
                    maxReadConnections = Math.Clamp(Environment.ProcessorCount * 2, 4, 16);
            }

            _readSemaphore = new SemaphoreSlim(maxReadConnections, maxReadConnections);
        }
    }

    private static string AllowedPragma(
        string? value,
        string fallback,
        params string[] allowed)
    {
        var candidate = (value ?? fallback).ToUpperInvariant();
        return allowed.Contains(candidate, StringComparer.Ordinal)
            ? candidate
            : fallback;
    }

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private bool TryRentReadConnectionSync(string? sql, out RentedConnection rented)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (AmbientTransaction.Value is not null)
        {
            rented = new RentedConnection(
                _writeConnection,
                isWriter: false,
                this,
                leaveOpen: true);
            return true;
        }

        if (_isMemory || (sql is not null && sql.Length <= 128 && sql.Contains("last_insert_rowid", StringComparison.OrdinalIgnoreCase)))
        {
            if (_writeGate.Wait(0))
            {
                rented = new RentedConnection(_writeConnection, isWriter: true, this);
                return true;
            }
            rented = default;
            return false;
        }

        if (!_readSemaphore.Wait(0))
        {
            rented = default;
            return false;
        }

        if (!_readConnections.TryDequeue(out var connection))
        {
            connection = CreateReadConnection();
            lock (_allConnections)
            {
                _allConnections.Add(connection);
            }
        }

        rented = new RentedConnection(connection, isWriter: false, this);
        return true;
    }

    private async ValueTask<RentedConnection> RentReadConnectionAsync(string? sql = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (AmbientTransaction.Value is not null)
        {
            return new RentedConnection(
                _writeConnection,
                isWriter: false,
                this,
                leaveOpen: true);
        }

        if (_isMemory || (sql is not null && sql.Length <= 128 && sql.Contains("last_insert_rowid", StringComparison.OrdinalIgnoreCase)))
        {
            await _writeGate.WaitAsync();
            return new RentedConnection(_writeConnection, isWriter: true, this);
        }

        await _readSemaphore.WaitAsync();
        if (!_readConnections.TryDequeue(out var connection))
        {
            connection = CreateReadConnection();
            lock (_allConnections)
            {
                _allConnections.Add(connection);
            }
        }

        return new RentedConnection(connection, isWriter: false, this);
    }

    private SqliteConnection CreateReadConnection()
    {
        var connection = new SqliteConnection(_readConnectionString);
        connection.Open();
        ExecutePragma(connection, "PRAGMA foreign_keys = ON");
        ExecutePragma(connection, "PRAGMA busy_timeout = 5000");
        return connection;
    }

    private readonly struct RentedConnection : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly bool _isWriter;
        private readonly bool _leaveOpen;
        private readonly SqliteDbClient _client;

        public RentedConnection(
            SqliteConnection connection,
            bool isWriter,
            SqliteDbClient client,
            bool leaveOpen = false)
        {
            _connection = connection;
            _isWriter = isWriter;
            _leaveOpen = leaveOpen;
            _client = client;
        }

        public SqliteConnection Connection => _connection;

        public void Dispose()
        {
            if (_leaveOpen)
                return;

            if (_isWriter)
            {
                _client._writeGate.Release();
            }
            else
            {
                _client._readConnections.Enqueue(_connection);
                _client._readSemaphore.Release();
            }
        }
    }

    public static bool IsTransientSqliteError(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY (5) or SQLITE_LOCKED (6)

    private static List<T> ReadAllRows<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (EntityMapper<T>.ResolveForSql(command.CommandText, reader) is { } map)
        {
            var results = new List<T>(24);
            while (reader.Read())
                results.Add(map(reader));
            return results;
        }

        var fallbackMap = RowMapper.Resolve<T>(reader);
        var fallbackResults = new List<T>(24);
        while (reader.Read())
            fallbackResults.Add(fallbackMap(reader));
        return fallbackResults;
    }

    private static T? ReadSingleRow<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return default;

        if (EntityMapper<T>.ResolveForSql(command.CommandText, reader) is { } map)
            return map(reader);

        var fallbackMap = RowMapper.Resolve<T>(reader);
        return fallbackMap(reader);
    }

    private static async Task<List<T>> ExecuteReaderInternalAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        SqliteCommand command)
    {
        await using (command)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return ReadAllRows<T>(command);
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex) && attempt < MaxTransientRetries)
                {
                    await DelayBackoffAsync(attempt).ConfigureAwait(false);
                }
            }
        }
    }

    public Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param = null)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, param))
            {
                try
                {
                    return Task.FromResult(ReadAllRows<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                    // Fall through to async retry path
                }
            }
        }

        return QueryAsyncSlow<T>(sql, param);
    }

    private async Task<List<T>> QueryAsyncSlow<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, param);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, p1))
            {
                try
                {
                    return Task.FromResult(ReadAllRows<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QueryAsyncSlow1<T>(sql, p1);
    }

    private async Task<List<T>> QueryAsyncSlow1<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, p1, p2))
            {
                try
                {
                    return Task.FromResult(ReadAllRows<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QueryAsyncSlow2<T>(sql, p1, p2);
    }

    private async Task<List<T>> QueryAsyncSlow2<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public async Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2,
        (string Name, object? Value) p3)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2, p3);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public async Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2,
        (string Name, object? Value) p3,
        (string Name, object? Value) p4)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2, p3, p4);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public Task<List<T>> QueryAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, parameters))
            {
                try
                {
                    return Task.FromResult(ReadAllRows<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QueryAsyncSlowArr<T>(sql, parameters);
    }

    private async Task<List<T>> QueryAsyncSlowArr<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value)[] parameters)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, parameters);
        return await ExecuteReaderInternalAsync<T>(command);
    }

    public async Task<List<T>> QueryMappedAsync<T>(
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map)
    {
        using var rented = await RentReadConnectionAsync(sql);
        await using var command = BuildCommand(rented.Connection, sql, param);
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<T>();

        while (await reader.ReadAsync())
        {
            var values = new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index)
                    ? null
                    : reader.GetValue(index);
            }

            results.Add(map(values));
        }

        return results;
    }

    private static async Task<T?> ExecuteSingleInternalAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        SqliteCommand command)
    {
        await using (command)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return ReadSingleRow<T>(command);
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex) && attempt < MaxTransientRetries)
                {
                    await DelayBackoffAsync(attempt).ConfigureAwait(false);
                }
            }
        }
    }

    public Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param = null)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, param))
            {
                try
                {
                    return Task.FromResult(ReadSingleRow<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QuerySingleOrDefaultSlowAsync<T>(sql, param);
    }

    private async Task<T?> QuerySingleOrDefaultSlowAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        object? param)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, param);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    public Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, p1))
            {
                try
                {
                    return Task.FromResult(ReadSingleRow<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QuerySingleOrDefaultSlow1Async<T>(sql, p1);
    }

    private async Task<T?> QuerySingleOrDefaultSlow1Async<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    public Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2)
    {
        if (TryRentReadConnectionSync(sql, out var rented))
        {
            using (rented)
            using (var command = BuildCommand(rented.Connection, sql, p1, p2))
            {
                try
                {
                    return Task.FromResult(ReadSingleRow<T>(command));
                }
                catch (SqliteException ex) when (IsTransientSqliteError(ex))
                {
                }
            }
        }

        return QuerySingleOrDefaultSlow2Async<T>(sql, p1, p2);
    }

    private async Task<T?> QuerySingleOrDefaultSlow2Async<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2,
        (string Name, object? Value) p3)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2, p3);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        (string Name, object? Value) p1,
        (string Name, object? Value) p2,
        (string Name, object? Value) p3,
        (string Name, object? Value) p4)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, p1, p2, p3, p4);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var rented = await RentReadConnectionAsync(sql);
        var command = BuildCommand(rented.Connection, sql, parameters);
        return await ExecuteSingleInternalAsync<T>(command);
    }

    private Task<int> ExecuteCommandAsync(SqliteCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ambient = AmbientTransaction.Value;
        if (ambient is not null)
        {
            command.Transaction = ambient;

            try
            {
                using (command)
                    return Task.FromResult(ExecuteNonQueryWithRetry(command));
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
        }

        var observer = WriteTimingObserver;
        long gateStarted = observer is not null ? Stopwatch.GetTimestamp() : 0;

        // Fast-path: when uncontended and no writes are queued, execute immediately without TCS allocation
        if (_pendingWrites.IsEmpty && _writeGate.Wait(0))
        {
            if (_pendingWrites.IsEmpty)
            {
                try
                {
                    long executeStarted = observer is not null ? Stopwatch.GetTimestamp() : 0;
                    using (command)
                    {
                        command.Transaction = null;
                        var result = ExecuteNonQueryWithRetry(command);
                        if (observer is not null)
                        {
                            observer(new DbWriteTiming(
                                executeStarted - gateStarted,
                                Stopwatch.GetTimestamp() - executeStarted));
                        }
                        return Task.FromResult(result);
                    }
                }
                catch (Exception ex)
                {
                    return Task.FromException<int>(ex);
                }
                finally
                {
                    // If concurrent writers arrived while we executed, hand off to the queue drainer
                    if (!_pendingWrites.IsEmpty && Interlocked.CompareExchange(ref _drainerRunning, 1, 0) == 0)
                    {
                        _ = DrainWriteQueueHoldingGateAsync();
                    }
                    else
                    {
                        _writeGate.Release();
                    }
                }
            }

            _writeGate.Release();
        }

        // Contended path: enqueue for group-commit batching
        var pending = new PendingWrite(
            command,
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
            gateStarted);

        _pendingWrites.Enqueue(pending);

        if (Interlocked.CompareExchange(ref _drainerRunning, 1, 0) == 0)
        {
            _ = DrainWriteQueueAsync();
        }

        return pending.Completion.Task;
    }

    private async Task DrainWriteQueueAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        await DrainWriteQueueHoldingGateAsync().ConfigureAwait(false);
    }

    private Task DrainWriteQueueHoldingGateAsync()
    {
        try
        {
            while (true)
            {
                while (_pendingWrites.TryDequeue(out var first))
                {
                    ExecutePendingBatch(first);
                }

                Interlocked.Exchange(ref _drainerRunning, 0);

                if (_pendingWrites.IsEmpty)
                    break;

                if (Interlocked.CompareExchange(ref _drainerRunning, 1, 0) != 0)
                    break;
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return Task.CompletedTask;
    }

    private void ExecutePendingBatch(PendingWrite first)
    {
        var observer = WriteTimingObserver;

        // Single queued statement: execute directly without transaction wrapper overhead
        if (_pendingWrites.IsEmpty)
        {
            ExecuteSinglePendingWrite(first, observer);
            return;
        }

        // Multiple statements queued: coalesce up to MaxBatchSize into a single SQLite transaction
        _batchBuffer.Clear();
        _batchBuffer.Add(first);

        while (_batchBuffer.Count < MaxBatchSize && _pendingWrites.TryDequeue(out var next))
        {
            _batchBuffer.Add(next);
        }

        long batchStarted = observer is not null ? Stopwatch.GetTimestamp() : 0;
        var results = new int[_batchBuffer.Count];
        var batchSucceeded = false;

        try
        {
            using var transaction = _writeConnection.BeginTransaction();

            try
            {
                for (var i = 0; i < _batchBuffer.Count; i++)
                {
                    var cmd = _batchBuffer[i].Command;
                    cmd.Transaction = transaction;
                    results[i] = cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                batchSucceeded = true;
            }
            catch
            {
                // Roll back explicitly so the connection has no active
                // transaction when the fallback replays statements individually.
                transaction.Rollback();
            }
        }
        catch
        {
            // BeginTransaction/rollback failed; fall through to isolated execution.
        }

        if (batchSucceeded)
        {
            long batchFinished = observer is not null ? Stopwatch.GetTimestamp() : 0;
            long perItemExecTicks = observer is not null
                ? Math.Max(1, (batchFinished - batchStarted) / _batchBuffer.Count)
                : 0;

            for (var i = 0; i < _batchBuffer.Count; i++)
            {
                var item = _batchBuffer[i];
                item.Command.Dispose();

                if (observer is not null)
                {
                    observer(new DbWriteTiming(
                        batchStarted - item.GateStartedTimestamp,
                        perItemExecTicks));
                }

                item.Completion.TrySetResult(results[i]);
            }

            _batchBuffer.Clear();
            return;
        }

        // Fallback: replay each statement individually for exact error isolation
        for (var i = 0; i < _batchBuffer.Count; i++)
        {
            var item = _batchBuffer[i];
            item.Command.Transaction = null;
            ExecuteSinglePendingWrite(item, observer);
        }

        _batchBuffer.Clear();
    }

    private static void ExecuteSinglePendingWrite(
        PendingWrite item,
        Action<DbWriteTiming>? observer)
    {
        long executeStarted = observer is not null ? Stopwatch.GetTimestamp() : 0;
        try
        {
            using (item.Command)
            {
                item.Command.Transaction = null;
                var rows = ExecuteNonQueryWithRetry(item.Command);
                if (observer is not null)
                {
                    observer(new DbWriteTiming(
                        executeStarted - item.GateStartedTimestamp,
                        Stopwatch.GetTimestamp() - executeStarted));
                }
                item.Completion.TrySetResult(rows);
            }
        }
        catch (Exception ex)
        {
            item.Completion.TrySetException(ex);
        }
    }

    private static int ExecuteNonQueryWithRetry(SqliteCommand command)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (IsTransientSqliteError(ex) && attempt < MaxTransientRetries)
            {
                Thread.Sleep(ComputeBackoffMs(attempt));
            }
        }
    }

    private static Task DelayBackoffAsync(int attempt) =>
        Task.Delay(ComputeBackoffMs(attempt));

    private static int ComputeBackoffMs(int attempt)
    {
        var baseDelay = (1 << attempt) * 2; // 2ms, 4ms, 8ms
        var jitter = Random.Shared.Next(0, 3);
        return baseDelay + jitter;
    }

    public Task<int> ExecuteAsync(string sql, object? param = null)
    {
        var command = BuildCommand(_writeConnection, sql, param);
        return ExecuteCommandAsync(command);
    }

    public Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1)
    {
        var command = BuildCommand(_writeConnection, sql, p1);
        return ExecuteCommandAsync(command);
    }

    public Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2)
    {
        var command = BuildCommand(_writeConnection, sql, p1, p2);
        return ExecuteCommandAsync(command);
    }

    public Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3)
    {
        var command = BuildCommand(_writeConnection, sql, p1, p2, p3);
        return ExecuteCommandAsync(command);
    }

    public Task<int> ExecuteAsync(string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3, (string Name, object? Value) p4)
    {
        var command = BuildCommand(_writeConnection, sql, p1, p2, p3, p4);
        return ExecuteCommandAsync(command);
    }

    public Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = BuildCommand(_writeConnection, sql, parameters);
        return ExecuteCommandAsync(command);
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
            throw new NotSupportedException("Nested SQLite transactions are not supported.");

        await _writeGate.WaitAsync();
        SqliteTransaction? transaction = null;

        try
        {
            transaction = (SqliteTransaction)await _writeConnection.BeginTransactionAsync();
            AmbientTransaction.Value = transaction;
            var result = await action();
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch
                {
                    // Preserve the original failure.
                }
            }

            throw;
        }
        finally
        {
            AmbientTransaction.Value = null;
            transaction?.Dispose();
            _writeGate.Release();
        }
    }

    public async Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        var ambient = AmbientTransaction.Value;
        if (ambient is not null)
        {
            var ambientChanges = 0;

            foreach (var statement in statements)
            {
                await using var command = BuildCommand(
                    _writeConnection,
                    statement.Sql,
                    statement.Param);
                command.Transaction = ambient;
                ambientChanges += await command.ExecuteNonQueryAsync();
            }

            return ambientChanges;
        }

        await _writeGate.WaitAsync();

        try
        {
            await using var transaction = await _writeConnection.BeginTransactionAsync();
            var changes = 0;

            try
            {
                foreach (var statement in statements)
                {
                    await using var command = BuildCommand(
                        _writeConnection,
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
            _writeGate.Release();
        }
    }

    public Task<T?> GetAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        TableDef table,
        object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException(
                $"Table '{table.Name}' has no single primary key.");

        return QuerySingleOrDefaultAsync<T>(
            $"SELECT * FROM \"{table.Name}\" WHERE \"{primaryKey}\" = @id",
            ("id", id));
    }

    public Task<List<T>> ListAsync<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] T>(
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
            if (SqlPagination.SupportsNamedParams(param))
            {
                sql.Append(" LIMIT @__limit");

                if (offset > 0)
                    sql.Append(" OFFSET @__offset");

                param = SqlPagination.MergeNamed(param, limit, offset);
            }
            else
            {
                // Exotic parameter shapes keep the legacy literal paging.
                sql.Append($" LIMIT {limit}");

                if (offset > 0)
                    sql.Append($" OFFSET {offset}");
            }
        }

        return QueryAsync<T>(sql.ToString(), param);
    }

    public Task<int> InsertAsync(TableDef table, object values)
    {
        if (values is IReptarEntity entity)
        {
            var rawParams = entity.ToDbParameters();
            var cols = new List<string>(rawParams.Length);
            var pnames = new List<string>(rawParams.Length);
            var boundParams = new List<(string Name, object? Value)>(rawParams.Length);

            foreach (var column in table.Columns)
            {
                for (var i = 0; i < rawParams.Length; i++)
                {
                    if (string.Equals(rawParams[i].Name, column.ClrName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawParams[i].Name, column.SqlName, StringComparison.OrdinalIgnoreCase))
                    {
                        cols.Add($"\"{column.SqlName}\"");
                        pnames.Add("@" + column.ClrName);
                        boundParams.Add((column.ClrName, rawParams[i].Value));
                        break;
                    }
                }
            }

            var entitySql = $"INSERT INTO \"{table.Name}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", pnames)})";
            return ExecuteAsync(entitySql, boundParams.ToArray());
        }

        if (values is IDictionary dictionary)
        {
            var cols = new List<string>();
            var pnames = new List<string>();
            var paramDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "";
                var col = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.ClrName, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.SqlName, key, StringComparison.OrdinalIgnoreCase));

                var sqlName = col?.SqlName ?? key;
                cols.Add($"\"{sqlName}\"");
                pnames.Add("@" + key);
                paramDict[key] = entry.Value;
            }

            var sql = $"INSERT INTO \"{table.Name}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", pnames)})";
            return ExecuteAsync(sql, paramDict);
        }

        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        if (properties.Count == 0 && values is not null)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(values);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                        System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        System.Text.Json.JsonValueKind.True => 1L,
                        System.Text.Json.JsonValueKind.False => 0L,
                        System.Text.Json.JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
                return InsertAsync(table, dict);
            }
            catch
            {
                // Fall through to standard property mapping
            }
        }

        var columns = table.Columns
            .Where(column => properties.ContainsKey(column.ClrName))
            .ToArray();

        var sqlBuilder = new StringBuilder();
        sqlBuilder.Append($"INSERT INTO \"{table.Name}\" (");
        sqlBuilder.Append(string.Join(", ", columns.Select(column => $"\"{column.SqlName}\"")));
        sqlBuilder.Append(") VALUES (");
        sqlBuilder.Append(string.Join(", ", columns.Select(column => "@" + column.ClrName)));
        sqlBuilder.Append(')');

        return ExecuteAsync(sqlBuilder.ToString(), values);
    }

    public Task<int> InsertAsync<T>(TableDef table, T values) where T : class =>
        InsertAsync(table, (object)values);

    public Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        var primaryKey = table.SinglePrimaryKey
            ?? throw new InvalidOperationException(
                $"Table '{table.Name}' has no single primary key.");

        if (values is IReptarEntity entity)
        {
            var rawParams = entity.ToDbParameters();
            var sets = new List<string>(rawParams.Length);
            var boundParams = new List<(string Name, object? Value)>(rawParams.Length + 1);

            foreach (var column in table.Columns)
            {
                if (string.Equals(column.SqlName, primaryKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                for (var i = 0; i < rawParams.Length; i++)
                {
                    if (string.Equals(rawParams[i].Name, column.ClrName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawParams[i].Name, column.SqlName, StringComparison.OrdinalIgnoreCase))
                    {
                        sets.Add($"\"{column.SqlName}\" = @{column.ClrName}");
                        boundParams.Add((column.ClrName, rawParams[i].Value));
                        break;
                    }
                }
            }

            boundParams.Add(("__id", id));
            var entityUpdateSql = $"UPDATE \"{table.Name}\" SET {string.Join(", ", sets)} WHERE \"{primaryKey}\" = @__id";
            return ExecuteAsync(entityUpdateSql, boundParams.ToArray());
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

                sets.Add($"\"{sqlName}\" = @{key}");
                paramDict[key] = entry.Value;
            }

            paramDict["__id"] = id;
            var updateSql = $"UPDATE \"{table.Name}\" SET {string.Join(", ", sets)} WHERE \"{primaryKey}\" = @__id";
            return ExecuteAsync(updateSql, paramDict);
        }

        var properties = values.GetType()
            .GetProperties()
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        if (properties.Count == 0 && values is not null)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(values);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                        System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        System.Text.Json.JsonValueKind.True => 1L,
                        System.Text.Json.JsonValueKind.False => 0L,
                        System.Text.Json.JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
                return UpdateAsync(table, dict, id);
            }
            catch
            {
                // Fall through to standard property mapping
            }
        }

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
            ("id", id));
    }

    public Task<int> DeleteAsync<T>(TableDef table, object id) => DeleteAsync(table, id);

    public Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null) =>
        ExecuteAsync($"DELETE FROM \"{table.Name}\" WHERE {where}", param);

    public async Task<bool> ExistsAsync(TableDef table, string where, object? param = null)
    {
        var sql = $"SELECT EXISTS(SELECT 1 FROM \"{table.Name}\" WHERE {where})";
        using var rented = await RentReadConnectionAsync(sql);

        await using var command = BuildCommand(
            rented.Connection,
            sql,
            param);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    public async Task EnsureCreatedAsync(params TableDef[] tables)
    {
        await _writeGate.WaitAsync();

        try
        {
            foreach (var table in tables)
            {
                await using (var command = _writeConnection.CreateCommand())
                {
                    command.CommandText = SchemaDdl.CreateTable(table) + ";";
                    await command.ExecuteNonQueryAsync();
                }

                foreach (var index in SchemaDdl.CreateIndexes(table))
                {
                    await using var command = _writeConnection.CreateCommand();
                    command.CommandText = index + ";";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _writeGate.Wait();
        try
        {
            lock (_allConnections)
            {
                foreach (var connection in _allConnections)
                {
                    try { connection.Dispose(); } catch { }
                }
                _allConnections.Clear();
            }
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
            _readSemaphore.Dispose();
        }
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, (string Name, object? Value) p1)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;
        var n = p1.Name.StartsWith('@') ? p1.Name : "@" + p1.Name;
        command.Parameters.AddWithValue(n, p1.Value ?? DBNull.Value);
        return command;
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, (string Name, object? Value) p1, (string Name, object? Value) p2)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;
        var n1 = p1.Name.StartsWith('@') ? p1.Name : "@" + p1.Name;
        var n2 = p2.Name.StartsWith('@') ? p2.Name : "@" + p2.Name;
        command.Parameters.AddWithValue(n1, p1.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n2, p2.Value ?? DBNull.Value);
        return command;
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;
        var n1 = p1.Name.StartsWith('@') ? p1.Name : "@" + p1.Name;
        var n2 = p2.Name.StartsWith('@') ? p2.Name : "@" + p2.Name;
        var n3 = p3.Name.StartsWith('@') ? p3.Name : "@" + p3.Name;
        command.Parameters.AddWithValue(n1, p1.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n2, p2.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n3, p3.Value ?? DBNull.Value);
        return command;
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, (string Name, object? Value) p1, (string Name, object? Value) p2, (string Name, object? Value) p3, (string Name, object? Value) p4)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;
        var n1 = p1.Name.StartsWith('@') ? p1.Name : "@" + p1.Name;
        var n2 = p2.Name.StartsWith('@') ? p2.Name : "@" + p2.Name;
        var n3 = p3.Name.StartsWith('@') ? p3.Name : "@" + p3.Name;
        var n4 = p4.Name.StartsWith('@') ? p4.Name : "@" + p4.Name;
        command.Parameters.AddWithValue(n1, p1.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n2, p2.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n3, p3.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(n4, p4.Value ?? DBNull.Value);
        return command;
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;
        for (var i = 0; i < parameters.Length; i++)
        {
            var n = parameters[i].Name.StartsWith('@') ? parameters[i].Name : "@" + parameters[i].Name;
            command.Parameters.AddWithValue(n, parameters[i].Value ?? DBNull.Value);
        }
        return command;
    }

    private static SqliteCommand BuildCommand(SqliteConnection connection, string sql, object? param)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = AmbientTransaction.Value;

        if (param is null)
            return command;

        if (param is IReptarEntity entity)
        {
            var entityParams = entity.ToDbParameters();
            for (var i = 0; i < entityParams.Length; i++)
            {
                var n = entityParams[i].Name.StartsWith('@') ? entityParams[i].Name : "@" + entityParams[i].Name;
                command.Parameters.AddWithValue(n, entityParams[i].Value ?? DBNull.Value);
            }

            return command;
        }

        if (param is System.Runtime.CompilerServices.ITuple singleTuple && singleTuple.Length == 2)
        {
            var name = singleTuple[0]?.ToString() ?? "";
            var n = name.StartsWith('@') ? name : "@" + name;
            command.Parameters.AddWithValue(n, singleTuple[1] ?? DBNull.Value);
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
                    var name = t[0]?.ToString() ?? "";
                    var n = name.StartsWith('@') ? name : "@" + name;
                    command.Parameters.AddWithValue(n, t[1] ?? DBNull.Value);
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
                var key = entry.Key?.ToString() ?? "";
                var n = key.StartsWith('@') ? key : "@" + key;
                command.Parameters.AddWithValue(
                    n,
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
