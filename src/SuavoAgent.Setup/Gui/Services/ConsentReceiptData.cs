using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// In-memory shape of the consent receipt the operator signs off on during the
/// Consent step. Serialized to <c>C:\ProgramData\SuavoAgent\consent-receipt.json</c>
/// by the install orchestrator; <c>HeartbeatWorker</c> reads it and uploads the
/// bytes verbatim on the first heartbeat. The on-disk schema is frozen to match
/// <c>bootstrap.ps1</c> so the cloud side stays single-source-of-truth.
/// </summary>
internal sealed record ConsentReceiptData(
    string AuthorizingName,
    string AuthorizingTitle,
    string BusinessState,
    bool MandatoryNoticeState,
    bool EmployeeNoticeAcknowledged,
    DateTimeOffset Timestamp)
{
    public static readonly IReadOnlySet<string> MandatoryNoticeStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CT", "DE", "NY" };

    public static readonly IReadOnlySet<string> HighRiskStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CA", "IL", "MA", "MD", "CO", "MT" };

    public static bool RequiresMandatoryNotice(string state) =>
        MandatoryNoticeStates.Contains(state.Trim().ToUpperInvariant());

    public static bool IsHighRisk(string state) =>
        HighRiskStates.Contains(state.Trim().ToUpperInvariant());

    /// <summary>
    /// Serializes to the JSON shape expected by HeartbeatWorker. Adds pharmacyId,
    /// agentId, installerVersion, and machineFingerprint from the install context.
    /// </summary>
    public string ToJson(string pharmacyId, string agentId, string installerVersion, string machineFingerprint)
    {
        var receipt = new Receipt(
            consentVersion: "1.0",
            authorizingParty: new AuthorizingParty(AuthorizingName, AuthorizingTitle),
            businessState: BusinessState.Trim().ToUpperInvariant(),
            mandatoryNoticeState: MandatoryNoticeState,
            consentTimestamp: Timestamp.ToString("o"),
            termsAccepted: true,
            employeeNoticeAcknowledged: EmployeeNoticeAcknowledged,
            installerVersion: installerVersion,
            machineFingerprint: machineFingerprint,
            pharmacyId: pharmacyId,
            agentId: agentId,
            source: "gui_installer");

        return JsonSerializer.Serialize(receipt, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Receipt(
        [property: JsonPropertyName("consentVersion")] string consentVersion,
        [property: JsonPropertyName("authorizingParty")] AuthorizingParty authorizingParty,
        [property: JsonPropertyName("businessState")] string businessState,
        [property: JsonPropertyName("mandatoryNoticeState")] bool mandatoryNoticeState,
        [property: JsonPropertyName("consentTimestamp")] string consentTimestamp,
        [property: JsonPropertyName("termsAccepted")] bool termsAccepted,
        [property: JsonPropertyName("employeeNoticeAcknowledged")] bool employeeNoticeAcknowledged,
        [property: JsonPropertyName("installerVersion")] string installerVersion,
        [property: JsonPropertyName("machineFingerprint")] string machineFingerprint,
        [property: JsonPropertyName("pharmacyId")] string pharmacyId,
        [property: JsonPropertyName("agentId")] string agentId,
        [property: JsonPropertyName("source")] string source);

    private sealed record AuthorizingParty(
        [property: JsonPropertyName("name")] string name,
        [property: JsonPropertyName("title")] string title);
}
