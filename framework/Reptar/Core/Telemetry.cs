using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Reptar;

/// <summary>
/// One structured request telemetry record. Kept as a readonly struct so the
/// telemetry middleware can build it without allocating a dictionary.
/// </summary>
public readonly record struct RequestTelemetry(
    string RequestId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    string Method,
    string Route,
    string Path,
    int Status,
    double DurationMs,
    double DbMs,
    int DbQueries,
    string? Cache,
    string? UserId,
    string? CfRay,
    string? Colo,
    string? ErrorCode,
    bool Slow)
{
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("request_id", RequestId);

            if (!string.IsNullOrEmpty(TraceId))
                writer.WriteString("trace_id", TraceId);

            if (!string.IsNullOrEmpty(SpanId))
                writer.WriteString("span_id", SpanId);

            if (!string.IsNullOrEmpty(ParentSpanId))
                writer.WriteString("parent_span_id", ParentSpanId);

            writer.WriteString("method", Method);
            writer.WriteString("route", Route);
            writer.WriteString("path", Path);
            writer.WriteNumber("status", Status);
            writer.WriteNumber("duration_ms", Math.Round(DurationMs, 3));
            writer.WriteNumber("db_ms", Math.Round(DbMs, 3));
            writer.WriteNumber("db_queries", DbQueries);

            if (!string.IsNullOrEmpty(Cache))
                writer.WriteString("cache", Cache);

            if (!string.IsNullOrEmpty(UserId))
                writer.WriteString("user_id", UserId);

            if (!string.IsNullOrEmpty(CfRay))
                writer.WriteString("cf_ray", CfRay);

            if (!string.IsNullOrEmpty(Colo))
                writer.WriteString("colo", Colo);

            if (!string.IsNullOrEmpty(ErrorCode))
                writer.WriteString("error_code", ErrorCode);

            if (Slow)
                writer.WriteBoolean("slow", true);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// Parsed W3C trace context. The incoming <c>traceparent</c> supplies the
/// trace id and parent span; the framework generates the local span id.
/// </summary>
public readonly record struct TraceParent(
    string TraceId,
    string SpanId,
    bool Sampled)
{
    public static TraceParent? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split('-');

        // version-traceid-spanid-flags; version ff is invalid, version 00 is
        // the only version we need to understand for now.
        if (parts.Length != 4 ||
            parts[0] != "00" ||
            !IsHex(parts[1], 32) ||
            !IsHex(parts[2], 16) ||
            !IsHex(parts[3], 2))
            return null;

        if (IsAllZero(parts[1]) || IsAllZero(parts[2]))
            return null;

        var flags = Convert.ToByte(parts[3], 16);
        return new TraceParent(parts[1], parts[2], (flags & 1) == 1);
    }

    private static bool IsHex(string value, int length)
    {
        if (value.Length != length)
            return false;

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') ||
                        (c >= 'a' && c <= 'f') ||
                        (c >= 'A' && c <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var c in value)
        {
            if (c != '0')
                return false;
        }

        return true;
    }
}

/// <summary>
/// Ambient trace context for the current request. Used by outbound HTTP and
/// other integrations that need to propagate <c>traceparent</c>.
/// </summary>
public readonly record struct TraceContext(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    bool Sampled)
{
    private static readonly AsyncLocal<TraceContext?> CurrentSlot = new();

    public static TraceContext? Current => CurrentSlot.Value;

    public static IDisposable Begin(TraceContext context)
    {
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = context;
        return new Scope(previous);
    }

    public string ToTraceParent() =>
        $"00-{TraceId}-{SpanId}-{(Sampled ? "01" : "00")}";

    public static string NewTraceId() => RandomHex(16);

    public static string NewSpanId() => RandomHex(8);

    private static string RandomHex(int bytes)
    {
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes);
        const string alphabet = "0123456789abcdef";
        var chars = new char[data.Length * 2];

        for (var i = 0; i < data.Length; i++)
        {
            chars[i * 2] = alphabet[data[i] >> 4];
            chars[(i * 2) + 1] = alphabet[data[i] & 0x0F];
        }

        return new string(chars);
    }

    private sealed class Scope(TraceContext? previous) : IDisposable
    {
        public void Dispose() => CurrentSlot.Value = previous;
    }
}

/// <summary>
/// Per-request database timing collected by <see cref="InstrumentedDbClient"/>.
/// The current instance flows with the async request via AsyncLocal; when no
/// telemetry scope is active, the DB decorator does not start a stopwatch.
/// </summary>
public sealed class DbMetrics
{
    private static readonly AsyncLocal<DbMetrics?> CurrentSlot = new();

    public static DbMetrics? Current => CurrentSlot.Value;

    /// <summary>Queries slower than this are counted as slow queries.</summary>
    public static int SlowQueryThresholdMs { get; set; } = 100;

    private long _queries;
    private long _ticks;
    private long _slowQueries;
    private long _slowestTicks;
    private string? _slowestSql;

    public int QueryCount => (int)Interlocked.Read(ref _queries);
    public double TotalMs => TicksToMs(Interlocked.Read(ref _ticks));
    public int SlowQueryCount => (int)Interlocked.Read(ref _slowQueries);
    public double SlowestQueryMs => TicksToMs(Interlocked.Read(ref _slowestTicks));
    public string? SlowestSql => _slowestSql;

