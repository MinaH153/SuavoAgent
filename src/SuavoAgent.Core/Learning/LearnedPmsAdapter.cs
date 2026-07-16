using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// An ILocalPmsAdapter generated from an approved POM.
/// Queries the Rx table using learned column names and delivery-ready status values.
/// Read-only -- writebacks deferred to Plan 4 (needs writeback column discovery).
/// </summary>
public sealed class LearnedPmsAdapter : ILocalPmsAdapter, IDisposable
{
    internal const int DetectionPageSize = 50;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly string? _sourceIdentitySalt;
    private readonly string _expectedSourceIdentityDigest;
    private readonly string _expectedDatabaseName;
    private readonly bool _trustServerCertificate;
    private readonly string? _serverCertificateSha256;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private SqlConnection? _conn;
    private bool _disposed;

    public string PmsName { get; }
    public string DetectionQuery { get; }
    public IReadOnlyDictionary<string, string> StatusParameters { get; }
    public string DetectionValidationQuery { get; }
    public IReadOnlyDictionary<string, string> DetectionValidationParameters { get; }
    public string RxNumberColumn { get; }
    public string RxNumberDataType { get; }
    public string StatusColumn { get; }
    public IReadOnlyList<string> DeliveryReadyStatuses { get; }
    public string? PatientLookupQuery { get; }
    public string? PatientLookupValidationQuery { get; }
    public IReadOnlyDictionary<string, string>? PatientLookupValidationParameters { get; }
    public bool SupportsPatientLookup =>
        !string.IsNullOrWhiteSpace(PatientLookupQuery) &&
        !string.IsNullOrWhiteSpace(PatientLookupValidationQuery) &&
        PatientLookupValidationParameters is not null;

    public LearnedPmsAdapter(
        string pmsName,
        string connectionString,
        string detectionQuery,
        IReadOnlyDictionary<string, string> statusParameters,
        string rxNumberColumn,
        string statusColumn,
        IReadOnlyList<string> deliveryReadyStatuses,
        string detectionValidationQuery,
        IReadOnlyDictionary<string, string> detectionValidationParameters,
        ILogger logger,
        string? patientLookupQuery = null,
        string? patientLookupValidationQuery = null,
        IReadOnlyDictionary<string, string>? patientLookupValidationParameters = null,
        string expectedSourceIdentityDigest = "",
        string expectedDatabaseName = "",
        string? sourceIdentitySalt = null,
        bool trustServerCertificate = false,
        string? serverCertificateSha256 = null,
        string rxNumberDataType = "nvarchar")
    {
        PmsName = pmsName;
        var sqlConnection = new SqlConnectionStringBuilder(connectionString)
        {
            Encrypt = true,
            TrustServerCertificate = false,
        };
        _connectionString = sqlConnection.ConnectionString;
        DetectionQuery = detectionQuery;
        StatusParameters = statusParameters;
        DetectionValidationQuery = detectionValidationQuery;
        DetectionValidationParameters = detectionValidationParameters;
        RxNumberColumn = rxNumberColumn;
        RxNumberDataType = rxNumberDataType;
        StatusColumn = statusColumn;
        DeliveryReadyStatuses = deliveryReadyStatuses;
        PatientLookupQuery = patientLookupQuery;
        PatientLookupValidationQuery = patientLookupValidationQuery;
        PatientLookupValidationParameters = patientLookupValidationParameters;
        _expectedSourceIdentityDigest = expectedSourceIdentityDigest;
        _expectedDatabaseName = expectedDatabaseName;
        _sourceIdentitySalt = sourceIdentitySalt;
        _trustServerCertificate = trustServerCertificate;
        _serverCertificateSha256 = serverCertificateSha256;
        _logger = logger;
    }

