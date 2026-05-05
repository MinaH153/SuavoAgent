using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// Core→Helper actuation IPC commands. Each verb in
/// <c>SuavoAgent.Core.ActionGrammarV1.Verbs.*</c> sends one of these via the
/// existing IpcCommandClient pipeline. Helper enforces the
/// <see cref="ActuationGateState"/> (Enabled, DryRun, PausedUntil) before
/// invoking any Win32 surface — Core can never bypass the gate.
///
/// Shape contract from docs/self-healing/action-grammar-v1.md §Verb registry +
/// next-session-2026-05-05-actuation-on-queen.md (locked 2026-05-04).
/// </summary>
public static class ActuationIpcCommands
{
    /// <summary>Status query — returns <see cref="ActuationGateState"/>.</summary>
    public const string GetState = "actuation.get_state";

    /// <summary>Click by accessible-name label (UIA). LOW-tier verb.</summary>
    public const string ClickByLabel = "actuation.click_by_label";

    /// <summary>Type plain text into the focused field via SendInput Unicode. MED-tier verb.</summary>
    public const string TypeText = "actuation.type_text";

    /// <summary>Press a sequence of virtual-key chords (e.g. Ctrl+S, Enter). LOW-tier verb.</summary>
    public const string PressKeys = "actuation.press_keys";

    /// <summary>Launch an allowlisted sandbox app (Notepad / Calc only). LOW-tier verb.</summary>
    public const string LaunchSandboxApp = "actuation.launch_sandbox_app";
}

public sealed record ActuationGateState(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("pausedUntilUtc")] DateTimeOffset? PausedUntilUtc,
    [property: JsonPropertyName("pauseReason")] string? PauseReason,
    [property: JsonPropertyName("killSwitchTrippedUtc")] DateTimeOffset? KillSwitchTrippedUtc
);

public sealed record ClickByLabelRequest(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("matchMode")] string MatchMode,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs
);

public sealed record TypeTextRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("clearFirst")] bool ClearFirst,
    [property: JsonPropertyName("perKeyDelayMs")] int PerKeyDelayMs
);

public sealed record PressKeysRequest(
    [property: JsonPropertyName("chords")] IReadOnlyList<string> Chords,
    [property: JsonPropertyName("interChordDelayMs")] int InterChordDelayMs
);

public sealed record LaunchSandboxAppRequest(
    [property: JsonPropertyName("appKey")] string AppKey
);

public sealed record ActuationResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("rejectionCode")] string? RejectionCode,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("evidenceHash")] string? EvidenceHash
)
{
    public static ActuationResult Reject(string code, string reason, bool dryRun) =>
        new(false, dryRun, 0, code, reason, null);

    public static ActuationResult Success(long durationMs, bool dryRun, string evidenceHash) =>
        new(true, dryRun, durationMs, null, null, evidenceHash);
}

public static class ActuationRejectionCodes
{
    public const string GateDisabled = "gate_disabled";
    public const string GatePaused = "gate_paused";
    public const string KillSwitchTripped = "kill_switch_tripped";
    public const string PhiPatternDetected = "phi_pattern_detected";
    public const string LabelNotFound = "label_not_found";
    public const string ProcessNotAllowed = "process_not_allowed";
    public const string AppNotInAllowlist = "app_not_in_allowlist";
    public const string MalformedRequest = "malformed_request";
    public const string ChordParseFailure = "chord_parse_failure";
    public const string ExecutionException = "execution_exception";
}

public static class ActuationAllowlistedSandboxApps
{
    public const string Notepad = "notepad";
    public const string Calculator = "calculator";

    public static IReadOnlyDictionary<string, string> ProcessNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Notepad] = "notepad.exe",
            [Calculator] = "calc.exe",
        };
}
