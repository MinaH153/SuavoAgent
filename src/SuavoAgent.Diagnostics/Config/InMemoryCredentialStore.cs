using System.Collections.Concurrent;

namespace SuavoAgent.Core.Config;

/// <summary>Process-local test store. Never selected by production startup.</summary>
public sealed class InMemoryCredentialStore : IEncryptedCredentialStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public string? Get(string key) => _store.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string value) => SetMany(
        new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_store)
        {
            foreach (var pair in values)
            {
                ValidateEntry(pair.Key, pair.Value);
                _store[pair.Key] = pair.Value;
            }
        }
    }

    public void Delete(string key) => DeleteMany([key]);

    public void DeleteMany(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_store)
        {
            foreach (var key in keys)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                _store.TryRemove(key, out _);
            }
        }
    }

    private static void ValidateEntry(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
    }
}
