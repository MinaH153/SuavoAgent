using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public class AgentOptionsSecurityTests
{
    [Fact]
    public void SqlTrustServerCertificate_DefaultsToTrue_ForLocalSelfSignedPioneerRx()
    {
        // Deliberate default (2026-06-20): PioneerRx is ALWAYS deployed as a local SQL Server
        // instance with a self-signed cert (no CA chain), so a false default made the agent unable
        // to connect to ANY real pharmacy DB ("certificate chain ... not trusted"). Encrypt stays
        // ON (data encrypted in transit) — only chain validation is skipped — and the RequiredTables
        // anti-impostor check still proves we reached PioneerRx's real schema, not a LAN impostor.
        // Operators on a CA-issued cert can set false via the Agent.SqlTrustServerCertificate override.
        var options = new AgentOptions();

        Assert.True(options.SqlTrustServerCertificate);
    }

    [Fact]
    public void LegacyPhiDeliveryQueueSync_DefaultsToFalse()
    {
        var options = new AgentOptions();

        Assert.False(options.EnableLegacyPhiDeliveryQueueSync);
    }
}
