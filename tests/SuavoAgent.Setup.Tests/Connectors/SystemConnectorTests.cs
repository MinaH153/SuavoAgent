using SuavoAgent.Setup.Connectors;
using Xunit;

namespace SuavoAgent.Setup.Tests.Connectors;

public class SystemConnectorTests
{
    [Fact] public void Factory_selects_pioneerrx() =>
        Assert.Equal("pioneerrx", SystemConnectorFactory.Select("pioneerrx").Key);

    [Fact] public void Factory_selects_null() =>
        Assert.Equal("none", SystemConnectorFactory.Select("none").Key);

    [Fact] public void Factory_throws_on_unknown() =>
        Assert.Throws<UnknownConnectorException>(() => SystemConnectorFactory.Select("redsail"));

    [Fact] public void Null_connector_is_observe_only()
    {
        var c = new NullConnector();
        Assert.False(c.Capabilities.HasPms);
        Assert.Null(c.Discover(c.Probe()));
        Assert.Equal("none", c.Capabilities.RedactionProfileId);
    }

    [Fact] public void Pioneer_connector_advertises_pms_and_phi_profile()
    {
        var c = new PioneerRxConnector();
        Assert.True(c.Capabilities.HasPms);
        Assert.Equal("PioneerRx", c.Capabilities.Label);
        Assert.Equal("phi-v1", c.Capabilities.RedactionProfileId);
    }
}
