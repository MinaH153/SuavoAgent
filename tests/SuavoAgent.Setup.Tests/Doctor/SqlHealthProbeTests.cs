// tests/SuavoAgent.Setup.Tests/Doctor/SqlHealthProbeTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class SqlHealthProbeTests
{
    private static GateResult Run(string? log) => new SqlHealthProbe(() => log).Check();

    [Fact]
    public void Anonymous_logon_is_Fail()
    {
        var r = Run("WRN SQL connection failed\nLogin failed for user 'NT AUTHORITY\\ANONYMOUS LOGON'. Error Number:18456");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("auth", r.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cert_not_trusted_is_Fail_with_exact_pin_remediation()
    {
        var r = Run("WRN SQL connection failed\n(SSL Provider) The certificate chain was issued by an authority that is not trusted");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("enroll", r.Detail, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlTrustServerCertificate=true", r.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Connected_is_Ok()
        => Assert.Equal(GateState.Ok, Run("INF SQL connected to PIONEERSERVER\\PHARMACY").State);

    [Fact]
    public void No_sql_activity_is_Warn()
        => Assert.Equal(GateState.Warn, Run("INF Tier-2 LocalInference ENABLED").State);
}
