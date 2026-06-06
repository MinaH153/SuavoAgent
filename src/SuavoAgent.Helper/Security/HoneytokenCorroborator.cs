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
/// The SAFETY CRUX of the immune reflex. A bare honeytoken touch is NEVER instant apoptosis — this maps
/// (who touched it, how often) onto the graduated ladder so a misjudged backup/AV/indexer touch lands on
/// reversible <see cref="CorroborationLevel.Degrade"/> (agent goes read-only for one Helper-restart, never
/// bricks), while a sensitive interactive process (powershell/cmd/…) or a repeat escalates to
/// <see cref="CorroborationLevel.Apoptosis"/>. Pure + deterministic; the watcher wraps every call fail-OPEN
/// (treat an exception as Observe) so a bug here can never quarantine a live pharmacy.
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
    // these is OBSERVE-only — they must NEVER trip a degrade on a live pharmacy (this is the #1 false-
    // positive guard: routine backup / AV / search-index / cloud-sync activity).
    private static readonly HashSet<string> SystemAllowlist = new(StringComparer.OrdinalIgnoreCase)
        { "SearchIndexer", "SearchProtocolHost", "SearchFilterHost",
          "MsMpEng", "MpDefenderCoreService", "NisSrv",        // Windows Defender
          "wbengine", "vssadmin", "vssvc",                     // Windows Backup / Volume Shadow Copy
          "OneDrive", "Dropbox", "GoogleDriveFS",              // cloud sync
          "TiWorker", "TrustedInstaller" };                    // Windows servicing

    // Interactive script/shell hosts — the signature of a hands-on-keyboard attacker poking the decoy.
    // A single touch by one of these is enough to corroborate compromise (straight to apoptosis).
    private static readonly HashSet<string> SensitiveDenylist = new(StringComparer.OrdinalIgnoreCase)
        { "powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
          "wmic", "bitsadmin", "certutil" };

    /// <summary>
    /// Classify a honeytoken touch. <paramref name="priorTouchCount"/> is how many non-allowlisted touches
    /// have already been seen this window — a repeat by an unknown process escalates to apoptosis.
    /// </summary>
    public CorroborationResult Corroborate(string? processName, string? exePath, int priorTouchCount)
    {
        var name = (processName ?? string.Empty).Trim();

        // 1. Agent's own processes, proven by running from inside the install dir → trusted (Observe).
        //    A process merely NAMED like the agent but running elsewhere is an impostor → not trusted.
        if (AgentProcesses.Contains(name) && InsideInstallDir(exePath))
            return new CorroborationResult(CorroborationLevel.Observe, Label("agent", name));

        // 2. Known system enumerators (backup/AV/indexer/sync) → Observe. The live-pharmacy guard.
        if (SystemAllowlist.Contains(name))
            return new CorroborationResult(CorroborationLevel.Observe, Label("system", name));

        // 3. Sensitive interactive shells/scripts → straight to apoptosis on the first touch.
        if (SensitiveDenylist.Contains(name))
            return new CorroborationResult(CorroborationLevel.Apoptosis, Label("sensitive", name));

        // 4. Anything else (incl. an unresolvable/unknown toucher) is non-allowlisted: first touch DEGRADES
        //    (reversible, never bricks); a repeat escalates to apoptosis.
        return priorTouchCount >= 1
            ? new CorroborationResult(CorroborationLevel.Apoptosis, Label("repeat", name))
            : new CorroborationResult(CorroborationLevel.Degrade, Label("unexpected", name));
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
