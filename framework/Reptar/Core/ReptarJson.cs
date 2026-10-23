using System.Text.Json;
#if !NETWASM
using System.Text.Json.Serialization.Metadata;
#endif

namespace Reptar;

public static class ReptarJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

#if !NETWASM
    static ReptarJson()
    {
        // File-based apps and AOT disable reflection-based JSON by default.
        // The net10.0 dev host opts back in; the Wasm target installs a
        // source-generated resolver at startup instead.
        Options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
    }
#endif
}
