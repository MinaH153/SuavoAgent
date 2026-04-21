namespace SuavoAgent.Verbs;

/// <summary>
/// In-memory registry of all verbs the agent knows how to execute. Loaded
/// from the signed verb catalog bundle at startup + refreshed on
/// ConfigSyncWorker signaled updates.
/// </summary>
public interface IVerbRegistry
{
    /// <summary>
    /// Look up a verb by name + version. Returns null if (name, version) pair
    /// is not registered. Version match is EXACT — schema-version fail-closed
    /// per action-grammar-v1.md §Fail-closed on schema mismatch.
    /// </summary>
    IVerb? Resolve(string name, string version);

    /// <summary>SHA-256 of the canonical metadata shape for (name, version).</summary>
    string SchemaHash(string name, string version);

    /// <summary>All currently-registered verbs.</summary>
    IEnumerable<VerbMetadata> AllMetadata();
}
