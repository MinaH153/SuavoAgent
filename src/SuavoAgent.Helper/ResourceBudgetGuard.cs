using System.Diagnostics;
using Serilog;

namespace SuavoAgent.Helper;

/// <summary>
/// Hard guarantee that SuavoAgent.Helper can never hang a customer PC.
///
/// Trip A 2026-04-25 surfaced the root cause: with Vision-On (Tesseract OCR
/// loaded ~50–100 MB per engine) plus UIA2 subtree event subscriptions firing
/// dozens-per-second on PioneerRx's deep WinForms tree, Helper's resource
/// usage can climb fast enough to saturate the pharmacy desktop. The first
/// install attempt at Nadim's required a hard power-cycle. That cannot
/// happen again.
///
/// This guard runs as a top-level background task inside Program.cs. Every
/// <see cref="PollInterval"/> it samples this process's working set + CPU
/// usage and tracks a rolling window. If either exceeds budget for the
/// configured sustain window, Helper logs a structured warning and exits
/// with a non-zero code so SuavoAgent.Watchdog auto-restarts it. This gives
/// the OS a chance to reclaim memory and breaks any runaway loop without
/// requiring the customer to power-cycle.
///
/// Budget defaults are intentionally generous — they fire only on actual
/// runaway conditions (Tesseract leak, UIA enumeration loop, OCR backlog),
/// not on steady-state Vision-On operation. A pharmacist clicking through
/// a 200-element PioneerRx grid for an hour stays well under budget; only
/// a real failure crosses the line.
///
/// FDA-grade rationale: bounded behavior + deterministic failure mode +
/// audit-loggable suicide event. The Watchdog restart loop is itself rate-
/// limited to prevent crash-loop lockstep — see SuavoAgent.Watchdog.
/// </summary>
public sealed class ResourceBudgetGuard
{
    public sealed class Budget
    {
        /// <summary>Process working-set ceiling. Hard-suicide above this.</summary>
        public long HardKillRssBytes { get; init; } = 800L * 1024 * 1024;

        /// <summary>Soft warning threshold — log + structured telemetry, no kill.</summary>
        public long SoftWarnRssBytes { get; init; } = 500L * 1024 * 1024;

        /// <summary>Sustained CPU% over the rolling window that triggers suicide.</summary>
        public double SustainedCpuKillPct { get; init; } = 80.0;

        /// <summary>Length of the rolling window for sustained-CPU detection.</summary>
        public TimeSpan SustainWindow { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>How often the guard samples its own resource usage.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Exit code when self-kill fires. Watchdog reads this to log.</summary>
        public int SelfKillExitCode { get; init; } = 137; // SIGKILL convention
    }

    private readonly Budget _budget;
    private readonly ILogger _logger;
    private readonly Action<int> _exit;
    private readonly Process _process;

    // Rolling window of (timestamp, cpu%) samples used to detect *sustained*
    // CPU pressure rather than a single transient spike (a UIA tree walk
    // legitimately spikes the core for a few hundred ms).
    private readonly LinkedList<(DateTimeOffset At, double CpuPct)> _samples = new();
    private TimeSpan _lastTotalCpu;
    private DateTimeOffset _lastSampleAt;

    public ResourceBudgetGuard(
        Budget budget,
        ILogger logger,
        Action<int>? exit = null,
        Process? process = null)
    {
        _budget = budget;
        _logger = logger.ForContext<ResourceBudgetGuard>();
        _exit = exit ?? Environment.Exit;
        _process = process ?? Process.GetCurrentProcess();
        _lastTotalCpu = _process.TotalProcessorTime;
        _lastSampleAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Long-running poll loop. Returns when the cancellation token cancels.
    /// Calls Environment.Exit(SelfKillExitCode) if budget is exceeded.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.Information(
            "ResourceBudgetGuard started — soft={SoftMb}MB hard={HardMb}MB cpuKill={Cpu}% over {Sustain}s",
            _budget.SoftWarnRssBytes / (1024 * 1024),
            _budget.HardKillRssBytes / (1024 * 1024),
            _budget.SustainedCpuKillPct,
            _budget.SustainWindow.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_budget.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                EvaluateOnce();
            }
            catch (Exception ex)
            {
                // The guard itself must never crash Helper. Log and keep polling.
                _logger.Warning(ex, "ResourceBudgetGuard: sampling failed — will retry next interval");
            }
        }

