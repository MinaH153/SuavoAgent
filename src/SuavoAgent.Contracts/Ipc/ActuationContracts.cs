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

    /// <summary>Launch an allowlisted sandbox app (Notepad / Calc only). LOW-tier verb.</summary>
    public const string LaunchSandboxApp = "actuation.launch_sandbox_app";
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
    // CompromiseReasonLabel is already PHI-safe (≤32-char safe charset from HoneytokenCorroborator).
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
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

public sealed record PressKeysRequest(
    [property: JsonPropertyName("chords")] IReadOnlyList<string> Chords,
    [property: JsonPropertyName("interChordDelayMs")] int InterChordDelayMs,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
);

public sealed record LaunchSandboxAppRequest(
    [property: JsonPropertyName("appKey")] string AppKey,
    [property: JsonPropertyName("dryRun")] bool DryRun = false
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

    // Built-in safe defaults — ALWAYS present and never removable. A PMS/PHI box gets ONLY these
    // (it never configures additions). The canary/non-PHI box extends this set via local actuation
    // config (operator-authorized apps only).
    private static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Notepad] = "notepad.exe",
            [Calculator] = "calc.exe",
        };

    // Volatile reference so a single startup Configure call is visible to the command threads that
    // follow. Defaults until ExtendAllowlist runs.
    private static volatile IReadOnlyDictionary<string, string> _current =
        new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);

    /// <summary>app_key → process file name (e.g. "notepad" → "notepad.exe"). Defaults plus any
    /// operator-authorized additions. Enforced by every actuation verb + the Helper driver.</summary>
    public static IReadOnlyDictionary<string, string> ProcessNames => _current;

    /// <summary>
    /// Extend the allowlist with operator-authorized apps (canary/non-PHI boxes only — driven by
    /// local <c>actuation.json</c>, never by a PMS box). The built-in Notepad/Calculator defaults are
    /// always retained and cannot be removed or overridden away. Each value must be a bare process
    /// file name ending in <c>.exe</c> (no path) so a malformed entry can't smuggle an arbitrary
    /// executable path. Call once at process startup, in BOTH Core and Helper, before commands flow.
    /// </summary>
    public static void ExtendAllowlist(IReadOnlyDictionary<string, string>? additions)
    {
        var next = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (additions is not null)
        {
            foreach (var kv in additions)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                var appKey = kv.Key.Trim();
                // The app_key is ALSO used by the click verbs as a process-name match token, so restrict
                // it to a plain identifier — a config key can't introduce a surprising process target or
                // smuggle path/format chars.
                if (!System.Text.RegularExpressions.Regex.IsMatch(appKey, "^[a-zA-Z0-9_-]{1,64}$")) continue;
                var proc = kv.Value.Trim();
                // Bare process file name only — reject paths/separators/wildcards.
                if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (proc.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) continue;
                if (Defaults.ContainsKey(appKey)) continue; // never override a default
                next[appKey] = proc;
            }
        }
        _current = next;
    }

    /// <summary>
    /// Read operator-authorized app additions from <c>%PROGRAMDATA%\SuavoAgent\actuation.json</c>
    /// (object key <c>AllowedApps</c>: <c>{ "app_key": "process.exe" }</c>) and apply them via
    /// <see cref="ExtendAllowlist"/>. Safe no-op when the file or section is absent/malformed — the
    /// built-in Notepad/Calculator defaults always remain. MUST be called once at process startup in
    /// BOTH Core and Helper so the verb pre-check (Core) and the driver's authoritative check (Helper)
    /// agree; if only one applies it, a launch fails closed in the other. A PMS/PHI box simply has no
    /// <c>AllowedApps</c> in its config, so it stays defaults-only.
    /// </summary>
    public static void LoadAndExtendFromConfig(string? programDataDir = null)
    {
        try
        {
            var dir = programDataDir
                ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
            var path = System.IO.Path.Combine(dir, "SuavoAgent", "actuation.json");
            if (!System.IO.File.Exists(path)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("AllowedApps", out var apps)
                || apps.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in apps.EnumerateObject())
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    additions[p.Name] = p.Value.GetString()!;
            ExtendAllowlist(additions);
        }
        catch
        {
            // Best-effort: a malformed config must never widen the allowlist nor crash startup —
            // the built-in defaults remain in force.
        }
    }
}
