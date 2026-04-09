using SuavoAgent.Adapters.PioneerRx.Sql;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class SqlDiscoveryTests
{
    [Fact]
    public void ParseBrowserResponse_ExtractsServerAndPort()
    {
        var resp = "ServerName;PIONEER10;InstanceName;SQLEXPRESS;IsClustered;No;Version;15.0.2000.5;tcp;49172;;";
        var result = SqlDiscovery.ParseBrowserResponse(resp);
        Assert.NotNull(result);
        Assert.Equal("SQLEXPRESS", result.Value.InstanceName);
        Assert.Equal(49172, result.Value.TcpPort);
    }

    [Fact]
    public void ParseBrowserResponse_HandlesDefault()
    {
        var resp = "ServerName;PIONEER10;InstanceName;MSSQLSERVER;IsClustered;No;Version;16.0;tcp;1433;;";
        var result = SqlDiscovery.ParseBrowserResponse(resp);
        Assert.NotNull(result);
        Assert.Equal(1433, result.Value.TcpPort);
    }

    [Fact]
    public void ParseBrowserResponse_ReturnsNullForGarbage()
    {
        Assert.Null(SqlDiscovery.ParseBrowserResponse("garbage"));
        Assert.Null(SqlDiscovery.ParseBrowserResponse(""));
        Assert.Null(SqlDiscovery.ParseBrowserResponse(null));
    }
}
