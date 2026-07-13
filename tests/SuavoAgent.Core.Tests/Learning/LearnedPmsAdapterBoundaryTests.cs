using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class LearnedPmsAdapterBoundaryTests
{
    public static TheoryData<string> SupportedCursorTypes => new()
    {
        "tinyint", "smallint", "int", "bigint", "decimal", "numeric",
        "char", "nchar", "varchar", "nvarchar", "uniqueidentifier",
        "INT", "NVarChar",
    };

    public static TheoryData<string, string, SqlDbType, Type> ValidCursors => new()
    {
        { "tinyint", "255", SqlDbType.TinyInt, typeof(byte) },
        { "smallint", "-32768", SqlDbType.SmallInt, typeof(short) },
        { "int", "-2147483648", SqlDbType.Int, typeof(int) },
        { "bigint", "9223372036854775807", SqlDbType.BigInt, typeof(long) },
        { "decimal", "1234.50", SqlDbType.Decimal, typeof(decimal) },
        { "numeric", "-0.25", SqlDbType.Decimal, typeof(decimal) },
        { "char", "RX-1", SqlDbType.VarChar, typeof(string) },
        { "varchar", "RX-2", SqlDbType.VarChar, typeof(string) },
        { "nchar", "RX-3", SqlDbType.NVarChar, typeof(string) },
        { "nvarchar", "RX-4", SqlDbType.NVarChar, typeof(string) },
        { "uniqueidentifier", "6f9619ff-8b86-d011-b42d-00c04fc964ff", SqlDbType.UniqueIdentifier, typeof(Guid) },
    };

    public static TheoryData<string, string> InvalidTypedCursors => new()
    {
        { "tinyint", "256" },
        { "tinyint", "+1" },
        { "smallint", "32768" },
        { "int", "2147483648" },
        { "bigint", "9223372036854775808" },
        { "decimal", "not-a-number" },
        { "numeric", "1e1000" },
        { "uniqueidentifier", "6f9619ff8b86d011b42d00c04fc964ff" },
    };

    [Theory]
    [MemberData(nameof(SupportedCursorTypes))]
    public void SupportsCursorDataType_RecognizesOnlyApprovedComparableTypes(string dataType)
    {
        Assert.True(LearnedPmsAdapter.SupportsCursorDataType(dataType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("datetime")]
    [InlineData("float")]
    [InlineData("binary")]
    [InlineData("sql_variant")]
    public void SupportsCursorDataType_RejectsUnapprovedTypes(string dataType)
    {
        Assert.False(LearnedPmsAdapter.SupportsCursorDataType(dataType));
    }

    [Theory]
    [MemberData(nameof(ValidCursors))]
    public void CursorParameter_UsesApprovedSqlTypeAndInvariantTypedValue(
        string dataType,
        string cursor,
        SqlDbType expectedSqlType,
        Type expectedValueType)
    {
        using var adapter = Adapter(rxNumberDataType: dataType);
        using var command = new SqlCommand();

        AddCursor(adapter, command, cursor);

        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal("@cursor", parameter.ParameterName);
        Assert.Equal(expectedSqlType, parameter.SqlDbType);
        Assert.IsType(expectedValueType, parameter.Value);
        if (expectedSqlType is SqlDbType.VarChar or SqlDbType.NVarChar)
            Assert.Equal(64, parameter.Size);
    }

    [Theory]
    [MemberData(nameof(ValidCursors))]
    public void NullCursor_RemainsDatabaseNullForEveryApprovedSqlType(
        string dataType,
        string ignored,
        SqlDbType expectedSqlType,
        Type ignoredType)
    {
        _ = ignored;
        _ = ignoredType;
        using var adapter = Adapter(rxNumberDataType: dataType);
        using var command = new SqlCommand();

        AddCursor(adapter, command, null);

        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal(expectedSqlType, parameter.SqlDbType);
        Assert.Equal(DBNull.Value, parameter.Value);
    }

    [Theory]
    [MemberData(nameof(InvalidTypedCursors))]
    public void CursorParameter_RejectsValueThatDoesNotMatchApprovedSqlType(
        string dataType,
        string cursor)
    {
        using var adapter = Adapter(rxNumberDataType: dataType);
        using var command = new SqlCommand();

        var error = Assert.Throws<TargetInvocationException>(
            () => AddCursor(adapter, command, cursor));

        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Equal("cursor", ((ArgumentException)error.InnerException!).ParamName);
    }

    [Theory]
    [InlineData("unsupported", "1", typeof(InvalidOperationException))]
    [InlineData("nvarchar", "12345678901234567890123456789012345678901234567890123456789012345", typeof(ArgumentException))]
    [InlineData("nvarchar", "RX\n1", typeof(ArgumentException))]
    public void CursorParameter_FailsClosedOnUnsupportedTypeOrUnsafeStructuralValue(
        string dataType,
        string cursor,
        Type expectedError)
    {
        using var adapter = Adapter(rxNumberDataType: dataType);
        using var command = new SqlCommand();

        var error = Assert.Throws<TargetInvocationException>(
            () => AddCursor(adapter, command, cursor));

        Assert.IsType(expectedError, error.InnerException);
    }

    [Fact]
    public async Task CapabilityAndWritebackContracts_AreReadOnlyAndFailClosed()
    {
        using var adapter = Adapter();

        var capabilities = await adapter.DiscoverCapabilitiesAsync(default);
        var receipt = await adapter.SubmitWritebackAsync(
            new DeliveryWritebackCommand(
                "delivery-1", "RX-1", 0, "sale-1", "A", "B", 1,
                "id-1", "CA", null, 10m, 0m, 0, DateTimeOffset.UtcNow),
            default);
        var verified = await adapter.VerifyWritebackAsync(receipt, default);

        Assert.True(capabilities.CanReadSql);
        Assert.False(capabilities.CanReadApi);
        Assert.False(capabilities.CanWritebackApi);
        Assert.False(capabilities.CanWritebackUia);
        Assert.False(capabilities.CanReceiveEvents);
        Assert.False(receipt.Success);
        Assert.False(receipt.Verified);
        Assert.Equal(WritebackMethod.Manual, receipt.Method);
        Assert.False(verified);
    }

    [Theory]
    [InlineData(null, "SELECT 1", true)]
    [InlineData("", "SELECT 1", true)]
    [InlineData("SELECT 1", null, false)]
    [InlineData("SELECT 1", "", false)]
    [InlineData("SELECT 1", "SELECT 1", false)]
    public void PatientLookupSupport_RequiresEveryApprovedContractComponent(
        string? query,
        string? validationQuery,
        bool omitParameters)
    {
        using var adapter = Adapter(
            patientLookupQuery: query,
            patientLookupValidationQuery: validationQuery,
            patientLookupValidationParameters: omitParameters
                ? null
                : new Dictionary<string, string>());

        var expected = !string.IsNullOrWhiteSpace(query) &&
            !string.IsNullOrWhiteSpace(validationQuery) &&
            !omitParameters;
        Assert.Equal(expected, adapter.SupportsPatientLookup);
    }

    [Fact]
    public async Task PatientLookupWithoutApprovedContract_IsRejectedBeforeSqlConnection()
    {
        using var adapter = Adapter();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.PullPatientForRxAsync("RX-1", default));

        Assert.Contains("no patient lookup contract", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RX\u0001")]
    public async Task PatientLookup_RejectsUnsafeRxKeyBeforeSqlConnection(string rxNumber)
    {
        using var adapter = Adapter(
            patientLookupQuery: "SELECT 1",
            patientLookupValidationQuery: "SELECT 1",
            patientLookupValidationParameters: new Dictionary<string, string>());

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.PullPatientForRxAsync(rxNumber, default));

        Assert.Equal("rxNumber", error.ParamName);
    }

    [Fact]
    public async Task PatientLookup_RejectsOversizedRxKeyBeforeSqlConnection()
    {
        using var adapter = Adapter(
            patientLookupQuery: "SELECT 1",
            patientLookupValidationQuery: "SELECT 1",
            patientLookupValidationParameters: new Dictionary<string, string>());

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.PullPatientForRxAsync(new string('R', 65), default));

        Assert.Equal("rxNumber", error.ParamName);
    }

    [Fact]
    public async Task Health_InvalidSqlEndpointSurfacesStructuredUnavailableWithoutPhi()
    {
        using var adapter = Adapter(connectionString: "Server=127.0.0.1,1;Database=none;User Id=x;Password=y;Connect Timeout=1;Encrypt=False");

        var health = await adapter.CheckHealthAsync(default);

        Assert.False(health.IsHealthy);
        Assert.Equal("unavailable", health.SqlStatus);
        Assert.NotNull(health.Details);
        Assert.Single(health.Details!);
        Assert.True(health.Details.ContainsKey("error_type"));
        Assert.DoesNotContain("Password", string.Join(";", health.Details));
    }

    [Fact]
    public async Task PullReady_PreCancelledRequestPropagatesCancellation()
    {
        using var adapter = Adapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.PullReadyAsync(null, cancellation.Token));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var adapter = Adapter();

        adapter.Dispose();
        adapter.Dispose();
    }

    private static LearnedPmsAdapter Adapter(
        string connectionString = "Server=127.0.0.1,1;Database=none;Integrated Security=true;Connect Timeout=1;Encrypt=False",
        string rxNumberDataType = "nvarchar",
        string? patientLookupQuery = null,
        string? patientLookupValidationQuery = null,
        IReadOnlyDictionary<string, string>? patientLookupValidationParameters = null) =>
        new(
            "learned-test",
            connectionString,
            "SELECT 1",
            new Dictionary<string, string>(),
            "RxNumber",
            "Status",
            new[] { "ready" },
            "SELECT 1",
            new Dictionary<string, string>(),
            NullLogger.Instance,
            patientLookupQuery,
            patientLookupValidationQuery,
            patientLookupValidationParameters,
            rxNumberDataType: rxNumberDataType);

    private static void AddCursor(
        LearnedPmsAdapter adapter,
        SqlCommand command,
        string? cursor)
    {
        var method = typeof(LearnedPmsAdapter).GetMethod(
            "AddCursorParameter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(adapter, new object?[] { command, cursor });
    }
}