    public void Record(string sql, long elapsedTicks, string? error = null)
    {
        Interlocked.Increment(ref _queries);
        Interlocked.Add(ref _ticks, elapsedTicks);

        var elapsedMs = TicksToMs(elapsedTicks);
        if (elapsedMs >= SlowQueryThresholdMs)
            Interlocked.Increment(ref _slowQueries);

        if (elapsedTicks > Interlocked.Read(ref _slowestTicks))
        {
            Interlocked.Exchange(ref _slowestTicks, elapsedTicks);
            _slowestSql = error is null
                ? Truncate(sql, 240)
                : $"{Truncate(sql, 200)} -- {Truncate(error, 120)}";
        }
    }

    public static IDisposable Begin(DbMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = metrics;
        return new Scope(previous);
    }

    private static double TicksToMs(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed class Scope(DbMetrics? previous) : IDisposable
    {
        public void Dispose() => CurrentSlot.Value = previous;
    }
}

/// <summary>
/// Decorates an <see cref="IDbClient"/> with optional per-request timing. The
/// fast path is a single AsyncLocal read; when telemetry is off, no stopwatch
/// is started and no query text is retained.
/// </summary>
public sealed class InstrumentedDbClient(IDbClient inner) : IDbClient, IDisposable, IAsyncDisposable
{
    public IDbClient Inner { get; } = inner;

    public void Dispose()
    {
        if (Inner is IDisposable disposable)
            disposable.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Inner is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.QueryAsync<T>(sql, param);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.QueryAsync<T>(sql, param);
            metrics.Record(sql, Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record(sql, Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.QuerySingleOrDefaultAsync<T>(sql, param);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.QuerySingleOrDefaultAsync<T>(sql, param);
            metrics.Record(sql, Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record(sql, Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.ExecuteAsync(sql, param);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.ExecuteAsync(sql, param);
            metrics.Record(sql, Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record(sql, Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<T?> GetAsync<T>(TableDef table, object id)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.GetAsync<T>(table, id);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.GetAsync<T>(table, id);
            metrics.Record($"get {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"get {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<List<T>> ListAsync<T>(
        TableDef table,
        string? clause = null,
        object? param = null,
        int limit = 0,
        int offset = 0)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.ListAsync<T>(table, clause, param, limit, offset);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.ListAsync<T>(table, clause, param, limit, offset);
            metrics.Record($"list {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"list {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> InsertAsync(TableDef table, object values)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.InsertAsync(table, values);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.InsertAsync(table, values);
            metrics.Record($"insert {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"insert {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> InsertAsync<T>(TableDef table, T values) where T : class
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.InsertAsync(table, values);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.InsertAsync(table, values);
            metrics.Record($"insert {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"insert {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> UpdateAsync(TableDef table, object values, object id)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.UpdateAsync(table, values, id);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.UpdateAsync(table, values, id);
            metrics.Record($"update {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"update {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> UpdateAsync<T>(TableDef table, T values, object id) where T : class
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.UpdateAsync(table, values, id);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.UpdateAsync(table, values, id);
            metrics.Record($"update {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"update {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> DeleteAsync(TableDef table, object id)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.DeleteAsync(table, id);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.DeleteAsync(table, id);
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> DeleteAsync<T>(TableDef table, object id)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.DeleteAsync<T>(table, id);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.DeleteAsync<T>(table, id);
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> DeleteWhereAsync(TableDef table, string where, object? param = null)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.DeleteWhereAsync(table, where, param);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.DeleteWhereAsync(table, where, param);
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"delete {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(TableDef table, string where, object? param = null)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.ExistsAsync(table, where, param);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.ExistsAsync(table, where, param);
            metrics.Record($"exists {table.Name}", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"exists {table.Name}", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<List<T>> QueryMappedAsync<T>(
        string sql,
        object? param,
        Func<IReadOnlyDictionary<string, object?>, T> map)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.QueryMappedAsync(sql, param, map);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.QueryMappedAsync(sql, param, map);
            metrics.Record(sql, Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record(sql, Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task EnsureCreatedAsync(params TableDef[] tables)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
        {
            await Inner.EnsureCreatedAsync(tables);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await Inner.EnsureCreatedAsync(tables);
            metrics.Record("ensure-created", Stopwatch.GetTimestamp() - started);
        }
        catch (Exception ex)
        {
            metrics.Record("ensure-created", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public async Task<int> BatchAsync(IReadOnlyList<DbStatement> statements)
    {
        var metrics = DbMetrics.Current;
        if (metrics is null)
            return await Inner.BatchAsync(statements);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Inner.BatchAsync(statements);
            metrics.Record($"batch ({statements.Count} statements)", Stopwatch.GetTimestamp() - started);
            return result;
        }
        catch (Exception ex)
        {
            metrics.Record($"batch ({statements.Count} statements)", Stopwatch.GetTimestamp() - started, ex.Message);
            throw;
        }
    }

    public Task TransactionAsync(Func<IDbClient, Task> action) =>
        Inner.TransactionAsync(action);

    public Task<T> TransactionAsync<T>(Func<IDbClient, Task<T>> action) =>
        Inner.TransactionAsync(action);
}
