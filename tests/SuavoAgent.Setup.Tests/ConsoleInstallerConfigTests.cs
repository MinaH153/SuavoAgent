using System.Collections.Generic;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the no-PMS-first-class contract of the console installer's appsettings builder:
/// PioneerRx/SQL is an OPTIONAL connector, so a no-PMS install must OMIT all Sql* keys (not
/// write null/empty) and stamp PmsDetected=false, while a PMS-present install emits exactly the
/// same Sql* keys it always has. Mirrors the GUI InstallOrchestrator.BuildAppSettings.
/// </summary>
public sealed class ConsoleInstallerConfigTests
{
    private static SetupConfig Cfg() => new(
        PharmacyId: "PH123",
        ApiKey: "sk_test_abc",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.52.0",
        LearningMode: false,
        AgentId: "15c16aae-fa55-49c6-9d4c-971606243b86");

    private static Dictionary<string, object?> Agent(Dictionary<string, object> cfg) =>
        (Dictionary<string, object?>)cfg["Agent"];

    [Fact]
    public void NoPms_OmitsAllSqlKeys_AndFlagsPmsDetectedFalse()
    {
        var cfg = ConsoleInstaller.BuildAgentConfig(Cfg(), "agent-1", "fp-1", pmsDetected: false, sqlCreds: null);
        var agent = Agent(cfg);

        Assert.False(agent.ContainsKey("SqlServer"));
        Assert.False(agent.ContainsKey("SqlDatabase"));
        Assert.False(agent.ContainsKey("SqlUser"));
        Assert.False(agent.ContainsKey("SqlPassword"));
        Assert.Equal(false, agent["PmsDetected"]);
    }

    [Fact]
    public void PmsPresent_SqlAuth_EmitsAllFourSqlKeys_AndFlagsPmsDetectedTrue()
    {
        var sql = new SqlCredentialDiscovery.SqlCredentials("SQLSRV\\PIONEER", "PioneerPharmacySystem", "sa", "p@ss");
        var cfg = ConsoleInstaller.BuildAgentConfig(Cfg(), "agent-1", "fp-1", pmsDetected: true, sqlCreds: sql);
        var agent = Agent(cfg);

        Assert.Equal("SQLSRV\\PIONEER", agent["SqlServer"]);
        Assert.Equal("PioneerPharmacySystem", agent["SqlDatabase"]);
        Assert.Equal("sa", agent["SqlUser"]);
        Assert.Equal("p@ss", agent["SqlPassword"]);
        Assert.Equal(true, agent["PmsDetected"]);
    }

    [Fact]
    public void PmsPresent_WindowsAuth_EmitsServerAndDb_ButNoUserOrPassword()
    {
        // Windows auth => User is null => IsWindowsAuth true => no credential keys on disk.
        var sql = new SqlCredentialDiscovery.SqlCredentials("SQLSRV\\PIONEER", "PioneerPharmacySystem", null, null);
        var cfg = ConsoleInstaller.BuildAgentConfig(Cfg(), "agent-1", "fp-1", pmsDetected: true, sqlCreds: sql);
        var agent = Agent(cfg);

        Assert.True(agent.ContainsKey("SqlServer"));
        Assert.True(agent.ContainsKey("SqlDatabase"));
        Assert.False(agent.ContainsKey("SqlUser"));
        Assert.False(agent.ContainsKey("SqlPassword"));
    }

    [Fact]
    public void AlwaysWritesCoreIdentityKeys_AndStripsLeadingVFromVersion()
    {
        var cfg = ConsoleInstaller.BuildAgentConfig(Cfg(), "agent-xyz", "fp-9", pmsDetected: false, sqlCreds: null);
        var agent = Agent(cfg);

        Assert.Equal("https://suavollc.com", agent["CloudUrl"]);
        Assert.Equal("sk_test_abc", agent["ApiKey"]);
        Assert.Equal("agent-xyz", agent["AgentId"]);
        Assert.Equal("PH123", agent["PharmacyId"]);
        Assert.Equal("fp-9", agent["MachineFingerprint"]);
        Assert.Equal("3.52.0", agent["Version"]); // leading 'v' stripped
    }
}
