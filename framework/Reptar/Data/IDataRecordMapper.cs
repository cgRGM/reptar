using System.Collections.Concurrent;
using System.Data;

namespace Reptar;

public interface IDataRecordMapper<out T>
{
    static abstract T Map(IDataRecord record);
}

public static class EntityMapper<T>
{
    public static Func<IDataRecord, Func<IDataRecord, T>>? Factory;
    public static Func<IDataRecord, T>? DirectMap;

    private static readonly ConcurrentDictionary<string, Func<IDataRecord, T>> SqlCache =
        new(StringComparer.Ordinal);

    public static Func<IDataRecord, T>? ResolveForSql(string sql, IDataRecord record)
    {
        if (Factory is not { } factory)
            return null;

        if (SqlCache.TryGetValue(sql, out var cached))
            return cached;

        var resolved = factory(record);
        SqlCache.TryAdd(sql, resolved);
        return resolved;
    }
}
