using System.Diagnostics;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Workers;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient _cloudClient;
    private int _consecutiveFailures;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AgentOptions> options,
        SuavoCloudClient cloudClient)
    {
        _logger = logger;
        _options = options.Value;
        _cloudClient = cloudClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat worker started. Interval: {Interval}s", _options.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = new
                {
                    agentId = _options.AgentId,
                    version = _options.Version,
                    pharmacyId = _options.PharmacyId,
                    memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
                    uptime = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                    status = "online"
                };

                await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                _consecutiveFailures = 0;
                _logger.LogDebug("Heartbeat OK");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogWarning(ex, "Heartbeat failed ({Failures} consecutive)", _consecutiveFailures);
            }

            var jitter = Random.Shared.Next(0, _options.HeartbeatJitterSeconds * 1000);
            var delay = TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds) + TimeSpan.FromMilliseconds(jitter);

            if (_consecutiveFailures > 0)
            {
                var backoff = Math.Min(_consecutiveFailures * _options.HeartbeatIntervalSeconds, 300);
                delay = TimeSpan.FromSeconds(backoff) + TimeSpan.FromMilliseconds(jitter);
            }

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("Heartbeat worker stopped");
    }
}
