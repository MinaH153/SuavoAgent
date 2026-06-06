using System;
using System.Collections.Generic;
using System.Text;

namespace SuavoAgent.Helper.Security;

/// <summary>How strongly a honeytoken touch corroborates a compromise.</summary>
public enum CorroborationLevel
{
    /// <summary>Allowlisted/known-safe toucher — audit only, zero gate change.</summary>
    Observe,
    /// <summary>Non-allowlisted toucher — reversible dry-run/disable (agent stays online, reads continue).</summary>
    Degrade,
    /// <summary>Sensitive interactive process or a repeat — latched kill-switch + LLM flush + fleet-revoke.</summary>
    Apoptosis,
}

/// <summary>The corroboration verdict + a PHI-safe (≤32-char, safe-charset) reason label.</summary>
public readonly record struct CorroborationResult(CorroborationLevel Level, string ReasonLabel);

/// <summary>
/// The SAFETY CRUX of the immune reflex.
///
/// STRUCTURAL INVARIANT (precedence-1, never brick a live pharmacy): the latched kill switch
/// (<see cref="CorroborationLevel.Apoptosis"/>) is reachable from a honeytoken touch ONLY via the explicit
/// <see cref="SensitiveDenylist"/> — a resolved interactive shell/script host (powershell/cmd/…), the one
/// signal strong enough to justify a latch. EVERY other toucher — known-safe, unknown, unresolvable, or a
/// resolved-but-not-shell name — can only ever reach reversible <see cref="CorroborationLevel.Degrade"/>, no
/// matter how many times it repeats. So an attribution gap or a mis-attribution (e.g. a future RestartManager
/// attributor naming whoever holds the decoy open at query time — a VSS snapshot, a dllhost COM surrogate,
/// Defender's MpCmdRun, a renamed 3rd-party EDR/backup — rather than the event's true cause) can degrade
/// actuation (reversible, self-heals on the next Helper restart) but can NEVER latch the kill switch.
///
/// An adversarial design review proved the older "non-allowlisted repeat → apoptosis" rule bricked the box on
/// ~day 2 (a nightly backup resolved to the same name, and the reflex's touch counter never decayed). That
/// rule is removed: <paramref name="Corroborate"/> ignores priorTouchCount for escalation. Re-introducing a
/// repeat path is only safe with a CONFIRMED held-handle + signature-validated exe (a future attributor).
///
/// Pure + deterministic; the watcher wraps every call fail-OPEN (treat an exception as Observe) so a bug here
/// can never quarantine a live pharmacy.
/// </summary>
public sealed class HoneytokenCorroborator
{
    private readonly string _installDirWithSep;

    public HoneytokenCorroborator(string installDir)
    {
        var trimmed = (installDir ?? string.Empty).TrimEnd('\\', '/');
        var sep = trimmed.Contains('\\') ? '\\' : System.IO.Path.DirectorySeparatorChar;
        _installDirWithSep = trimmed.Length == 0 ? string.Empty : trimmed + sep;
    }

