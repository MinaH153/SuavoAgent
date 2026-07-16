using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Setup;

internal sealed record BrainDiskSpaceResult(
    bool IsSufficient,
    string Detail,
    long InstallRequiredBytes,
    long DataRequiredBytes);

/// <summary>
/// Computes storage from signed artifact sizes, worst-case bounded native
/// extraction, and a post-install safety reserve. Existing cohorts are retained;
/// current free-space readings already account for their occupied bytes.
/// </summary>
internal static class BrainDiskSpaceGate
{
    internal const long InstallRuntimeReserveBytes = 512L * 1024 * 1024;
    internal const long DataSafetyReserveBytes = 1024L * 1024 * 1024;

    internal static bool HasDataVolumeCapacity(
        string dataDirectory,
        AgentReasoningConfig reasoning,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now,
        bool forceFullProvisioning = false,
        Func<string, long>? availableBytes = null)
    {
        try
        {
            var required = CalculateDataRequiredBytes(
                dataDirectory,
                reasoning,
                forceFullProvisioning ? _ => false : Directory.Exists,
                trustedPublisherKeys,
                now);
            var root = RequireVolumeRoot(dataDirectory);
            return (availableBytes?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace) >= required;
        }
        catch
        {
            return false;
        }
    }

    internal static BrainDiskSpaceResult Evaluate(
        string installDirectory,
        string dataDirectory,
        AgentReasoningConfig? reasoning,
        Func<string, long> availableBytes,
        Func<string, bool>? verifiedCohortExists = null,
        IReadOnlyDictionary<string, string>? trustedPublisherKeys = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(availableBytes);
        // Path existence is never proof of a usable cohort. Callers may supply
        // a predicate only after full signature/hash/inventory verification.
        verifiedCohortExists ??= _ => false;
        try
        {
            var installRoot = RequireVolumeRoot(installDirectory);
            var dataRoot = RequireVolumeRoot(dataDirectory);
            var dataRequired = CalculateDataRequiredBytes(
                dataDirectory,
                reasoning,
                verifiedCohortExists,
                trustedPublisherKeys ?? BrainCohortContract.ProductionTrustedPublisherKeys,
                now ?? DateTimeOffset.UtcNow);

            var sameVolume = string.Equals(
                installRoot,
                dataRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
            var installAvailable = availableBytes(installRoot);
            var dataAvailable = sameVolume
                ? installAvailable
                : availableBytes(dataRoot);
            var sufficient = sameVolume
                ? installAvailable >= checked(InstallRuntimeReserveBytes + dataRequired)
                : installAvailable >= InstallRuntimeReserveBytes && dataAvailable >= dataRequired;
            var requiredText = sameVolume
                ? $"{ToGiB(InstallRuntimeReserveBytes + dataRequired):F1} GB"
                : $"{ToGiB(InstallRuntimeReserveBytes):F1} GB on {installRoot} and " +
                  $"{ToGiB(dataRequired):F1} GB on {dataRoot}";
            var availableText = sameVolume
                ? $"{ToGiB(installAvailable):F1} GB available on {installRoot}"
                : $"{ToGiB(installAvailable):F1} GB on {installRoot}; " +
                  $"{ToGiB(dataAvailable):F1} GB on {dataRoot}";
            return sufficient
                ? new(
                    true,
                    $"{availableText}; {requiredText} reserved for the signed brain, native staging, and recovery headroom",
                    InstallRuntimeReserveBytes,
                    dataRequired)
                : new(
                    false,
                    $"Only {availableText}. Free at least {requiredText} before installing; old signed cohorts stay retained for rollback.",
                    InstallRuntimeReserveBytes,
                    dataRequired);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return new(
                false,
                "Could not prove signed-brain storage capacity. Check the install and ProgramData volumes, then retry. Support code: SETUP-BRAIN-DISK-PROBE.",
                InstallRuntimeReserveBytes,
                DataSafetyReserveBytes);
        }
    }

    private static string RequireVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException("Storage volume root is unavailable.");
        return root;
    }

    private static long CalculateDataRequiredBytes(
        string dataDirectory,
        AgentReasoningConfig? reasoning,
        Func<string, bool> verifiedCohortExists,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now)
    {
        var required = DataSafetyReserveBytes;
        if (reasoning is not { Enabled: true }) return required;
        var publisher = reasoning.ValidatePublisher(trustedPublisherKeys, now);
        if (!publisher.IsValid ||
            reasoning.ModelSizeBytes is not > 0 ||
            reasoning.NativeLibsSizeBytes is not > 0)
            throw new InvalidDataException("Signed brain storage metadata is invalid.");
        if (verifiedCohortExists(reasoning.GetBrainCohortRoot(dataDirectory))) return required;
        return checked(
            required +
            reasoning.ModelSizeBytes.Value +
            reasoning.NativeLibsSizeBytes.Value +
            InstalledBrainCohortVerifier.MaxNativeUncompressedBytes);
    }

    private static double ToGiB(long bytes) => bytes / (1024d * 1024 * 1024);
}
