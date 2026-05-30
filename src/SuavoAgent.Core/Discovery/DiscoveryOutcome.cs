using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Why a <see cref="DiscoveryClient.FindAsync"/> attempt failed. Each value maps
/// to one of the sites the method previously collapsed into an opaque null. The
/// reason is surfaced (PHI-scrubbed) to the cloud so a failed discovery is
/// debuggable remotely instead of "discovery failed — see agent logs".
/// See the agent-as-bridge architecture decision.
/// </summary>
public enum DiscoveryFailureReason
{
    /// <summary>IPC send threw before any response (Helper not running / pipe down).</summary>
    IpcUnavailable,

    /// <summary>Send returned null — Helper did not respond or the read timed out.</summary>
    HelperTimeout,

    /// <summary>Response id != request id — pipe desync, usually a Helper/agent version mismatch.</summary>
    IpcDesync,

    /// <summary>Helper returned a non-OK IPC status with an error code.</summary>
    HelperError,

    /// <summary>Helper returned OK but with no data payload (export file not found?).</summary>
    NoData,

    /// <summary>Helper returned data the agent could not deserialize — schema/version mismatch.</summary>
    DeserializeError,
}

/// <summary>
/// PHI-safe classification of a discovery failure. Carries only fixed reason
/// codes and machine scalars (IPC status, IPC error code) — never raw Helper
/// free-text, file paths, or file contents — so it is safe to ship to the cloud
/// through <c>OutboundPhiGuard</c> and the cloud ack PHI validator.
/// </summary>
public sealed record DiscoveryFailure(
    DiscoveryFailureReason Reason,
    int? IpcStatus = null,
    string? ErrorCode = null,
    bool HelperVersionSuspect = false)
{
    /// <summary>Stable snake_case code the cloud + dashboard key off of.</summary>
    public string ReasonCode => Reason switch
    {
        DiscoveryFailureReason.IpcUnavailable => "ipc_unavailable",
        DiscoveryFailureReason.HelperTimeout => "helper_timeout",
        DiscoveryFailureReason.IpcDesync => "ipc_desync",
        DiscoveryFailureReason.HelperError => "helper_error",
        DiscoveryFailureReason.NoData => "no_data",
        DiscoveryFailureReason.DeserializeError => "deserialize_error",
        _ => "unknown",
    };

    /// <summary>Operator-facing one-line explanation. Contains no PHI and no raw Helper text.</summary>
    public string HumanMessage => Reason switch
    {
        DiscoveryFailureReason.IpcUnavailable =>
            "Discovery failed: the agent could not reach the Helper process (IPC unavailable — Helper not running).",
        DiscoveryFailureReason.HelperTimeout =>
            "Discovery failed: the Helper did not respond in time (try again; if it persists, restart the agent).",
        DiscoveryFailureReason.IpcDesync =>
            "Discovery failed: Helper/agent version mismatch (IPC desync). Reinstall the agent to sync the Helper.",
        DiscoveryFailureReason.HelperError =>
            $"Discovery failed: the Helper reported an error (status {IpcStatus?.ToString() ?? "?"}, code {ErrorCode ?? "none"}).",
        DiscoveryFailureReason.NoData =>
            "Discovery failed: the Helper ran but returned no file (the export may not be in the expected folder).",
        DiscoveryFailureReason.DeserializeError =>
            "Discovery failed: the agent could not read the Helper response (version mismatch suspected). Reinstall the agent.",
        _ => "Discovery failed.",
    };

    /// <summary>
    /// The structured, PHI-safe object posted as the command ack <c>result</c>.
    /// Mirrors the success-path ack shape (<c>stage</c>/<c>outcome</c>/…) the
    /// dashboard reads, so a failed run renders a real reason in the cockpit.
    /// </summary>
    public object ToAckResult() => new
    {
        stage = "discovery",
        outcome = "failed",
        reason = ReasonCode,
        ipcStatus = IpcStatus,
        errorCode = ErrorCode,
        helperVersionSuspect = HelperVersionSuspect,
    };
}

/// <summary>
/// Result of a discovery attempt: either a <see cref="FileDiscoveryResult"/>
/// (success — note that <see cref="FileDiscoveryResolution.NotFound"/> is still
/// a successful attempt) or a <see cref="DiscoveryFailure"/> classification.
/// Replaces the old "nullable result == failure" signal so callers can report
/// WHY discovery failed rather than swallowing it.
/// </summary>
public sealed record DiscoveryOutcome
{
    private DiscoveryOutcome(FileDiscoveryResult? result, DiscoveryFailure? failure)
    {
        Result = result;
        Failure = failure;
    }

    public FileDiscoveryResult? Result { get; }
    public DiscoveryFailure? Failure { get; }

    public bool Succeeded => Result is not null && Failure is null;

    public static DiscoveryOutcome Success(FileDiscoveryResult result) => new(result, null);
    public static DiscoveryOutcome Failed(DiscoveryFailure failure) => new(null, failure);
}
