namespace SuavoAgent.Contracts.Adapters;

/// <summary>
/// Core-facing configuration for one PMS/app adapter, keyed by <see cref="AdapterType"/>
/// (the same family key as <c>IPharmacyReadAdapter.AdapterType</c>). Holds only what the
/// app-agnostic Core needs — identity, PHI policy, and SQL provenance/catalog. PMS-internal
/// detail (status vocabulary, schema/GUID discovery) stays inside the adapter project.
/// </summary>
public sealed record AdapterConfig
{
    /// <summary>Normalized PMS-family key: "pioneerrx", "computerrx", ...</summary>
    public required string AdapterType { get; init; }

    /// <summary>Human label, also the <c>Pms</c> provenance value stamped on detection payloads.</summary>
    public required string DisplayName { get; init; }

    public required ProcessIdentity Process { get; init; }
    public required PhiPolicy Phi { get; init; }
    public required SqlProfile Sql { get; init; }
}

/// <summary>Process/window identity used to recognize the target app.</summary>
public sealed record ProcessIdentity(string ProcessName, string DefaultWindowTitle);

/// <summary>SQL provenance + the default catalog the Core falls back to when a pharmacy omits one.</summary>
public sealed record SqlProfile(string DefaultCatalog, string SchemaSignature);
