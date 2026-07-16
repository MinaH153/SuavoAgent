using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Cloud;

internal enum ObservationActivationCommandClass
{
    MaintenanceControlPlane,
    ApprovedPioneerRxObservation,
    ReleaseProhibited,
    Unknown,
}

/// <summary>
/// One exhaustive release-cohort policy for commands received by Heartbeat.
/// Pairing, command signature validity, and live-command expiry never imply
/// workstation-observation authority. Unknown commands and general-desktop
/// surfaces are rejected; only the explicit maintenance/control-plane set can
/// run while the machine is dormant.
/// </summary>
internal static class ObservationActivationCommandPolicy
{
    internal const string ActiveCode = "observation_command_active";
    internal const string MaintenanceCode = "observation_command_maintenance";
    internal const string DormantCode = "observation_command_authority_required";
    internal const string ProhibitedCode = "observation_command_release_prohibited";
    internal const string UnknownCode = "observation_command_unknown";

    private static readonly IReadOnlyDictionary<string, ObservationActivationCommandClass>
        Commands = new Dictionary<string, ObservationActivationCommandClass>(StringComparer.Ordinal)
    {
        // PHI-free health, lifecycle, repair, and signed configuration ledgers.
        ["repair"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["repair_agent"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["collect_health_probe"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["fetch_diagnostics"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["update"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        [Release1ConvergenceCommand.Name] =
            ObservationActivationCommandClass.MaintenanceControlPlane,
        ["approve_pom"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["install_pioneerrx_process_approval"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["set_vision_config"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["install_pricing_cost_basis_approval"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["revoke_pricing_cost_basis_approval"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["abort_workflow"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["abort_navigation"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["force_restart"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["restart_helper"] = ObservationActivationCommandClass.MaintenanceControlPlane,
        ["self_uninstall"] = ObservationActivationCommandClass.MaintenanceControlPlane,

        // Exact pharmacy cohort surfaces. Every one additionally acquires a
        // current signed ObservationActivationAuthority execution lease.
        ["fetch_patient"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["delivery_writeback"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["export_pioneerrx_shadow_fixture"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["acknowledge_drift"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["approve_candidate"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["reject_candidate"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["reapprove_candidate"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["force_relearn"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["adjust_window"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["acknowledge_stale"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["find_and_run_pricing_job"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["run_pricing_job"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["transition_auto_rule_approval"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["update_selector"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["navigate_pricing"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["force_learning_phase"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["set_reasoning_config"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,
        ["chat"] = ObservationActivationCommandClass.ApprovedPioneerRxObservation,

        // General desktop, multi-app, and broad discovery are not in this RC.
        ["show_cursor"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["show_intent_cursor"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["computer_use_observe"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["computer_use_propose"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["run_workflow"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["navigate_app"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["replay_template"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["run_learned_template"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["explore_sandbox"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["replay_skill"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["extend_app_allowlist"] = ObservationActivationCommandClass.ReleaseProhibited,
        ["discover_elements"] = ObservationActivationCommandClass.ReleaseProhibited,

        // Retired command with deliberately no local mutation or audit. It is
        // not an audited maintenance exemption; self_uninstall is the sole
        // evidence-preserving removal path.
        ["decommission"] = ObservationActivationCommandClass.ReleaseProhibited,
    };

    internal static IReadOnlyCollection<string> ExplicitCommands =>
        Commands.Keys.ToArray();

    internal static ObservationActivationCommandClass Classify(string command) =>
        Commands.TryGetValue(command, out var classification)
            ? classification
            : ObservationActivationCommandClass.Unknown;

    internal static ObservationActivationCommandAdmission Admit(
        string command,
        ObservationActivationAuthority? authority,
        CancellationToken lifetime)
    {
        var classification = Classify(command);
        if (classification == ObservationActivationCommandClass.MaintenanceControlPlane)
            return ObservationActivationCommandAdmission.AllowMaintenance(lifetime);
        if (classification == ObservationActivationCommandClass.ReleaseProhibited)
            return ObservationActivationCommandAdmission.Reject(classification, ProhibitedCode);
        if (classification == ObservationActivationCommandClass.Unknown)
            return ObservationActivationCommandAdmission.Reject(classification, UnknownCode);
        if (lifetime.IsCancellationRequested || authority is null)
            return ObservationActivationCommandAdmission.Reject(classification, DormantCode);

        var lease = authority.TryAcquireExecutionLease(lifetime);
        return lease is null
            ? ObservationActivationCommandAdmission.Reject(classification, DormantCode)
            : ObservationActivationCommandAdmission.AllowObservation(lease);
    }
}

internal sealed class ObservationActivationCommandAdmission : IDisposable
{
    private readonly ObservationActivationExecutionLease? _lease;

    private ObservationActivationCommandAdmission(
        ObservationActivationCommandClass classification,
        bool admitted,
        string code,
        CancellationToken token,
        ObservationActivationExecutionLease? lease)
    {
        Classification = classification;
        Admitted = admitted;
        Code = code;
        Token = token;
        _lease = lease;
    }

    internal ObservationActivationCommandClass Classification { get; }
    internal bool Admitted { get; }
    internal string Code { get; }
    internal CancellationToken Token { get; }

    internal static ObservationActivationCommandAdmission AllowMaintenance(
        CancellationToken lifetime) => new(
            ObservationActivationCommandClass.MaintenanceControlPlane,
            true,
            ObservationActivationCommandPolicy.MaintenanceCode,
            lifetime,
            null);

    internal static ObservationActivationCommandAdmission AllowObservation(
        ObservationActivationExecutionLease lease) => new(
            ObservationActivationCommandClass.ApprovedPioneerRxObservation,
            true,
            ObservationActivationCommandPolicy.ActiveCode,
            lease.Token,
            lease);

    internal static ObservationActivationCommandAdmission Reject(
        ObservationActivationCommandClass classification,
        string code) => new(
            classification,
            false,
            code,
            new CancellationToken(canceled: true),
            null);

    public void Dispose() => _lease?.Dispose();
}
