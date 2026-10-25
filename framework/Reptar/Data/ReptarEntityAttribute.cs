namespace Reptar;

public interface IReptarEntity
{
    (string Name, object? Value)[] ToDbParameters();
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class ReptarEntityAttribute : Attribute
{
    public string? TableName { get; }

    public ReptarEntityAttribute(string? tableName = null)
    {
        TableName = tableName;
    }
}
