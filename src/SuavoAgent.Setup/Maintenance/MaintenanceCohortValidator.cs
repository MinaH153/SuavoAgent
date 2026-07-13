using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Fail-closed validation performed before repair touches the Service Control
/// Manager or ACLs. The install marker fixes the allowed executable names;
/// binaries.manifest binds every member of the immutable five-executable cohort
/// to the bytes currently installed.
/// </summary>
internal static class MaintenanceCohortValidator
{
    public static CohortValidationResult Validate(
        string installDir,
        string manifestPath,
        string? updatePublicKeyDerBase64 = null,
        Func<string, AuthenticodePublisherTrust>? verifyAuthenticode = null,
        Func<string, MaintenanceHostTrustResult>? verifyMaintenanceTrust = null)
    {
        verifyAuthenticode ??= AuthenticodePublisherVerifier.Verify;
        verifyMaintenanceTrust ??= updatePublicKeyDerBase64 is null
            ? MaintenanceHostTrustVerifier.Verify
            : path => MaintenanceHostTrustVerifier.Verify(path, updatePublicKeyDerBase64);
        try
        {
            var statePath = Path.Combine(installDir, MaintenanceContract.InstallStateFileName);
            var maintenancePath = Path.Combine(installDir, MaintenanceContract.ExecutableName);
            if (!File.Exists(statePath))
                return CohortValidationResult.Fail("install_state_missing");
            if (!File.Exists(maintenancePath))
                return CohortValidationResult.Fail("maintenance_host_missing");
            if (!File.Exists(manifestPath))
                return CohortValidationResult.Fail("binaries_manifest_missing");

            var trust = verifyMaintenanceTrust(maintenancePath);
            if (!trust.IsTrusted)
                return CohortValidationResult.Fail($"maintenance_host_untrusted:{trust.Code}");

            var state = JsonSerializer.Deserialize<InstallState>(
                File.ReadAllText(statePath),
                MaintenanceHostInstaller.JsonOptions);
            if (state is null ||
                state.SchemaVersion != 1 ||
                !string.Equals(
                    state.InstallerKind,
                    MaintenanceHostInstaller.InstallerKind,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    state.MaintenanceExecutable,
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CohortValidationResult.Fail("install_state_invalid");
            }

            var expectedCohort = BinaryDownloader.InstalledCohort;
            if (state.InstalledCohort is null ||
                state.InstalledCohort.Count != expectedCohort.Count ||
                !state.InstalledCohort.SequenceEqual(expectedCohort, StringComparer.OrdinalIgnoreCase))
            {
                return CohortValidationResult.Fail("install_cohort_invalid");
            }

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object)
                return CohortValidationResult.Fail("binaries_manifest_invalid");

            foreach (var fileName in expectedCohort)
            {
                var binaryPath = Path.Combine(installDir, fileName);
                if (!File.Exists(binaryPath))
                    return CohortValidationResult.Fail($"binary_missing:{fileName}");
                if (!manifest.RootElement.TryGetProperty(fileName, out var hashElement) ||
                    hashElement.ValueKind != JsonValueKind.String)
                {
                    return CohortValidationResult.Fail($"manifest_entry_missing:{fileName}");
                }

                if (!HashMatches(binaryPath, hashElement.GetString()))
                    return CohortValidationResult.Fail($"binary_hash_mismatch:{fileName}");
                var publisher = verifyAuthenticode(binaryPath);
                if (!publisher.IsTrusted)
                    return CohortValidationResult.Fail(
                        $"binary_publisher_invalid:{fileName}:{publisher.Code}");
            }

            return CohortValidationResult.Ok();
        }
        catch (JsonException)
        {
            return CohortValidationResult.Fail("maintenance_metadata_invalid_json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return CohortValidationResult.Fail($"maintenance_metadata_unreadable:{ex.GetType().Name}");
        }
    }

    private static bool HashMatches(string path, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) ||
            expectedHex.Length != 64 ||
            !expectedHex.All(Uri.IsHexDigit))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        var expected = Convert.FromHexString(expectedHex);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

internal sealed record CohortValidationResult(bool IsValid, string Code)
{
    public static CohortValidationResult Ok() => new(true, "ok");
    public static CohortValidationResult Fail(string code) => new(false, code);
}
