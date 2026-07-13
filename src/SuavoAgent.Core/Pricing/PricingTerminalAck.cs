using System.Collections.Frozen;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Minimum-necessary terminal failure projection for pricing commands. Every
/// field is finite and structural; workbook paths, filenames, Rx data, and
/// free-form exception text cannot be represented by this contract.
/// </summary>
internal sealed record PricingTerminalAck(
    string ResultKind,
    string ErrorCode,
    string? JobId = null,
    string? Mode = null,
    int? TotalItems = null,
    int? CompletedItems = null,
    int? FailedItems = null,
    string? ReasonCode = null,
    int? CandidateCount = null,
    bool? HelperVersionSuspect = null)
{
    internal const string NoResult = "none";
    internal const string CancelledResult = "cancelled";
    internal const string PathRejectedResult = "path_rejected";
    internal const string NotFoundResult = "not_found";
    internal const string PricingFailedResult = "pricing_failed";
    internal const string LocalConfirmationResult = "local_confirmation_required";
    internal const string AutopilotRejectedResult = "autopilot_rejected";
    internal const string DiscoveryFailedResult = "discovery_failed";

    internal static readonly FrozenSet<string> EarlyFailureCodes = new[]
    {
        "pricing_executor_unavailable",
        "autonomy_not_earned",
        "pricing_candidate_token_required",
        "ipc_not_configured",
        "pipe_unreachable",
        "ping_unanswered",
        "ping_bad_status",
        "ping_no_diagnostics",
        "not_interactive",
        "helper_preflight_failed",
        "pricing_candidate_expired",
        "pricing_candidate_resolution_failed",
        "autonomy_latch_persistence_failed",
        "pricing_job_in_flight",
        "helper_restart_in_progress",
        "pricing_execution_exception",
        "pricing_command_authority_expired",
        "pricing_cost_basis_approval_expired",
        "pricing_cost_basis_approval_revoked",
        "pricing_cloud_authority_revoked",
        "pricing_result_manual_reconciliation_required",
        "pricing_cost_basis_approval_invalid",
        "pricing_cost_basis_approval_required",
        "pricing_job_authority_identity_invalid",
        "pricing_job_authority_binding_missing",
        "pricing_job_authority_binding_invalid",
        "unknown_pack",
        "helper_unreachable",
        "pricing_discovery_unavailable",
        "pricing_discovery_exception",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> TerminalFailureCodes = new[]
    {
        "pricing_workbook_validation_failed",
        "pricing_result_payload_too_large",
        "helper_unreachable",
        "actuation_gate_closed",
        "pioneerrx_not_attached",
        "pricing_brain_operator_required",
        "pricing_job_failed",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> DiscoveryFailureCodes = new[]
    {
        "ipc_unavailable",
        "helper_timeout",
        "ipc_desync",
        "helper_error",
        "no_data",
        "deserialize_error",
        "unknown",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> AutopilotFailureCodes = new[]
    {
        "autopilot_paused",
        "autopilot_stopped",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static PricingTerminalAck Early(string errorCode) =>
        new PricingTerminalAck(NoResult, errorCode).Validated();

    internal static PricingTerminalAck Cancelled() =>
        new PricingTerminalAck(CancelledResult, "pricing_cancelled").Validated();

    internal static PricingTerminalAck PathRejected(string errorCode) =>
        new PricingTerminalAck(PathRejectedResult, errorCode).Validated();

    internal static PricingTerminalAck NotFound() =>
        new PricingTerminalAck(NotFoundResult, "pricing_workbook_not_found").Validated();

    internal static PricingTerminalAck PricingFailed(
        string jobId,
        string mode,
        int totalItems,
        int completedItems,
        int failedItems,
        string reasonCode) =>
        new PricingTerminalAck(
            PricingFailedResult,
            reasonCode,
            jobId,
            mode,
            totalItems,
            completedItems,
            failedItems,
            reasonCode).Validated();

    internal static PricingTerminalAck LocalConfirmation(int candidateCount) =>
        new PricingTerminalAck(
            LocalConfirmationResult,
            "pricing_local_confirmation_required",
            CandidateCount: candidateCount).Validated();

    internal static PricingTerminalAck AutopilotRejected(string reasonCode) =>
        new PricingTerminalAck(
            AutopilotRejectedResult,
            reasonCode,
            ReasonCode: reasonCode).Validated();

    internal static PricingTerminalAck DiscoveryFailed(
        string reasonCode,
        bool helperVersionSuspect) =>
        new PricingTerminalAck(
            DiscoveryFailedResult,
            reasonCode,
            ReasonCode: reasonCode,
            HelperVersionSuspect: helperVersionSuspect).Validated();

    internal object? BuildResult() => ResultKind switch
    {
        NoResult => null,
        CancelledResult => new
        {
            status = "cancelled",
            completedItems = 0,
            failedItems = 0,
        },
        PathRejectedResult => new { status = "path_rejected" },
        NotFoundResult => new { status = "not_found" },
        PricingFailedResult => new
        {
            status = "pricing_failed",
            jobId = JobId,
            mode = Mode,
            totalItems = TotalItems,
            completedItems = CompletedItems,
            failedItems = FailedItems,
            reason = ReasonCode,
        },
        LocalConfirmationResult => new
        {
            status = "local_confirmation_required",
            candidateCount = CandidateCount,
        },
        AutopilotRejectedResult => new
        {
            admitted = false,
            kind = "Pricing",
            outcome = ReasonCode,
        },
        DiscoveryFailedResult => new
        {
            stage = "discovery",
            outcome = "failed",
            reason = ReasonCode,
            helperVersionSuspect = HelperVersionSuspect,
        },
        _ => throw new InvalidDataException("Pricing terminal ACK kind is invalid."),
    };

    internal PricingTerminalAck Validated()
    {
        var emptyAuxiliary = JobId is null && Mode is null && TotalItems is null &&
            CompletedItems is null && FailedItems is null && ReasonCode is null &&
            CandidateCount is null && HelperVersionSuspect is null;
        var valid = ResultKind switch
        {
            NoResult => emptyAuxiliary && EarlyFailureCodes.Contains(ErrorCode),
            CancelledResult => emptyAuxiliary && ErrorCode == "pricing_cancelled",
            PathRejectedResult => emptyAuxiliary && ErrorCode is
                "pricing_candidate_extension_invalid" or
                "pricing_candidate_path_invalid",
            NotFoundResult => emptyAuxiliary &&
                ErrorCode == "pricing_workbook_not_found",
            PricingFailedResult => IsPricingFailureValid(),
            LocalConfirmationResult =>
                JobId is null && Mode is null && TotalItems is null &&
                CompletedItems is null && FailedItems is null && ReasonCode is null &&
                HelperVersionSuspect is null && CandidateCount is >= 1 and <= 100 &&
                ErrorCode == "pricing_local_confirmation_required",
            AutopilotRejectedResult =>
                JobId is null && Mode is null && TotalItems is null &&
                CompletedItems is null && FailedItems is null && CandidateCount is null &&
                HelperVersionSuspect is null && ReasonCode == ErrorCode &&
                AutopilotFailureCodes.Contains(ErrorCode),
            DiscoveryFailedResult =>
                JobId is null && Mode is null && TotalItems is null &&
                CompletedItems is null && FailedItems is null && CandidateCount is null &&
                HelperVersionSuspect is not null && ReasonCode == ErrorCode &&
                DiscoveryFailureCodes.Contains(ErrorCode),
            _ => false,
        };
        return valid
            ? this
            : throw new ArgumentException("Pricing terminal ACK is invalid.");
    }

    private bool IsPricingFailureValid() =>
        IsLowerHex(JobId, 32) &&
        Mode is "sql" or "uia" or "vision" &&
        IsCount(TotalItems) && IsCount(CompletedItems) && IsCount(FailedItems) &&
        CompletedItems!.Value + FailedItems!.Value <= TotalItems!.Value &&
        ReasonCode == ErrorCode && TerminalFailureCodes.Contains(ErrorCode) &&
        CandidateCount is null && HelperVersionSuspect is null;

    private static bool IsCount(int? value) => value is >= 0 and <= 1_000_000;

    internal static bool IsCanonicalCommandId(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        parsed.ToString("D") == value && value[14] == '4' &&
        value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
