using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Workers;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private readonly IServiceProvider _serviceProvider;
    private int _consecutiveFailures;
    private bool _updateInProgress;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AgentOptions> options,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cloudClient == null)
        {
            _logger.LogWarning("Heartbeat disabled — no cloud client configured");
            return;
        }

        // Cleanup old binary from a previous self-update
        SelfUpdater.CleanupOldBinary(_logger);

        _logger.LogInformation("Heartbeat worker started. Interval: {Interval}s", _options.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Read Rx detection state if available
                var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
                var sqlConnected = rxWorker?.IsSqlConnected ?? false;
                var rxReadyCount = rxWorker?.LastDetectedCount ?? 0;

                var payload = new
                {
                    agentId = _options.AgentId,
                    version = _options.Version,
                    pharmacyId = _options.PharmacyId,
                    memoryUsageMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
                    uptime = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                    status = "online",
                    sqlConnected,
                    rxReadyCount,
                    pioneerrxStatus = sqlConnected ? "connected" : "not_connected"
                };

                var response = await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                _consecutiveFailures = 0;
                _logger.LogDebug("Heartbeat OK");

                // Check for pending self-update
                if (response.HasValue && !_updateInProgress)
                    await CheckForUpdateAsync(response.Value, stoppingToken);
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

    private async Task CheckForUpdateAsync(JsonElement response, CancellationToken ct)
    {
        try
        {
            if (!response.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("pendingUpdate", out var update)) return;
            if (update.ValueKind == JsonValueKind.Null) return;

            var url = update.TryGetProperty("url", out var u) ? u.GetString() : null;
            var sha256 = update.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            var version = update.TryGetProperty("version", out var v) ? v.GetString() : null;
            var signature = update.TryGetProperty("signature", out var sig) ? sig.GetString() : null;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha256))
            {
                _logger.LogDebug("Pending update missing url or sha256 — skipping");
                return;
            }

            if (string.IsNullOrEmpty(signature))
            {
                _logger.LogWarning("Pending update has no Ed25519 signature — rejecting");
                return;
            }

            // Don't update to the same version we're already running
            if (version == _options.Version)
            {
                _logger.LogDebug("Already running v{Version} — skipping update", version);
                return;
            }

            _updateInProgress = true;
            _logger.LogInformation("Pending update detected: v{Version} sha:{Sha}", version, sha256);

            await SelfUpdater.TryApplyUpdateAsync(url, sha256, version ?? "unknown", signature, _logger, ct);
            // If we get here, update failed (TryApplyUpdateAsync exits on success)
            _updateInProgress = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            _updateInProgress = false;
        }
    }
}
