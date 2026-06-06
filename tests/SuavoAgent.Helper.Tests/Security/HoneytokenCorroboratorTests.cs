using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

/// <summary>
/// The corroboration ladder is the SAFETY CRUX of the immune reflex: a bare honeytoken touch must NEVER
/// jump to latched apoptosis — only a sensitive interactive process (or a repeat) does, and a misjudged
/// backup/AV/indexer touch lands on reversible DEGRADE. Pure + fully testable; the watcher wraps it
/// fail-OPEN so a corroborator bug can never brick a pharmacy.
/// </summary>
public sealed class HoneytokenCorroboratorTests
{
    private const string InstallDir = @"C:\Program Files\SuavoAgent";

    private static readonly HoneytokenCorroborator Corroborator = new(InstallDir);

    // --- Allowlist → OBSERVE (zero gate change) ------------------------------

    [Theory]
    [InlineData("SuavoAgent.Helper", @"C:\Program Files\SuavoAgent\SuavoAgent.Helper.exe")]
    [InlineData("SuavoAgent.Broker", @"C:\Program Files\SuavoAgent\SuavoAgent.Broker.exe")]
    [InlineData("SuavoAgent.Core", @"C:\Program Files\SuavoAgent\SuavoAgent.Core.exe")]
    [InlineData("SuavoAgent.Watchdog", @"C:\Program Files\SuavoAgent\SuavoAgent.Watchdog.exe")]
    public void AgentProcessInsideInstallDir_Observe(string name, string exe)
    {
        var r = Corroborator.Corroborate(name, exe, priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Observe, r.Level);
    }

    [Theory]
    [InlineData("SearchIndexer")]
    [InlineData("MsMpEng")]      // Windows Defender
    [InlineData("wbengine")]     // Windows Backup
    [InlineData("vssadmin")]     // Volume Shadow Copy
    [InlineData("OneDrive")]
    public void SystemProcess_Observe(string name)
    {
        var r = Corroborator.Corroborate(name, exePath: $@"C:\Windows\System32\{name}.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Observe, r.Level);
    }

    [Fact]
    public void AgentNameButOutsideInstallDir_NotTrusted_Degrade()
    {
        // A process NAMED like the agent but running from elsewhere is an impostor → not allowlisted.
        var r = Corroborator.Corroborate("SuavoAgent.Helper", @"C:\Temp\SuavoAgent.Helper.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
    }

    // --- Non-allowlisted, non-sensitive → DEGRADE (reversible) ---------------

    [Fact]
    public void UnknownProcess_FirstTouch_Degrade()
    {
        var r = Corroborator.Corroborate("explorer", @"C:\Windows\explorer.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
    }

    [Fact]
    public void UnresolvableProcess_Degrade_NotObserve()
    {
        // FSW can't always resolve the PID→name; an UNKNOWN toucher is non-allowlisted → reversible degrade,
        // never observe (observe is only for KNOWN-safe), never instant apoptosis.
        var r = Corroborator.Corroborate(processName: "", exePath: null, priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
    }

    // --- Escalation → APOPTOSIS (denylist shells ONLY — see HoneytokenCorroboratorNeverLatchTests) -----

    [Fact]
    public void UnknownProcess_Repeat_StillDegrade_NeverLatches()
    {
        // CHANGED (was UnknownProcess_Repeat_Apoptosis): a resolved-but-not-shell name no longer escalates on
        // repeat — that rule bricked live pharmacies (mis-attributed nightly backup → apoptosis on day 2).
        // priorTouchCount is now inert for non-shell names; the comprehensive proof lives in the never-latch suite.
        var r = Corroborator.Corroborate("explorer", @"C:\Windows\explorer.exe", priorTouchCount: 1);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("wscript")]
    [InlineData("cscript")]
    [InlineData("mshta")]
    public void SensitiveInteractiveProcess_Apoptosis_OnFirstTouch(string name)
    {
        var r = Corroborator.Corroborate(name, $@"C:\Windows\System32\{name}.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Apoptosis, r.Level);
    }

    // --- PHI-safe reason label (always) -------------------------------------

    [Theory]
    [InlineData("powershell", @"C:\x\powershell.exe", 0)]
    [InlineData("explorer", @"C:\x\explorer.exe", 0)]
    [InlineData("a name with spaces & symbols!", null, 0)] // hostile/odd name
    [InlineData("SuavoAgent.Helper", @"C:\Program Files\SuavoAgent\SuavoAgent.Helper.exe", 0)]
    public void ReasonLabel_AlwaysPhiSafe(string name, string? exe, int prior)
    {
        var r = Corroborator.Corroborate(name, exe, prior);
        Assert.NotNull(r.ReasonLabel);
        Assert.True(r.ReasonLabel.Length <= 32, $"label too long: {r.ReasonLabel}");
        // Locked to lowercase: SafeName always lowercases, so the test charset must be lowercase-only —
        // a permissive [A-Za-z] would silently accept an uppercase regression (HIPAA §164.312(b) audit).
        Assert.Matches("^[a-z0-9._-]+$", r.ReasonLabel); // safe charset only — no spaces/contents/PHI
    }
}
