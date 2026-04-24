using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Deterministic in-memory pharmacy adapter for Mission Loop end-to-end
/// tests. Only implements the read surface — writes are a separate surface
/// and not part of Phase 1.
///
/// The fixture refuses to silently return empty data: a lookup for an
/// unknown identifier returns null (tests assert this path); a lookup for
/// a known identifier returns the seeded record.
/// </summary>
public sealed class MockPharmacyReadAdapter : IPharmacyReadAdapter
{
    public string AdapterType => "mock";

    private readonly Dictionary<string, PatientRecord> _patientsByIdentifier;
    private readonly Dictionary<string, List<RxHistoryRecord>> _historyByPatientId;

    public int LookupInvocationCount { get; private set; }
    public int HistoryInvocationCount { get; private set; }

    public MockPharmacyReadAdapter()
    {
        _patientsByIdentifier = new Dictionary<string, PatientRecord>(StringComparer.Ordinal);
        _historyByPatientId = new Dictionary<string, List<RxHistoryRecord>>(StringComparer.Ordinal);
    }

    public void SeedPatient(
        string identifier,
        string patientId,
        string displayNameHash,
        DateTimeOffset lastActivityUtc,
        params RxHistoryRecord[] history)
    {
        _patientsByIdentifier[identifier] = new PatientRecord(patientId, displayNameHash, lastActivityUtc);
        _historyByPatientId[patientId] = history.ToList();
    }

    public Task<PatientRecord?> LookupPatientAsync(string patientIdentifier, CancellationToken ct)
    {
        LookupInvocationCount++;
        _patientsByIdentifier.TryGetValue(patientIdentifier, out var record);
        return Task.FromResult<PatientRecord?>(record);
    }

    public Task<IReadOnlyList<RxHistoryRecord>> GetTopNdcsForPatientAsync(
        string patientId,
        int topN,
        CancellationToken ct)
    {
        HistoryInvocationCount++;
        if (!_historyByPatientId.TryGetValue(patientId, out var rows))
        {
            return Task.FromResult<IReadOnlyList<RxHistoryRecord>>(Array.Empty<RxHistoryRecord>());
        }

        var ranked = rows
            .OrderByDescending(r => r.FillCount)
            .ThenByDescending(r => r.LastFillUtc)
            .Take(topN)
            .ToArray();
        return Task.FromResult<IReadOnlyList<RxHistoryRecord>>(ranked);
    }
}
