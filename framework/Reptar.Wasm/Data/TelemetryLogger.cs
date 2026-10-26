using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

/// <summary>
/// Sends structured request telemetry to the Worker host. The host owns
/// enrichment (cf-ray/colo/wasm timing) and export; the Wasm guest only
/// produces one JSON record per sampled request.
/// </summary>
public sealed class WasmAppLogger : IAppLogger
{
    [JSImport("telemetry", "app.host")]
    private static extern void Telemetry(string json);

    public void Info(string message) => Console.WriteLine(message);

    public void Warn(string message) => Console.Error.WriteLine($"WARN {message}");

    public void Error(string message) => Console.Error.WriteLine(message);

    public void Request(in RequestTelemetry telemetry) =>
        Telemetry(telemetry.ToJson());
}
