namespace SuavoAgent.Core.Config;

/// <summary>
/// Abstraction over OS-native at-rest credential storage for the agent's
/// auth key (and future secrets), used by the v4 device-code onboarding flow
/// instead of plaintext setup.json.
///
/// Implementations:
///   - <see cref="DpapiCredentialStore"/> — Windows DPAPI (LocalMachine), the
///     only first-class target for Phase 1.
///   - <see cref="InMemoryCredentialStore"/> — process-lifetime only; for
///     tests and non-Windows dev.
///   - macOS Keychain — deferred to v5 (the Mac runtime port; see spec §3).
///
/// Keys are short stable identifiers (e.g. "AuthKey", "AgentId"). Values are
/// opaque strings. Reads return null when absent.
/// </summary>
public interface IEncryptedCredentialStore
{
    /// <summary>Returns the stored value for <paramref name="key"/>, or null if absent.</summary>
    string? Get(string key);

    /// <summary>Stores (or overwrites) <paramref name="value"/> under <paramref name="key"/>.</summary>
    void Set(string key, string value);

    /// <summary>Removes <paramref name="key"/> if present; no-op otherwise.</summary>
    void Delete(string key);
}
