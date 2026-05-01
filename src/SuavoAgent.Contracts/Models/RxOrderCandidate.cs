namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Canonical delivery-order candidate extracted from a local PMS.
/// Plain Rx numbers never cross this contract; the external key is a
/// per-pharmacy HMAC of the Rx number generated on the agent.
/// </summary>
public sealed record RxOrderCandidate(
    string SourceExternalKeyHash,
    string SourceExternalKeyKind,
    string? MedicationDisplay,
    string? Ndc,
    decimal Quantity,
    int FillNumber,
    int DaysSupply,
    string StatusGuid,
    bool IsControlled,
    int? DrugSchedule,
    bool PatientIdRequired,
    bool CounselingRequired,
    string Priority,
    string TemperatureRequirement,
    DateTimeOffset DetectedAt,
    DetectionSource Source,
    double Confidence,
    string SchemaSignature,
    string? WindowSignature,
    string LocalEvidenceId,
    IReadOnlyDictionary<string, RxFieldProvenance> Provenance,
    IReadOnlyList<string> Warnings,
    string? PatientFirstName = null,
    string? PatientLastInitial = null,
    string? PatientPhone = null,
    string? DeliveryAddress1 = null,
    string? DeliveryAddress2 = null,
    string? DeliveryCity = null,
    string? DeliveryState = null,
    string? DeliveryZip = null);

public sealed record RxFieldProvenance(
    DetectionSource Source,
    string SourceDetail,
    double Confidence,
    string Classification,
    string? EvidenceId = null,
    string? Signature = null);
