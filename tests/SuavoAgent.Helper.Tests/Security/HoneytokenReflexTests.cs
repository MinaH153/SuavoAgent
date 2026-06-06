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
        private readonly FileToucher _t;
        public bool Throw;
        public FakeAttributor(string? name, string? exe) => _t = new FileToucher(name, exe);
        public FileToucher Attribute(string path) => Throw ? throw new InvalidOperationException("boom") : _t;
    }

    private static (ActuationGate gate, HoneytokenReflex reflex, Action<TimeSpan> advance) Build(
        FakeAttributor attributor)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var gate = new ActuationGate(new ActuationConfig { Enabled = true, DryRun = false }, logger);
        var clock = DateTimeOffset.UnixEpoch;
        var reflex = new HoneytokenReflex(
            new HoneytokenCorroborator(InstallDir),
            new ApoptosisOrchestrator(gate),
            attributor,
            now: () => clock,
            dedupWindow: TimeSpan.FromSeconds(1));
        return (gate, reflex, ts => clock = clock.Add(ts));
    }

    private static string? Code(ActuationGate g) => g.CheckOrReject()?.RejectionCode;

    [Fact]
    public void UnknownToucher_SingleTouch_Degrade()
    {
        var (gate, reflex, _) = Build(new FakeAttributor(null, null));
        reflex.OnTouch(@"C:\ProgramData\SuavoAgent\honeytokens\decoy.dat");
        Assert.Equal(ActuationRejectionCodes.GateDisabled, Code(gate)); // reversible degrade, not a latch
    }

    [Fact]
    public void FswStorm_SameWindow_CountsAsOneTouch_DoesNotEscalate()
    {
        // FileSystemWatcher fires several events for one access; the dedup must NOT turn that into a repeat.
        var (gate, reflex, _) = Build(new FakeAttributor("explorer", @"C:\Windows\explorer.exe"));
        reflex.OnTouch("p");
        reflex.OnTouch("p"); // same window
        reflex.OnTouch("p"); // same window
        Assert.Equal(ActuationRejectionCodes.GateDisabled, Code(gate)); // still just DEGRADE, not apoptosis
        Assert.Equal("degrade", gate.Snapshot().CompromiseLevel);
    }

    [Fact]
    public void TwoDistinctTouches_Escalate_ToApoptosis()
    {
        var (gate, reflex, advance) = Build(new FakeAttributor("explorer", @"C:\Windows\explorer.exe"));
        reflex.OnTouch("p");                 // distinct touch #1 → degrade
        Assert.Equal(ActuationRejectionCodes.GateDisabled, Code(gate));
        advance(TimeSpan.FromSeconds(2));    // past the dedup window → a genuinely new access
        reflex.OnTouch("p");                 // distinct touch #2 → apoptosis
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, Code(gate));
        Assert.Equal("apoptosis", gate.Snapshot().CompromiseLevel);
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
