namespace SuavoAgent.Contracts.Canary;

/// Raw observed schema metadata from preflight queries.
/// Fed into SchemaCanaryClassifier alongside the approved baseline.
public record ObservedContract(
    IReadOnlyList<ObservedObject> Objects,
    IReadOnlyList<ObservedStatus> StatusMap,
    string QueryFingerprint,
    string? ResultShapeFingerprint);

public record ObservedObject(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataTypeName,
    int? MaxLength,
    bool IsNullable,
    bool IsRequired);

public record ObservedStatus(
    string Description,
    string GuidValue);
