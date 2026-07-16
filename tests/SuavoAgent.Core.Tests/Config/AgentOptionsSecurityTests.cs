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
        Assert.Null(options.SqlServerCertificateSha256);
        Assert.Null(options.ValidatedSqlServerCertificatePath);
    }

    [Fact]
    public void LegacyPhiDeliveryQueueSync_DefaultsToFalse()
    {
        var options = new AgentOptions();

        Assert.False(options.EnableLegacyPhiDeliveryQueueSync);
    }
}
