using System.Diagnostics;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record MaintenanceRunnerStageResult(
    bool Succeeded,
    string Code,
    string? RunnerPath = null)
{
    public static MaintenanceRunnerStageResult Success(string path) => new(true, "staged", path);
    public static MaintenanceRunnerStageResult Fail(string code) => new(false, code);
}

/// <summary>
/// Re-executes Maintenance from a SYSTEM/Admin-only directory so the installed
/// directory can be atomically renamed without trying to move the running image.
/// The copied host is independently bound to the same signed release/OTA receipt
/// before it is ever launched.
/// </summary>
internal sealed class NativeMaintenanceRunnerStager
{
    private static readonly string[] TrustReceiptNames =
    [
        MaintenanceContract.ReleaseChecksumsFileName,
        MaintenanceContract.ReleaseChecksumsSignatureFileName,
        MaintenanceContract.FieldReleaseReceiptFileName,
        MaintenanceContract.CurrentOtaManifestFileName,
        MaintenanceContract.CurrentOtaManifestSignatureFileName,
    ];

    private readonly Action<string> _lockdown;
    private readonly Func<string, MaintenanceHostTrustResult> _verifyTrust;
    private readonly Func<ProcessStartInfo, bool> _launch;

    public NativeMaintenanceRunnerStager(
        Action<string>? lockdown = null,
        Func<string, MaintenanceHostTrustResult>? verifyTrust = null,
        Func<ProcessStartInfo, bool>? launch = null)
    {
        _lockdown = lockdown ?? ServiceInstaller.LockdownMaintenanceDirectoryAcl;
        _verifyTrust = verifyTrust ?? MaintenanceHostTrustVerifier.Verify;
        _launch = launch ?? LaunchDetached;
    }

    public MaintenanceRunnerStageResult Stage(
        string installedMaintenancePath,
        string maintenanceRoot,
        string stagingId)
    {
        try
        {
            if (!IsExactMaintenanceName(installedMaintenancePath) ||
                stagingId.Length != 64 ||
                !stagingId.All(Uri.IsHexDigit) ||
                !File.Exists(installedMaintenancePath))
                return MaintenanceRunnerStageResult.Fail("runner_source_invalid");
            var sourceTrust = _verifyTrust(installedMaintenancePath);
            if (!sourceTrust.IsTrusted)
                return MaintenanceRunnerStageResult.Fail("runner_source_untrusted:" + sourceTrust.Code);

            var root = Path.GetFullPath(maintenanceRoot);
            var runnerDirectory = Path.Combine(
                root,
                UpdateActivationContract.RunnerDirectoryName,
                stagingId.ToLowerInvariant());
            var runnerPath = Path.Combine(runnerDirectory, MaintenanceContract.ExecutableName);
            Directory.CreateDirectory(root);
            _lockdown(root);

            if (!Directory.Exists(runnerDirectory))
            {
                var temp = runnerDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(temp);
                    _lockdown(temp);
                    CopyRegularFile(
                        installedMaintenancePath,
                        Path.Combine(temp, MaintenanceContract.ExecutableName));
                    var sourceDirectory = Path.GetDirectoryName(installedMaintenancePath)!;
                    foreach (var receiptName in TrustReceiptNames)
                    {
                        var source = Path.Combine(sourceDirectory, receiptName);
                        if (File.Exists(source))
                            CopyRegularFile(source, Path.Combine(temp, receiptName));
                    }

                    var tempRunner = Path.Combine(temp, MaintenanceContract.ExecutableName);
                    var copiedTrust = _verifyTrust(tempRunner);
                    if (!copiedTrust.IsTrusted || !HashEquals(installedMaintenancePath, tempRunner))
                        return MaintenanceRunnerStageResult.Fail("runner_copy_untrusted:" + copiedTrust.Code);
                    Directory.Move(temp, runnerDirectory);
                }
                finally
                {
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                }
            }

            var trust = _verifyTrust(runnerPath);
            if (!trust.IsTrusted)
                return MaintenanceRunnerStageResult.Fail("runner_existing_untrusted:" + trust.Code);
            return MaintenanceRunnerStageResult.Success(runnerPath);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            ArgumentException)
        {
            return MaintenanceRunnerStageResult.Fail("runner_stage_failed:" + ex.GetType().Name);
        }
    }

    public bool LaunchRunner(string runnerPath, string trustedRequestPath)
    {
        try
        {
            var info = BuildRunnerStartInfo(runnerPath, trustedRequestPath);
            return _launch(info);
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo BuildRunnerStartInfo(
        string runnerPath,
        string trustedRequestPath)
    {
        if (!Path.IsPathFullyQualified(runnerPath) ||
            !IsExactMaintenanceName(runnerPath) ||
            !Path.IsPathFullyQualified(trustedRequestPath))
            throw new ArgumentException("Runner or request path is invalid.");
        var info = new ProcessStartInfo
        {
            FileName = runnerPath,
            WorkingDirectory = Path.GetDirectoryName(runnerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(UpdateActivationContract.RunnerSwitch);
        info.ArgumentList.Add(UpdateActivationContract.RequestPathSwitch);
        info.ArgumentList.Add(trustedRequestPath);
        return info;
    }

    private static bool LaunchDetached(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private static void CopyRegularFile(string source, string destination)
    {
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Runner source is not a regular file.");
        File.Copy(source, destination, overwrite: false);
    }

    private static bool HashEquals(string left, string right)
    {
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(leftStream),
            SHA256.HashData(rightStream));
    }

    private static bool IsExactMaintenanceName(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(
            Path.GetFileName(path),
            MaintenanceContract.ExecutableName,
            StringComparison.OrdinalIgnoreCase);
}
