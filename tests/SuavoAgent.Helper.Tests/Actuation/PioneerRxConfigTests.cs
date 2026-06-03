using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class PioneerRxConfigTests
{
    [Fact]
    public void SafeDefault_DisabledAndDryRun()
    {
        var cfg = PioneerRxConfig.SafeDefault();
        Assert.False(cfg.Enabled);
        Assert.True(cfg.DryRun);
        Assert.Equal("PioneerPharmacy.exe", cfg.ProcessName);
    }

    [Fact]
    public void RecordWith_PreservesUnsetFields()
    {
        var cfg = PioneerRxConfig.SafeDefault() with { Enabled = true };
        Assert.True(cfg.Enabled);
        Assert.True(cfg.DryRun); // unchanged
        Assert.Equal("PioneerPharmacy.exe", cfg.ProcessName);
    }
}
