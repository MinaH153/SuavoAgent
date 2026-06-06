using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

/// <summary>
/// THE precedence-1 proof: the honeytoken kill switch is structurally unreachable except from a resolved
/// interactive-shell name. An adversarial design review found the original ladder bricked a live pharmacy:
/// RestartManager (the planned attributor) resolves whoever holds the decoy open AT QUERY TIME — often a
/// benign system process (VSS / dllhost COM surrogate / Defender MpCmdRun / renamed 3rd-party EDR/backup),
/// NOT the event's true cause. Under the old rule a resolved-but-not-allowlisted name Degraded on touch #1
/// and APOPTOSED on touch #2; because the reflex's touch counter never decayed, a nightly backup bricked the
/// box on ~day 2. The fix makes Apoptosis reachable ONLY from <see cref="HoneytokenCorroborator"/>'s explicit
/// SensitiveDenylist (shells/scripts) — every other toucher, resolved or unknown, known or not, can only ever
/// reach reversible Degrade no matter how many times it repeats. These cases lock that invariant.
/// </summary>
public sealed class HoneytokenCorroboratorNeverLatchTests
{
    private const string InstallDir = @"C:\Program Files\SuavoAgent";
    private static readonly HoneytokenCorroborator Corroborator = new(InstallDir);

    // --- The invariant: a NON-shell toucher NEVER latches, no matter the repeat count ---------

    [Theory]
    [InlineData("")]            // unresolvable / blank — the read-and-close race
    [InlineData("explorer")]    // resolved, benign, non-allowlisted (a curious user / GUI access)
    [InlineData("AcmeEdrAgent")] // a 3rd-party EDR not (yet) on the per-site allowlist
    [InlineData("Veeam.Backup.Agent")] // a renamed 3rd-party backup not (yet) on the allowlist
    public void NonShellToucher_NeverApoptosis_AtAnyRepeatCount(string name)
    {
        // 0, 1, 2, … 50 repeats — the kill switch must remain unreachable for a non-shell name.
        for (var prior = 0; prior <= 50; prior++)
        {
            var r = Corroborator.Corroborate(name, exePath: null, priorTouchCount: prior);
            Assert.Equal(CorroborationLevel.Degrade, r.Level);
        }
    }

    [Fact]
    public void BlankToucher_FirstTouch_Degrade()
    {
        var r = Corroborator.Corroborate(processName: "", exePath: null, priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Degrade, r.Level);
    }

    // --- The genuine-attacker signal IS preserved: a resolved shell still apoptoses ----------

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("wscript")]
    [InlineData("cscript")]
    [InlineData("mshta")]
    public void ResolvedShell_FirstTouch_StillApoptosis(string shell)
    {
        var r = Corroborator.Corroborate(shell, $@"C:\Windows\System32\{shell}.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Apoptosis, r.Level);
    }

    [Fact]
    public void ResolvedShell_IsTheONLYApoptosisPath_EvenOnFirstTouch()
    {
        // A shell apoptoses on touch #1 (priorTouchCount 0) — the latch does not depend on repeats at all.
        Assert.Equal(CorroborationLevel.Apoptosis,
            Corroborator.Corroborate("cmd", @"C:\Windows\System32\cmd.exe", priorTouchCount: 0).Level);
    }

    // --- Known-safe touchers stay Observe (no response) -------------------------------------

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("MpCmdRun")]       // M4: Defender on-demand scan host (distinct from MsMpEng)
    [InlineData("SearchIndexer")]
    [InlineData("wbengine")]
    [InlineData("vssvc")]
    [InlineData("OneDrive")]
    [InlineData("svchost")]        // M4 expansion: generic service host
    [InlineData("dllhost")]        // M4 expansion: COM surrogate (thumbnail/preview) — benign noise → Observe
    [InlineData("RuntimeBroker")]  // M4 expansion
    public void KnownSystemToucher_Observe(string name)
    {
        var r = Corroborator.Corroborate(name, exePath: $@"C:\Windows\System32\{name}.exe", priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Observe, r.Level);
    }

    [Fact]
    public void AgentInsideInstallDir_Observe_ButImpostorOutside_Degrades_NeverLatches()
    {
        Assert.Equal(CorroborationLevel.Observe,
            Corroborator.Corroborate("SuavoAgent.Helper", @"C:\Program Files\SuavoAgent\SuavoAgent.Helper.exe", 0).Level);
        // An impostor NAMED like the agent but running elsewhere is non-allowlisted → Degrade, and stays
        // Degrade on repeat (never latches — it is not a shell).
        Assert.Equal(CorroborationLevel.Degrade,
            Corroborator.Corroborate("SuavoAgent.Helper", @"C:\Temp\SuavoAgent.Helper.exe", 5).Level);
    }
}
