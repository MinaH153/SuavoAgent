namespace SuavoAgent.Contracts.Canary;

/// Approved detection contract baseline with 4 component hashes.
/// Stored in schema_canary_baselines table.
public record ContractBaseline(
    string AdapterType,
    string ObjectFingerprint,
    string StatusMapFingerprint,
    string QueryFingerprint,
    string ResultShapeFingerprint,
    string ContractFingerprint,
    string ContractJson,
    int SchemaEpoch,
    int ContractVersion = 1);
