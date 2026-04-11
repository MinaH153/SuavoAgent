using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Workers;

public sealed class RxDetectionWorker : BackgroundService
{
    private readonly ILogger<RxDetectionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private readonly AgentStateDb _stateDb;
    private PioneerRxSqlEngine? _sqlEngine;
    private bool _sqlConnected;

    public int DetectionIntervalSeconds { get; set; } = 300;
    public int LastDetectedCount { get; private set; }
    public DateTimeOffset? LastDetectionTime { get; private set; }
    public bool IsSqlConnected => _sqlConnected;
    public PioneerRxSqlEngine? SqlEngine => _sqlEngine;

    public RxDetectionWorker(
        ILogger<RxDetectionWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options,
        AgentStateDb stateDb,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _stateDb = stateDb;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Rx detection worker started");

        _stateDb.PurgeExpiredDeadLetters();

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

                // Retry persisted unsynced batches first
                var pendingBatches = _stateDb.GetPendingBatches();
                if (pendingBatches.Count > 0)
                {
                    _logger.LogInformation("Retrying {Count} persisted unsynced batches", pendingBatches.Count);
                    foreach (var batch in pendingBatches)
                    {
                        if (await TrySyncPayloadToCloudAsync(batch.Payload, stoppingToken))
                            _stateDb.DeleteBatch(batch.Id);
                        else
                            _stateDb.IncrementBatchRetry(batch.Id);
                    }
                }

                // PHI-free detection: metadata only, no Person JOIN (HIPAA 164.502(b))
                var readyRxs = await _sqlEngine!.ReadReadyMetadataAsync(stoppingToken);
                LastDetectedCount = readyRxs.Count;
                LastDetectionTime = DateTimeOffset.UtcNow;

                if (readyRxs.Count > 0)
                {
                    _logger.LogInformation("Detected {Count} ready prescriptions (metadata-only)", readyRxs.Count);
                    var json = SerializeRxBatch(readyRxs);
                    if (!await TrySyncPayloadToCloudAsync(json, stoppingToken))
                        _stateDb.InsertUnsyncedBatch(json);
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
            _loggerFactory.CreateLogger<PioneerRxSqlEngine>(),
            _options.SqlUser, _options.SqlPassword);

        _sqlConnected = await _sqlEngine.TryConnectAsync(ct);

        if (_sqlConnected)
        {
            _logger.LogInformation("SQL connected to {Server}/{Db}", server, database);
            await SyncSchemaDiscoveryAsync(ct);
        }
        else
        {
            _logger.LogWarning("SQL connection failed for {Server}/{Db}", server, database);
        }
    }

    private async Task SyncSchemaDiscoveryAsync(CancellationToken ct)
    {
        if (_cloudClient is null || _sqlEngine is null) return;

        try
        {
            var schema = await _sqlEngine.DiscoverSchemaAsync(ct);
            if (schema.Count == 0) return;

            var payload = new
            {
                snapshotType = "schema_discovery",
                data = new
                {
                    tables = schema.ToDictionary(
                        kv => kv.Key,
                        kv => (object)kv.Value),
                    discoveredAt = DateTimeOffset.UtcNow.ToString("o")
                },
                sqlConnected = true
            };

            await _cloudClient.SyncRxAsync(payload, ct);
            _logger.LogInformation("Schema discovery synced: {Count} tables", schema.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Schema discovery sync failed — non-critical");
        }
    }

    /// <summary>
    /// Serializes PHI-free metadata batch. Contains ZERO patient data.
    /// Patient data is only accessible via signed fetch_patient command.
    /// </summary>
    internal static string SerializeRxBatch(IReadOnlyList<RxMetadata> rxs)
    {
        var payload = new
        {
            snapshotType = "rx_delivery_queue",
            data = new
            {
                rxDeliveryQueue = rxs.Select(rx => new
                {
                    rxNumber = rx.RxNumber,
                    drugName = rx.DrugName,
                    ndc = rx.Ndc,
                    dateFilled = rx.DateFilled?.ToString("o"),
                    quantity = rx.Quantity,
                    statusGuid = rx.StatusGuid.ToString(),
                    detectedAt = rx.DetectedAt.ToString("o")
                }).ToArray(),
                totalDetected = rxs.Count,
                syncedAt = DateTimeOffset.UtcNow.ToString("o")
            },
            sqlConnected = true
        };
        return JsonSerializer.Serialize(payload);
    }

    private async Task<bool> TrySyncPayloadToCloudAsync(string json, CancellationToken ct)
    {
        if (_cloudClient is null) return true; // no cloud = nothing to sync

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            await _cloudClient.SyncRxAsync(payload, ct);
            _logger.LogInformation("Synced batch to cloud");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cloud sync FAILED — will retry next cycle");
            return false;
        }
    }
}
