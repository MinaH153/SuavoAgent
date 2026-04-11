namespace SuavoAgent.Contracts.Models;

/// <summary>
/// PHI-free prescription metadata for detection polling.
/// Contains ZERO patient data (HIPAA 164.502(b) minimum necessary).
/// </summary>
public record RxMetadata(
    string RxNumber,
    string? DrugName,
    string? Ndc,
    DateTime? DateFilled,
    decimal Quantity,
    Guid StatusGuid,
    DateTimeOffset DetectedAt);
