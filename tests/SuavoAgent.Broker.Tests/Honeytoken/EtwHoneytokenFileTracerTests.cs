using SuavoAgent.Broker.Honeytoken;
using Xunit;

namespace SuavoAgent.Broker.Tests.Honeytoken;

/// <summary>
/// The tracer's pure, headless-testable cores: the PHI-wall decoy filter (empty-prefix safe, sibling-dir
/// guarded), the denylist-format name derivation, and the never-blind self-test. The live ETW session itself
/// is the only OS-bound part and is covered by the windows smoke (continue-on-error CI).
/// </summary>
public sealed class EtwHoneytokenFileTracerTests
{
    private const string Prefix = @"\Device\HarddiskVolume3\ProgramData\SuavoAgent\honeytokens";

    // --- MatchesDecoy: the PHI wall ------------------------------------------

    [Fact]
    public void MatchesDecoy_FileDirectlyUnderDecoyDir_True()
        => Assert.True(EtwHoneytokenFileTracer.MatchesDecoy(Prefix + @"\backup_credentials.dat", Prefix));

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\ProgramData\Other\patient-rx.pdf")] // unrelated (PHI) path
    [InlineData(@"\Device\HarddiskVolume3\ProgramData\SuavoAgent\honeytokens-evil\x")] // sibling-dir false match
    [InlineData(@"\Device\HarddiskVolume3\ProgramData\SuavoAgent\honeytokens")] // the dir itself (no child)
    public void MatchesDecoy_NonDecoyPaths_False(string ntPath)
        => Assert.False(EtwHoneytokenFileTracer.MatchesDecoy(ntPath, Prefix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MatchesDecoy_EmptyPrefix_MatchesNothing_NeverAll(string? prefix)
    {
        // The inversion footgun: an empty prefix must match NOTHING (a naive StartsWith("") would match every
        // file open → the whole-box PHI firehose into resolve+write).
        Assert.False(EtwHoneytokenFileTracer.MatchesDecoy(@"\Device\HarddiskVolume3\anything\at\all.txt", prefix));
        Assert.False(EtwHoneytokenFileTracer.MatchesDecoy(Prefix + @"\backup_credentials.dat", prefix));
    }

    // --- DeriveProcessName: denylist-format ----------------------------------

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell")]
    [InlineData("powershell.exe", "powershell")]
    [InlineData(@"C:\Program Files\SuavoAgent\SuavoAgent.Helper.exe", "suavoagent.helper")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void DeriveProcessName_ReducesToBareLowercaseName(string? exe, string? expected)
        => Assert.Equal(expected, EtwHoneytokenFileTracer.DeriveProcessName(exe));

    // --- SelfTestPrefix: never-blind -----------------------------------------

    [Fact]
    public void SelfTestPrefix_ValidPrefix_Passes()
    {
        Assert.True(EtwHoneytokenFileTracer.SelfTestPrefix(Prefix, "backup_credentials.dat", out var reason));
        Assert.Equal("ok", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelfTestPrefix_EmptyPrefix_FailsLoud(string? prefix)
    {
        Assert.False(EtwHoneytokenFileTracer.SelfTestPrefix(prefix, "backup_credentials.dat", out var reason));
        Assert.Contains("NT device", reason);
    }

    [Fact]
    public void SelfTestPrefix_SynthPathMustMatchOwnPrefix()
    {
        // Sanity: the prefix the self-test resolves must pass the very filter production uses (catches a
        // normalization/prefix bug at startup, not silently at runtime).
        Assert.True(EtwHoneytokenFileTracer.SelfTestPrefix(Prefix, "backup_credentials.dat", out _));
        var synth = Prefix + @"\backup_credentials.dat";
        Assert.True(EtwHoneytokenFileTracer.MatchesDecoy(synth, Prefix));
    }

    // --- PID-reuse guard (never-brick): a recycled PID's process started AFTER the event ---------

    [Fact]
    public void IsPlausibleIssuer_StartedBeforeEvent_True()
    {
        var ev = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.True(ProcessImageInterop.IsPlausibleIssuer(ev.AddSeconds(-30), ev));
    }

    [Fact]
    public void IsPlausibleIssuer_CreatedAtEvent_True()
    {
        var ev = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.True(ProcessImageInterop.IsPlausibleIssuer(ev, ev)); // created at-or-before is the issuer bound
    }

    [Theory]
    [InlineData(1)]      // 1ms after — NO forward window: a recycled PID created after the event is rejected
    [InlineData(500)]
    [InlineData(5000)]
    public void IsPlausibleIssuer_CreatedAfterEvent_False_ReusedPid(int msAfter)
    {
        // The exploit Codex flagged: a benign PID exits, a powershell is recreated on the same PID. Its
        // creation is strictly AFTER the decoy-open event → must be rejected, with ZERO forward tolerance.
        var ev = DateTimeOffset.UnixEpoch.AddHours(1);
        Assert.False(ProcessImageInterop.IsPlausibleIssuer(ev.AddMilliseconds(msAfter), ev));
    }
}
