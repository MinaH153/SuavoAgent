using Microsoft.Data.SqlClient;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class SqlConnectionSecurityTests
{
    [Fact]
    public void Apply_UsesMandatoryEncryptionAndCertificateValidation()
    {
        var builder = new SqlConnectionStringBuilder();

        SqlConnectionSecurity.Apply(builder, new AgentOptions());

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.Equal("False", builder["TrustServerCertificate"].ToString());
        Assert.Equal(string.Empty, builder["ServerCertificate"].ToString());
    }

    [Fact]
    public void Apply_RejectsLegacyCertificateBypass()
    {
        var options = new AgentOptions { SqlTrustServerCertificate = true };

        Assert.Throws<InvalidOperationException>(() =>
            SqlConnectionSecurity.Apply(new SqlConnectionStringBuilder(), options));
    }

    [Fact]
    public void ApprovalDigestComparison_IsStrictLowercaseFixedLength()
    {
        var digest = new string('a', 64);

        Assert.True(PioneerRxSqlCertificatePinVerifier.DigestsMatch(digest, digest));
        Assert.False(PioneerRxSqlCertificatePinVerifier.DigestsMatch(digest, new string('b', 64)));
        Assert.False(PioneerRxSqlCertificatePinVerifier.DigestsMatch(digest, digest.ToUpperInvariant()));
        Assert.False(PioneerRxSqlCertificatePinVerifier.DigestsMatch(digest, null));
    }
}
