using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// The install must complete with the minimum needed to take control even when
/// PioneerRx / SQL are absent — the agent comes online, heartbeats, and
/// self-heals the SQL connection later. So <c>appsettings.json</c> must be
/// well-formed without SQL credentials, and only carry SQL keys when they were
/// actually captured.
/// </summary>
public sealed class InstallOrchestratorTests
{
    [Fact]
    public void AppSettings_omits_sql_when_deferred()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = null; // deferred — no PioneerRx on this box

        var json = new InstallOrchestrator(ctx).BuildAppSettings();

        Assert.DoesNotContain("SqlServer", json);
        Assert.DoesNotContain("SqlDatabase", json);
        Assert.DoesNotContain("SqlUser", json);
        Assert.DoesNotContain("SqlPassword", json);
        // …but the control-plane identity the agent needs to phone home is present.
        Assert.Contains("\"PharmacyId\"", json);
        Assert.Contains("\"ApiKey\"", json);
        Assert.Contains("\"AgentId\"", json);
        Assert.Contains("\"CloudUrl\"", json);
    }

    [Fact]
    public void AppSettings_includes_sql_when_captured()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: "localhost,49202",
            Database: "PioneerPharmacySystem",
            User: "suavo_read",
            Password: "s3cret");

        var json = new InstallOrchestrator(ctx).BuildAppSettings();

        Assert.Contains("\"SqlServer\"", json);
        Assert.Contains("localhost,49202", json);
        Assert.Contains("\"SqlUser\"", json);
    }

    [Fact]
    public void AppSettings_windows_auth_omits_user_and_password()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: "localhost",
            Database: "PioneerPharmacySystem",
            User: null,    // Windows / integrated auth
            Password: null);

        var json = new InstallOrchestrator(ctx).BuildAppSettings();

        Assert.Contains("\"SqlServer\"", json);
        Assert.DoesNotContain("\"SqlUser\"", json);
        Assert.DoesNotContain("\"SqlPassword\"", json);
    }

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.15.0",
        LearningMode: false));
}
