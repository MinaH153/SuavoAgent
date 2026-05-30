using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

/// <summary>
/// After the registry migration, AgentOptions no longer bakes the PioneerRx catalog literal
/// into synthesized pharmacy configs (Codex Q3). The catalog is resolved downstream from the
/// selected adapter's default; here we only assert the literal is gone and AdapterType defaults.
/// </summary>
public class AgentOptionsAdapterTests
{
    [Fact]
    public void GetEffectivePharmacies_NullTopLevelSqlDatabase_LeavesSynthesizedCatalogNull()
    {
        var opts = new AgentOptions { SqlServer = "srv", PharmacyId = "p1", SqlDatabase = null };

        var pharmacy = Assert.Single(opts.GetEffectivePharmacies());

        Assert.Null(pharmacy.SqlDatabase);
        Assert.Equal("pioneerrx", pharmacy.AdapterType);
    }

    [Fact]
    public void PharmacyConfig_DefaultsToPioneerRxAdapter_AndNullCatalog()
    {
        var pharmacy = new PharmacyConfig();
        Assert.Equal("pioneerrx", pharmacy.AdapterType);
        Assert.Null(pharmacy.SqlDatabase);
    }
}
