using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Core-owned, machine-wide pause/stop latch. Setup creates the initial running
/// state; after that every transition is monotonic and atomically persisted.
/// Missing, corrupt, stale-generation, or identity-mismatched state is paused.
/// </summary>
public static class ObservationControlStateStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "observation-control.json";
    public const int MaximumBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        ObservationActivationAuthority.StateDirectoryName,
        FileName);

    public static ObservationControlSnapshot Load(
        string path,
        ObservationActivationIdentity? identity)
    {
        if (!ObservationActivationStateStore.TryAcquireCrossProcessLock(out var crossProcess) ||
            crossProcess is null)
            return ObservationControlSnapshot.FailClosed(ObservationActivationCodes.StateBusy);
        using (crossProcess) return LoadUnderLock(path, identity);
    }

    internal static ObservationControlSnapshot LoadUnderLock(
        string path,
        ObservationActivationIdentity? identity)
    {
        if (identity is null)
            return ObservationControlSnapshot.FailClosed(ObservationActivationCodes.IdentityMissing);
        if (!File.Exists(path))
            return ObservationControlSnapshot.FailClosed(
                ObservationActivationCodes.ControlStateMissing);

        byte[] bytes = Array.Empty<byte>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes)
                return ObservationControlSnapshot.FailClosed(
                    ObservationActivationCodes.ControlStateInvalid);
            bytes = File.ReadAllBytes(path);
            var state = JsonSerializer.Deserialize<ObservationControlState>(bytes, JsonOptions);
            if (!IsValid(state, identity))
                return ObservationControlSnapshot.FailClosed(
                    ObservationActivationCodes.ControlStateInvalid);
            return new(
                state!.Paused || state.Stopped,
                state.Stopped,
                state.ControlGeneration,
                state.Stopped
                    ? ObservationActivationCodes.ControlStopped
                    : state.Paused
                        ? ObservationActivationCodes.ControlPaused
                        : ObservationActivationCodes.Active);
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            return ObservationControlSnapshot.FailClosed(
                ObservationActivationCodes.ControlStateInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool TryInitialize(
        string path,
        ObservationActivationIdentity identity,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!ObservationActivationStateStore.TryAcquireCrossProcessLock(out var crossProcess) ||
            crossProcess is null)
            return false;
        using (crossProcess)
        {
            if (File.Exists(path))
                return LoadUnderLock(path, identity).Code is
                    ObservationActivationCodes.Active or
                    ObservationActivationCodes.ControlPaused or
                    ObservationActivationCodes.ControlStopped;
            return TryWriteUnderLock(path, new(
                CurrentSchemaVersion,
                identity.AgentId,
                identity.PolicyDigest,
                1,
                false,
                false,
                now));
        }
    }

    public static bool TryTransition(
        string path,
        ObservationActivationIdentity identity,
        long expectedGeneration,
        bool paused,
        bool stopped,
        DateTimeOffset now,
        out long nextGeneration)
    {
        ArgumentNullException.ThrowIfNull(identity);
        nextGeneration = expectedGeneration;
        if (!ObservationActivationStateStore.TryAcquireCrossProcessLock(out var crossProcess) ||
            crossProcess is null)
            return false;
        using (crossProcess)
        {
            var current = LoadUnderLock(path, identity);
            if (current.Code is not (
                    ObservationActivationCodes.Active or
                    ObservationActivationCodes.ControlPaused or
                    ObservationActivationCodes.ControlStopped) ||
                current.Generation != expectedGeneration ||
                current.Stopped && !stopped)
                return false;
            nextGeneration = checked(current.Generation + 1);
            return TryWriteUnderLock(path, new(
                CurrentSchemaVersion,
                identity.AgentId,
                identity.PolicyDigest,
                nextGeneration,
                paused || stopped,
                stopped,
                now));
        }
    }

    public static string Serialize(ObservationControlState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    private static bool IsValid(
        ObservationControlState? state,
        ObservationActivationIdentity identity) =>
        state is not null &&
        state.SchemaVersion == CurrentSchemaVersion &&
        state.ControlGeneration > 0 &&
        (!state.Stopped || state.Paused) &&
        FixedEquals(state.AgentId, identity.AgentId) &&
        FixedEquals(state.PolicyDigest, identity.PolicyDigest);

    private static bool TryWriteUnderLock(string path, ObservationControlState state)
    {
        byte[] bytes = Array.Empty<byte>();
        string? temporary = null;
        try
        {
            bytes = Encoding.UTF8.GetBytes(Serialize(state));
            if (bytes.Length is <= 0 or > MaximumBytes) return false;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length ||
            !left.All(char.IsAscii) || !right.All(char.IsAscii))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}

public sealed record ObservationControlState(
    int SchemaVersion,
    string AgentId,
    string PolicyDigest,
    long ControlGeneration,
    bool Paused,
    bool Stopped,
    DateTimeOffset UpdatedAtUtc);

public readonly record struct ObservationControlSnapshot(
    bool Paused,
    bool Stopped,
    long Generation,
    string Code)
{
    public static ObservationControlSnapshot FailClosed(string code) =>
        new(true, false, 0, code);
}
