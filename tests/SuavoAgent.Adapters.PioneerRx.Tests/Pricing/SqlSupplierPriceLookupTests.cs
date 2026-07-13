using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

public sealed class SqlSupplierPriceLookupTests
{
    [Fact]
    public void BindQueryParameters_BindsEveryValueWithExplicitBoundedTypes()
    {
        using var command = new SqlCommand();

        SqlSupplierPriceLookup.BindQueryParameters(
            command,
            "55111064501",
            new[] { "Available", "Active" },
            TextShape("varchar", 11),
            TextShape("varchar", 16));

        Assert.Equal(3, command.Parameters.Count);
        AssertParameter(command.Parameters["@ndc"], SqlDbType.VarChar, 11, "55111064501");
        AssertParameter(command.Parameters["@status0"], SqlDbType.VarChar, 16, "Available");
        AssertParameter(command.Parameters["@status1"], SqlDbType.VarChar, 16, "Active");
    }

    [Fact]
    public void BindQueryParameters_MatchesDiscoveredUnicodeColumnTypesAndSizes()
    {
        using var command = new SqlCommand();

        SqlSupplierPriceLookup.BindQueryParameters(
            command,
            "55111064501",
            new[] { "Available" },
            TextShape("nchar", 11),
            TextShape("nvarchar", 24));

        AssertParameter(command.Parameters["@ndc"], SqlDbType.NChar, 11, "55111064501");
        AssertParameter(command.Parameters["@status0"], SqlDbType.NVarChar, 24, "Available");
    }

    [Theory]
    [InlineData("")]
    [InlineData("5511106450")]
    [InlineData("551110645012")]
    [InlineData("55111-0645-01")]
    [InlineData("5511106450A")]
    [InlineData(" 55111064501")]
    [InlineData("٥٥١١١٠٦٤٥٠١")]
    public async Task FindCheapestSupplierAsync_RejectsNonCanonicalNdcWithoutOpeningConnection(string ndc)
    {
        var connectionCalls = 0;
        var lookup = new SqlSupplierPriceLookup(
            Schema(),
            _ =>
            {
                connectionCalls++;
                throw new InvalidOperationException("connection_factory_must_not_run");
            },
            NullLogger<SqlSupplierPriceLookup>.Instance);

        var result = await lookup.FindCheapestSupplierAsync("job", 7, ndc, CancellationToken.None);

        Assert.False(result.Found);
        Assert.Equal(SqlSupplierPriceLookup.InvalidNdcCode, result.ErrorMessage);
        Assert.Equal(0, connectionCalls);
    }

    private static DiscoveredPricingSchema Schema() => new(
        CatalogSchema: "Inventory",
        CatalogTable: "ItemPricing",
        CostColumn: "Cost",
        CostPerUnitColumn: "CostPerUnit",
        NdcColumn: "NDC",
        ItemJoin: null,
        SupplierSource: new CatalogSupplierSource(
            SupplierResolution.Denormalized,
            "SupplierName",
            null,
            null,
            null,
            null,
            null,
            null),
        StatusColumn: "Status",
        AvailableStatusValues: new[] { "Available", "Active" },
        ConfidenceScore: 1.0,
        DiagnosticNotes: Array.Empty<string>(),
        CostColumnShape: new PricingSqlColumnShape("money", 8, 19, 4, false),
        CostPerUnitColumnShape: new PricingSqlColumnShape("decimal", 9, 18, 4, false),
        NdcColumnShape: TextShape("varchar", 11),
        StatusColumnShape: TextShape("varchar", 16));

    private static PricingSqlColumnShape TextShape(string type, int characters) =>
        new(type, type.StartsWith('n') ? characters * 2 : characters, null, null, false);

    private static void AssertParameter(
        SqlParameter parameter,
        SqlDbType expectedType,
        int expectedSize,
        string expectedValue)
    {
        Assert.Equal(expectedType, parameter.SqlDbType);
        Assert.Equal(expectedSize, parameter.Size);
        Assert.Equal(expectedValue, parameter.Value);
    }
}
