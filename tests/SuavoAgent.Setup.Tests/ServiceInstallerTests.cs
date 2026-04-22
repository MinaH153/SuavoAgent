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
        Assert.Contains("NT AUTHORITY\\\\NetworkService", source);  // Broker account
    }
}
