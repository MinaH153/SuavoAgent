using PioneerRxSim;
using ProductionNdcNormalizer = SuavoAgent.Core.Pricing.NdcNormalizer;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class PioneerRxSimNdcNormalizerTests
{
    [Fact]
    public void DocumentedRawDemoNdc_NormalizesToSeededCatalogItem()
    {
        var ndc11 = SimNdcNormalizer.TryNormalize("0006-0734-60");

        Assert.Equal(SimCatalog.NdcMultiSupplier, ndc11);
        Assert.True(SimCatalog.Items(SimVariant.Faithful).ContainsKey(ndc11!));
    }

    [Theory]
    [InlineData("0006-0734-60", "00006073460")]
    [InlineData("50242-041-21", "50242004121")]
    [InlineData("50242-0041-21", "50242004121")]
    [InlineData("00006073460", "00006073460")]
    [InlineData(" 0006-0734-60 ", "00006073460")]
    public void TryNormalize_ValidProductionShapes_ReturnsCanonicalNdc11(
        string input,
        string expected)
    {
        Assert.Equal(expected, SimNdcNormalizer.TryNormalize(input));
        Assert.Equal(
            ProductionNdcNormalizer.TryNormalize(input),
            SimNdcNormalizer.TryNormalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0006073460")]
    [InlineData("0006 0734 60")]
    [InlineData("1234-ABC")]
    [InlineData("123-4567-89")]
    [InlineData("12345-6789-01-2")]
    public void TryNormalize_MalformedOrAmbiguousInput_RemainsRejected(string? input)
    {
        Assert.Null(SimNdcNormalizer.TryNormalize(input));
        Assert.Equal(
            ProductionNdcNormalizer.TryNormalize(input),
            SimNdcNormalizer.TryNormalize(input));
    }
}
