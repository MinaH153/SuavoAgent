using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

internal sealed record UpdateActivationGateResult(
    bool IsValid,
    string Code,
    UpdateActivationRequest? Request = null,
    UpdatePackageManifest? Manifest = null,
    string? ReplayId = null)
{
    public static UpdateActivationGateResult Valid(
        UpdateActivationRequest request,
        UpdatePackageManifest manifest) =>
        new(true, "valid", request, manifest, UpdateActivationContract.ComputeReplayId(request));

    public static UpdateActivationGateResult Reject(string code) => new(false, code);
}

/// <summary>
/// LocalSystem-side preflight for an untrusted LocalService staging area. Maintenance repeats every
/// signature/hash check after copying into its SYSTEM-only transaction directory; this gate prevents
/// malformed, stale, replayed, downgraded, incomplete, linked, or observably-racing requests from
/// ever reaching that privileged process boundary.
/// </summary>
internal sealed class UpdateActivationGate
{
    private readonly IReadOnlyDictionary<string, string> _commandKeys;
    private readonly IReadOnlyDictionary<string, string> _updatePublicKeys;
    private readonly ILogger _logger;

    public UpdateActivationGate(
        IReadOnlyDictionary<string, string> commandKeys,
        string updatePublicKey,
        ILogger logger)
        : this(
            commandKeys,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OtaUpdateTrust.LegacyV1KeyId] = updatePublicKey,
            },
            logger)
    {
    }

    public UpdateActivationGate(
        IReadOnlyDictionary<string, string> commandKeys,
        IReadOnlyDictionary<string, string> updatePublicKeys,
        ILogger logger)
    {
        _commandKeys = commandKeys;
        _updatePublicKeys = updatePublicKeys;
        _logger = logger;
    }

    public UpdateActivationGateResult Validate(
        string requestPath,
        string updateRoot,
        UpdateReplayLedger replayLedger,
        string expectedAgentId,
        string expectedMachineFingerprint,
        string currentVersion,
        DateTimeOffset now,
        Action? betweenVerificationPasses = null)
    {
        try
        {
            if (!IsExactRequestPath(requestPath, updateRoot) ||
                !File.Exists(requestPath) ||
                HasReparsePoint(requestPath))
                return UpdateActivationGateResult.Reject("request_path_invalid");

            var requestBytes = ReadBounded(requestPath, UpdateActivationContract.MaxRequestBytes);
            var requestJson = new System.Text.UTF8Encoding(false, true).GetString(requestBytes);
            if (!UpdateActivationContract.TryDeserialize(
                    requestJson,
                    out var request,
                    out var deserializeCode))
                return UpdateActivationGateResult.Reject(deserializeCode);

            var validation = UpdateActivationContract.Validate(
                request!,
                _commandKeys,
                _updatePublicKeys,
                now,
                expectedAgentId,
                expectedMachineFingerprint);
            if (!validation.IsValid)
                return UpdateActivationGateResult.Reject(validation.Code);
            if (!IsStrictUpgrade(validation.Manifest!.Version, currentVersion))
                return UpdateActivationGateResult.Reject("version_not_strictly_newer");

            var replayId = UpdateActivationContract.ComputeReplayId(request!);
            if (replayLedger.Contains(replayId, now))
                return UpdateActivationGateResult.Reject("request_replay");

            var stagingDir = UpdateActivationContract.GetIncomingStagingDirectory(
                updateRoot,
                request!.StagingId);
            if (!IsExactStagingDirectory(stagingDir, updateRoot, request.StagingId) ||
                !Directory.Exists(stagingDir) ||
                HasReparsePoint(stagingDir))
                return UpdateActivationGateResult.Reject("staging_path_invalid");

            var expected = validation.Manifest.Files.ToDictionary(
                file => file.FileName,
                file => file.Sha256,
                StringComparer.OrdinalIgnoreCase);
            var actualNames = Directory.EnumerateFiles(stagingDir, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray();
            if (actualNames.Length != expected.Count ||
                actualNames.Any(name => !expected.ContainsKey(name)))
                return UpdateActivationGateResult.Reject("staging_file_set_mismatch");

            var first = CaptureStagedFiles(stagingDir, expected);
            if (first is null)
                return UpdateActivationGateResult.Reject("staging_hash_invalid");

            betweenVerificationPasses?.Invoke();

            var second = CaptureStagedFiles(stagingDir, expected);
            if (second is null || !SnapshotsEqual(first, second))
                return UpdateActivationGateResult.Reject("staging_toctou_detected");

            var secondRequest = ReadBounded(requestPath, UpdateActivationContract.MaxRequestBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(requestBytes),
                    SHA256.HashData(secondRequest)))
                return UpdateActivationGateResult.Reject("request_toctou_detected");

            return UpdateActivationGateResult.Valid(request, validation.Manifest);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            CryptographicException or
            FormatException or
            ArgumentException or
            DecoderFallbackException)
        {
            _logger.LogSafeWarning(ex);
            return UpdateActivationGateResult.Reject("activation_preflight_unreadable");
        }
    }

    internal static bool IsExactRequestPath(string requestPath, string updateRoot)
    {
        if (string.IsNullOrWhiteSpace(requestPath) ||
            string.IsNullOrWhiteSpace(updateRoot) ||
            !Path.IsPathFullyQualified(requestPath) ||
            !Path.IsPathFullyQualified(updateRoot))
            return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(
                updateRoot,
                UpdateActivationContract.ActivationRequestFileName));
            return string.Equals(
                Path.GetFullPath(requestPath),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExactStagingDirectory(
        string stagingDirectory,
        string updateRoot,
        string stagingId)
    {
        try
        {
            var expected = Path.GetFullPath(
                UpdateActivationContract.GetIncomingStagingDirectory(updateRoot, stagingId));
            return Path.IsPathFullyQualified(stagingDirectory) &&
                   string.Equals(
                       Path.GetFullPath(stagingDirectory),
                       expected,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsStrictUpgrade(string targetVersion, string currentVersion)
    {
        if (!TryParseVersion(targetVersion, out var target) ||
            !TryParseVersion(currentVersion, out var current))
            return false;
        return target > current;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = (value ?? string.Empty).TrimStart('v').Split('-', 2)[0];
        return Version.TryParse(normalized, out version!);
    }

    private static Dictionary<string, StagedFileSnapshot>? CaptureStagedFiles(
        string stagingDir,
        IReadOnlyDictionary<string, string> expected)
    {
        var result = new Dictionary<string, StagedFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, expectedHash) in expected)
        {
            var path = Path.Combine(stagingDir, name);
            if (!File.Exists(path) || HasReparsePoint(path)) return null;
            var before = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var after = new FileInfo(path);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                before.Length != after.Length ||
                before.LastWriteTimeUtc != after.LastWriteTimeUtc)
                return null;
            result[name] = new StagedFileSnapshot(
                actualHash,
                after.Length,
                after.LastWriteTimeUtc.Ticks);
        }
        return result;
    }

    private static bool SnapshotsEqual(
        IReadOnlyDictionary<string, StagedFileSnapshot> left,
        IReadOnlyDictionary<string, StagedFileSnapshot> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var other) && pair.Value == other);

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Activation request size is invalid");

        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total <= 0 || total > maximumBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Activation request size is invalid");
        return bytes.AsSpan(0, total).ToArray();
    }

    private sealed record StagedFileSnapshot(string Sha256, long Length, long LastWriteTicks);
}

