using System.Collections.Frozen;
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

    /// <summary>Click by GREEN-tier structural signature (ControlType + AutomationId). LOW-tier verb — learned-template replay.</summary>
    public const string ClickBySignature = "actuation.click_by_signature";

    /// <summary>Type plain text into the focused field via SendInput Unicode. MED-tier verb.</summary>
    public const string TypeText = "actuation.type_text";

    /// <summary>Press a sequence of virtual-key chords (e.g. Ctrl+S, Enter). LOW-tier verb.</summary>
    public const string PressKeys = "actuation.press_keys";

    /// <summary>Launch an allowlisted sandbox app (Calculator by default). LOW-tier verb.</summary>
    public const string LaunchSandboxApp = "actuation.launch_sandbox_app";

    /// <summary>Re-read %PROGRAMDATA%\SuavoAgent\actuation.json AllowedApps and re-apply the app
    /// allowlist in THIS process (no restart). The file must originate from an authorized local
    /// installation/configuration flow; remote signed commands cannot widen it.</summary>
    public const string ReloadAllowlist = "actuation.reload_allowlist";

    /// <summary>Read a UIA element's value and assert it matches an expected string. READ-ONLY,
    /// non-mutating — the verification keystone: lets a workflow PROVE it reached the intended end
    /// state (Calc reads "12", the note typed). PHI-safe: the raw read value never leaves the box;
    /// only pass/fail + scrubbed length hints are returned.</summary>
    public const string AssertElement = "actuation.assert_element";

    /// <summary>Enumerate an allowlisted app's actionable UIA elements (controlType + automationId +
    /// PHI-scrubbed name) so the agent can SEE an unfamiliar UI and ground its clicks/asserts on real
    /// elements instead of guessing locators. READ-ONLY; structural data only (names PHI-filtered).</summary>
    public const string DiscoverElements = "actuation.discover_elements";
}

public sealed record ActuationGateState(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("pausedUntilUtc")] DateTimeOffset? PausedUntilUtc,
    [property: JsonPropertyName("pauseReason")] string? PauseReason,
    [property: JsonPropertyName("killSwitchTrippedUtc")] DateTimeOffset? KillSwitchTrippedUtc,
    // Honeytoken immune reflex — the Helper stamps a corroborated compromise here; Core reads it via the
    // existing actuation.get_state IPC and emits the PHI-free self-compromise heartbeat signal (and, on
    // apoptosis, flushes its own Tier-2 LLM). Nullable defaults keep this backward-compatible on the wire.
    // CompromiseReasonLabel is one HoneytokenReasonLabels fixed category.
    [property: JsonPropertyName("compromiseDetected")] bool CompromiseDetected = false,
    [property: JsonPropertyName("compromiseLevel")] string? CompromiseLevel = null,
    [property: JsonPropertyName("compromiseReasonLabel")] string? CompromiseReasonLabel = null,
    [property: JsonPropertyName("compromiseAtUtc")] DateTimeOffset? CompromiseAtUtc = null
);

// Bug 21 (MinaH153/SuavoAgent#63): every actuation request DTO carries an
// explicit dryRun flag set from the workflow definition. The Helper-side
// effective dry-run is `request.DryRun || ActuationGate.IsDryRun` — either
// flag forces dry-run; real input only fires when BOTH are false.
// Fail-closed: missing field defaults to false (the workflow author had
// to opt in to live actuation), and the local gate's default
// (ActuationConfig.DryRun = true) still wins.
public sealed record ClickByLabelRequest(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("matchMode")] string MatchMode,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

/// <summary>
/// Click an element matched by GREEN-tier structural signature (ControlType + AutomationId
/// [+ ClassName]) rather than accessible name — the resolution learned-template replay needs,
/// since templates store signatures (never PHI names). ClassName optional.
/// </summary>
public sealed record ClickBySignatureRequest(
    [property: JsonPropertyName("controlType")] string ControlType,
    [property: JsonPropertyName("automationId")] string AutomationId,
    [property: JsonPropertyName("className")] string? ClassName,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

public sealed record TypeTextRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("clearFirst")] bool ClearFirst,
    [property: JsonPropertyName("perKeyDelayMs")] int PerKeyDelayMs,
    [property: JsonPropertyName("dryRun")] bool DryRun = false,
    [property: JsonPropertyName("processName")] string? ProcessName = null
);

