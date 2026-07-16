using System.Text.Json.Serialization;

namespace SuavoAgent.Setup;

/// <summary>
/// In-memory configuration returned by approved device-code pairing. The native
/// installer intentionally has no setup-file or API-key command-line ingress.
/// </summary>
public sealed record SetupConfig(
    [property: JsonPropertyName("pharmacy_id")] string PharmacyId,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("cloud_url")] string CloudUrl,
    [property: JsonPropertyName("release_tag")] string ReleaseTag,
    [property: JsonPropertyName("learning_mode")] bool LearningMode,
    [property: JsonPropertyName("agent_id")] string AgentId = "",
    // Set by the device-code/install-context flow so the installer can bake
    // the on-device brain config. Null installs rules-only and self-heals later.
    [property: JsonIgnore] AgentReasoningConfig? Reasoning = null,
    // Vertical-config fields are resolved to compliance posture + connector;
    // they are never persisted to appsettings.json directly.
    [property: JsonIgnore] string? VerticalConfigRaw = null,
    [property: JsonIgnore] VerticalConfigDto? VerticalConfig = null,
    [property: JsonIgnore] string? VerticalConfigSignature = null,
    [property: JsonIgnore] string? VerticalConfigKeyId = null,
    // Pairing identifier retained only through the durable authority cutover.
    [property: JsonIgnore] string? DeviceCode = null,
    // Public identity of the versioned pending TPM key. Setup promotes this
    // exact slot only after the replacement install passes its health gate.
    [property: JsonIgnore] string? DeviceKeyId = null,
    [property: JsonIgnore] string? DeviceKeyName = null,
    // SYSTEM-only TPM authority used for privileged local approvals. Core cannot sign with it.
    [property: JsonIgnore] string? MaintenanceKeyId = null,
    [property: JsonIgnore] string? DeviceFingerprint = null,
    // Server-issued 256-bit challenge. Only its hash is stored server-side;
    // the pending TPM key must sign it before cloud authority is promoted.
    [property: JsonIgnore] string? DeviceChallenge = null);
