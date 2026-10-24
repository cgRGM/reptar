using System.Data;

namespace Reptar;

public interface IDataRecordMapper<out T>
{
    static abstract T Map(IDataRecord record);
}

public static class EntityMapper<T>
{
    public static Func<IDataRecord, Func<IDataRecord, T>>? Factory;
}
