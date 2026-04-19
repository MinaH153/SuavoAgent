using System.Text.Json;

namespace SuavoAgent.Contracts.Cloud;

/// <summary>
/// Wire shape of GET /api/agent/config — what the cloud returns when the
/// agent asks for its current config-override set. Mirrors the Next.js
/// handler at src/app/api/agent/config/route.ts.
/// </summary>
public sealed record ConfigOverrideResponse
{
    public bool Success { get; init; }
    public string AsOf { get; init; } = string.Empty;
    public IReadOnlyList<ConfigOverride> Overrides { get; init; } = Array.Empty<ConfigOverride>();
}

/// <summary>
/// One override row. <see cref="Path"/> uses appsettings.json dotted
/// notation (e.g. "Reasoning.PricingBrainEnabled"). <see cref="Value"/> is
/// a JSON scalar/array/object serialized to string when flattening for
/// appsettings layering — the cloud stores it as JSONB and hands it back
/// as a JsonElement.
/// </summary>
public sealed record ConfigOverride
{
    public string Path { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
    public string Scope { get; init; } = "pharmacy";
    public string UpdatedAt { get; init; } = string.Empty;
}
