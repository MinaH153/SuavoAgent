using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Stages the exact running Setup executable as the fixed, ACL-protected native
/// maintenance host. Renaming an Authenticode-signed PE does not alter its bytes or
/// signature. The copy is verified byte-for-byte by SHA-256 before it is committed.
/// </summary>
internal static class MaintenanceHostInstaller
{
    internal const string InstallerKind = "native-maintenance-bridge";

    public static MaintenanceHostStage StageCurrentProcess(string installDir)
    {
        var sourcePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException(
                "The running signed Setup executable could not be resolved; native maintenance cannot be staged.");

        return Stage(sourcePath, installDir);
    }

    internal static MaintenanceHostStage Stage(string sourcePath, string installDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Setup executable not found.", sourcePath);

        Directory.CreateDirectory(installDir);
        var destinationPath = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);

        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            var existingHash = ComputeSha256(destinationFullPath);
            return new MaintenanceHostStage(destinationFullPath, existingHash);
        }

        var tempPath = destinationFullPath + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourceFullPath, tempPath, overwrite: false);
            var sourceHash = ComputeSha256(sourceFullPath);
            var stagedHash = ComputeSha256(tempPath);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(sourceHash),
                    Convert.FromHexString(stagedHash)))
            {
                throw new InvalidDataException(
                    "The staged maintenance executable did not match the running Setup executable.");
            }

            File.Move(tempPath, destinationFullPath, overwrite: true);
            var committedHash = ComputeSha256(destinationFullPath);
            if (!string.Equals(sourceHash, committedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The committed maintenance executable failed SHA-256 verification.");

            return new MaintenanceHostStage(destinationFullPath, committedHash);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Writes the non-PHI managed-install marker beside the maintenance host. The
    /// marker fixes the allowed cohort names; binaries.manifest binds those names
    /// to the current signed install/OTA bytes. The containing install directory has
    /// already had inheritance removed, so this file inherits only the hardened ACL.
    /// </summary>
    public static string WriteInstallState(
        string installDir,
        string manifestPath,
        string version,
        DateTimeOffset? installedAtUtc = null)
    {
        var maintenancePath = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        if (!File.Exists(maintenancePath))
            throw new FileNotFoundException("Native maintenance host is missing.", maintenancePath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Immutable binary manifest is missing.", manifestPath);

        var state = new InstallState(
            SchemaVersion: 1,
            InstallerKind,
            Version: (version ?? string.Empty).TrimStart('v'),
            MaintenanceExecutable: MaintenanceContract.ExecutableName,
            InstalledCohort: BinaryDownloader.InstalledCohort.ToArray(),
            InstalledAtUtc: installedAtUtc ?? DateTimeOffset.UtcNow);

        var statePath = Path.Combine(installDir, MaintenanceContract.InstallStateFileName);
        var tempPath = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, statePath, overwrite: true);
            return statePath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed record MaintenanceHostStage(string Path, string Sha256);

internal sealed record InstallState(
    int SchemaVersion,
    string InstallerKind,
    string Version,
    string MaintenanceExecutable,
    IReadOnlyList<string> InstalledCohort,
    DateTimeOffset InstalledAtUtc);
