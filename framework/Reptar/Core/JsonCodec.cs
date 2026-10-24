using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Reptar;

public interface IJsonCodec
{
    string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value);

    T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json);
}

public sealed class DefaultJsonCodec : IJsonCodec
{
    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.Serialize(value, ReptarJson.Options);

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ReptarJson.Options);
}