internal sealed record UpdateReplayReservation(
    string ReplayId,
    DateTimeOffset ReservedAtUtc);

internal sealed record UpdateReplayLedgerRecord(
    int SchemaVersion,
    IReadOnlyList<UpdateReplayReservation> Reservations);

/// <summary>
/// SYSTEM-side launch deduplication only. A reservation is deliberately leased: if Maintenance dies
/// before durably claiming and removing the untrusted request, Watchdog retries after the lease.
/// Maintenance owns the authoritative, non-expiring replay journal and makes every retry idempotent.
/// </summary>
internal sealed class UpdateReplayLedger
{
    private const int SchemaVersion = 2;
    internal static readonly TimeSpan LaunchLease = TimeSpan.FromMinutes(2);
    private readonly string _path;
    private readonly object _gate = new();

    public UpdateReplayLedger(string path) => _path = path;

    public bool Contains(string replayId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Read().Any(reservation =>
                string.Equals(reservation.ReplayId, replayId, StringComparison.Ordinal) &&
                IsLeaseActive(reservation, now));
        }
    }

    public bool TryReserve(string replayId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var reservations = Read()
                .Where(reservation => IsLeaseActive(reservation, now))
                .ToList();
            if (reservations.Any(reservation =>
                    string.Equals(reservation.ReplayId, replayId, StringComparison.Ordinal)))
                return false;
            reservations.Add(new UpdateReplayReservation(replayId, now));
            Write(reservations);
            return true;
        }
    }

    public void Release(string replayId)
    {
        lock (_gate)
        {
            var reservations = Read()
                .Where(reservation =>
                    !string.Equals(reservation.ReplayId, replayId, StringComparison.Ordinal))
                .ToArray();
            Write(reservations);
        }
    }

    private IReadOnlyList<UpdateReplayReservation> Read()
    {
        if (!File.Exists(_path)) return Array.Empty<UpdateReplayReservation>();
        var info = new FileInfo(_path);
        if (info.Length <= 0 || info.Length > 1024 * 1024)
            throw new InvalidDataException("Replay ledger size invalid");
        var record = JsonSerializer.Deserialize<UpdateReplayLedgerRecord>(
            File.ReadAllText(_path),
            UpdateActivationContract.JsonOptions)
            ?? throw new InvalidDataException("Replay ledger is null");
        if (record.SchemaVersion != SchemaVersion ||
            record.Reservations is null ||
            record.Reservations.Count > 10_000 ||
            record.Reservations.Any(reservation =>
                reservation is null ||
                reservation.ReplayId is not { Length: 64 } ||
                !reservation.ReplayId.All(Uri.IsHexDigit) ||
                reservation.ReservedAtUtc == default) ||
            record.Reservations
                .Select(reservation => reservation.ReplayId)
                .Distinct(StringComparer.Ordinal)
                .Count() != record.Reservations.Count)
            throw new InvalidDataException("Replay ledger invalid");
        return record.Reservations;
    }

    private void Write(IReadOnlyList<UpdateReplayReservation> reservations)
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("Replay ledger directory missing");
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    new UpdateReplayLedgerRecord(SchemaVersion, reservations),
                    UpdateActivationContract.JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool IsLeaseActive(
        UpdateReplayReservation reservation,
        DateTimeOffset now) =>
        reservation.ReservedAtUtc > now - LaunchLease &&
        reservation.ReservedAtUtc <= now + UpdateActivationContract.MaximumFutureSkew;
}

