using Reptar;
using System.Text.Json.Serialization;

public sealed record LifecycleJob(string Id);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LifecycleJob))]
internal partial class LifecycleJson : JsonSerializerContext;

internal sealed class LifecycleModule(string root) : IReptarModule
{
    public Task StopAsync(ReptarApp app, CancellationToken cancellationToken)
    {
        var handlerStopped = File.Exists(Path.Combine(root, "handler-stopped"));
        var cronStarted = File.Exists(Path.Combine(root, "cron-started"));
        var cronStopped = File.Exists(Path.Combine(root, "cron-stopped"));
        File.WriteAllText(Path.Combine(root, "module-stop"),
            handlerStopped && (!cronStarted || cronStopped) ? "workers-stopped" : "disposed-before-workers");
        return Task.CompletedTask;
    }
}
