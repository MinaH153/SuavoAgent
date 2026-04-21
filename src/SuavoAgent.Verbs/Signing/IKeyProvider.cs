namespace SuavoAgent.Verbs.Signing;

/// <summary>
/// Provides the current HMAC signing key for inbound command verification.
/// Delegated so that <c>ConfigSyncWorker</c>-driven key rotation replaces the
/// key without replacing the verifier instance.
/// </summary>
public interface IKeyProvider
{
    /// <summary>Current 32-byte HMAC-SHA256 key.</summary>
    byte[] CurrentKey();

    /// <summary>
    /// During the 24-hour rotation grace window, the previous key is still
    /// accepted. Null outside the grace window. See key-custody.md §Rotation protocol.
    /// </summary>
    byte[]? PreviousKey();
}

public sealed class StaticKeyProvider : IKeyProvider
{
    private readonly byte[] _key;
    public StaticKeyProvider(byte[] key) => _key = key;
    public byte[] CurrentKey() => _key;
    public byte[]? PreviousKey() => null;
}
