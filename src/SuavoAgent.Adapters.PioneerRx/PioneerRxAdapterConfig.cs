using SuavoAgent.Contracts.Adapters;

namespace SuavoAgent.Adapters.PioneerRx;

/// <summary>
/// Builds the PioneerRx <see cref="AdapterConfig"/> from <see cref="PioneerRxConstants"/> —
/// the single source of PioneerRx's Core-facing identity, PHI policy, and SQL provenance.
/// The default catalog and schema signature are PioneerRx's own values, declared here so the
/// app-agnostic Core (worker, pricing factory) no longer hardcodes them.
/// </summary>
public static class PioneerRxAdapterConfig
{
    public const string AdapterType = "pioneerrx";

    public static AdapterConfig Create() => new()
    {
        AdapterType = AdapterType,
        DisplayName = "PioneerRx",
        Process = new ProcessIdentity(PioneerRxConstants.ProcessName, PioneerRxConstants.DefaultWindowTitle),
        Phi = PhiPolicy.Create(PioneerRxConstants.PhiColumnBlocklist, PioneerRxConstants.PhiColumnPatterns),
        Sql = new SqlProfile(
            DefaultCatalog: "PioneerPharmacySystem",
            SchemaSignature: "pioneerrx.sql.metadata.v1"),
    };
}
