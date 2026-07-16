using PioneerRxSim;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PackageCostSimulatorContractTests
{
    [Fact]
    public void FaithfulGrid_ContainsEligibilityAdversariesAndDisagreeingWinners()
    {
        var rows = SimCatalog.Items(SimVariant.Faithful)[SimCatalog.NdcTwoSupplier]
            .Suppliers;

        Assert.Contains(rows, row => !row.Linked && row.Cost == 1.00m);
        Assert.Contains(rows, row => row.InventoryGroup == "340B" && row.Cost == 1.50m);
        Assert.Contains(rows, row => row.Status == "Discontinued" && row.Cost == 2.00m);
        Assert.Contains(rows, row =>
            row.Discontinued && row.Status == "Available" && row.Cost == 0.50m);

        var packageWinner = rows
            .Where(row => row.Linked)
            .Where(row => row.InventoryGroup == "Rx")
            .Where(row => !row.Discontinued)
            .Where(row => row.Status is "Available" or "Active")
            .OrderBy(row => row.Cost)
            .First();
        var cpuWinner = rows
            .Where(row => !row.Discontinued)
            .Where(row => row.Status is "Available" or "Active")
            .OrderBy(row => row.CostPerUnit)
            .First();

        Assert.Equal("Real Value Rx", packageWinner.Supplier);
        Assert.Equal(3.16m, packageWinner.Cost);
        Assert.Equal("McKesson", cpuWinner.Supplier);
        Assert.Equal(0.0099m, cpuWinner.CostPerUnit);
        Assert.NotEqual(packageWinner.Supplier, cpuWinner.Supplier);
    }
}
