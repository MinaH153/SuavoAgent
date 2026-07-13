using System;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Helper-side feature-flag for the Phase 5.3 PioneerRx actuation surface.
/// Reads <c>%PROGRAMDATA%\SuavoAgent\pioneerrx.json</c> at startup and
/// enforces the same fail-closed default as <see cref="ActuationConfig"/> —
/// disabled until the operator explicitly opts in by writing a non-empty
/// config file.
///
/// Even when enabled the workflow still has to clear THREE other gates
/// before any PioneerRx verb dispatches:
///
///   1. Cloud-side <c>baa_scope='BaaAmendment'</c> match against
///      <c>pharmacy_profiles.baa_amendments</c>.
///   2. Workflow definition <c>tier &gt; 'sandbox'</c>.
///   3. Charter-driven authz policy (HIGH-risk verbs denied unconditionally
///      until Phase A item A1 lands).
///
/// This config is the AGENT'S own opt-in lever — the operator running the
/// agent service has to write the file. Cloud cannot remotely flip it.
/// </summary>
public sealed record PioneerRxConfig
{
    public bool Enabled { get; init; }
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// The PioneerRx process binary the click + type-into-field verbs are
    /// allowed to target. Locked by default to the canonical name field
    /// observation from Better Life (2026-04-25 pilot day) — operators can
    /// override per-host if the binary is renamed in their installation.
    /// </summary>
    public string ProcessName { get; init; } = "PioneerPharmacy.exe";

    /// <summary>
    /// Legacy display-only field. Mutable ProgramData configuration is never authorization. The exact
    /// closed BAA scope set used by the runtime is bound into the locally and cloud co-signed approval
    /// receipt; this value cannot widen it.
    /// </summary>
    public string[] AllowedBaaScopeTags { get; init; } = Array.Empty<string>();

    public static PioneerRxConfig SafeDefault() => new()
    {
        Enabled = false,
        DryRun = true,
        ProcessName = "PioneerPharmacy.exe",
        AllowedBaaScopeTags = Array.Empty<string>(),
    };
}
