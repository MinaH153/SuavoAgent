namespace SuavoAgent.Core.Adapters;

/// <summary>
/// Read-only pharmacy adapter contract. Both PioneerRx and Computer-Rx
/// implementations land here (writes are separate verbs). Mock fixtures in
/// test projects also implement this surface so Mission Loop can be
/// exercised end-to-end against deterministic data.
///
/// This is the boundary the Mission Loop dispatches through. Production
/// adapters that cannot fulfill a method MUST throw a specific named
/// exception — never return empty results silently.
/// </summary>
public interface IPharmacyReadAdapter
{
    /// <summary>
    /// Short identifier for the PMS family this adapter targets —
    /// "pioneerrx", "computerrx", "enterpriserx", "mock".
    /// </summary>
    string AdapterType { get; }

    Task<PatientRecord?> LookupPatientAsync(
        string patientIdentifier,
        CancellationToken ct);

    Task<IReadOnlyList<RxHistoryRecord>> GetTopNdcsForPatientAsync(
        string patientId,
        int topN,
        CancellationToken ct);
}

public sealed record PatientRecord(
    string PatientId,
    string DisplayNameHash,
    DateTimeOffset LastActivityUtc
);

/// <summary>
/// Per-Rx history row returned from the adapter. NDC is the national drug
/// code — the key that lets the pricing pipeline look up AWP. No patient
/// name, no medication name, no free-form text: Context Assembler whitelist
/// (invariants §I.1.2) forbids those from crossing this boundary.
/// </summary>
public sealed record RxHistoryRecord(
    string Ndc,
    int FillCount,
    DateTimeOffset LastFillUtc,
    decimal LastQuantity
);
