using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Reptar;

public interface IJsonCodec
{
    string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value);

    byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        Encoding.UTF8.GetBytes(Serialize(value));

    T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json);

    T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> utf8Json) =>
        Deserialize<T>(Encoding.UTF8.GetString(utf8Json));
}

public sealed class DefaultJsonCodec : IJsonCodec
{
    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.Serialize(value, ReptarJson.Options);

    public byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, ReptarJson.Options);

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ReptarJson.Options);

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, ReptarJson.Options);
}