        _logger.Information("ResourceBudgetGuard stopped");
    }

    /// <summary>
    /// Single sampling pass. Public for unit-test injection — production
    /// callers use <see cref="RunAsync"/>.
    /// </summary>
    public void EvaluateOnce()
    {
        _process.Refresh();
        var now = DateTimeOffset.UtcNow;
        var rss = _process.WorkingSet64;
        var cpuPct = SampleCpuPercent(now);

        // RSS hard-kill — single sample is enough, no rolling window. A
        // process that genuinely grows past 800 MB is leaking, not spiking.
        if (rss >= _budget.HardKillRssBytes)
        {
            _logger.Fatal(
                "ResourceBudgetGuard SELF-KILL: RSS={RssMb}MB exceeds hard ceiling {HardMb}MB. " +
                "Watchdog will restart Helper. Likely cause: Tesseract leak or UIA enumeration runaway.",
                rss / (1024 * 1024),
                _budget.HardKillRssBytes / (1024 * 1024));
            _exit(_budget.SelfKillExitCode);
            return;
        }

        if (rss >= _budget.SoftWarnRssBytes)
        {
            _logger.Warning(
                "ResourceBudgetGuard: RSS={RssMb}MB above soft warn {SoftMb}MB — Vision/UIA may be backing up",
                rss / (1024 * 1024),
                _budget.SoftWarnRssBytes / (1024 * 1024));
        }

        // Sustained-CPU kill — only fires when *every* sample in the
        // rolling window is over budget. Single spikes (e.g. one UIA tree
        // walk burning a core for 800 ms) never trigger this.
        if (HasSustainedCpu(now, cpuPct))
        {
            _logger.Fatal(
                "ResourceBudgetGuard SELF-KILL: sustained CPU {Pct:F1}% over {Sustain}s window. " +
                "Watchdog will restart Helper. Likely cause: UIA subtree event storm or OCR backlog.",
                cpuPct,
                _budget.SustainWindow.TotalSeconds);
            _exit(_budget.SelfKillExitCode);
        }
    }

    // Computes per-sample CPU% as (delta TotalProcessorTime / wall-clock delta / processor count) * 100.
    // Returns 0 on the first sample (no baseline yet) and clamps to [0, 100*N] in case the OS hands
    // us something nonsensical after a sleep/resume jump.
    private double SampleCpuPercent(DateTimeOffset now)
    {
        var totalCpu = _process.TotalProcessorTime;
        var cpuDelta = totalCpu - _lastTotalCpu;
        var wallDelta = now - _lastSampleAt;
        _lastTotalCpu = totalCpu;
        _lastSampleAt = now;

        if (wallDelta <= TimeSpan.Zero) return 0;
        if (cpuDelta < TimeSpan.Zero) return 0; // clock jumped, ignore this sample

        var pct = (cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds) * 100.0
                  / Math.Max(1, Environment.ProcessorCount);
        return Math.Max(0, Math.Min(100.0, pct));
    }

    private bool HasSustainedCpu(DateTimeOffset now, double cpuPct)
    {
        _samples.AddLast((now, cpuPct));

        // Drop samples outside the sustain window.
        var cutoff = now - _budget.SustainWindow;
        while (_samples.First is { } first && first.Value.At < cutoff)
        {
            _samples.RemoveFirst();
        }

        // Need at least the full window's worth of samples before declaring
        // sustained pressure — otherwise a single spike right at startup
        // would self-kill before the rolling window is meaningful.
        var requiredSamples = (int)(_budget.SustainWindow.TotalMilliseconds /
                                    _budget.PollInterval.TotalMilliseconds);
        if (_samples.Count < requiredSamples) return false;

        foreach (var sample in _samples)
        {
            if (sample.CpuPct < _budget.SustainedCpuKillPct) return false;
        }
        return true;
    }
}
