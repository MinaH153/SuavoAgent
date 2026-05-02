using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public class AgentOptionsSecurityTests
{
    [Fact]
    public void SqlTrustServerCertificate_DefaultsToFalse()
    {
        var options = new AgentOptions();

        Assert.False(options.SqlTrustServerCertificate);
    }
}
