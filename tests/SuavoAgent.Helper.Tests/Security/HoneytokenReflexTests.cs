using System;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

public sealed class HoneytokenReflexTests
{
    private const string InstallDir = @"C:\Program Files\SuavoAgent";

    private sealed class FakeAttributor : IFileAccessAttributor
    {
        public bool Throw;
        public FileToucher Current;                       // mutable so a test can change the toucher mid-run
        public FakeAttributor(string? name, string? exe) => Current = new FileToucher(name, exe);
        public FileToucher Attribute(string path) => Throw ? throw new InvalidOperationException("boom") : Current;
    }

    private static (ActuationGate gate, HoneytokenReflex reflex, Action<TimeSpan> advance) Build(
        FakeAttributor attributor, TimeSpan? retention = null)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var gate = new ActuationGate(new ActuationConfig { Enabled = true, DryRun = false }, logger);
        var clock = DateTimeOffset.UnixEpoch;
        var reflex = new HoneytokenReflex(
            new HoneytokenCorroborator(
                InstallDir,
                [
                    @"C:\Windows\System32",
                    @"C:\ProgramData\Microsoft\Windows Defender",
                ],
                _ => true),
            new ApoptosisOrchestrator(gate),
            attributor,
            now: () => clock,
            dedupWindow: TimeSpan.FromSeconds(1),
            retention: retention);
        return (gate, reflex, ts => clock = clock.Add(ts));
    }

    private static string? Code(ActuationGate g) => g.CheckOrReject()?.RejectionCode;

    [Fact]
    public void UnknownToucher_SingleTouch_Degrade()
    {
        var (gate, reflex, _) = Build(new FakeAttributor(null, null));
        reflex.OnTouch(@"C:\ProgramData\SuavoAgent\honeytokens\decoy.dat");
        Assert.Equal(ActuationRejectionCodes.CompromiseDetected, Code(gate));
        Assert.Null(gate.Snapshot().KillSwitchTrippedUtc); // reversible degrade, not a kill-switch latch
    }

    [Fact]
    public void FswStorm_SameWindow_CountsAsOneTouch_DoesNotEscalate()
    {
        // FileSystemWatcher fires several events for one access; the dedup must NOT turn that into a repeat.
        var (gate, reflex, _) = Build(new FakeAttributor("explorer", @"C:\Windows\explorer.exe"));
        reflex.OnTouch("p");
        reflex.OnTouch("p"); // same window
        reflex.OnTouch("p"); // same window
        Assert.Equal(ActuationRejectionCodes.CompromiseDetected, Code(gate)); // still DEGRADE, not apoptosis
        Assert.Equal("degrade", gate.Snapshot().CompromiseLevel);
    }

    [Fact]
    public void TwoDistinctNonShellTouches_StayDegrade_NeverLatch()
    {
        // CHANGED (was TwoDistinctTouches_Escalate_ToApoptosis): a resolved-but-not-shell process repeating
        // must NEVER reach the latched kill switch — that escalation bricked live pharmacies. Both touches
        // land on reversible Degrade; the gate is disabled (recoverable) but the kill switch never trips.
        var (gate, reflex, advance) = Build(new FakeAttributor("explorer", @"C:\Windows\explorer.exe"));
        reflex.OnTouch("p");                 // distinct touch #1 → degrade
        Assert.Equal(ActuationRejectionCodes.CompromiseDetected, Code(gate));
        advance(TimeSpan.FromSeconds(2));    // past the dedup window → a genuinely new access
        reflex.OnTouch("p");                 // distinct touch #2 → STILL degrade (no escalation)
        Assert.Equal(ActuationRejectionCodes.CompromiseDetected, Code(gate));
        Assert.Equal("degrade", gate.Snapshot().CompromiseLevel);
        Assert.Null(gate.Snapshot().KillSwitchTrippedUtc);
        Assert.NotEqual(ActuationRejectionCodes.KillSwitchTripped, Code(gate));
    }

    [Fact]
    public void StaleTouchers_AreEvicted_AfterRetentionWindow()
    {
        // M2 hygiene: a toucher's count must not accrue forever. After the retention window, a prior toucher
        // is evicted so the map can't grow unbounded and a count can never sum across hours/days.
        var attr = new FakeAttributor("procA", @"C:\x\procA.exe");
        var (_, reflex, advance) = Build(attr, retention: TimeSpan.FromMinutes(10));
        reflex.OnTouch("p");                          // tracks procA
        Assert.Equal(1, reflex.TrackedToucherCount);
        advance(TimeSpan.FromMinutes(11));            // procA now older than retention
        attr.Current = new FileToucher("procB", @"C:\x\procB.exe");
        reflex.OnTouch("p");                          // eviction runs first → procA dropped, procB tracked
        Assert.Equal(1, reflex.TrackedToucherCount);  // would be 2 without eviction
    }

    [Fact]
    public void SensitiveProcess_FirstTouch_Apoptosis()
    {
        var (gate, reflex, _) = Build(new FakeAttributor("powershell", @"C:\Windows\System32\powershell.exe"));
        reflex.OnTouch("p");
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, Code(gate));
    }

    [Fact]
    public void SystemProcess_Observe_GateStaysOpen()
    {
        var (gate, reflex, _) = Build(new FakeAttributor("MsMpEng", @"C:\ProgramData\Microsoft\Windows Defender\MsMpEng.exe"));
        reflex.OnTouch("p");
        Assert.Null(Code(gate)); // observe — no gate change
        Assert.False(gate.Snapshot().CompromiseDetected);
    }

    [Fact]
    public void AttributorThrows_FailOpen_NoGateChange_NoThrow()
    {
        var attr = new FakeAttributor("powershell", "x") { Throw = true };
        var (gate, reflex, _) = Build(attr);
        var ex = Record.Exception(() => reflex.OnTouch("p"));
        Assert.Null(ex);          // fail-open: never throws
        Assert.Null(Code(gate));  // gate untouched — a reflex bug must not brick a pharmacy
    }
}
