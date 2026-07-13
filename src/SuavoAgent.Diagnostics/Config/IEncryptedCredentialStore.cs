namespace SuavoAgent.Core.Config;

/// <summary>
/// Machine-protected credential storage shared by Setup and Core. Implementations
/// must make a multi-value update visible atomically so identity metadata and the
/// corresponding authentication key cannot be torn across a crash.
/// </summary>
public interface IEncryptedCredentialStore
{
    string? Get(string key);

    void Set(string key, string value);

    void SetMany(IReadOnlyDictionary<string, string> values);

    void Delete(string key);

    void DeleteMany(IReadOnlyCollection<string> keys);
}