public sealed record PressKeysRequest(
    [property: JsonPropertyName("chords")] IReadOnlyList<string> Chords,
    [property: JsonPropertyName("interChordDelayMs")] int InterChordDelayMs,
    [property: JsonPropertyName("dryRun")] bool DryRun = false,
    [property: JsonPropertyName("processName")] string? ProcessName = null
);

public sealed record LaunchSandboxAppRequest(
    [property: JsonPropertyName("appKey")] string AppKey,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

/// <summary>No-arg reload — actuation.json on disk is the source of truth (Core writes it first).</summary>
public sealed record ReloadAllowlistRequest();

/// <summary>Enumerate the actionable UIA elements of an allowlisted app. Read-only; the response
/// (in <see cref="ActuationResult.Payload"/>) is a JSON array of {controlType, automationId, name}
/// with names PHI-scrubbed Helper-side. Lets the agent introspect an unfamiliar UI.</summary>
public sealed record DiscoverElementsRequest(
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("max")] int Max = 60,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

/// <summary>
/// Read a UIA element's value and assert it equals/contains <see cref="Expected"/>. Locate the
/// element by AutomationId (preferred), accessible Name, or ControlType — at least one is required.
/// Read order Helper-side: ValuePattern → TextPattern → Name (display-only controls like the
/// Calculator result expose their value via Name, e.g. "Display is 12"). The match is forgiving by
/// default (<c>normalized</c>: alphanumeric-lowercase) so "Display is 12" satisfies expected "12".
/// READ-ONLY; <see cref="DryRun"/> short-circuits to a pass (asserting real state after a dry-run
/// actuation is meaningless). The raw read value is NEVER returned to the cloud — only pass/fail.
/// </summary>
public sealed record AssertElementRequest(
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("expected")] string Expected,
    [property: JsonPropertyName("matchMode")] string MatchMode,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs,
    [property: JsonPropertyName("automationId")] string? AutomationId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("controlType")] string? ControlType = null,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

public sealed record ActuationResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("rejectionCode")] string? RejectionCode,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("evidenceHash")] string? EvidenceHash,
    // Optional structured result for READ commands (discover_elements): a JSON string the caller
    // surfaces. Null for the actuation verbs. Backward-compatible (defaults null on the wire).
    [property: JsonPropertyName("payload")] string? Payload = null
)
{
    public static ActuationResult Reject(string code, string reason, bool dryRun) =>
        new(false, dryRun, 0, code, reason, null);

    public static ActuationResult Success(long durationMs, bool dryRun, string evidenceHash) =>
        new(true, dryRun, durationMs, null, null, evidenceHash);

    public static ActuationResult SuccessWithPayload(long durationMs, bool dryRun, string evidenceHash, string payload) =>
        new(true, dryRun, durationMs, null, null, evidenceHash, payload);
}

public static class ActuationRejectionCodes
{
    public const string GateStateUnavailable = "gate_state_unavailable";
    public const string GateDisabled = "gate_disabled";
    public const string GatePaused = "gate_paused";
    public const string GateDryRun = "gate_dry_run";
    public const string KillSwitchTripped = "kill_switch_tripped";
    public const string CompromiseDetected = "compromise_detected";
    public const string PhiPatternDetected = "phi_pattern_detected";
    public const string LabelNotFound = "label_not_found";
    public const string ProcessNotAllowed = "process_not_allowed";
    public const string ProcessIdentityUntrusted = "process_identity_untrusted";
    // type/press refused because the launched target window could not be confirmed in the foreground —
    // fail-closed so keystrokes never leak into an unintended window (e.g. a shell or another app).
    public const string ForegroundNotTarget = "foreground_not_target";
    // type SENT its keystrokes but a UIA read-back of the focused field did NOT contain the typed text:
    // the keystrokes silently didn't land (dropped chars, wrong control). Self-verification fail.
    public const string TypeNotVerified = "type_not_verified";
    public const string AppNotInAllowlist = "app_not_in_allowlist";
    public const string MalformedRequest = "malformed_request";
    public const string ChordParseFailure = "chord_parse_failure";
    public const string ExecutionException = "execution_exception";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string RemotePolicyMutationDenied = "remote_policy_mutation_denied";
    // assert_element: the located UIA element could not be found within the timeout.
    public const string ElementNotFound = "element_not_found";
    // assert_element: the element was read but its value did NOT match expected (carries a
    // PHI-safe length/mode hint, never the raw value).
    public const string AssertMismatch = "assert_mismatch";
}

