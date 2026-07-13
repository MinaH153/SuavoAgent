using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Vision;

public interface IVisionConfigurationStore
{
    VisionRegistryReadResult Read();
    void Write(string value);
}

public sealed class WindowsVisionConfigurationStore : IVisionConfigurationStore
{
    public VisionRegistryReadResult Read() => VisionRegistryAuthority.ReadState();

    public void Write(string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The vision registry authority exists only on Windows.");
        VisionRegistryAuthority.WriteState(value);
    }
}

public sealed record VisionConfigurationLoadResult(
    bool IsValid,
    bool IsMissing,
    string Code,
    VisionOptionsSnapshot EffectiveOptions,
    VisionConfigurationState? State = null)
{
    public long EffectiveGeneration => State?.Generation ?? 0;
}

public static class VisionConfigurationRegistry
{
    public static VisionConfigurationLoadResult Load(
        IVisionConfigurationStore store,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        var raw = store.Read();
        if (raw.Status == VisionRegistryReadStatus.Missing)
        {
            return new(
                true,
                true,
                raw.Code,
                VisionOptionsSnapshot.DisabledDefault());
        }
        if (raw.Status != VisionRegistryReadStatus.Present || raw.Value is null)
        {
            return new(
                false,
                false,
                raw.Code,
                VisionOptionsSnapshot.DisabledDefault());
        }

        var parsed = VisionConfigurationStateCodec.Parse(raw.Value, dataDirectory);
        return parsed.IsValid && parsed.State is not null
            ? new(
                true,
                false,
                "active",
                parsed.State.VisionOptions,
                parsed.State)
            : new(
                false,
                false,
                parsed.Code,
                VisionOptionsSnapshot.DisabledDefault());
    }
}

public sealed record VisionConfigurationApplyResult(
    bool Succeeded,
    string Code,
    VisionConfigurationState? State = null,
    bool IdempotentReplay = false);

/// <summary>
/// Serializes generation/replay decisions for <c>set_vision_config</c>. The
/// backing registry value itself is atomic; this gate prevents two in-process
/// commands from deriving the same next generation.
/// </summary>
public sealed class VisionConfigurationCoordinator
{
    private readonly IVisionConfigurationStore _store;
    private readonly string _dataDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    public VisionConfigurationCoordinator(
        IVisionConfigurationStore store,
        string dataDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public VisionConfigurationApplyResult Apply(
        string commandId,
        VisionOptionsSnapshot options)
    {
        lock (_gate)
        {
            var current = VisionConfigurationRegistry.Load(_store, _dataDirectory);
            if (!current.IsValid)
                return new(false, current.Code);

            var digest = VisionConfigurationStateCodec.ComputeConfigDigest(options);
            if (current.State is not null &&
                string.Equals(current.State.CommandId, commandId, StringComparison.Ordinal))
            {
                return string.Equals(
                    current.State.ConfigDigest,
                    digest,
                    StringComparison.Ordinal)
                    ? new(true, "idempotent_replay", current.State, true)
                    : new(false, "vision_command_replay_conflict", current.State);
            }

            if (current.EffectiveGeneration == long.MaxValue)
                return new(false, "vision_generation_exhausted", current.State);

            VisionConfigurationState next;
            string serialized;
            try
            {
                next = VisionConfigurationStateCodec.Create(
                    current.EffectiveGeneration + 1,
                    commandId,
                    _utcNow().ToUniversalTime(),
                    options,
                    _dataDirectory);
                serialized = VisionConfigurationStateCodec.Serialize(next, _dataDirectory);
                _store.Write(serialized);
            }
            catch (Exception exception) when (exception is
                       ArgumentException or InvalidOperationException or
                       UnauthorizedAccessException or IOException or
                       PlatformNotSupportedException or System.Security.SecurityException)
            {
                return new(
                    false,
                    $"vision_registry_write_failed_{exception.GetType().Name}",
                    current.State);
            }

            var verified = VisionConfigurationRegistry.Load(_store, _dataDirectory);
            if (!verified.IsValid || verified.State is null ||
                verified.State != next)
                return new(false, "vision_registry_write_verification_failed", current.State);
            return new(true, "applied", verified.State);
        }
    }
}

public sealed record VisionConfigurationTelemetry(
    string Status,
    long EffectiveGeneration,
    long? StagedGeneration,
    string EffectiveState,
    string StagedState,
    string? EffectiveDigest,
    string? StagedDigest,
    string? LastStructuralFailure,
    string? LastStructuralFailureAt);

/// <summary>
/// Keeps the startup-effective state distinct from the current registry state,
/// making restart-required and invalid staged updates visible in heartbeats.
/// </summary>
public sealed class VisionConfigurationStatusProvider
{
    private readonly VisionConfigurationLoadResult _effective;
    private readonly IVisionConfigurationStore _store;
    private readonly string _dataDirectory;
    private readonly object _failureGate = new();
    private string? _lastFailure;
    private DateTimeOffset? _lastFailureAt;

    public VisionConfigurationStatusProvider(
        VisionConfigurationLoadResult effective,
        IVisionConfigurationStore store,
        string dataDirectory)
    {
        if (!effective.IsValid)
            throw new ArgumentException("Effective vision state must be valid.", nameof(effective));
        _effective = effective;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public VisionConfigurationLoadResult Effective => _effective;

    public SuavoAgent.Contracts.Ipc.VisionStateHandshake EffectiveHandshake => new(
        SuavoAgent.Contracts.Ipc.VisionStateHandshake.CurrentSchemaVersion,
        _effective.EffectiveGeneration,
        _effective.State?.ConfigDigest ??
        VisionConfigurationStateCodec.ComputeConfigDigest(_effective.EffectiveOptions));

    public void RecordStructuralFailure(string code, DateTimeOffset? recordedAt = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        lock (_failureGate)
        {
            _lastFailure = code;
            _lastFailureAt = recordedAt ?? DateTimeOffset.UtcNow;
        }
    }

    public VisionConfigurationTelemetry Snapshot()
    {
        var staged = VisionConfigurationRegistry.Load(_store, _dataDirectory);
        string? failure;
        DateTimeOffset? failureAt;
        lock (_failureGate)
        {
            failure = _lastFailure;
            failureAt = _lastFailureAt;
        }

        var status = !staged.IsValid
            ? "registry_invalid"
            : StatesMatch(_effective.State, staged.State) && _effective.IsMissing == staged.IsMissing
                ? (_effective.IsMissing ? "missing_default_disabled" : "active")
                : staged.State is not null &&
                  staged.EffectiveGeneration > _effective.EffectiveGeneration
                    ? "restart_required"
                    : "generation_conflict";
        return new(
            status,
            _effective.EffectiveGeneration,
            staged.IsValid ? staged.State?.Generation ?? 0 : null,
            _effective.IsMissing ? "missing_default_disabled" : "active",
            staged.IsValid
                ? (staged.IsMissing ? "missing_default_disabled" : "present")
                : staged.Code,
            _effective.State?.ConfigDigest,
            staged.IsValid ? staged.State?.ConfigDigest : null,
            failure,
            failureAt?.ToString("O"));
    }

    private static bool StatesMatch(
        VisionConfigurationState? left,
        VisionConfigurationState? right) =>
        left is null && right is null || left is not null && left == right;
}
