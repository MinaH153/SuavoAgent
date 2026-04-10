using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Workers;

public sealed class RxDetectionWorker : BackgroundService
{
    private readonly ILogger<RxDetectionWorker> _logger;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private PioneerRxSqlEngine? _sqlEngine;
    private bool _sqlConnected;

    public int DetectionIntervalSeconds { get; set; } = 300;
    public int LastDetectedCount { get; private set; }
    public DateTimeOffset? LastDetectionTime { get; private set; }
    public bool IsSqlConnected => _sqlConnected;

    public RxDetectionWorker(
        ILogger<RxDetectionWorker> logger,
        IOptions<AgentOptions> options,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Rx detection worker started");

        await TryConnectSqlAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_sqlConnected)
                {
                    await TryConnectSqlAsync(stoppingToken);
                    if (!_sqlConnected)
                    {
                        _logger.LogDebug("SQL not connected, skipping detection cycle");
                        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                        continue;
                    }
                }

                var readyRxs = await _sqlEngine!.ReadReadyPrescriptionsAsync(stoppingToken);
                LastDetectedCount = readyRxs.Count;
                LastDetectionTime = DateTimeOffset.UtcNow;

                if (readyRxs.Count > 0)
                {
                    _logger.LogInformation("Detected {Count} ready prescriptions", readyRxs.Count);
                    await SyncToCloudAsync(readyRxs, stoppingToken);
                }
                else
                {
                    _logger.LogDebug("No ready prescriptions found");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rx detection cycle failed");
                _sqlConnected = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(DetectionIntervalSeconds), stoppingToken);
        }

        _sqlEngine?.Dispose();
        _logger.LogInformation("Rx detection worker stopped");
    }

    private async Task TryConnectSqlAsync(CancellationToken ct)
    {
        var server = _options.SqlServer ?? "localhost";
        var database = _options.SqlDatabase ?? "PioneerPharmacySystem";

        _sqlEngine?.Dispose();
        _sqlEngine = new PioneerRxSqlEngine(
            server, database,
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole())
                .CreateLogger<PioneerRxSqlEngine>(),
            _options.SqlUser, _options.SqlPassword);

        _sqlConnected = await _sqlEngine.TryConnectAsync(ct);

        if (_sqlConnected)
            _logger.LogInformation("SQL connected to {Server}/{Db}", server, database);
        else
            _logger.LogWarning("SQL connection failed for {Server}/{Db}", server, database);
    }

    private async Task SyncToCloudAsync(IReadOnlyList<RxReadyForDelivery> rxs, CancellationToken ct)
    {
        if (_cloudClient is null) return;

        try
        {
            var payload = new
            {
                agentId = _options.AgentId,
                pharmacyId = _options.PharmacyId,
                prescriptions = rxs.Select(rx => new
                {
                    rx.RxNumber,
                    rx.FillNumber,
                    rx.DrugName,
                    rx.Ndc,
                    rx.Quantity,
                    rx.DaysSupply,
                    rx.StatusText,
                    rx.IsControlled,
                    rx.DrugSchedule,
                    rx.PatientIdRequired,
                    rx.CounselingRequired,
                    detectedAt = rx.DetectedAt.ToString("o"),
                    source = rx.Source.ToString()
                }).ToArray(),
                syncedAt = DateTimeOffset.UtcNow.ToString("o")
            };

            await _cloudClient.HeartbeatAsync(payload, ct);
            _logger.LogDebug("Synced {Count} prescriptions to cloud", rxs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud sync failed for {Count} prescriptions", rxs.Count);
        }
    }
}