    private static readonly HashSet<string> AgentProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "SuavoAgent.Helper", "SuavoAgent.Broker", "SuavoAgent.Core", "SuavoAgent.Watchdog" };

    // System processes that legitimately enumerate/read files across the whole disk. A touch by ANY of
    // these is OBSERVE-only — keeps routine backup / AV / search-index / cloud-sync activity quiet (NO
    // reversible degrade, NO cloud alarm). NOTE: this is a NAME-only allowlist — it is intentionally
    // over-broad for noise reduction, NOT a security boundary (the never-brick guarantee lives in the
    // denylist-only-apoptosis rule, not here). It is therefore spoofable (malware named "MsMpEng" would be
    // Observed — a detection MISS, never a privilege gain); a future attributor that can read the holder's
    // signed exe path should tighten Observe to system-path + Microsoft-publisher. Expanded per the design
    // review with the common Windows background hosts that touch files broadly; the per-pharmacy shadow day
    // adds any site-specific EDR/backup image before arming.
    private static readonly HashSet<string> SystemAllowlist = new(StringComparer.OrdinalIgnoreCase)
        { "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "SearchApp",
          "MsMpEng", "MpCmdRun", "MpDefenderCoreService", "NisSrv", "smartscreen", "SgrmBroker", // Defender / SmartScreen
          "wbengine", "vssadmin", "vssvc",                     // Windows Backup / Volume Shadow Copy
          "OneDrive", "Dropbox", "GoogleDriveFS",              // cloud sync
          "TiWorker", "TrustedInstaller",                      // Windows servicing
          "svchost", "dllhost", "RuntimeBroker", "taskhostw",  // generic OS service / COM-surrogate hosts
          "sihost", "backgroundTaskHost", "SysMain", "prefetch" }; // shell / superfetch / prefetch

    // Interactive script/shell hosts — the signature of a hands-on-keyboard attacker poking the decoy.
    // A single touch by one of these is enough to corroborate compromise (straight to apoptosis).
    private static readonly HashSet<string> SensitiveDenylist = new(StringComparer.OrdinalIgnoreCase)
        { "powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
          "wmic", "bitsadmin", "certutil" };

    /// <summary>
    /// Classify a honeytoken touch onto the graduated ladder. Apoptosis is reachable ONLY from a resolved
    /// <see cref="SensitiveDenylist"/> shell — see the class-level structural invariant.
    /// <paramref name="priorTouchCount"/> is accepted for interface stability + future use but DELIBERATELY
    /// does NOT drive escalation: a repeat by a non-shell process stays reversible Degrade (re-introducing a
    /// repeat→apoptosis path is only safe with a confirmed held-handle + signature-validated exe).
    /// </summary>
    public CorroborationResult Corroborate(string? processName, string? exePath, int priorTouchCount)
    {
        _ = priorTouchCount; // reserved (see summary) — intentionally not used for escalation
        var name = (processName ?? string.Empty).Trim();

        // 1. Agent's own processes, proven by running from inside the install dir → trusted (Observe).
        //    A process merely NAMED like the agent but running elsewhere is an impostor → not trusted.
        if (AgentProcesses.Contains(name) && InsideInstallDir(exePath))
            return new CorroborationResult(CorroborationLevel.Observe, Label("agent", name));

        // 2. Known system enumerators (backup/AV/indexer/sync) → Observe. The live-pharmacy guard.
        if (SystemAllowlist.Contains(name))
            return new CorroborationResult(CorroborationLevel.Observe, Label("system", name));

        // 3. Sensitive interactive shells/scripts → the ONLY honeytoken path to a latched kill switch.
        //    A resolved shell holding the decoy open is the one signal strong enough to justify apoptosis.
        if (SensitiveDenylist.Contains(name))
            return new CorroborationResult(CorroborationLevel.Apoptosis, Label("sensitive", name));

        // 4. Anything else — unknown, unresolvable, or a resolved-but-not-shell name — is reversible DEGRADE,
        //    ALWAYS, no matter how often it repeats. This is the never-brick floor: a benign mis-attribution
        //    (RestartManager naming a VSS/COM-surrogate/EDR holder rather than the true cause) or a backup
        //    scan touching the decoy nightly can only ever take actuation read-only (self-heals on the next
        //    Helper restart) + raise a cloud compromise alarm — it can NEVER latch the kill switch.
        return new CorroborationResult(CorroborationLevel.Degrade, Label("unexpected", name));
    }

    private bool InsideInstallDir(string? exePath)
        => !string.IsNullOrWhiteSpace(exePath)
           && _installDirWithSep.Length > 0
           && exePath.StartsWith(_installDirWithSep, StringComparison.OrdinalIgnoreCase);

    // PHI-SAFE label: "<category>.<sanitized-name>", safe charset only, ≤32 chars. NEVER carries a path or
    // file contents — only the category + the offending process name reduced to [a-z0-9._-].
    private static string Label(string category, string? name)
    {
        var s = category + "." + SafeName(name);
        return s.Length <= 32 ? s : s[..32];
    }

    private static string SafeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (ok) sb.Append(char.ToLowerInvariant(c));
        }
        var s = sb.ToString().Trim('.');
        return s.Length == 0 ? "unknown" : s;
    }
}
