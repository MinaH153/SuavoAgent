namespace SuavoAgent.Contracts.Models;

/// <summary>
/// HIPAA "minimum necessary" payload for /api/agent/patient-details cloud
/// upload. Codex 2026-04-26 audit caught the prior shape — SendPatientDetailsAsync
/// took an opaque <c>object details</c> and the only caller passed an
/// <see cref="RxPatientDetails"/> that included <c>RxNumber</c> as a cleartext
/// field even though the hashed version was already the routing key.
///
/// This record is the deliberate contract for what the cloud receives:
///   - Driver-needed fields ONLY: name, phone, delivery address
///   - NO RxNumber (the API call already passes it as <c>rxNumberHash</c>)
///   - NO MRN, DOB, diagnosis, medication name, prescriber, payer
///   - NO opaque catch-all — adding fields requires editing this record
///
/// The structural typing is the audit defense: any new PHI field shipped
/// to cloud has to land here first, which makes the diff impossible to miss
/// in code review.
/// </summary>
public sealed record PatientDetailsPayload(
    string? FirstName,
    string? LastInitial,
    string? Phone,
    string? Address1,
    string? Address2,
    string? City,
    string? State,
    string? Zip)
{
    /// <summary>
    /// Project from a full <see cref="RxPatientDetails"/> (which includes
    /// RxNumber + every PHI field the SQL adapter could read), keeping ONLY
    /// the driver-needed delivery fields. The RxNumber is dropped here — the
    /// cloud receives it via the separate <c>rxNumberHash</c> argument so
    /// we never ship the raw value alongside the hashed key.
    /// </summary>
    public static PatientDetailsPayload FromRxPatientDetails(RxPatientDetails source) =>
        new(
            FirstName: source.FirstName,
            LastInitial: source.LastInitial,
            Phone: source.Phone,
            Address1: source.Address1,
            Address2: source.Address2,
            City: source.City,
            State: source.State,
            Zip: source.Zip);
}
