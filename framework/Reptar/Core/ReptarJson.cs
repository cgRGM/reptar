using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Reptar;

public static class ReptarJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // File-based apps and AOT disable reflection-based JSON by default.
        // The net10.0 dev host opts back in; the Wasm target will swap this for
        // source-generated metadata.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
