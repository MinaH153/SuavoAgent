using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuavoAgent.Setup;

/// <summary>Neutral parser shared by every device-code response surface.</summary>
public static class VerticalConfigPayloadParser
{
    public static ParsedVerticalConfig Parse(JsonObject? data)
    {
        if (data is null ||
            !data.TryGetPropertyValue("verticalConfig", out var configNode) ||
            configNode is null)
            return new(null, null, null, null);

        var raw = configNode.ToJsonString();
        VerticalConfigDto? dto = null;
        try { dto = JsonSerializer.Deserialize<VerticalConfigDto>(raw); }
        catch (JsonException) { }

        var signature = data.TryGetPropertyValue(
            "verticalConfigSignature", out var signatureNode)
            ? signatureNode?.GetValue<string?>()
            : null;
        var keyId = data.TryGetPropertyValue("verticalConfigKeyId", out var keyNode)
            ? keyNode?.GetValue<string?>()
            : null;
        return new(raw, dto, signature, keyId);
    }
}
