namespace SuavoAgent.Contracts.Models;

/// <summary>
/// The 4 component booleans that compose the agent health signal.
/// All Operational tier per <c>field-registry.md</c>; no PHI.
/// </summary>
public sealed record HealthCompositeComponents(
    bool HelperAttached,
    bool IpcConnected,
    bool SchemaCanaryGreen,
    bool ExtractionRecent);
