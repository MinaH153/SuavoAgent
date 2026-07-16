using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Health;

internal sealed record ProbationSqlCanaryResult(
    bool SqlConnected,
    bool SchemaCanaryGreen,
    string Code);

internal interface IPioneerRxProbationSqlCanary
{
    Task<ProbationSqlCanaryResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deliberately narrow pre-PIC SQL proof. It validates the administrator-enrolled
/// DER certificate, opens a read-only TLS connection, and reads only
/// INFORMATION_SCHEMA table-existence metadata. It has no prescription query,
/// state database, cloud sync, actuation, or writeback dependency.
/// </summary>
internal sealed class PioneerRxProbationSqlCanary : IPioneerRxProbationSqlCanary
{
    private static readonly (string Schema, string Table)[] RequiredTables =
    [
        ("Prescription", "Rx"),
        ("Prescription", "RxTransaction"),
        ("Prescription", "RxTransactionStatusType"),
    ];

    private readonly AgentOptions _options;

    internal PioneerRxProbationSqlCanary(IOptions<AgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<ProbationSqlCanaryResult> ProbeAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SqlServer) ||
            string.IsNullOrWhiteSpace(_options.SqlDatabase) ||
            string.IsNullOrWhiteSpace(_options.SqlServerCertificateSha256) ||
            _options.SqlTrustServerCertificate)
            return new(false, false, "probation_sql_configuration_invalid");

        var certificatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            PioneerRxSqlCertificatePinContract.InstalledFileName);
        if (!PioneerRxSqlCertificatePinContract.TryVerifyFile(
                certificatePath,
                _options.SqlServerCertificateSha256,
                DateTimeOffset.UtcNow,
                out _))
            return new(false, false, "probation_sql_certificate_invalid");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.SqlServer,
            InitialCatalog = _options.SqlDatabase,
            ApplicationName = "SuavoAgent-Probation-Canary",
            ConnectTimeout = 15,
            Pooling = false,
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        builder["Encrypt"] = "Mandatory";
        builder["TrustServerCertificate"] = "false";
        builder["ServerCertificate"] = certificatePath;
        if (!string.IsNullOrWhiteSpace(_options.SqlUser) &&
            !string.IsNullOrWhiteSpace(_options.SqlPassword))
        {
            builder.UserID = _options.SqlUser;
            builder.Password = _options.SqlPassword;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string query = """
                SELECT COUNT_BIG(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table;
                """;
            foreach (var required in RequiredTables)
            {
                await using var command = new SqlCommand(query, connection)
                {
                    CommandTimeout = 5,
                };
                command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128)
                {
                    Value = required.Schema,
                });
                command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128)
                {
                    Value = required.Table,
                });
                var count = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (count != 1)
                    return new(true, false, "probation_pioneerrx_schema_mismatch");
            }
            return new(true, true, "pms_schema_canary");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            SqlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, false, "probation_sql_unavailable");
        }
    }
}
