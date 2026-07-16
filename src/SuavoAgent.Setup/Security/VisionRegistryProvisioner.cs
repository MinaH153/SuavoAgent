using SuavoAgent.Contracts.Security;
using System.Text.Json;

namespace SuavoAgent.Setup.Security;

/// <summary>Elevated install/repair ownership of the vision registry boundary.</summary>
internal static class VisionRegistryProvisioner
{
    internal static bool ProvisionAndRetireLegacy(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var provision = VisionRegistryAuthority.ProvisionAndRepair(
                dataDirectory,
                result => WriteRepairReceipt(dataDirectory, result));
            if (!VisionRegistryAuthority.VerifyProvisionedAcl(out var code))
                throw new UnauthorizedAccessException(
                    $"Vision registry ACL verification failed ({code}).");
            if (provision.StateCleared)
            {
                SetupLog.Append(
                    "WARN",
                    $"vision_registry_repaired code={provision.Code} " +
                    $"invalid_state_sha256={provision.InvalidStateSha256 ?? "unavailable"}");
            }
            var cohortProvision = ProvisionReleaseCohorts(
                dataDirectory,
                ServiceInstaller.TryLockdownVisionCohortAcl);
            if (!cohortProvision.Succeeded)
            {
                ConsoleUI.WriteFail(
                    "Setup could not prepare the reviewed on-device vision engine. " +
                    "Check the internet connection and choose Repair. " +
                    "Support code: SETUP-VISION-COHORT");
                SetupLog.Append(
                    "ERROR",
                    $"vision_release_cohort_required code={cohortProvision.Code}");
                return false;
            }
            if (!RetireLegacyConfig(dataDirectory))
                throw new InvalidDataException(
                    "The legacy vision configuration is not a regular file.");
            return true;
        }
        catch (Exception exception) when (exception is
                   UnauthorizedAccessException or InvalidOperationException or
                   IOException or System.Security.SecurityException)
        {
            ConsoleUI.WriteFail(
                "Windows could not establish the protected vision configuration authority. " +
                "Support code: SETUP-VISION-REGISTRY-ACL");
            SetupLog.Append(
                "ERROR",
                $"vision_registry_provision_failed error_type={exception.GetType().Name}");
            return false;
        }
    }

    internal static ReleaseOcrProvisionResult ProvisionReleaseCohorts(
        string dataDirectory,
        Func<string, bool> lockdownCohortAcl,
        Func<
            string,
            Func<string, bool>,
            CancellationToken,
            Task<ReleaseOcrProvisionResult>>? provision = null)
    {
        provision ??= static (root, lockdown, cancellationToken) =>
            ReleaseOcrCohortProvisioner.ProvisionAllAsync(
                root,
                lockdown,
                cancellationToken);
        try
        {
            var result = provision(
                    dataDirectory,
                    lockdownCohortAcl,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            SetupLog.Append(
                result.Succeeded ? "INFO" : "ERROR",
                $"vision_release_cohort status={result.Code}");
            return result;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or HttpRequestException or
                   InvalidDataException or OperationCanceledException)
        {
            SetupLog.Append(
                "ERROR",
                $"vision_release_cohort_exception error_type={exception.GetType().Name}");
            return new(false, $"vision_release_cohort_exception_{exception.GetType().Name}");
        }
    }

    internal static bool WriteRepairReceipt(
        string dataDirectory,
        VisionRegistryProvisionResult result)
    {
        if (!result.StateCleared || string.IsNullOrWhiteSpace(result.Code)) return false;
        string? temporary = null;
        try
        {
            var root = Path.GetFullPath(dataDirectory);
            var path = Path.Combine(root, "vision-registry-repair.json");
            if ((File.Exists(path) || Directory.Exists(path)) &&
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return false;
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = "repaired_default_disabled",
                repairCode = result.Code,
                invalidStateSha256 = result.InvalidStateSha256,
                repairedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return File.Exists(path) &&
                   !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or IOException or
                   UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    /// <summary>
    /// Deletes only the exact legacy regular file. A directory, junction,
    /// symlink, or other reparse point is a repair failure and is never followed.
    /// </summary>
    internal static bool RetireLegacyConfig(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return false;
        string path;
        try
        {
            path = Path.Combine(Path.GetFullPath(dataDirectory), "vision.json");
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
        }
        catch (Exception exception) when (exception is
                   FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or IOException or
                   UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            File.Delete(path);
            try
            {
                _ = File.GetAttributes(path);
                return false;
            }
            catch (Exception exception) when (exception is
                       FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
