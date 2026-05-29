using System.Collections.Concurrent;

namespace SuavoAgent.Core.Config;

/// <summary>
/// Process-lifetime credential store. NOT persistent — for unit tests and
/// non-Windows dev where DPAPI is unavailable. Never use in production on a
/// real workstation (the auth key would be lost on restart, forcing re-pair).
/// </summary>
public sealed class InMemoryCredentialStore : IEncryptedCredentialStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value) => _store[key] = value;

    public void Delete(string key) => _store.TryRemove(key, out _);
}
