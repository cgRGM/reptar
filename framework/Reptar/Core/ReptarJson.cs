using System.Text.Json;
using System.Text.Json.Serialization;
#if !NETWASM
using System.Text.Json.Serialization.Metadata;
#endif

namespace Reptar;

public static class ReptarJson
{
    private static readonly object ContextsLock = new();
    private static readonly List<JsonSerializerContext> RegisteredContexts = [];

    public static JsonSerializerOptions Options { get; private set; } =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Source-generated JSON contexts registered by generated app code. This is
    /// how <c>SourceGenJsonCodec</c> and the default reflection fallback find
    /// app DTO metadata without a hand-written context list.
    /// </summary>
    public static IReadOnlyList<JsonSerializerContext> Contexts
    {
        get
        {
            lock (ContextsLock)
                return RegisteredContexts.ToArray();
        }
    }

    /// <summary>
    /// Resolves generated metadata for a type without relying on a resolver
    /// attached to <see cref="Options"/>. The framework's internal context is
    /// consulted first, then app-registered contexts. Serializing with the
    /// returned <c>JsonTypeInfo</c> avoids the "JsonSerializerOptions setting
    /// does not match" error when a context uses its own options instance.
    /// </summary>
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo? FindTypeInfo(
        Type type)
    {
        // `object` metadata resolves polymorphically and fails when the
        // runtime type is not registered. Callers with an object-typed value
        // should resolve value.GetType() instead.
        if (type == typeof(object))
            return null;

        // Framework envelopes (ErrorBody, ValidationError, built-in auth
        // shapes) are always available, even when the app's context does not
        // list them explicitly.
        var internalInfo = ReptarInternalJsonContext.Default.GetTypeInfo(type);
        if (internalInfo is not null && internalInfo.Type == type)
            return internalInfo;

        foreach (var context in Contexts)
        {
            var info = context.GetTypeInfo(type);
            if (info is not null && info.Type == type)
                return info;
        }

        return null;
    }

    /// <summary>
    /// Deserializes with the app's generated metadata when available. Prefer
    /// this over passing <see cref="Options"/> directly in AOT/NetWasm code:
    /// the metadata is bound to the context's options instance, and resolving
    /// it explicitly avoids resolver/options mismatch and reflection fallback.
    /// </summary>
    public static T? Deserialize<T>(string json)
    {
        var info = FindTypeInfo(typeof(T)) as
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>;
        return info is not null
            ? JsonSerializer.Deserialize(json, info)
            : JsonSerializer.Deserialize<T>(json, Options);
    }

    public static void RegisterContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (ContextsLock)
        {
            if (!RegisteredContexts.Contains(context))
                RegisteredContexts.Add(context);
        }
    }

    /// <summary>
    /// Installs a resolver over all registered contexts. Call this after
    /// registering app/context-generated contexts on hosts that do not use
    /// SourceGenJsonCodec directly (for example the Workers Program.cs).
    /// </summary>
    public static void UseRegisteredContexts()
    {
#if NETWASM
        // A source-generated context's JsonTypeInfo is bound to the exact
        // JsonSerializerOptions instance that created it. Returning that info
        // from a resolver attached to a different instance throws
        // "JsonTypeInfo instance whose JsonSerializerOptions setting does not
        // match" on first use. Adopt the app context's options (including its
        // camelCase naming policy) as the framework-wide instance.
        var registered = Contexts;
        if (registered.Count > 0)
        {
            Options = registered[0].Options;
            return;
        }

        Options.TypeInfoResolver = new RegisteredContextResolver();
#else
        Options.TypeInfoResolver = new GeneratedFirstResolver();
#endif
    }

#if !NETWASM
    static ReptarJson()
    {
        // File-based apps and AOT disable reflection-based JSON by default.
        // Generated contexts are consulted first; the reflection resolver is
        // the dev-host fallback for types that have no generated metadata.
        Options.TypeInfoResolver = new GeneratedFirstResolver();
    }

    private sealed class GeneratedFirstResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver _fallback = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            foreach (var context in Contexts)
            {
                var info = context.GetTypeInfo(type);
                if (info is not null && info.Type == type)
                    return info;
            }

            return _fallback.GetTypeInfo(type, options);
        }
    }
#endif

#if NETWASM
    private sealed class RegisteredContextResolver :
        System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver
    {
        public System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(
            Type type,
            JsonSerializerOptions options)
        {
            foreach (var context in Contexts)
            {
                var info = context.GetTypeInfo(type);
                if (info is not null && info.Type == type)
                    return info;
            }

            return null;
        }
    }
#endif
}
