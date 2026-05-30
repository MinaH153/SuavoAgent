using SuavoAgent.Adapters.ComputerRx;
using SuavoAgent.Contracts.Adapters;
using Xunit;

namespace SuavoAgent.Adapters.ComputerRx.Tests;

/// <summary>
/// Proves a real second adapter produces a registry-valid config: distinct AdapterType and a
/// non-empty PHI policy (so AdapterRegistry won't reject it on HIPAA invariant #1).
/// </summary>
public class ComputerRxAdapterConfigTests
{
    [Fact]
    public void Create_HasDistinctTypeAndNonEmptyPhiPolicy()
    {
        var c = ComputerRxAdapterConfig.Create();

        Assert.Equal("computerrx", c.AdapterType);
        Assert.Equal("Computer-Rx", c.DisplayName);
        Assert.True(c.Phi.ColumnBlocklist.Count > 0);
        Assert.True(c.Phi.IsPhiColumn("PatientName"));
        Assert.False(c.Phi.IsPhiColumn("Ndc"));
    }

    [Fact]
    public void Create_IsAcceptedByAdapterConfigInvariant()
    {
        // PhiPolicy.Create would have thrown on an empty policy; reaching here proves validity.
        var c = ComputerRxAdapterConfig.Create();
        Assert.NotNull(c.Phi);
        Assert.NotEqual("pioneerrx", c.AdapterType);
    }
}
