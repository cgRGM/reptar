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

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

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
                if (info is not null)
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
                if (info is not null)
                    return info;
            }

            return null;
        }
    }
#endif
}
