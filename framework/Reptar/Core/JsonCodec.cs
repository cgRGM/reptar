using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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

public sealed class SourceGenJsonCodec : IJsonCodec
{
    private readonly JsonSerializerContext[] _contexts;

    public SourceGenJsonCodec(params JsonSerializerContext[] contexts)
    {
        _contexts = contexts;
    }

    private JsonTypeInfo Resolve(Type type) =>
        ResolveOrNull(type)
        ?? throw new InvalidOperationException($"No JSON metadata registered in SourceGenJsonCodec for {type}.");

    private JsonTypeInfo? ResolveOrNull(Type type)
    {
        for (var i = 0; i < _contexts.Length; i++)
        {
            var info = _contexts[i].GetTypeInfo(type);
            if (info is not null)
                return info;
        }
        return ReptarInternalJsonContext.Default.GetTypeInfo(type);
    }

    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.Serialize(value, Resolve(typeof(T)));

    public byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Resolve(typeof(T)));

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json) =>
        (T?)JsonSerializer.Deserialize(json, Resolve(typeof(T)));

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> utf8Json) =>
        (T?)JsonSerializer.Deserialize(utf8Json, Resolve(typeof(T)));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(ValidationIssue))]
[JsonSerializable(typeof(ValidationError))]
public sealed partial class ReptarInternalJsonContext : JsonSerializerContext;

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
