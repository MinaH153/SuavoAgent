using Microsoft.Extensions.Logging;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;
using System.Text.Json;

namespace SuavoAgent.Adapters.PioneerRx.Canary;

public sealed class PioneerRxCanarySource : ICanaryDetectionSource
{
    private readonly PioneerRxSqlEngine _engine;
    private readonly ILogger _logger;

    private static readonly (string Schema, string Table, string Column, bool IsRequired)[] ContractedObjects =
    {
        ("Prescription", "RxTransaction", "RxTransactionID", true),
        ("Prescription", "RxTransaction", "DateFilled", true),
        ("Prescription", "RxTransaction", "RefillNumber", true),
        ("Prescription", "RxTransaction", "DispensedQuantity", true),
        ("Prescription", "RxTransaction", "DaysSupply", true),
        ("Prescription", "RxTransaction", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransaction", "RxID", true),
        ("Prescription", "RxTransaction", "DispensedItemID", true),
        ("Prescription", "Rx", "RxID", true),
        ("Prescription", "Rx", "RxNumber", true),
        ("Prescription", "RxTransactionStatusType", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransactionStatusType", "Description", true),
        ("Inventory", "Item", "ItemID", true),
        ("Inventory", "Item", "ItemName", true),
        ("Inventory", "Item", "NDC", true),
        ("Inventory", "Item", "DeaSchedule", true),
    };

    private static string QueryTemplate => PioneerRxSqlEngine.BuildMetadataQuery(
        PioneerRxConstants.DeliveryReadyStatusNames);

    private static readonly (string Name, string TypeName)[] ExpectedResultShape =
    {
        ("RxNumber", "int"),
        ("DateFilled", "datetime"),
        ("RefillNumber", "int"),
        ("DispensedQuantity", "decimal"),
        ("DaysSupply", "int"),
        ("TradeName", "nvarchar"),
        ("NDC", "nvarchar"),
        ("DeaSchedule", "int"),
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

    public async Task<ContractBaseline> EstablishBaselineAsync(CancellationToken ct)
    {
        var observedObjects = await _engine.QueryContractMetadataAsync(ContractedObjects, ct);
        var observedStatuses = await _engine.QueryStatusMapAsync(
            PioneerRxConstants.DeliveryReadyStatusNames.ToList(), ct);

        return BuildBaselineFromObserved(observedObjects, observedStatuses);
    }

    public static ContractBaseline BuildBaselineFromObserved(
        IReadOnlyList<ObservedObject> observedObjects,
        IReadOnlyList<ObservedStatus> observedStatuses,
        int schemaEpoch = 1)
    {
        var missingObjects = FindMissingRequiredObjects(observedObjects);
        if (missingObjects.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot establish PioneerRx canary baseline; missing required schema objects: " +
                string.Join(", ", missingObjects));
        }

        var missingStatuses = PioneerRxConstants.DeliveryReadyStatusNames
            .Where(status => !observedStatuses.Any(observed =>
                string.Equals(observed.Description, status, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingStatuses.Length > 0)
        {
            throw new InvalidOperationException(
                "Cannot establish PioneerRx canary baseline; missing delivery-ready statuses: " +
                string.Join(", ", missingStatuses));
        }

        var duplicateStatuses = observedStatuses
            .GroupBy(status => status.Description, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateStatuses.Length > 0)
        {
            throw new InvalidOperationException(
                "Cannot establish PioneerRx canary baseline; duplicate delivery-ready statuses: " +
                string.Join(", ", duplicateStatuses));
        }

        var objHash = ContractFingerprinter.HashObjects(observedObjects);
        var statusHash = ContractFingerprinter.HashStatusMap(observedStatuses);
        var queryHash = ContractFingerprinter.HashQuery(QueryTemplate);
        var shapeHash = ContractFingerprinter.HashResultShape(ExpectedResultShape);
        var contractJson = JsonSerializer.Serialize(observedObjects.Select(o => new
        {
            o.SchemaName,
            o.TableName,
            o.ColumnName,
            o.DataTypeName,
            o.MaxLength,
            o.IsNullable,
            o.IsRequired
        }));

        return new ContractBaseline(
            "pioneerrx",
            objHash,
            statusHash,
            queryHash,
            shapeHash,
            ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            contractJson,
            schemaEpoch);
    }

    private static IReadOnlyList<string> FindMissingRequiredObjects(
        IReadOnlyList<ObservedObject> observedObjects)
    {
        var observedKeys = observedObjects
            .Select(o => $"{o.SchemaName}.{o.TableName}.{o.ColumnName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ContractedObjects
            .Where(o => o.IsRequired)
            .Select(o => $"{o.Schema}.{o.Table}.{o.Column}")
            .Where(required => !observedKeys.Contains(required))
            .ToArray();
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
            ContractFingerprinter.HashResultShape(ExpectedResultShape));

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
