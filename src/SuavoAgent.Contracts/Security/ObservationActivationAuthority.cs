using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// The one machine-wide authority for every observation-capable runtime. Pairing
/// credentials never imply this capability: only a current control-plane-signed
/// lease can make <see cref="ObservationEnabled"/> true.
/// </summary>
public sealed partial class ObservationActivationAuthority
{
    public const int CurrentSchemaVersion = 1;
    public const string CommandName = "observation_activation_lease_v1";
    public const string StateDirectoryName = "observation-activation";
    public const string StateFileName = "observation-activation.json";
    public const string HighWaterFileName = "observation-activation.highwater.json";
    public const int MaximumStateBytes = 32 * 1024;
    public static readonly TimeSpan MaximumLeaseLifetime = TimeSpan.FromSeconds(130);
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        WriteIndented = false,
    };

    private readonly string _statePath;
    private readonly string _highWaterPath;
    private readonly string _controlStatePath;
    private readonly ObservationActivationIdentity? _identity;
    private readonly IReadOnlyDictionary<string, string> _trustedKeys;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private int _locallyRevoked;
    private ObservationActivationSnapshot _snapshot = ObservationActivationSnapshot.Dormant(
        ObservationActivationCodes.StateMissing);

    public ObservationActivationAuthority(
        string? statePath = null,
        string? highWaterPath = null,
        ObservationActivationIdentity? identity = null,
        IReadOnlyDictionary<string, string>? trustedKeys = null,
        TimeProvider? clock = null,
        string? controlStatePath = null)
    {
        _statePath = statePath ?? DefaultStatePath();
        _highWaterPath = highWaterPath ?? DefaultHighWaterPath();
        _controlStatePath = controlStatePath ?? ObservationControlStateStore.DefaultPath();
        _identity = identity;
        _trustedKeys = trustedKeys ?? RemoteCommandTrust.CreateProductionKeyRegistry();
        _clock = clock ?? TimeProvider.System;
        Refresh();
    }

    public bool ObservationEnabled => Snapshot.ObservationEnabled;

    /// <summary>
    /// Returns the signed anti-rollback floor used to bind the next device
    /// request. Missing state is the first-request value zero; corrupt state
    /// is never silently reset because that could replay a retired generation.
    /// </summary>
    public long GetKnownGeneration()
    {
        if (_identity is null)
            throw new InvalidOperationException("Observation activation identity is unavailable.");
        if (!ObservationActivationStateStore.TryAcquireCrossProcessLock(out var crossProcess) ||
            crossProcess is null)
            throw new IOException("Observation activation state is busy.");
        using (crossProcess)
        {
            if (!File.Exists(_highWaterPath)) return 0;
            var highWater = LoadHighWater(_highWaterPath, _identity, _trustedKeys);
            if (!highWater.Valid)
                throw new InvalidDataException("Observation activation high-water state is invalid.");
            return highWater.Generation;
        }
    }

    public event Action<string>? AuthorityLost;

    public ObservationActivationSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public ObservationActivationSnapshot Refresh()
    {
        var evaluated = Volatile.Read(ref _locallyRevoked) != 0
            ? ObservationActivationSnapshot.Dormant(ObservationActivationCodes.Revoked)
            : LoadAndValidate(
            _statePath,
            _highWaterPath,
            _controlStatePath,
            _identity,
            _trustedKeys,
            _clock.GetUtcNow());
        lock (_sync) _snapshot = evaluated;
        if (!evaluated.ObservationEnabled) SignalAuthorityLost(evaluated.Code);
        return evaluated;
    }

    public ObservationActivationInstallResult TryInstall(ObservationActivationState candidate)
    {
        if (_identity is null)
            return ObservationActivationInstallResult.Reject(
                ObservationActivationCodes.IdentityMissing);
        var result = ObservationActivationStateStore.TryInstall(
            _statePath,
            _highWaterPath,
            candidate,
            _identity,
            _trustedKeys,
            _clock.GetUtcNow());
        if (result.Succeeded) Volatile.Write(ref _locallyRevoked, 0);
        Refresh();
        return result;
    }

    public ObservationActivationExecutionLease? TryAcquireExecutionLease(
        CancellationToken parent)
    {
        if (!Refresh().ObservationEnabled) return null;
        var lease = new ObservationActivationExecutionLease(this, parent);
        if (Refresh().ObservationEnabled) return lease;
        lease.Dispose();
        return null;
    }

    public bool RevokeLocalAuthority()
    {
        Volatile.Write(ref _locallyRevoked, 1);
        lock (_sync)
            _snapshot = ObservationActivationSnapshot.Dormant(
                ObservationActivationCodes.Revoked);
        SignalAuthorityLost(ObservationActivationCodes.Revoked);
        return ObservationActivationStateStore.RemoveCurrent(_statePath);
    }

    private void SignalAuthorityLost(string code)
    {
        var handlers = AuthorityLost;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(code); } catch { }
        }
    }

    public static string DefaultStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        StateDirectoryName,
        StateFileName);

    public static string DefaultHighWaterPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        StateDirectoryName,
        HighWaterFileName);

    public static ObservationActivationSnapshot LoadAndValidate(
        string path,
        string highWaterPath,
        string controlStatePath,
        ObservationActivationIdentity? identity,
        IReadOnlyDictionary<string, string> trustedKeys,
        DateTimeOffset now)
    {
        if (ObservationEmergencyStop.IsLatched())
            return ObservationActivationSnapshot.Dormant(
                ObservationActivationCodes.EmergencyStopLatched);
        if (!ObservationActivationStateStore.TryAcquireCrossProcessLock(out var crossProcess) ||
            crossProcess is null)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateBusy);
        using (crossProcess)
        {
        if (identity is null)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.IdentityMissing);
        var control = ObservationControlStateStore.LoadUnderLock(controlStatePath, identity);
        if (control.Paused)
            return ObservationActivationSnapshot.Dormant(control.Code);
        if (!File.Exists(path))
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateMissing);

        byte[] bytes = Array.Empty<byte>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumStateBytes)
                return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateInvalid);
            bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumStateBytes)
                return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateInvalid);
            var state = JsonSerializer.Deserialize<ObservationActivationState>(bytes, JsonOptions);
            var current = Validate(state, identity, trustedKeys, now);
            if (!current.ObservationEnabled) return current;

            var highWater = LoadHighWater(highWaterPath, identity, trustedKeys);
            if (!highWater.Valid)
                return ObservationActivationSnapshot.Dormant(highWater.Code);
            if (current.Generation != highWater.Generation ||
                !string.Equals(current.LeaseId, highWater.LeaseId, StringComparison.Ordinal) ||
                !string.Equals(current.Nonce, highWater.Nonce, StringComparison.Ordinal))
                return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.ReplayDetected);
            return current;
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        }
    }

    public static ObservationActivationSnapshot LoadAndValidate(
        string path,
        string highWaterPath,
        ObservationActivationIdentity? identity,
        IReadOnlyDictionary<string, string> trustedKeys,
        DateTimeOffset now) =>
        LoadAndValidate(
            path,
            highWaterPath,
            Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty,
                ObservationControlStateStore.FileName),
            identity,
            trustedKeys,
            now);

    public static ObservationActivationSnapshot Validate(
        ObservationActivationState? state,
        ObservationActivationIdentity? identity,
        IReadOnlyDictionary<string, string> trustedKeys,
        DateTimeOffset now)
    {
        if (state is null || state.SchemaVersion != CurrentSchemaVersion ||
            state.Lease is null || string.IsNullOrWhiteSpace(state.Lease.DataJson) ||
            Encoding.UTF8.GetByteCount(state.Lease.DataJson) > 12 * 1024)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.StateInvalid);

        var lease = state.Lease;
        if (!string.Equals(lease.Command, CommandName, StringComparison.Ordinal) ||
            !SafeToken(lease.AgentId, 128) || !SafeToken(lease.MachineFingerprint, 256) ||
            !SafeToken(lease.Nonce, 128) || !SafeToken(lease.KeyId, 64) ||
            !LowerHex64(lease.DataHash) ||
            !trustedKeys.TryGetValue(lease.KeyId, out var publicKey) ||
            !DateTimeOffset.TryParse(
                lease.Timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var commandTimestamp) ||
            commandTimestamp > now + MaximumClockSkew ||
            now - commandTimestamp > TimeSpan.FromMinutes(5))
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.EnvelopeInvalid);

        var computedHash = RemoteCommandTrust.ComputeSha256Hex(lease.DataJson);
        if (!FixedAsciiEquals(computedHash, lease.DataHash) ||
            !VerifySignature(lease, publicKey))
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.SignatureInvalid);

        ObservationActivationLeaseData? data;
        try
        {
            data = JsonSerializer.Deserialize<ObservationActivationLeaseData>(
                lease.DataJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.DataInvalid);
        }

        if (data is null || data.SchemaVersion != CurrentSchemaVersion ||
            !CanonicalUuid(data.LeaseId) || !CanonicalUuid(data.RequestId) ||
            !LowerHex64(data.RequestDigest) || !CanonicalUuid(data.PharmacyId) ||
            !CanonicalUuid(data.WorkstationId) || !CanonicalUuid(data.AuthorizationId) ||
            !LowerHex64(data.DeviceKeyId) || !LowerHex64(data.PolicyDigest) ||
            !ReleaseCohortShape().IsMatch(data.ReleaseCohort ?? string.Empty) ||
            data.Generation <= 0 || data.IssuedAtUtc > now + MaximumClockSkew ||
            data.NotBeforeUtc > now + MaximumClockSkew ||
            data.NotBeforeUtc < data.IssuedAtUtc - MaximumClockSkew ||
            data.ExpiresAtUtc <= data.NotBeforeUtc ||
            data.ExpiresAtUtc <= data.IssuedAtUtc ||
            data.ExpiresAtUtc - data.IssuedAtUtc > MaximumLeaseLifetime)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.DataInvalid);

        if (identity is null)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.IdentityMissing);

        if (!FixedAsciiEquals(lease.AgentId, identity.AgentId) ||
             !FixedAsciiEquals(lease.MachineFingerprint, identity.MachineFingerprint) ||
             !FixedAsciiEquals(data.WorkstationId, identity.WorkstationId) ||
             !FixedAsciiEquals(data.PharmacyId, identity.PharmacyId) ||
             !FixedAsciiEquals(data.DeviceKeyId, identity.DeviceKeyId) ||
             !FixedAsciiEquals(data.ReleaseCohort, identity.ReleaseCohort) ||
             !FixedAsciiEquals(data.PolicyDigest, identity.PolicyDigest))
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.BindingInvalid);

        if (now < data.NotBeforeUtc)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.NotYetValid);
        if (now >= data.ExpiresAtUtc)
            return ObservationActivationSnapshot.Dormant(ObservationActivationCodes.Expired);

        return new(
            true,
            ObservationActivationCodes.Active,
            data.Generation,
            data.LeaseId,
            lease.Nonce,
            data.ExpiresAtUtc,
            data.ReleaseCohort,
            data.PolicyDigest);
    }

    public static string Serialize(ObservationActivationState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    internal static ObservationActivationHighWaterSnapshot LoadHighWater(
        string path,
        ObservationActivationIdentity identity,
        IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (!File.Exists(path))
            return ObservationActivationHighWaterSnapshot.Reject(
                ObservationActivationCodes.HighWaterMissing);
        byte[] bytes = Array.Empty<byte>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumStateBytes)
                return ObservationActivationHighWaterSnapshot.Reject(
                    ObservationActivationCodes.HighWaterInvalid);
            bytes = File.ReadAllBytes(path);
            var highWater = JsonSerializer.Deserialize<ObservationActivationState>(bytes, JsonOptions);
            var staticValidation = ValidateStatic(highWater, identity, trustedKeys);
            return staticValidation;
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            return ObservationActivationHighWaterSnapshot.Reject(
                ObservationActivationCodes.HighWaterInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static ObservationActivationHighWaterSnapshot ValidateStatic(
        ObservationActivationState? state,
        ObservationActivationIdentity identity,
        IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (state is null || state.SchemaVersion != CurrentSchemaVersion || state.Lease is null)
            return ObservationActivationHighWaterSnapshot.Reject(
                ObservationActivationCodes.HighWaterInvalid);
        // Static validation deliberately ignores time so an expired signed lease
        // remains an anti-rollback floor after restart.
        var lease = state.Lease;
        if (!TryValidateSignedData(lease, identity, trustedKeys, out var data))
            return ObservationActivationHighWaterSnapshot.Reject(
                ObservationActivationCodes.HighWaterInvalid);
        return new(
            true,
            ObservationActivationCodes.Active,
            data!.Generation,
            data.LeaseId,
            lease.Nonce);
    }

    private static bool TryValidateSignedData(
        ObservationActivationSignedLease lease,
        ObservationActivationIdentity identity,
        IReadOnlyDictionary<string, string> trustedKeys,
        out ObservationActivationLeaseData? data)
    {
        data = null;
        if (!string.Equals(lease.Command, CommandName, StringComparison.Ordinal) ||
            !SafeToken(lease.AgentId, 128) ||
            !SafeToken(lease.MachineFingerprint, 256) ||
            !SafeToken(lease.Nonce, 128) ||
            !SafeToken(lease.KeyId, 64) ||
            !LowerHex64(lease.DataHash) ||
            !trustedKeys.TryGetValue(lease.KeyId, out var publicKey) ||
            !FixedAsciiEquals(RemoteCommandTrust.ComputeSha256Hex(lease.DataJson), lease.DataHash) ||
            !VerifySignature(lease, publicKey))
            return false;
        try
        {
            data = JsonSerializer.Deserialize<ObservationActivationLeaseData>(lease.DataJson, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        return data is not null && data.SchemaVersion == CurrentSchemaVersion &&
               data.Generation > 0 && CanonicalUuid(data.LeaseId) &&
               CanonicalUuid(data.RequestId) && LowerHex64(data.RequestDigest) &&
               FixedAsciiEquals(lease.AgentId, identity.AgentId) &&
               FixedAsciiEquals(lease.MachineFingerprint, identity.MachineFingerprint) &&
               FixedAsciiEquals(data.WorkstationId, identity.WorkstationId) &&
               FixedAsciiEquals(data.PharmacyId, identity.PharmacyId) &&
               FixedAsciiEquals(data.DeviceKeyId, identity.DeviceKeyId) &&
               FixedAsciiEquals(data.ReleaseCohort, identity.ReleaseCohort) &&
               FixedAsciiEquals(data.PolicyDigest, identity.PolicyDigest);
    }

    private static bool VerifySignature(ObservationActivationSignedLease lease, string publicKeyBase64)
    {
        byte[] keyBytes = Array.Empty<byte>();
        byte[] signature = Array.Empty<byte>();
        try
        {
            keyBytes = Convert.FromBase64String(publicKeyBase64);
            signature = Convert.FromBase64String(lease.Signature);
            if (signature.Length != 64) return false;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
            if (consumed != keyBytes.Length) return false;
            var canonical = RemoteCommandTrust.BuildCommandCanonical(
                lease.Command,
                lease.AgentId,
                lease.MachineFingerprint,
                lease.Timestamp,
                lease.Nonce,
                lease.DataHash);
            return verifier.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool FixedAsciiEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length ||
            !left.All(char.IsAscii) || !right.All(char.IsAscii))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static bool SafeToken(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.All(character => char.IsAscii(character) && !char.IsControl(character) && character != '|');

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$")]
    private static partial Regex ReleaseCohortShape();
}

public sealed record ObservationActivationState(
    int SchemaVersion,
    ObservationActivationSignedLease Lease);

public sealed record ObservationActivationSignedLease(
    string Command,
    string AgentId,
    string MachineFingerprint,
    string Timestamp,
    string Nonce,
    string KeyId,
    string Signature,
    string DataHash,
    string DataJson);

public sealed record ObservationActivationLeaseData(
    int SchemaVersion,
    string LeaseId,
    string RequestId,
    string RequestDigest,
    string PharmacyId,
    string WorkstationId,
    string DeviceKeyId,
    string ReleaseCohort,
    long Generation,
    string PolicyDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string AuthorizationId);

public sealed record ObservationActivationIdentity(
    string AgentId,
    string WorkstationId,
    string PharmacyId,
    string MachineFingerprint,
    string DeviceKeyId,
    string ReleaseCohort,
    string PolicyDigest);

public readonly record struct ObservationActivationSnapshot(
    bool ObservationEnabled,
    string Code,
    long Generation,
    string? LeaseId,
    string? Nonce,
    DateTimeOffset? ExpiresAtUtc,
    string? ReleaseCohort,
    string? PolicyDigest)
{
    public static ObservationActivationSnapshot Dormant(string code) =>
        new(false, code, 0, null, null, null, null, null);
}

public static class ObservationActivationCodes
{
    public const string Active = "observation_activation_active";
    public const string StateMissing = "observation_activation_required";
    public const string StateInvalid = "observation_activation_state_invalid";
    public const string IdentityMissing = "observation_activation_identity_missing";
    public const string HighWaterMissing = "observation_activation_highwater_missing";
    public const string HighWaterInvalid = "observation_activation_highwater_invalid";
    public const string ReplayDetected = "observation_activation_replay_detected";
    public const string StatePersistenceFailed = "observation_activation_persistence_failed";
    public const string StateBusy = "observation_activation_state_busy";
    public const string ControlStateMissing = "observation_control_state_missing";
    public const string ControlStateInvalid = "observation_control_state_invalid";
    public const string ControlPaused = "observation_control_paused";
    public const string ControlStopped = "observation_control_stopped";
    public const string EmergencyStopLatched = "observation_emergency_stop_latched";
    public const string EnvelopeInvalid = "observation_activation_envelope_invalid";
    public const string SignatureInvalid = "observation_activation_signature_invalid";
    public const string DataInvalid = "observation_activation_data_invalid";
    public const string BindingInvalid = "observation_activation_device_binding_invalid";
    public const string NotYetValid = "observation_activation_not_yet_valid";
    public const string Expired = "observation_activation_expired";
    public const string Revoked = "observation_activation_revoked";
}

internal readonly record struct ObservationActivationHighWaterSnapshot(
    bool Valid,
    string Code,
    long Generation,
    string? LeaseId,
    string? Nonce)
{
    public static ObservationActivationHighWaterSnapshot Reject(string code) =>
        new(false, code, 0, null, null);
}

public readonly record struct ObservationActivationInstallResult(
    bool Succeeded,
    string Code,
    long Generation,
    string? LeaseId)
{
    public static ObservationActivationInstallResult Reject(string code) =>
        new(false, code, 0, null);

    public static ObservationActivationInstallResult Accepted(long generation, string leaseId) =>
        new(true, ObservationActivationCodes.Active, generation, leaseId);
}
