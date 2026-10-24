using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class WasmEnvironment : IEnvironment
{
    [JSImport("envGet", "app.host")]
    private static extern string? GetJs(string name);

    public string? Get(string name) => GetJs(name);
}
