using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Canary;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Canary;

public sealed class PioneerRxCanarySourceTests
{
    private static readonly ObservedObject[] CompleteObservedObjects =
    {
        new("Prescription", "RxTransaction", "RxTransactionID", "uniqueidentifier", null, false, true),
        new("Prescription", "RxTransaction", "DateFilled", "datetime", null, true, true),
        new("Prescription", "RxTransaction", "RefillNumber", "int", null, true, true),
        new("Prescription", "RxTransaction", "DispensedQuantity", "decimal", null, true, true),
        new("Prescription", "RxTransaction", "DaysSupply", "int", null, true, true),
        new("Prescription", "RxTransaction", "RxTransactionStatusTypeID", "uniqueidentifier", null, false, true),
        new("Prescription", "RxTransaction", "RxID", "uniqueidentifier", null, false, true),
        new("Prescription", "RxTransaction", "DispensedItemID", "uniqueidentifier", null, true, true),
        new("Prescription", "Rx", "RxID", "uniqueidentifier", null, false, true),
        new("Prescription", "Rx", "RxNumber", "int", null, false, true),
        new("Prescription", "RxTransactionStatusType", "RxTransactionStatusTypeID", "uniqueidentifier", null, false, true),
        new("Prescription", "RxTransactionStatusType", "Description", "nvarchar", 100, false, true),
        new("Inventory", "Item", "ItemID", "uniqueidentifier", null, false, true),
        new("Inventory", "Item", "ItemName", "nvarchar", 200, true, true),
        new("Inventory", "Item", "NDC", "nvarchar", 20, true, true),
        new("Inventory", "Item", "DeaSchedule", "int", null, true, true),
    };

    private static readonly ObservedStatus[] CompleteObservedStatuses =
    {
        new("Waiting for Pick up", "53ce4c47-dff2-46ac-a310-719e792239ef"),
        new("Waiting for Delivery", "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de"),
        new("To Be Put in Bin", "9a5f3f55-b6e0-4f03-86dd-f509483cdb2e"),
    };

    [Fact]
    public void ContractBaseline_FingerprintsActualMetadataExtractionQuery()
    {
        using var engine = NewEngine();
        var source = new PioneerRxCanarySource(
            engine,
            NullLogger<PioneerRxCanarySource>.Instance);

        var baseline = source.GetContractBaseline();
        var actualQuery = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);

        Assert.Equal(
            ContractFingerprinter.HashQuery(actualQuery),
            baseline.QueryFingerprint);
    }

    [Theory]
    [InlineData("Prescription", "RxTransaction", "RefillNumber")]
    [InlineData("Prescription", "RxTransaction", "DaysSupply")]
    [InlineData("Inventory", "Item", "DeaSchedule")]
    public void ContractBaseline_CoversCanonicalCandidateMetadataFields(
        string schema,
        string table,
        string column)
    {
        using var engine = NewEngine();
        var source = new PioneerRxCanarySource(
            engine,
            NullLogger<PioneerRxCanarySource>.Instance);

        var baseline = source.GetContractBaseline();
        using var doc = JsonDocument.Parse(baseline.ContractJson);
        var objects = doc.RootElement.EnumerateArray();

        Assert.Contains(objects, obj =>
            string.Equals(obj.GetProperty("SchemaName").GetString(), schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(obj.GetProperty("TableName").GetString(), table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(obj.GetProperty("ColumnName").GetString(), column, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildBaselineFromObserved_UsesLiveObjectAndStatusFingerprints()
    {
        var baseline = PioneerRxCanarySource.BuildBaselineFromObserved(
            CompleteObservedObjects,
            CompleteObservedStatuses);

        Assert.Equal(
            ContractFingerprinter.HashObjects(CompleteObservedObjects),
            baseline.ObjectFingerprint);
        Assert.Equal(
            ContractFingerprinter.HashStatusMap(CompleteObservedStatuses),
            baseline.StatusMapFingerprint);

        var observed = new ObservedContract(
            CompleteObservedObjects,
            CompleteObservedStatuses,
            baseline.QueryFingerprint,
            baseline.ResultShapeFingerprint);
        var verification = SchemaCanaryClassifier.Classify(baseline, observed);

        Assert.True(verification.IsValid);
        Assert.Equal(CanarySeverity.None, verification.Severity);
        Assert.Contains("\"DataTypeName\":\"uniqueidentifier\"", baseline.ContractJson);
    }

    [Fact]
    public void BuildBaselineFromObserved_RejectsMissingRequiredCandidateField()
    {
        var missingRxNumber = CompleteObservedObjects
            .Where(o => !(
                o.SchemaName == "Prescription" &&
                o.TableName == "Rx" &&
                o.ColumnName == "RxNumber"))
            .ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PioneerRxCanarySource.BuildBaselineFromObserved(
                missingRxNumber,
                CompleteObservedStatuses));

        Assert.Contains("Prescription.Rx.RxNumber", ex.Message);
    }

    private static PioneerRxSqlEngine NewEngine() => new(
        "127.0.0.1,1433",
        "PioneerPharmacySystem",
        NullLogger<PioneerRxSqlEngine>.Instance);
}
