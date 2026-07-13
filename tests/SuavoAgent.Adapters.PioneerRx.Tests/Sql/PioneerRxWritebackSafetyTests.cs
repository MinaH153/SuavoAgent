using SuavoAgent.Adapters.PioneerRx.Sql;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public sealed class PioneerRxWritebackSafetyTests
{
    [Theory]
    [InlineData(true, false, "instead_of_trigger")]
    [InlineData(false, true, "after_trigger_requires_signed_approval")]
    [InlineData(true, true, "instead_of_trigger")]
    public void TriggerVerdict_BlocksInsteadOfAndAfterTriggers(
        bool insteadOf,
        bool after,
        string expectedCode)
    {
        var result = PioneerRxWritebackEngine.TriggerVerdict(insteadOf, after);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("trigger_blocked", result.Outcome);
        Assert.Equal(expectedCode, result.Details);
    }

    [Fact]
    public void TriggerVerdict_AllowsOnlyProvenTriggerFreeState()
        => Assert.Null(PioneerRxWritebackEngine.TriggerVerdict(false, false));

    [Fact]
    public void WritebackQueries_NeverUseTopOneSelection()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Adapters.PioneerRx", "Sql", "PioneerRxWritebackEngine.cs"));

        Assert.DoesNotContain("SELECT TOP 1 rt.RxTransactionID", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT TOP (2) rt.RxTransactionID", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous_transaction", source, StringComparison.Ordinal);
    }
}
