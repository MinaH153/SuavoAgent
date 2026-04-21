namespace SuavoAgent.Attestation;

/// <summary>
/// When attestation detects a Mismatch or SignatureInvalid result, it calls
/// this signal. Implementations halt all mutation verbs on the agent while
/// observation (heartbeats) continues.
/// </summary>
public interface IAttestationHaltSignal
{
    /// <summary>True if we're currently in the halt state.</summary>
    bool IsHalted { get; }

    /// <summary>Why the halt happened (for audit + operator display).</summary>
    string? HaltReason { get; }

    /// <summary>When the halt was entered (UTC).</summary>
    DateTimeOffset? HaltedAt { get; }

    /// <summary>Move into the halt state. Idempotent — repeat calls update reason.</summary>
    void Halt(string reason);

    /// <summary>Move out of the halt state. Called only after operator re-enables via fleet portal.</summary>
    void Clear(string authorizedBy);
}

public sealed class AttestationHaltSignal : IAttestationHaltSignal
{
    private readonly object _lock = new();
    private bool _isHalted;
    private string? _reason;
    private DateTimeOffset? _haltedAt;

    public bool IsHalted { get { lock (_lock) return _isHalted; } }
    public string? HaltReason { get { lock (_lock) return _reason; } }
    public DateTimeOffset? HaltedAt { get { lock (_lock) return _haltedAt; } }

    public void Halt(string reason)
    {
        lock (_lock)
        {
            _isHalted = true;
            _reason = reason;
            _haltedAt ??= DateTimeOffset.UtcNow;
        }
    }

    public void Clear(string authorizedBy)
    {
        lock (_lock)
        {
            _isHalted = false;
            _reason = $"cleared_by:{authorizedBy}";
            _haltedAt = null;
        }
    }
}
