namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Canonical delivery-order candidate extracted from a local PMS.
/// Plain Rx numbers, patient names, patient phones, patient addresses, and
/// medication names never cross this contract. Hashes use the pharmacy-scoped
/// HMAC key currently held by the agent.
/// </summary>
public sealed record RxOrderCandidate(
    string RxHash,
    RxOrderMedication Medication,
    RxOrderPatientDelivery PatientDelivery,
    RxOrderCandidateProvenance Provenance,
    double Confidence,
    IReadOnlyDictionary<string, double> FieldConfidence,
    IReadOnlyDictionary<string, RxFieldProvenance> FieldProvenance,
    IReadOnlyList<RxOrderCandidateWarning> Warnings,
    int SchemaVersion);

public sealed record RxOrderMedication(
    string? NameHash,
    string? Ndc,
    string? Strength,
    string? Form,
    decimal Quantity,
    int DaysSupply,
    int Refills,
    bool IsControlled,
    int? DrugSchedule,
    bool PatientIdRequired,
    bool CounselingRequired,
    string Priority,
    string TemperatureRequirement);

public sealed record RxOrderPatientDelivery(
    string? NameHash,
    string? AddressLine1Hash,
    string? AddressLine2Hash,
    string? City,
    string? State,
    string? Zip5,
    bool Zip4Present,
    string? PhoneHash,
    IReadOnlyList<RxMissingAddressFlag> MissingAddressFlags);

public sealed record RxOrderCandidateProvenance(
    string? PharmacyId,
    string? AgentInstallId,
    string EvidenceId,
    string Pms,
    string? PmsVersion,
    DetectionSource ExtractionMethod,
    DateTimeOffset CapturedAtUtc,
    string ScanWindowId,
    string SchemaSignature,
    string? WindowSignature,
    string HashKeyVersion);

public sealed record RxFieldProvenance(
    DetectionSource Source,
    string SourceDetail,
    double Confidence,
    string Classification,
    string? EvidenceId = null,
    string? Signature = null);

public enum RxOrderCandidateWarning
{
    MissingPatientIdentity,
    MissingDeliveryAddress,
    MissingZip5,
    AmbiguousAddress,
    ControlledSubstanceNoDeaMatch,
    SchemaCanaryDrift,
    SchemaCanaryObject,
    SchemaCanaryColumn,
    SchemaCanaryIndex,
    SchemaCanaryType
}

public enum RxMissingAddressFlag
{
    MissingName,
    MissingPhone,
    MissingAddressLine1,
    MissingCity,
    MissingState,
    MissingZip5
}
