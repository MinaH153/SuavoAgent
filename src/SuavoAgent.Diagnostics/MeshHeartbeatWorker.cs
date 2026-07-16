using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Diagnostics;

/// BackgroundService that fires Wire.Heartbeat every 5 minutes. Registered
/// in each long-running entry point's host builder per spec §4 self-
/// heartbeat contract. Phase A A1 silent-agent alarm pages Joshua if
/// Queen's heartbeat absent &gt;30 minutes — the mesh's liveness is a
/// continuously-verified claim, not a hopeful assumption.
public sealed class MeshHeartbeatWorker : BackgroundService
{
    private readonly WireComponent _component;
    private readonly TimeSpan _interval;
    private readonly ILogger<MeshHeartbeatWorker>? _logger;

    public MeshHeartbeatWorker(WireComponent component, TimeSpan interval,
        ILogger<MeshHeartbeatWorker>? logger = null)
    {
        _component = component;
        _interval = interval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("MeshHeartbeatWorker starting for {Component} every {Interval}",
            _component, _interval);

        try
        {
            // Fire immediately so Phase A A1 alarm sees a heartbeat within
            // 5min of process start, not 10min. Wrapped in TryHeartbeat so a
            // first-call exception (e.g., Wire.AttachUnhandledHooks silently
            // failed → uninitialized statics) does NOT fault ExecuteAsync
            // and stop the host via BackgroundServiceExceptionBehavior.StopHost
            // (.NET 8 default) — Codex chunk 4a HIGH.
            TryHeartbeat();

            using var timer = new PeriodicTimer(_interval);
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                TryHeartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // expected on host shutdown
        }
    }

    private void TryHeartbeat()
    {
        try
        {
            Wire.Heartbeat(_component);
        }
        catch (Exception ex)
        {
            // Heartbeat must never crash the host. Log + continue.
            _logger?.LogWarning(
                "diagnostics.mesh_heartbeat_failed exception_type={ExceptionType}",
                ex.GetType().Name);
        }
    }
}
