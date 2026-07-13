using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Owns the actuation-readiness probe timer so the HEARTBEAT path never pays for a probe: the
/// HeartbeatWorker only reads <see cref="ActuationReadinessTracker.Current"/> (lock-free).
/// Cycle: probe once (bounded by <see cref="ActuationReadinessProbe.ProbeBudget"/>, skipped
/// entirely when the command pipe is busy with a live job), hand a CONCLUSIVE verdict to the
/// self-heal coordinator, sleep <see cref="Interval"/> ± jitter. Supervised like every other
/// worker — a fault restarts with backoff instead of silently killing readiness reporting.
/// </summary>
public sealed class ActuationReadinessWorker : ResilientHostedService
{
    /// <summary>~1 probe/min: fast enough to catch a strand before a demo run, light enough
    /// (one inline ping answered from the Helper's read loop) to be load-invisible.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Jitter = TimeSpan.FromSeconds(5);

    private readonly ActuationReadinessProbe _probe;
    private readonly HelperSelfHealCoordinator? _selfHeal;
    private readonly ILogger<ActuationReadinessWorker> _logger;

    public ActuationReadinessWorker(
        ActuationReadinessProbe probe,
        HelperSelfHealCoordinator? selfHeal,
        ILogger<ActuationReadinessWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _selfHeal = selfHeal;
        _logger = logger;
    }

    protected override string WorkerName => "actuation-readiness";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Actuation readiness worker started — ping-only probe every {Interval}s, self-heal {SelfHeal}",
            Interval.TotalSeconds, _selfHeal is null ? "disabled" : "enabled");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            // ProbeOnceAsync is internally fail-closed (records a not-ready snapshot on any
            // error) and never throws except on genuine cancellation.
            var snapshot = await _probe.ProbeOnceAsync(now, stoppingToken).ConfigureAwait(false);

            // Self-heal sees only conclusive verdicts; its policy enforces streak + backoff +
            // the 24h cap. Guarded so a coordinator bug can't kill readiness reporting.
            if (_selfHeal is not null && snapshot.SkippedReason is null)
            {
                try { _selfHeal.Consider(snapshot, now); }
                catch (Exception ex) { _logger.LogSafeError(ex); }
            }

            var delay = Interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Jitter.TotalMilliseconds));
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
