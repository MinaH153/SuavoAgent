using System.Text.Json;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Workers;

internal sealed class PioneerRxShadowReplayHarness
{
    private readonly DateTimeOffset _serializedAtUtc;
    private readonly string _hmacSalt;
    private readonly string _hashKeyVersion;
    private readonly string _pharmacyId;
    private readonly string _agentInstallId;
    private readonly string _pmsVersion;
    private readonly IReadOnlyList<RxMetadata> _rxs;
    private readonly IReadOnlyDictionary<string, RxPatientDetails> _patients;
    private readonly IReadOnlyList<string> _forbiddenInCandidate;
    private readonly IReadOnlyList<string> _forbiddenEverywhere;
    private readonly IReadOnlyList<string> _minimumNecessaryInQueue;

    private PioneerRxShadowReplayHarness(
        DateTimeOffset serializedAtUtc,
        string hmacSalt,
        string hashKeyVersion,
        string pharmacyId,
        string agentInstallId,
        string pmsVersion,
        IReadOnlyList<RxMetadata> rxs,
        IReadOnlyDictionary<string, RxPatientDetails> patients,
        IReadOnlyList<string> forbiddenInCandidate,
        IReadOnlyList<string> forbiddenEverywhere,
        IReadOnlyList<string> minimumNecessaryInQueue)
    {
        _serializedAtUtc = serializedAtUtc;
        _hmacSalt = hmacSalt;
        _hashKeyVersion = hashKeyVersion;
        _pharmacyId = pharmacyId;
        _agentInstallId = agentInstallId;
        _pmsVersion = pmsVersion;
        _rxs = rxs;
        _patients = patients;
        _forbiddenInCandidate = forbiddenInCandidate;
        _forbiddenEverywhere = forbiddenEverywhere;
        _minimumNecessaryInQueue = minimumNecessaryInQueue;
    }

    public static PioneerRxShadowReplayHarness Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("PioneerRx shadow fixture path is required.", nameof(path));
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var patients = root.GetProperty("patients")
            .EnumerateArray()
            .Select(ReadPatient)
            .ToDictionary(p => p.RxNumber, p => p);

        return new PioneerRxShadowReplayHarness(
            root.GetProperty("serializedAtUtc").GetDateTimeOffset(),
            RequiredString(root, "hmacSalt"),
            RequiredString(root, "hashKeyVersion"),
            RequiredString(root, "pharmacyId"),
            RequiredString(root, "agentInstallId"),
            RequiredString(root, "pmsVersion"),
            root.GetProperty("rxs").EnumerateArray().Select(ReadRx).ToArray(),
            patients,
            ReadStringArray(root, "forbiddenInCandidate"),
            ReadStringArray(root, "forbiddenEverywhere"),
            ReadStringArray(root, "minimumNecessaryInQueue"));
    }

    public PioneerRxShadowReplayResult Replay(bool includeLegacyDeliveryQueue = false)
    {
        var payloadJson = RxDetectionWorker.SerializeRxBatch(
            _rxs,
            _hmacSalt,
            _patients,
            pharmacyId: _pharmacyId,
            agentInstallId: _agentInstallId,
            pmsVersion: _pmsVersion,
            hashKeyVersion: _hashKeyVersion,
            serializedAtUtc: _serializedAtUtc,
            includeLegacyDeliveryQueue: includeLegacyDeliveryQueue);

        using var doc = JsonDocument.Parse(payloadJson);
        var data = doc.RootElement.GetProperty("data");
        var candidateJson = data.GetProperty("rxOrderCandidates").GetRawText();
        var queuePresent = data.TryGetProperty("rxDeliveryQueue", out var queueElement);
        var queueJson = queuePresent ? queueElement.GetRawText() : string.Empty;

        EnsureAbsent(candidateJson, _forbiddenInCandidate, "forbiddenInCandidate");
        EnsureAbsent(payloadJson, _forbiddenEverywhere, "forbiddenEverywhere");

        if (includeLegacyDeliveryQueue)
        {
            EnsureAbsent(candidateJson, _minimumNecessaryInQueue, "minimumNecessaryInCandidate");
            EnsurePresent(queueJson, _minimumNecessaryInQueue, "minimumNecessaryInQueue");
        }
        else
        {
            EnsureAbsent(payloadJson, _forbiddenInCandidate, "forbiddenInCandidate");
            EnsureAbsent(payloadJson, _minimumNecessaryInQueue, "minimumNecessaryInQueue");
        }

        var candidates = data.GetProperty("rxOrderCandidates").EnumerateArray().ToArray();
        var evidenceIds = candidates
            .Select(candidate => candidate
                .GetProperty("provenance")
                .GetProperty("evidenceId")
                .GetString()!)
            .ToArray();

        return new PioneerRxShadowReplayResult(
            payloadJson,
            candidates.Length,
            queuePresent ? queueElement.GetArrayLength() : 0,
            evidenceIds,
            _serializedAtUtc,
            _pharmacyId,
            _agentInstallId,
            _pmsVersion,
            _hashKeyVersion);
    }

    private static RxMetadata ReadRx(JsonElement element) => new(
        RxNumber: RequiredString(element, "rxNumber"),
        DrugName: OptionalString(element, "drugName"),
        Ndc: OptionalString(element, "ndc"),
        DateFilled: element.GetProperty("dateFilled").GetDateTime(),
        Quantity: element.GetProperty("quantity").GetDecimal(),
        StatusGuid: element.GetProperty("statusGuid").GetGuid(),
        DetectedAt: element.GetProperty("detectedAt").GetDateTimeOffset(),
        FillNumber: element.GetProperty("fillNumber").GetInt32(),
        DaysSupply: element.GetProperty("daysSupply").GetInt32(),
        DrugSchedule: element.GetProperty("drugSchedule").GetInt32(),
        Priority: RequiredString(element, "priority"),
        TemperatureRequirement: RequiredString(element, "temperatureRequirement"));

    private static RxPatientDetails ReadPatient(JsonElement element) => new(
        RxNumber: RequiredString(element, "rxNumber"),
        FirstName: OptionalString(element, "firstName"),
        LastInitial: OptionalString(element, "lastInitial"),
        Phone: OptionalString(element, "phone"),
        Address1: OptionalString(element, "address1"),
        Address2: OptionalString(element, "address2"),
        City: OptionalString(element, "city"),
        State: OptionalString(element, "state"),
        Zip: OptionalString(element, "zip"));

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"PioneerRx shadow fixture is missing required property: {propertyName}");
        }

        return value;
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetString();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static void EnsureAbsent(string haystack, IReadOnlyList<string> tokens, string ruleName)
    {
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                throw BoundaryFailure(ruleName);
            }
        }
    }

    private static void EnsurePresent(string haystack, IReadOnlyList<string> tokens, string ruleName)
    {
        foreach (var token in tokens)
        {
            if (!haystack.Contains(token, StringComparison.Ordinal))
            {
                throw BoundaryFailure(ruleName);
            }
        }
    }

    private static InvalidOperationException BoundaryFailure(string ruleName) =>
        new($"PioneerRx shadow replay failed boundary check: {ruleName}");
}

internal sealed record PioneerRxShadowReplayResult(
    string PayloadJson,
    int CandidateCount,
    int LegacyQueueCount,
    IReadOnlyList<string> EvidenceIds,
    DateTimeOffset SerializedAtUtc,
    string PharmacyId,
    string AgentInstallId,
    string PmsVersion,
    string HashKeyVersion);
