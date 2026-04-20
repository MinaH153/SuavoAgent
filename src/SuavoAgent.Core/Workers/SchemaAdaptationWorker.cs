using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Polls the cloud adaptations endpoint on a fixed cadence, runs every
/// returned <see cref="SchemaAdaptation"/> through
/// <see cref="SchemaAdaptationApplier.ApplyIfEligible"/>, and applies every
/// <see cref="AdaptationRevocation"/> via <see cref="SchemaAdaptationApplier.Revoke"/>.
///
/// Gated on <see cref="FleetFeaturesOptions.SchemaAdaptation"/> — off by
/// default in v3.12.x so installs are byte-identical to the disabled path
/// until the cloud endpoint is live.
///
/// Never throws out of <see cref="ExecuteAsync"/>. Transport returns null on
/// transient error; worker sleeps to next tick.
/// </summary>
public sealed class SchemaAdaptationWorker : BackgroundService
{
    private readonly ISchemaAdaptationTransport _transport;
    private readonly SchemaAdaptationApplier _applier;
    private readonly AgentOptions _agentOptions;
    private readonly Func<PmsVersionFingerprint> _fingerprintProvider;
    private readonly ILogger<SchemaAdaptationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    public SchemaAdaptationWorker(
        ISchemaAdaptationTransport transport,
        SchemaAdaptationApplier applier,
        AgentOptions agentOptions,
        Func<PmsVersionFingerprint> fingerprintProvider,
        ILogger<SchemaAdaptationWorker> logger,
        TimeSpan? interval = null,
        TimeSpan? initialDelay = null)
    {
        _transport = transport;
        _applier = applier;
        _agentOptions = agentOptions;
        _fingerprintProvider = fingerprintProvider;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(15);
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_agentOptions.FleetFeatures.SchemaAdaptation)
        {
            _logger.LogInformation(
                "SchemaAdaptationWorker: FleetFeatures:SchemaAdaptation disabled — idle");
            return;
        }

        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SchemaAdaptationWorker: tick failed — retrying next cycle");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One tick of the worker loop. Exposed internally for deterministic
    /// testing — tests call this directly instead of spinning the background
    /// service. Never throws.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        PmsVersionFingerprint fp;
        try { fp = _fingerprintProvider(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SchemaAdaptationWorker: fingerprint provider threw");
            return;
        }

        var response = await _transport.PullAsync(fp.PmsType, fp.SchemaHash, ct);
        if (response is null) return;

        if (response.Revocations is { Count: > 0 } revs)
        {
            foreach (var rev in revs)
            {
                try { _applier.Revoke(rev.TargetAdaptationId, rev.Reason ?? "cloud-revocation"); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "SchemaAdaptationWorker: revoke failed for {Id}", rev.TargetAdaptationId);
                }
            }
        }

        if (response.Adaptations is { Count: > 0 } adapts)
        {
            foreach (var a in adapts)
            {
                try
                {
                    var result = _applier.ApplyIfEligible(a);
                    _logger.LogInformation(
                        "SchemaAdaptationWorker: {Id} → {Outcome} ({Detail})",
                        a.AdaptationId, result.Status, result.Detail ?? "-");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "SchemaAdaptationWorker: apply failed for {Id}", a.AdaptationId);
                }
            }
        }
    }
}
