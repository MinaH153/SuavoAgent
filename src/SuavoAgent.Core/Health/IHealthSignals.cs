using System;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Snapshot of the 4 health signals at a point in time. Pure data, no
/// computation. The actual signal sources live in different subsystems
/// (IPC, schema canary, extraction worker) — this interface is a seam
/// so <c>HealthCompositeCalculator</c> stays unit-testable.
/// </summary>
public interface IHealthSignals
{
    /// <summary>
    /// Take a snapshot of all 4 signals + the agent's "last extraction"
    /// timestamp (used by the calculator to apply the 30-minute window).
    /// </summary>
    HealthSignalsSnapshot Snapshot();
}

/// <summary>
/// Raw signals — the calculator applies the 30-minute / off-hours rules.
/// </summary>
public sealed record HealthSignalsSnapshot(
    bool HelperAttached,
    bool IpcConnected,
    bool SchemaCanaryGreen,
    DateTimeOffset? LastExtractionAt);
