using SuavoAgent.Helper.Security;
using SuavoAgent.Contracts.Models;
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

    private static readonly HoneytokenCorroborator Corroborator = new(
        InstallDir,
        [@"C:\Windows\System32"],
        _ => true);

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
        Assert.Equal(HoneytokenReasonLabels.AgentProcess, r.ReasonLabel);
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
        Assert.Equal(HoneytokenReasonLabels.SystemProcess, r.ReasonLabel);
    }

    [Fact]
    public void AgentNameButOutsideInstallDir_NotTrusted_Degrade()
    {
        // A process NAMED like the agent but running from elsewhere is an impostor → not allowlisted.
        var r = Corroborator.Corroborate("SuavoAgent.Helper", @"C:\Temp\SuavoAgent.Helper.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
        Assert.Equal(HoneytokenReasonLabels.UnexpectedProcess, r.ReasonLabel);
    }

    // --- Non-allowlisted, non-sensitive → DEGRADE (reversible) ---------------

    [Fact]
    public void UnknownProcess_FirstTouch_Degrade()
    {
        var r = Corroborator.Corroborate("explorer", @"C:\Windows\explorer.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
        Assert.Equal(HoneytokenReasonLabels.UnexpectedProcess, r.ReasonLabel);
    }

    [Fact]
    public void UnresolvableProcess_Degrade_NotObserve()
    {
        // FSW can't always resolve the PID→name; an UNKNOWN toucher is non-allowlisted → reversible degrade,
        // never observe (observe is only for KNOWN-safe), never instant apoptosis.
        var r = Corroborator.Corroborate(processName: "", exePath: null, priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
        Assert.Equal(HoneytokenReasonLabels.UnknownProcess, r.ReasonLabel);
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
        Assert.Equal(HoneytokenReasonLabels.SensitiveShell, r.ReasonLabel);
    }

    // --- Fixed PHI-negative reason label (always) --------------------------

    [Fact]
    public void PatientNamedExecutable_NeverEntersReasonLabel()
    {
        const string patientLikeProcess = "Jane_Doe_01-15-1990";

        var r = Corroborator.Corroborate(
            patientLikeProcess,
            @"C:\Temp\Jane_Doe_01-15-1990.exe",
            priorTouchCount: 0);

        Assert.Equal(HoneytokenReasonLabels.UnexpectedProcess, r.ReasonLabel);
        Assert.DoesNotContain("jane", r.ReasonLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1990", r.ReasonLabel, StringComparison.Ordinal);
    }
}
