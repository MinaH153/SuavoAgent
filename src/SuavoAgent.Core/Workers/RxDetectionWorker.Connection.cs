using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Canary;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.Workers;

public sealed partial class RxDetectionWorker
{
    private async Task RetryPendingBatchesAsync(CancellationToken ct)
    {
        var pendingBatches = _stateDb.GetPendingBatches();
        if (pendingBatches.Count > 0)
        {
            _logger.LogInformation("Retrying {Count} persisted unsynced batches", pendingBatches.Count);
            foreach (var batch in pendingBatches)
            {
                if (await TrySyncPayloadToCloudAsync(batch.Payload, ct))
                    _stateDb.DeleteBatch(batch.Id);
                else
                    _stateDb.IncrementBatchRetry(batch.Id);
            }
        }
    }

    private string RequireHmacSalt()
    {
        if (string.IsNullOrWhiteSpace(_options.HmacSalt))
            throw new InvalidOperationException("Per-agent HMAC key is unavailable; candidate sync is fail-closed.");
        return _options.HmacSalt;
    }

    private void PersistRxCorrelations(
        IReadOnlyList<RxMetadata> readyRxs,
        string hmacSalt,
        string sourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
        string? sourceBinding = null)
    {
        if (_rxCorrelationStore is null)
            throw new InvalidOperationException("Protected Rx correlation store is unavailable; candidate sync is fail-closed.");
        if (string.IsNullOrWhiteSpace(_options.PharmacyId) ||
            string.IsNullOrWhiteSpace(_options.AgentId) ||
            string.IsNullOrWhiteSpace(_options.MachineFingerprint))
            throw new InvalidOperationException("Agent identity is incomplete; candidate sync is fail-closed.");

        foreach (var rx in readyRxs)
        {
            var rxHash = PhiScrubber.HmacHash(rx.RxNumber, hmacSalt);
            var evidenceId = BuildLocalEvidenceId(
                rxHash,
                rx,
                sourceKind,
                sourceBinding);
            _rxCorrelationStore.UpsertObservation(new RxCorrelationObservation(
                new RxCorrelationKey(_options.PharmacyId, _options.AgentId, rxHash, evidenceId),
                _options.MachineFingerprint,
                rx.RxNumber,
                rx.FillNumber,
                sourceKind,
                sourceBinding));
        }
    }

    // B2 state transitions — internal so tests can drive the degraded state machine without a live PMS.
    internal void MarkSqlConnected()
    {
        _sqlConnected = true;
        _consecutiveSqlFailures = 0;
        _sqlDownSince = null;
        _degradedLogged = false;
        _sqlBackoff.Reset();
    }

    internal void MarkSqlConnectFailed(DateTimeOffset now)
    {
        _sqlConnected = false;
        _consecutiveSqlFailures++;
        _sqlDownSince ??= now; // first failure of this outage stamps when it went dark
    }

    // No PMS on this host (dev box / sandbox): not connected, but NOT an outage — clear any dark state
    // so a machine without PioneerRx never reports `degraded`.
    private void MarkSqlNotApplicable()
    {
        _sqlConnected = false;
        _consecutiveSqlFailures = 0;
        _sqlDownSince = null;
        _degradedLogged = false;
    }

    private async Task TryConnectSqlAsync(CancellationToken ct)
    {
        // No-PMS short-circuit: skip the 30s SqlConnection.OpenAsync timeout
        // (+ warning-log noise that counts toward error_event_count_24h) on
        // sandboxes and dev workstations where PioneerRx isn't installed at
        // all. Fail-open inside the detector handles the registry-permissions
        // edge case. We log the skip once per worker lifetime so log volume
        // stays bounded.
        if (!PioneerRxInstallDetector.IsInstalled(_logger))
        {
            MarkSqlNotApplicable();
            if (!_loggedNoPmsOnce)
            {
                _logger.LogInformation(
                    "PioneerRx not installed on this host — skipping SQL detection (no-PMS mode)");
                _loggedNoPmsOnce = true;
            }
            return;
        }

        var server = _options.SqlServer ?? "localhost";
        var database = AdapterCatalog.Resolve(_options.SqlDatabase, _adapterConfig);

        _sqlEngine?.Dispose();
        // The canary source is bound to the engine instance. Disposing+replacing the engine on a
        // reconnect (now more frequent under the worker supervisor) would otherwise leave the
        // `_canarySource == null` guard below holding a source bound to the disposed engine — so
        // clear it here to force a rebuild against the new engine.
        _canarySource = null;
        var sqlCertificatePath = PioneerRxSqlCertificatePinVerifier.ResolveProduction(_options);
        _sqlEngine = new PioneerRxSqlEngine(
            server, database,
            _loggerFactory.CreateLogger<PioneerRxSqlEngine>(),
            _options.SqlUser,
            _options.SqlPassword,
            _options.SqlTrustServerCertificate,
            sqlCertificatePath,
            sqlCertificatePath is null ? null : _options.SqlServerCertificateSha256);

        var connected = await _sqlEngine.TryConnectAsync(ct);

        if (connected)
        {
            MarkSqlConnected();
            _logger.LogInformation("SQL connected to {Server}/{Db}", server, database);
            await SyncSchemaDiscoveryAsync(ct);

            // Create canary source after successful SQL connection
            if (_canaryEnabled && _canarySource == null)
            {
                _canarySource = new PioneerRxCanarySource(_sqlEngine,
                    _loggerFactory.CreateLogger<PioneerRxCanarySource>());
                _logger.LogInformation("Canary detection source initialized for PioneerRx");
            }

            // Create writeback engine with separate connection pool
            if (_sqlConnected && _sqlEngine != null)
            {
                var allGuids = _sqlEngine.GetAllDiscoveredGuids();
                if (allGuids != null && allGuids.Count >= 5)
                {
                    var writebackCsb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder();
                    if (!string.IsNullOrEmpty(_options.SqlServer)) writebackCsb.DataSource = _options.SqlServer;
                    if (!string.IsNullOrEmpty(_options.SqlDatabase)) writebackCsb.InitialCatalog = _options.SqlDatabase;
                    writebackCsb.ApplicationName = "SuavoWriteback";
                    writebackCsb.MaxPoolSize = 1;
                    SqlConnectionSecurity.Apply(writebackCsb, _options);
                    if (!string.IsNullOrEmpty(_options.SqlUser))
                    {
                        writebackCsb.UserID = _options.SqlUser;
                        writebackCsb.Password = _options.SqlPassword;
                    }
                    else
                    {
                        writebackCsb.IntegratedSecurity = true;
                    }

                    _writebackEngine = new PioneerRxWritebackEngine(
                        writebackCsb.ConnectionString,
                        allGuids,
                        _loggerFactory.CreateLogger<PioneerRxWritebackEngine>());

                    await _writebackEngine.DetectTriggersAsync(ct);
                    _logger.LogInformation("Writeback engine created (enabled={Enabled})", _writebackEngine.WritebackEnabled);

                }
                else
                {
                    _logger.LogWarning("Writeback engine NOT created — insufficient status GUIDs ({Count}/5)",
                        allGuids?.Count ?? 0);
                }
            }
        }
        else
        {
            MarkSqlConnectFailed(DateTimeOffset.UtcNow);
            _logger.LogWarning("SQL connection failed for {Server}/{Db}", server, database);
            _canarySource = null;
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

            _logger.LogInformation(
                "Schema discovery retained locally ({Count} tables); cloud sync is fail-closed until signed built-in runtime provenance is enrolled",
                schema.Count);
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
        }
    }

}
