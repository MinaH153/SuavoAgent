using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

internal sealed record SqlSourceIdentity(string DatabaseName, string Digest);

/// <summary>
/// Derives a non-PHI, session-HMAC-bound identity from the server itself. Config
/// strings are deliberately insufficient: a repaired workstation can point the
/// same approved query at another same-shaped database.
/// </summary>
internal static class SqlSourceIdentityVerifier
{
    private const string IdentityQuery = """
        SELECT
            COALESCE(CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), ''),
            COALESCE(CONVERT(nvarchar(256), SERVERPROPERTY('MachineName')), ''),
            COALESCE(CONVERT(nvarchar(256), SERVERPROPERTY('InstanceName')), 'MSSQLSERVER'),
            DB_NAME(),
            CONVERT(nvarchar(36), service_broker_guid)
        FROM sys.databases
        WHERE name = DB_NAME()
        """;

    internal static async Task<SqlSourceIdentity> ComputeAsync(
        SqlConnection connection,
        string sessionSalt,
        bool trustServerCertificate,
        string? serverCertificateSha256,
        CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQL source identity requires an open connection.");
        await using var command = new SqlCommand(IdentityQuery, connection)
        {
            CommandTimeout = 10,
        };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.FieldCount != 5)
            throw new InvalidDataException("SQL source identity row is unavailable.");
        var values = Enumerable.Range(0, 5)
            .Select(index => reader.IsDBNull(index) ? "" : reader.GetString(index).Trim().ToUpperInvariant())
            .ToArray();
        if (await reader.ReadAsync(ct).ConfigureAwait(false) ||
            values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("SQL source identity is incomplete or ambiguous.");

        var certificateBinding = string.IsNullOrWhiteSpace(serverCertificateSha256)
            ? "OS_TRUST"
            : serverCertificateSha256;
        var canonical = string.Join('|', values) +
                        $"|ENCRYPT=MANDATORY|TRUST_SERVER_CERTIFICATE={trustServerCertificate.ToString().ToUpperInvariant()}" +
                        $"|SERVER_CERTIFICATE_SHA256={certificateBinding}";
        var digest = PhiScrubber.HmacHash(canonical, sessionSalt);
        return new SqlSourceIdentity(values[3], digest);
    }

    internal static bool FixedDigestEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