internal sealed record WatchdogInstallIdentity(
    string AgentId,
    string MachineFingerprint,
    string Version);

internal static class WatchdogInstallIdentityReader
{
    public static WatchdogInstallIdentity? TryRead(string installDir)
    {
        try
        {
            var settingsPath = Path.Combine(installDir, "appsettings.json");
            var installStatePath = Path.Combine(installDir, MaintenanceContract.InstallStateFileName);
            if (!File.Exists(settingsPath) || !File.Exists(installStatePath) ||
                new FileInfo(settingsPath).Length > 1024 * 1024 ||
                new FileInfo(installStatePath).Length > 64 * 1024)
                return null;

            using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            using var state = JsonDocument.Parse(File.ReadAllText(installStatePath));
            if (!settings.RootElement.TryGetProperty("Agent", out var agent) ||
                !agent.TryGetProperty("AgentId", out var agentIdElement) ||
                !agent.TryGetProperty("MachineFingerprint", out var fingerprintElement) ||
                !state.RootElement.TryGetProperty("version", out var versionElement))
                return null;

            var agentId = agentIdElement.GetString();
            var fingerprint = fingerprintElement.GetString();
            var version = versionElement.GetString();
            return string.IsNullOrWhiteSpace(agentId) ||
                   string.IsNullOrWhiteSpace(fingerprint) ||
                   string.IsNullOrWhiteSpace(version)
                ? null
                : new WatchdogInstallIdentity(agentId, fingerprint, version);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException)
        {
            return null;
        }
    }
}
