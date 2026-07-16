using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class FlaUiElementExtractorStateTests
{
    [Fact]
    public void EncodeStructuralState_AllFalse_IsZero()
    {
        Assert.Equal(
            (byte)0x00,
            FlaUiElementExtractor.EncodeStructuralState(
                false, false, false, false, false, false, false, false));
    }

    [Fact]
    public void EncodeStructuralState_AllTrue_IsFf()
    {
        Assert.Equal(
            (byte)0xFF,
            FlaUiElementExtractor.EncodeStructuralState(
                true, true, true, true, true, true, true, true));
    }

    [Fact]
    public void EncodeStructuralState_MixedBits_UsesLockedMsbToLsbOrder()
    {
        Assert.Equal(
            (byte)0xAA,
            FlaUiElementExtractor.EncodeStructuralState(
                true, false, true, false, true, false, true, false));
    }

    [Fact]
    public void EncodeStructuralState_PasswordOnly_SetsBitOne()
    {
        Assert.Equal(
            (byte)0x02,
            FlaUiElementExtractor.EncodeStructuralState(
                false, false, false, false, false, false, true, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EncodeStructuralState_AnyUnsupportedProperty_IsIneligible(
        int missingIndex)
    {
        var values = new bool?[]
        {
            true, true, true, true, true, true, true, true,
        };
        values[missingIndex] = null;

        Assert.Null(FlaUiElementExtractor.EncodeStructuralState(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7]));
    }
}
