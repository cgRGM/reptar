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
        // object metadata serializes polymorphically and asks the context for
        // the runtime type; resolve value.GetType() in Serialize instead.
        if (type == typeof(object))
            return null;

        for (var i = 0; i < _contexts.Length; i++)
        {
            var info = _contexts[i].GetTypeInfo(type);
            if (info is not null && info.Type == type)
                return info;
        }

        foreach (var context in ReptarJson.Contexts)
        {
            var info = context.GetTypeInfo(type);
            if (info is not null && info.Type == type)
                return info;
        }

        var internalInfo = ReptarInternalJsonContext.Default.GetTypeInfo(type);
        return internalInfo is not null && internalInfo.Type == type
            ? internalInfo
            : null;
    }

    private JsonTypeInfo ResolveForValue<T>(T value) =>
        ResolveOrNull(value?.GetType() ?? typeof(T))
        ?? Resolve(typeof(T));

    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.Serialize(value, ResolveForValue(value));

    public byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, ResolveForValue(value));

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
// Built-in auth response shapes. Registering them here means any app that
// opts into SourceGenJsonCodec gets AOT-safe auth routes for free.
[JsonSerializable(typeof(SessionUser))]
[JsonSerializable(typeof(SessionResponse))]
#if !NETWASM
// The Wasm target links its own auth slice (Reptar.Wasm/Auth), so these
// native-only response types are not part of that compilation.
[JsonSerializable(typeof(Auth.SignInInput))]
[JsonSerializable(typeof(Auth.SignUpInput))]
[JsonSerializable(typeof(Auth.BuiltInSocialInput))]
[JsonSerializable(typeof(Auth.RevokeSessionInput))]
[JsonSerializable(typeof(Auth.ChangePasswordInput))]
[JsonSerializable(typeof(Auth.CreateApiKeyInput))]
[JsonSerializable(typeof(Auth.SessionInfo))]
[JsonSerializable(typeof(List<Auth.SessionInfo>))]
[JsonSerializable(typeof(Auth.ApiKeyInfo))]
[JsonSerializable(typeof(List<Auth.ApiKeyInfo>))]
[JsonSerializable(typeof(Auth.ApiKeyResponse))]
[JsonSerializable(typeof(Auth.AuthErrorResponse))]
[JsonSerializable(typeof(Auth.AuthUrlResponse))]
[JsonSerializable(typeof(Auth.AuthSuccessResponse))]
[JsonSerializable(typeof(BetterAuthSignInInput))]
[JsonSerializable(typeof(BetterAuthSignUpInput))]
[JsonSerializable(typeof(BetterAuthSocialInput))]
[JsonSerializable(typeof(BetterAuthUser))]
[JsonSerializable(typeof(BetterAuthSession))]
[JsonSerializable(typeof(BetterAuthSessionResponse))]
[JsonSerializable(typeof(BetterAuthSignInResponse))]
[JsonSerializable(typeof(BetterAuthSocialResponse))]
[JsonSerializable(typeof(BetterAuthSessionQueryResult))]
#endif
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class ReptarInternalJsonContext : JsonSerializerContext;

public sealed class DefaultJsonCodec : IJsonCodec
{
    public string Serialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        // Resolve the runtime type when T is object (or a base type): object
        // metadata would ask the resolver for the concrete type anyway, but
        // with the wrong resolver on AOT/NetWasm.
        var info = ReptarJson.FindTypeInfo(value?.GetType() ?? typeof(T))
            ?? ReptarJson.FindTypeInfo(typeof(T));
        if (info is null)
            ReptarJson.EnsureReflectionJsonAllowed(value?.GetType() ?? typeof(T));
        return info is not null
            ? JsonSerializer.Serialize(value, info)
            : JsonSerializer.Serialize(value, ReptarJson.Options);
    }

    public byte[] SerializeUtf8<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(T value)
    {
        var info = ReptarJson.FindTypeInfo(value?.GetType() ?? typeof(T))
            ?? ReptarJson.FindTypeInfo(typeof(T));
        if (info is null)
            ReptarJson.EnsureReflectionJsonAllowed(value?.GetType() ?? typeof(T));
        return info is not null
            ? JsonSerializer.SerializeToUtf8Bytes(value, info)
            : JsonSerializer.SerializeToUtf8Bytes(value, ReptarJson.Options);
    }

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string json)
    {
        var info = ReptarJson.FindTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        if (info is null)
            ReptarJson.EnsureReflectionJsonAllowed(typeof(T));
        return info is not null
            ? JsonSerializer.Deserialize<T>(json, info)
            : JsonSerializer.Deserialize<T>(json, ReptarJson.Options);
    }

    public T? Deserialize<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(ReadOnlySpan<byte> utf8Json)
    {
        var info = ReptarJson.FindTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        if (info is null)
            ReptarJson.EnsureReflectionJsonAllowed(typeof(T));
        return info is not null
            ? JsonSerializer.Deserialize<T>(utf8Json, info)
            : JsonSerializer.Deserialize<T>(utf8Json, ReptarJson.Options);
    }
}
