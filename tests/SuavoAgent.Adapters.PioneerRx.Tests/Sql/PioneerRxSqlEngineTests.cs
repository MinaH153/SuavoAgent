using SuavoAgent.Adapters.PioneerRx;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class PioneerRxSqlEngineTests
{
    [Fact]
    public void Constructor_RequiresCertificateChainAndHostnameValidation()
    {
        var engine = new PioneerRxSqlEngine(
            "127.0.0.1,1433",
            "PioneerPharmacySystem",
            NullLogger<PioneerRxSqlEngine>.Instance,
            trustServerCertificate: false);

        var field = typeof(PioneerRxSqlEngine).GetField(
            "_connectionString",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var connectionString = Assert.IsType<string>(field?.GetValue(engine));

        Assert.Contains("Trust Server Certificate=False", connectionString);
        Assert.Throws<InvalidOperationException>(() => new PioneerRxSqlEngine(
            "127.0.0.1,1433",
            "PioneerPharmacySystem",
            NullLogger<PioneerRxSqlEngine>.Instance,
            trustServerCertificate: true));
    }

    [Fact]
    public void Constructor_UsesExactValidatedServerCertificatePin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Pioneer SQL", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), "suavo-sql-pin-" + Guid.NewGuid().ToString("N") + ".cer");
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
            var digest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(path);
            using var engine = new PioneerRxSqlEngine(
                "127.0.0.1,1433",
                "PioneerPharmacySystem",
                NullLogger<PioneerRxSqlEngine>.Instance,
                serverCertificatePath: path,
                serverCertificateSha256: digest);
            var field = typeof(PioneerRxSqlEngine).GetField(
                "_connectionString",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var connectionString = Assert.IsType<string>(field?.GetValue(engine));

            Assert.Contains("Server Certificate=", connectionString, StringComparison.Ordinal);
            Assert.Contains(path, connectionString, StringComparison.Ordinal);
            Assert.Contains("Trust Server Certificate=False", connectionString, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void BuildDeliveryQuery_UsesRealSchema()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.Contains("Prescription.RxTransaction rt", query);
        Assert.Contains("Prescription.Rx r", query);
        Assert.Contains("rt.RxID = r.RxID", query);
        Assert.DoesNotContain("dbo.", query);
        Assert.DoesNotContain("RxLocal.ActiveRx", query);
    }

    [Fact]
    public void BuildDeliveryQuery_UsesParameterPlaceholders()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.Contains("@status0", query);
        Assert.Contains("@status1", query);
        Assert.Contains("@status2", query);
        Assert.DoesNotContain("'53ce4c47", query); // no hardcoded GUIDs
    }

    [Fact]
    public void BuildDeliveryQuery_DefaultsTo100()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("TOP 100", query);
    }

    [Fact]
    public void BuildDeliveryQuery_NoDateFilter()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.DoesNotContain("DATEADD", query);
    }

    [Fact]
    public void BuildDeliveryQuery_SelectsNoPhiColumns()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.DoesNotContain("PatientID", query);
        Assert.DoesNotContain("PatientName", query);
        Assert.DoesNotContain("Person.Patient", query);
    }

    [Fact]
    public void BuildDeliveryQuery_SelectsOperationalColumns()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("r.RxNumber", query);
        Assert.Contains("rt.DispensedQuantity", query);
        Assert.Contains("rt.DaysSupply", query);
        Assert.Contains("rt.RxTransactionStatusTypeID", query);
        Assert.Contains("rt.RefillNumber", query);
    }

    [Fact]
    public void BuildDeliveryQuery_JoinsItemTableForDrugName()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("Inventory.Item i", query);
        Assert.Contains("rt.DispensedItemID = i.ItemID", query);
        Assert.Contains("i.ItemName", query);
        Assert.Contains("i.NDC", query);
    }

    [Fact]
    public void BuildDeliveryQuery_OrdersByDateFilledDesc()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("ORDER BY rt.DateFilled DESC", query);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public void BuildDeliveryQueryBase_UsesConfigurableBatchSize(int batchSize)
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQueryBase(3, batchSize);
        Assert.Contains($"TOP {batchSize}", query);
    }

    [Fact]
    public void BuildDeliveryQueryBase_DefaultsTo100()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQueryBase(3);
        Assert.Contains("TOP 100", query);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public void BuildFullDeliveryQuery_UsesConfigurableBatchSize(int batchSize)
    {
        var query = PioneerRxSqlEngine.BuildFullDeliveryQuery(3, batchSize);
        Assert.Contains($"TOP {batchSize}", query);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public void BuildDeliveryQuery_UsesConfigurableBatchSize(int batchSize)
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3, batchSize);
        Assert.Contains($"TOP {batchSize}", query);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public void BuildMetadataQuery_UsesConfigurableBatchSize(int batchSize)
    {
        var names = new List<string> { "Waiting for Pick up", "Waiting for Delivery" };
        var query = PioneerRxSqlEngine.BuildMetadataQuery(names, batchSize);
        Assert.Contains($"TOP {batchSize}", query);
    }

    [Theory]
    [InlineData("PatientName")]
    [InlineData("PatientSSN")]
    [InlineData("DiagnosisCode")]
    [InlineData("PatientID")]
    [InlineData("PersonID")]
    public void IsPhiColumn_BlocksPhi(string col) => Assert.True(PioneerRxSqlEngine.IsPhiColumn(col));

    [Theory]
    [InlineData("RxNumber")]
    [InlineData("MedicationDescription")]
    [InlineData("DispensedNDC")]
    public void IsPhiColumn_AllowsOperational(string col) => Assert.False(PioneerRxSqlEngine.IsPhiColumn(col));

    [Theory]
    [InlineData("Waiting for Pick up")]
    [InlineData("Waiting for Pickup")]
    [InlineData("WAITING FOR PICK UP")]
    [InlineData("waiting for pick up")]
    public void StatusPattern_MatchesPickupVariants(string statusDesc)
    {
        Assert.True(PioneerRxConstants.MatchesDeliveryReadyPattern(statusDesc));
    }

    [Fact]
    public void DeliveryReadyStatusNames_PinObservedPilotReadyStates()
    {
        Assert.Equal(new[]
        {
            "Waiting for Pick up",
            "Waiting for Delivery",
            "To Be Put in Bin"
        }, PioneerRxConstants.DeliveryReadyStatusNames);

        Assert.DoesNotContain("Out for Delivery", PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.DoesNotContain("Completed", PioneerRxConstants.DeliveryReadyStatusNames);
    }

    [Theory]
    [InlineData("Waiting for Delivery")]
    [InlineData("Out For Delivery")]
    [InlineData("out for delivery")]
    [InlineData("Completed")]
    public void StatusPattern_MatchesDeliveryVariants(string statusDesc)
    {
        Assert.True(PioneerRxConstants.MatchesDeliveryStatusPattern(statusDesc));
    }

    [Theory]
    [InlineData("Data Entry")]
    [InlineData("Suspended")]
    [InlineData("Voided")]
    public void StatusPattern_RejectsNonDeliveryStatuses(string statusDesc)
    {
        Assert.False(PioneerRxConstants.MatchesDeliveryReadyPattern(statusDesc));
        Assert.False(PioneerRxConstants.MatchesDeliveryStatusPattern(statusDesc));
    }
}
