using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Generic;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Diagnostics.Maintenance;

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

/// <summary>The corroboration verdict plus one fixed PHI-negative reason category.</summary>
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
    private readonly ImmutableArray<string> _trustedSystemRoots;
    private readonly Func<string, bool> _isTrustedSystemPublisher;

    public HoneytokenCorroborator(string installDir)
        : this(
            installDir,
            ResolveTrustedSystemRoots(),
            executablePath => AuthenticodePublisherVerifier.VerifyPublisher(
                executablePath,
                PrivilegedExecutableStaging.MicrosoftPublisher).IsTrusted)
    {
    }

    internal HoneytokenCorroborator(
        string installDir,
        IEnumerable<string> trustedSystemRoots,
        Func<string, bool> isTrustedSystemPublisher)
    {
        var trimmed = (installDir ?? string.Empty).TrimEnd('\\', '/');
        var sep = trimmed.Contains('\\') ? '\\' : System.IO.Path.DirectorySeparatorChar;
        _installDirWithSep = trimmed.Length == 0 ? string.Empty : trimmed + sep;
        _trustedSystemRoots = (trustedSystemRoots ?? Array.Empty<string>())
            .Take(32)
            .Select(root => TryNormalizeWindowsPath(root, out var normalized) ? normalized : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        _isTrustedSystemPublisher = isTrustedSystemPublisher
            ?? throw new ArgumentNullException(nameof(isTrustedSystemPublisher));
    }

    private static readonly FrozenSet<string> AgentProcesses = new[]
        { "SuavoAgent.Helper", "SuavoAgent.Broker", "SuavoAgent.Core", "SuavoAgent.Watchdog" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // A system process is Observe-only only when three independent facts agree: allowlisted process name,
    // exact matching executable filename beneath a protected Windows/Microsoft root, and a valid Microsoft
    // Authenticode publisher. Name-only, path-only, missing-path, and unsigned lookalikes fall through to
    // reversible Degrade. Third-party sync/EDR/backup publishers are deliberately not enrolled here.
    private static readonly FrozenSet<string> SystemAllowlist = new[]
        { "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "SearchApp",
          "MsMpEng", "MpCmdRun", "MpDefenderCoreService", "NisSrv", "smartscreen", "SgrmBroker", // Defender / SmartScreen
          "wbengine", "vssadmin", "vssvc",                     // Windows Backup / Volume Shadow Copy
          "OneDrive",                                           // Microsoft cloud sync
          "TiWorker", "TrustedInstaller",                      // Windows servicing
          "svchost", "dllhost", "RuntimeBroker", "taskhostw",  // generic OS service / COM-surrogate hosts
          "sihost", "backgroundTaskHost", "SysMain", "prefetch" } // shell / superfetch / prefetch
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Interactive script/shell hosts — the signature of a hands-on-keyboard attacker poking the decoy.
    // A single touch by one of these is enough to corroborate compromise (straight to apoptosis).
    private static readonly FrozenSet<string> SensitiveDenylist = new[]
        { "powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
          "wmic", "bitsadmin", "certutil" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            return new CorroborationResult(
                CorroborationLevel.Observe,
                HoneytokenReasonLabels.AgentProcess);

        // 2. Signed Microsoft system enumerators in protected roots → Observe. A spoofed name/path,
        //    missing attribution, or failed publisher verification is unknown trust and degrades instead.
        if (SystemAllowlist.Contains(name) && IsTrustedSystemProcess(name, exePath))
            return new CorroborationResult(
                CorroborationLevel.Observe,
                HoneytokenReasonLabels.SystemProcess);

        // 3. Sensitive interactive shells/scripts → the ONLY honeytoken path to a latched kill switch.
        //    A resolved shell holding the decoy open is the one signal strong enough to justify apoptosis.
        if (SensitiveDenylist.Contains(name))
            return new CorroborationResult(
                CorroborationLevel.Apoptosis,
                HoneytokenReasonLabels.SensitiveShell);

        // 4. Anything else — unknown, unresolvable, or a resolved-but-not-shell name — is reversible DEGRADE,
        //    ALWAYS, no matter how often it repeats. This is the never-brick floor: a benign mis-attribution
        //    (RestartManager naming a VSS/COM-surrogate/EDR holder rather than the true cause) or a backup
        //    scan touching the decoy nightly can only ever take actuation read-only (self-heals on the next
        //    Helper restart) + raise a cloud compromise alarm — it can NEVER latch the kill switch.
        return new CorroborationResult(
            CorroborationLevel.Degrade,
            string.IsNullOrWhiteSpace(name)
                ? HoneytokenReasonLabels.UnknownProcess
                : HoneytokenReasonLabels.UnexpectedProcess);
    }

    private bool InsideInstallDir(string? exePath)
        => !string.IsNullOrWhiteSpace(exePath)
           && _installDirWithSep.Length > 0
           && exePath.StartsWith(_installDirWithSep, StringComparison.OrdinalIgnoreCase);

    private bool IsTrustedSystemProcess(string processName, string? exePath)
    {
        if (!TryNormalizeWindowsPath(exePath, out var normalizedPath))
            return false;

        var finalSeparator = normalizedPath.LastIndexOf('\\');
        if (finalSeparator < 3 || !string.Equals(
                normalizedPath[(finalSeparator + 1)..],
                processName + ".exe",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var isUnderTrustedRoot = _trustedSystemRoots.Any(root =>
            normalizedPath.Length > root.Length + 1 &&
            normalizedPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
        if (!isUnderTrustedRoot)
            return false;

        try
        {
            return _isTrustedSystemPublisher(normalizedPath);
        }
        catch (Exception)
        {
            // Publisher verification is a trust boundary. Any verifier failure is untrusted and therefore
            // reversible Degrade; it must never bubble into the watcher's broader fail-open exception path.
            return false;
        }
    }

    private static ImmutableArray<string> ResolveTrustedSystemRoots()
    {
        var windows = Environment.GetEnvironmentVariable("WINDIR");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new[]
            {
                CombineWindowsPath(windows, "System32"),
                CombineWindowsPath(windows, "SysWOW64"),
                CombineWindowsPath(windows, "servicing"),
                CombineWindowsPath(windows, "WinSxS"),
                CombineWindowsPath(programData, "Microsoft", "Windows Defender"),
                CombineWindowsPath(programFiles, "Windows Defender"),
                CombineWindowsPath(programFiles, "Microsoft OneDrive"),
                CombineWindowsPath(programFilesX86, "Microsoft OneDrive"),
                CombineWindowsPath(localAppData, "Microsoft", "OneDrive"),
            }
            .Where(path => path is not null)
            .Select(path => path!)
            .ToImmutableArray();
    }

    private static string? CombineWindowsPath(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        return root.TrimEnd('\\', '/') + "\\" + string.Join("\\", segments);
    }

    private static bool TryNormalizeWindowsPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().Replace('/', '\\').TrimEnd('\\');
        if (candidate.Length is < 4 or > 1_024 ||
            !char.IsAsciiLetter(candidate[0]) ||
            candidate[1] != ':' ||
            candidate[2] != '\\' ||
            candidate.IndexOf(':', 2) >= 0 ||
            candidate.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 ||
            candidate.Contains("\\\\", StringComparison.Ordinal))
            return false;

        var segments = candidate[3..].Split('\\', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            return false;

        normalized = char.ToUpperInvariant(candidate[0]) + candidate[1..];
        return true;
    }

}
