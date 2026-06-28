using System.Reflection;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Regression guards for <see cref="ServiceInstaller"/>. The installer is a
/// static class that shells out to <c>sc.exe</c>, so end-to-end behaviour can
/// only be verified on Windows with admin rights — these tests instead assert
/// the internal shape (which services are installed, which SCM recovery policy
/// is applied) so that nobody silently drops a service when editing the class.
/// </summary>
public class ServiceInstallerTests
{
    private static string? GetConstant(string name)
    {
        var field = typeof(ServiceInstaller).GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        return field?.GetRawConstantValue() as string;
    }

    [Fact]
    public void Installer_Registers_Core_Broker_And_Watchdog()
    {
        // Watchdog was missing from the GUI installer path until 2026-04-22.
        // Keep this test as a permanent regression guard — any rename or
        // removal of the constant fails here, not in the field.
        Assert.Equal("SuavoAgent.Core", GetConstant("CoreServiceName"));
        Assert.Equal("SuavoAgent.Broker", GetConstant("BrokerServiceName"));
        Assert.Equal("SuavoAgent.Watchdog", GetConstant("WatchdogServiceName"));
    }

    [Fact]
    public void Installer_Source_Registers_Watchdog_With_Longer_Recovery_Windows()
    {
        // The source-text guard catches "constants exist but sc.exe failure
        // was never wired" regressions without needing a Windows runner.
        // bootstrap.ps1 uses 10s/60s/5min for Watchdog (vs 5s/30s/60s for
        // Core/Broker) because Watchdog churn would mask real issues.
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup", "ServiceInstaller.cs");
        var source = File.Exists(sourcePath)
            ? File.ReadAllText(sourcePath)
            : string.Empty;

        // Skip the assertion if the source file isn't resolvable from this
        // runner — the reflection test above is the authoritative guard.
        if (source.Length == 0) return;

        Assert.Contains("restart/10000/restart/60000/restart/300000", source);
        Assert.Contains("LocalSystem", source);  // Watchdog account
        // File text uses escaped backslashes in C# string literals, so the
        // on-disk bytes are "NT AUTHORITY\\LocalService" (two backslashes).
        // In the test source, that's "\\\\LocalService" (four).
        Assert.Contains("NT AUTHORITY\\\\LocalService", source);    // Core account

        // Broker MUST register as LocalSystem. WTSQueryUserToken +
        // CreateProcessAsUser require SeTcbPrivilege, which is held ONLY by
        // LocalSystem. Under NetworkService the privileged launch fails 1314
        // and the Broker silently falls back to launching the Helper in its
        // OWN session (Session 0) — an invisible desktop where the intent
        // cursor, vision capture, and UIA never render. The C# installer
        // regressed to NetworkService (with a false "NetworkService has
        // SeTcbPrivilege" comment) and shipped a Helper that never painted on
        // the pilot box (2026-06-01). install.ps1 + bootstrap.ps1 already
        // register LocalSystem; this keeps all three install paths in sync and
        // guards the regression forever.
        Assert.Matches(@"create \{BrokerServiceName\}.*LocalSystem", source);  // Broker account
        // No service may be REGISTERED as NetworkService (the word may still
        // appear in explanatory comments). Targets the `obj= ...NetworkService`
        // clause specifically — the exact regression we are guarding against.
        Assert.DoesNotMatch(@"obj=[^\n]*NetworkService", source);
    }

    // Regression for the 2026-06-10 Helper crash-loop: LockdownDirectoryAcl strips
    // the data dir to SYSTEM/Admins/LocalService, but the Helper runs de-privileged
    // and died on its first log write — before it could log anything. The carve-out
    // grants BUILTIN\Users (*S-1-5-32-545 — robust vs INTERACTIVE for a UAC-filtered
    // token; the proven principal from bootstrap.ps1): traverse on the root (dir-only,
    // NO inherited file reads — state.db is plaintext PHI, state.key machine-DPAPI),
    // Modify on logs\helper + diagnostics\helper, per-file read on the helper configs.
    [Fact]
    public void Helper_carveout_grants_minimum_and_never_root_file_reads()
    {
        const string users = @"*S-1-5-32-545"; // BUILTIN\Users
        var grants = ServiceInstaller.BuildInteractiveGrantArgs(@"C:\ProgramData\SuavoAgent");

        Assert.Equal(10, grants.Count);

        var root = grants[0];
        Assert.Equal(@"C:\ProgramData\SuavoAgent", root.Target);
        Assert.Equal($"{users}:(RX)", root.Grant);
        // The root grant must NOT inherit to files — (OI) would expose state.db/state.key.
        Assert.DoesNotContain("(OI)", root.Grant);
        Assert.DoesNotContain("(CI)", root.Grant);

        // logs\ root: traverse only — SYSTEM services write here, so the de-privileged
        // user must never gain create/delete (junction-planting EoP).
        var logsRoot = grants[1];
        Assert.EndsWith("logs", logsRoot.Target);
        Assert.Equal($"{users}:(RX)", logsRoot.Grant);

        Assert.Contains(grants, g => g.Target.EndsWith(Path.Combine("logs", "helper")) && g.Grant.Contains("(OI)(CI)(M)") && g.EnsureDir);
        // diagnostics\ root: traverse only (SYSTEM appends events.jsonl there);
        // the Helper's journal gets its own Modify subtree.
        Assert.Contains(grants, g => g.Target.EndsWith("diagnostics") && g.Grant.EndsWith(":(RX)") && g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith(Path.Combine("diagnostics", "helper")) && g.Grant.Contains("(OI)(CI)(M)") && g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith("honeytokens") && g.Grant.Contains("(OI)(CI)(M)") && g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith("vision.json") && g.Grant.EndsWith(":(R)") && !g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith("actuation.json") && g.Grant.EndsWith(":(R)") && !g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith("pioneerrx.json") && g.Grant.EndsWith(":(R)") && !g.EnsureDir);
        Assert.Contains(grants, g => g.Target.EndsWith("honeytoken-attribution.json") && g.Grant.EndsWith(":(R)") && !g.EnsureDir);

        // Every grant goes to BUILTIN\Users — never Everyone/Authenticated Users; the SID form
        // is locale-independent (icacls won't mis-resolve a localized "Users" on a non-en box).
        Assert.All(grants, g => Assert.StartsWith($"{users}:", g.Grant));
    }

    // ParseVersion feeds the ARP VersionMajor/Minor DWORDs. Must tolerate a leading 'v',
    // a -rc/-suffix, and a short/garbage string without throwing.
    [Theory]
    [InlineData("3.77.0", 3, 77)]
    [InlineData("v3.77.0", 3, 77)]
    [InlineData("v3.77.0-rc1", 3, 77)]
    [InlineData("4", 4, 0)]
    [InlineData("", 0, 0)]
    [InlineData("garbage", 0, 0)]
    public void ParseVersion_extracts_major_minor(string input, int major, int minor)
    {
        var (m, n) = ServiceInstaller.ParseVersion(input);
        Assert.Equal(major, m);
        Assert.Equal(minor, n);
    }
}
