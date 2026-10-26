namespace Reptar;

/// <summary>
/// Optional application module lifecycle. Configure is synchronous and runs
/// immediately; StartAsync/StopAsync are invoked by hosts that have a real
/// lifecycle (Kestrel, Lambda). Workers only get Configure/StartAsync because
/// the isolate has no graceful shutdown.
/// </summary>
public interface IReptarModule
{
    void Configure(ReptarApp app) { }

    Task StartAsync(ReptarApp app, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task StopAsync(ReptarApp app, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
