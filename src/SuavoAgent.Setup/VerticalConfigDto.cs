using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SuavoAgent.Setup;

public sealed record VerticalFraming(
    [property: JsonPropertyName("productNoun")] string ProductNoun,
    [property: JsonPropertyName("systemNoun")]  string SystemNoun,
    [property: JsonPropertyName("businessNoun")] string BusinessNoun,
    [property: JsonPropertyName("idLabel")]     string IdLabel);

public sealed record VerticalCompliance(
    [property: JsonPropertyName("baaRequired")]   bool   BaaRequired,
    [property: JsonPropertyName("consentCopyId")] string ConsentCopyId);

public sealed record VerticalConfigDto(
    [property: JsonPropertyName("vertical")]           string            Vertical,
    [property: JsonPropertyName("complianceMode")]     string            ComplianceMode,
    [property: JsonPropertyName("systemConnector")]    string            SystemConnector,
    [property: JsonPropertyName("connectorLabel")]     string            ConnectorLabel,
    [property: JsonPropertyName("redactionProfileId")] string            RedactionProfileId,
    [property: JsonPropertyName("framing")]            VerticalFraming   Framing,
    [property: JsonPropertyName("compliance")]         VerticalCompliance Compliance)
{
    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Vertical)
        && ComplianceMode is "hipaa" or "pci" or "none"
        && SystemConnector is "pioneerrx" or "none"
        && Framing is not null
        && Compliance is not null;
}

/// <summary>Result of parsing the verticalConfig field from a cloud response.
/// Raw==null means field was absent; Raw!=null + Dto==null means present but malformed.</summary>
public sealed record ParsedVerticalConfig(
    string?          Raw,
    VerticalConfigDto? Dto,
    string?          Signature,
    string?          KeyId);
