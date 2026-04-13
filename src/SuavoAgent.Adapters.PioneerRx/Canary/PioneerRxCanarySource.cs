using Microsoft.Extensions.Logging;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Adapters.PioneerRx.Canary;

public sealed class PioneerRxCanarySource : ICanaryDetectionSource
{
    private readonly PioneerRxSqlEngine _engine;
    private readonly ILogger _logger;

    private static readonly (string Schema, string Table, string Column, bool IsRequired)[] ContractedObjects =
    {
        ("Prescription", "RxTransaction", "RxTransactionID", true),
        ("Prescription", "RxTransaction", "DateFilled", true),
        ("Prescription", "RxTransaction", "DispensedQuantity", true),
        ("Prescription", "RxTransaction", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransaction", "RxID", true),
        ("Prescription", "RxTransaction", "DispensedItemID", true),
        ("Prescription", "Rx", "RxID", true),
        ("Prescription", "Rx", "RxNumber", true),
        ("Prescription", "RxTransactionStatusType", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransactionStatusType", "Description", true),
        ("Inventory", "Item", "ItemID", false),
        ("Inventory", "Item", "ItemName", false),
        ("Inventory", "Item", "NDC", false),
    };

    private const string QueryTemplate =
        "SELECT TOP 50 r.RxNumber, rt.DateFilled, rt.DispensedQuantity, " +
        "i.ItemName AS TradeName, i.NDC, rt.RxTransactionStatusTypeID AS StatusGuid " +
        "FROM Prescription.RxTransaction rt JOIN Prescription.Rx r ON rt.RxID = r.RxID " +
        "LEFT JOIN Inventory.Item i ON rt.DispensedItemID = i.ItemID " +
        "LEFT JOIN Prescription.RxTransactionStatusType st ON rt.RxTransactionStatusTypeID = st.RxTransactionStatusTypeID " +
        "WHERE st.Description IN ({statusParams}) AND rt.DateFilled >= @cutoff " +
        "ORDER BY rt.DateFilled DESC";

    private static readonly (string Name, string TypeName)[] ExpectedResultShape =
    {
        ("RxNumber", "int"),
        ("DateFilled", "datetime"),
        ("DispensedQuantity", "decimal"),
        ("TradeName", "nvarchar"),
        ("NDC", "nvarchar"),
        ("StatusGuid", "uniqueidentifier"),
    };

    public string AdapterType => "pioneerrx";

    public PioneerRxCanarySource(PioneerRxSqlEngine engine, ILogger<PioneerRxCanarySource> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public ContractBaseline GetContractBaseline()
    {
        var objects = ContractedObjects.Select(c =>
            new ObservedObject(c.Schema, c.Table, c.Column, "", null, false, c.IsRequired)).ToList();
        var objHash = ContractFingerprinter.HashObjects(objects);
        var queryHash = ContractFingerprinter.HashQuery(QueryTemplate);
        var shapeHash = ContractFingerprinter.HashResultShape(ExpectedResultShape);
        var contractJson = System.Text.Json.JsonSerializer.Serialize(
            objects.Select(o => new { o.SchemaName, o.TableName, o.ColumnName, o.DataTypeName, o.IsRequired }));

        // Status map hash empty for template — populated during establishment
        return new ContractBaseline(AdapterType, objHash, "", queryHash, shapeHash,
            ContractFingerprinter.CompositeHash(objHash, "", queryHash, shapeHash),
            contractJson, 1);
    }

    public async Task<ContractVerification> VerifyPreflightAsync(
        ContractBaseline approved, CancellationToken ct)
    {
        var observedObjects = await _engine.QueryContractMetadataAsync(ContractedObjects, ct);
        var observedStatuses = await _engine.QueryStatusMapAsync(
            PioneerRxConstants.DeliveryReadyStatusNames.ToList(), ct);

        var observed = new ObservedContract(
            observedObjects, observedStatuses,
            ContractFingerprinter.HashQuery(QueryTemplate),
            null);

        return SchemaCanaryClassifier.Classify(approved, observed);
    }

    public async Task<DetectionResult> DetectWithCanaryAsync(
        ContractBaseline approved, CancellationToken ct)
    {
        var connIdBefore = _engine.ConnectionId;

        var preflight = await VerifyPreflightAsync(approved, ct);
        if (!preflight.IsValid && preflight.Severity == CanarySeverity.Critical)
            return new DetectionResult(Array.Empty<RxMetadata>(), preflight);

        if (_engine.ConnectionId != connIdBefore)
        {
            _logger.LogWarning("Connection reset between preflight and detection — aborting cycle");
            return new DetectionResult(Array.Empty<RxMetadata>(),
                new ContractVerification(false, CanarySeverity.Critical,
                    new[] { "connection" }, null, null, "Connection reset during cycle"));
        }

        var rxs = await _engine.ReadReadyMetadataAsync(ct);
        return new DetectionResult(rxs, preflight);
    }
}