/// <summary>
/// One cross-process definition of a fully open live-actuation gate. Core uses
/// it before dispatch and Helper uses it at the exact mutation boundary, so a
/// new gate axis cannot accidentally be enforced on only one side of IPC.
/// </summary>
public static class LiveActuationGatePolicy
{
    public static string? RejectionCode(ActuationGateState? state, DateTimeOffset now)
    {
        if (state is null) return ActuationRejectionCodes.GateStateUnavailable;
        if (state.KillSwitchTrippedUtc is not null) return ActuationRejectionCodes.KillSwitchTripped;
        if (state.CompromiseDetected) return ActuationRejectionCodes.CompromiseDetected;
        if (!state.Enabled) return ActuationRejectionCodes.GateDisabled;
        if (state.PausedUntilUtc is { } until && until > now) return ActuationRejectionCodes.GatePaused;
        if (state.DryRun) return ActuationRejectionCodes.GateDryRun;
        return null;
    }
}

public static class ActuationAllowlistedSandboxApps
{
    // Legacy identifier retained for wire compatibility only. Windows 11 Notepad
    // is tabbed/single-instance and can reopen or attach to an existing document,
    // including one containing PHI, so it is intentionally protected and can no
    // longer be a sandbox target.
    public const string Notepad = "notepad";
    public const string Calculator = "calculator";

    // Built-in safe defaults — ALWAYS present and never removable. Calculator
    // has no general document surface and is the only in-box default. A future
    // purpose-built Suavo sandbox may be added here after its window isolation is
    // proven. Notepad is deliberately absent (single-instance document reuse).
    private static readonly FrozenDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Calculator] = "calc.exe",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>app_key → process file name (e.g. "calculator" → "calc.exe"). Defaults plus any
    /// locally approved additions once that receipt protocol exists. Today this is
    /// intentionally the immutable defaults-only set.</summary>
    public static IReadOnlyDictionary<string, string> ProcessNames => Defaults;

    /// <summary>
    /// Core/Helper shared declaration check.  The immutable protected-process
    /// classifier runs before the mutable allowlist so even a corrupt/legacy
    /// config cannot turn a PMS, browser, Office app, or shell into a sandbox.
    /// </summary>
    public static bool IsDeclaredSandboxProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            ProtectedDesktopProcessClassifier.IsProtectedIdentity(processName))
        {
            return false;
        }

        return Defaults.Keys.Concat(Defaults.Values)
            .Any(allowed => string.Equals(allowed, processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Legacy compatibility entry point. Additions are ignored until SuavoAgent has
    /// a workstation-local, cryptographically bound physical-approval receipt. A
    /// signed cloud command or a pre-upgrade <c>actuation.json</c> is not proof that
    /// a human approved a process on this exact workstation.
    /// </summary>
    public static void ExtendAllowlist(IReadOnlyDictionary<string, string>? additions)
    {
        _ = additions;
    }

    /// <summary>
    /// Legacy startup hook. Deliberately ignores every <c>AllowedApps</c> entry,
    /// including a valid-looking file left by an older remotely-writable release.
    /// Defaults-only remains in force until a physical-approval receipt is built.
    /// </summary>
    public static void LoadAndExtendFromConfig(string? programDataDir = null)
    {
        _ = programDataDir;
    }
}
