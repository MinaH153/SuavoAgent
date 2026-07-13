using Microsoft.Data.SqlClient;

namespace SuavoAgent.Core.Config;

internal static class SqlConnectionSecurity
{
    internal static void Apply(SqlConnectionStringBuilder builder, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SqlTrustServerCertificate)
            throw new InvalidOperationException("sql_certificate_validation_bypass_forbidden");

        builder["Encrypt"] = "Mandatory";
        builder["TrustServerCertificate"] = "false";
        var pinPath = PioneerRxSqlCertificatePinVerifier.ResolveProduction(options);
        if (pinPath is not null)
            builder["ServerCertificate"] = pinPath;
    }
}