    public Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new CapabilityManifest(
            CanReadSql: true,
            CanReadApi: false,
            CanWritebackApi: false,
            CanWritebackUia: false,
            CanReceiveEvents: false,
            PmsVersion: null,
            SqlServerEndpoint: null,
            ApiEndpoint: null,
            DiscoveredScreens: Array.Empty<string>()));
    }

    // Transient SQL error classes: connection, timeout, transport
    private static readonly HashSet<int> TransientErrorNumbers = new()
    {
        -2, 20, 64, 233, 10053, 10054, 10060, 40143, 40197, 40501, 40613, 49918, 49919, 49920,
    };

    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => TransientErrorNumbers.Contains(e.Number));

    internal static bool SupportsCursorDataType(string dataType) =>
        dataType.ToLowerInvariant() is
            "tinyint" or "smallint" or "int" or "bigint" or
            "decimal" or "numeric" or
            "char" or "nchar" or "varchar" or "nvarchar" or
            "uniqueidentifier";

    public async Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct)
    {
        var results = new List<RxReadyForDelivery>();
        await _operationLock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct);
            if (!await ValidateContractAsync(
                    DetectionValidationQuery,
                    DetectionValidationParameters,
                    ct).ConfigureAwait(false))
                throw new InvalidDataException("Approved learned detection schema contract drifted.");

            await using var cmd = new SqlCommand(DetectionQuery, _conn);
            cmd.CommandTimeout = 30;
            foreach (var (name, value) in StatusParameters)
                cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.Add("@pageSize", SqlDbType.Int).Value = DetectionPageSize;
            AddCursorParameter(cmd, cursor);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var rxNum = reader[RxNumberColumn]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(rxNum))
                    throw new InvalidDataException("Approved learned detection returned an empty cursor key.");
                results.Add(new RxReadyForDelivery(
                    RxNumber: rxNum,
                    FillNumber: 0,
                    DrugName: "",
                    Ndc: "",
                    Quantity: 0,
                    DaysSupply: 0,
                    StatusText: reader[StatusColumn]?.ToString() ?? "",
                    IsControlled: false,
                    DrugSchedule: null,
                    PatientIdRequired: false,
                    CounselingRequired: false,
                    DetectedAt: DateTimeOffset.UtcNow,
                    Source: DetectionSource.Sql));
            }

            _logger.LogInformation("Learned adapter detected {Count} delivery-ready Rxs", results.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (IsTransient(ex))
        {
            ResetConnection();
            _logger.LogWarning(
                "Learned adapter query unavailable (category=transient_sql, errorType={ErrorType})",
                ex.GetType().Name);
            throw;
        }
        catch (Exception ex)
        {
            ResetConnection();
            _logger.LogWarning(
                "Learned adapter query unavailable (category=non_transient, errorType={ErrorType})",
                ex.GetType().Name);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }

        return results;
    }

    /// <summary>
    /// Reads the minimum delivery recipient fields for exactly one pharmacist-approved Rx.
    /// The query is part of the human-reviewed template digest and is absent unless schema
    /// discovery proved an exact foreign-key edge and one unambiguous field mapping.
    /// </summary>
    internal async Task<RxPatientDetails?> PullPatientForRxAsync(
        string rxNumber,
        CancellationToken ct)
    {
        if (!SupportsPatientLookup)
            throw new InvalidOperationException("The approved learned adapter has no patient lookup contract.");
        if (string.IsNullOrWhiteSpace(rxNumber) || rxNumber.Length > 64 ||
            rxNumber.Any(char.IsControl))
            throw new ArgumentException("Rx lookup key is invalid.", nameof(rxNumber));

        await _operationLock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct);
            if (!await ValidateContractAsync(
                    PatientLookupValidationQuery!,
                    PatientLookupValidationParameters!,
                    ct).ConfigureAwait(false))
                throw new InvalidDataException("Approved learned patient schema contract drifted.");
            await using var command = new SqlCommand(PatientLookupQuery!, _conn)
            {
                CommandTimeout = 15,
            };
            command.Parameters.AddWithValue("@rx", rxNumber);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var details = new RxPatientDetails(
                RxNumber: rxNumber,
                FirstName: ReadNullableText(reader, "FirstName"),
                LastInitial: ReadNullableText(reader, "LastInitial"),
                Phone: ReadNullableText(reader, "Phone"),
                Address1: ReadNullableText(reader, "Address1"),
                Address2: ReadNullableText(reader, "Address2"),
                City: ReadNullableText(reader, "City"),
                State: ReadNullableText(reader, "State"),
                Zip: ReadNullableText(reader, "Zip"));
            if (await reader.ReadAsync(ct))
                throw new InvalidDataException("Approved learned patient lookup was not unique.");
            return details;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ResetConnection();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct)
    {
        // Writeback not supported by learned adapters yet
        return Task.FromResult(new WritebackReceipt(
            Success: false,
            TransactionId: null,
            Error: "Writeback not supported by learned adapter",
            Method: WritebackMethod.Manual,
            Verified: false,
            CompletedAt: DateTimeOffset.UtcNow));
    }

    public Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public async Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectionOpenAsync(ct);
            if (!await ValidateContractAsync(
                    DetectionValidationQuery,
                    DetectionValidationParameters,
                    ct).ConfigureAwait(false) ||
                SupportsPatientLookup && !await ValidateContractAsync(
                    PatientLookupValidationQuery!,
                    PatientLookupValidationParameters!,
                    ct).ConfigureAwait(false))
                throw new InvalidDataException("Approved learned schema contract drifted.");
            return new AdapterHealthReport(
                AdapterName: "learned-approved",
                IsHealthy: true,
                SqlStatus: "connected",
                UiaStatus: null,
                ApiStatus: null,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ResetConnection();
            _logger.LogWarning(
                "Learned adapter health check failed (errorType={ErrorType})",
                ex.GetType().Name);
            return new AdapterHealthReport(
                AdapterName: "learned-approved",
                IsHealthy: false,
                SqlStatus: "unavailable",
                UiaStatus: null,
                ApiStatus: null,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, string>
                {
                    ["error_type"] = ex.GetType().Name,
                });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _operationLock.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            ResetConnection();
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }

    private async Task EnsureConnectionOpenAsync(CancellationToken ct)
    {
        if (_conn is { State: System.Data.ConnectionState.Open }) return;
        ResetConnection();
        _conn = new SqlConnection(_connectionString);
        await _conn.OpenAsync(ct);
        if (string.IsNullOrWhiteSpace(_sourceIdentitySalt) ||
            _expectedSourceIdentityDigest.Length != 64 ||
            string.IsNullOrWhiteSpace(_expectedDatabaseName))
        {
            ResetConnection();
            throw new InvalidOperationException("Learned SQL source identity binding is unavailable.");
        }
        var observed = await SqlSourceIdentityVerifier.ComputeAsync(
            _conn,
            _sourceIdentitySalt,
            _trustServerCertificate,
            _serverCertificateSha256,
            ct).ConfigureAwait(false);
        if (!string.Equals(observed.DatabaseName, _expectedDatabaseName, StringComparison.OrdinalIgnoreCase) ||
            !SqlSourceIdentityVerifier.FixedDigestEquals(
                observed.Digest,
                _expectedSourceIdentityDigest))
        {
            ResetConnection();
            throw new InvalidDataException("Learned SQL source identity changed.");
        }
    }

    private void ResetConnection()
    {
        _conn?.Dispose();
        _conn = null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static string? ReadNullableText(SqlDataReader reader, string alias)
    {
        var ordinal = reader.GetOrdinal(alias);
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<bool> ValidateContractAsync(
        string validationQuery,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(validationQuery, _conn)
        {
            CommandTimeout = 10,
        };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null && result is not DBNull && Convert.ToInt32(result) == 1;
    }

    private void AddCursorParameter(SqlCommand command, string? cursor)
    {
        if (cursor is { Length: > 64 } || cursor?.Any(char.IsControl) == true)
            throw new ArgumentException("Learned detection cursor is invalid.", nameof(cursor));

        var normalizedType = RxNumberDataType.ToLowerInvariant();
        var sqlType = normalizedType switch
        {
            "tinyint" => SqlDbType.TinyInt,
            "smallint" => SqlDbType.SmallInt,
            "int" => SqlDbType.Int,
            "bigint" => SqlDbType.BigInt,
            "decimal" or "numeric" => SqlDbType.Decimal,
            "char" or "varchar" => SqlDbType.VarChar,
            "nchar" or "nvarchar" => SqlDbType.NVarChar,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            _ => throw new InvalidOperationException("Approved learned cursor type is unsupported."),
        };
        var parameter = command.Parameters.Add("@cursor", sqlType);
        if (sqlType is SqlDbType.VarChar or SqlDbType.NVarChar)
            parameter.Size = 64;
        if (cursor is null)
        {
            parameter.Value = DBNull.Value;
            return;
        }

        parameter.Value = normalizedType switch
        {
            "tinyint" when byte.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) => value,
            "smallint" when short.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            "int" when int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            "bigint" when long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            "decimal" or "numeric" when decimal.TryParse(cursor, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) => value,
            "char" or "nchar" or "varchar" or "nvarchar" => cursor,
            "uniqueidentifier" when Guid.TryParseExact(cursor, "D", out var value) => value,
            _ => throw new ArgumentException("Learned detection cursor does not match its approved SQL type.", nameof(cursor)),
        };
    }
}
