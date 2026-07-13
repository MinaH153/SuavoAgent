using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Setup.Gui.Services;

internal static partial class BrainInstaller
{
    private const int MaxRepairReceiptBytes = 8 * 1024;
    private const string RepairReceiptsDirectoryName = "repair-receipts";
    private static readonly JsonSerializerOptions RepairReceiptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    private sealed record BrainRepairReceipt(
        int SchemaVersion,
        string CohortId,
        string RepairId,
        string VerificationCode,
        string Status,
        string DetectedAtUtc,
        string UpdatedAtUtc);

    private static bool QuarantineInvalidCohort(
        string cohortRoot,
        string cohortId,
        string verificationCode,
        string cohortsRoot,
        DateTimeOffset now)
    {
        if (!Directory.Exists(cohortRoot) || !IsLowerHex(cohortId.AsSpan()) ||
            cohortId.Length != 64 ||
            string.IsNullOrWhiteSpace(verificationCode) ||
            verificationCode.Length > 128 ||
            verificationCode.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            return false;
        var repairId = Guid.NewGuid().ToString("N");
        var quarantine = cohortRoot + ".quarantine-" + repairId;
        var timestamp = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        var receipt = new BrainRepairReceipt(
            1,
            cohortId,
            repairId,
            verificationCode,
            "repair_authorized",
            timestamp,
            timestamp);
        try
        {
            if (!WriteRepairReceipt(cohortsRoot, receipt)) return false;
            Directory.Move(cohortRoot, quarantine);
            if (!DeleteStage(quarantine)) return false;
            return WriteRepairReceipt(
                cohortsRoot,
                receipt with
                {
                    Status = "invalid_cohort_removed",
                    UpdatedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                });
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool CleanupAbandonedQuarantines(
        string cohortsRoot,
        Func<bool> repairAuthorized)
    {
        try
        {
            var quarantines = Directory.EnumerateFileSystemEntries(
                    cohortsRoot,
                    "*.quarantine-*",
                    SearchOption.TopDirectoryOnly)
                .Take(2)
                .ToArray();
            if (quarantines.Length == 0) return true;
            // One content-addressed cohort can have at most one crash-left repair
            // quarantine. More is unexpected state and requires manual review.
            if (quarantines.Length != 1 || !repairAuthorized()) return false;
            var quarantine = quarantines[0];
            var name = Path.GetFileName(quarantine);
            const string marker = ".quarantine-";
            var split = name.IndexOf(marker, StringComparison.Ordinal);
            if (split != 64 || name.Length != 64 + marker.Length + 32 ||
                !IsLowerHex(name.AsSpan(0, split)) ||
                !IsLowerHex(name.AsSpan(split + marker.Length)))
                return false;
            var cohortId = name[..split];
            var repairId = name[(split + marker.Length)..];
            if (!TryReadRepairReceipt(cohortsRoot, cohortId, out var receipt) ||
                receipt is null ||
                receipt.SchemaVersion != 1 ||
                receipt.CohortId != cohortId ||
                receipt.RepairId != repairId ||
                receipt.Status != "repair_authorized")
                return false;
            if (!DeleteStage(quarantine)) return false;
            return WriteRepairReceipt(
                cohortsRoot,
                receipt with
                {
                    Status = "invalid_cohort_removed",
                    UpdatedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                });
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool WriteRepairReceipt(
        string cohortsRoot,
        BrainRepairReceipt receipt)
    {
        var receiptRoot = Path.Combine(cohortsRoot, RepairReceiptsDirectoryName);
        Directory.CreateDirectory(receiptRoot);
        if (!ProtectCohort(receiptRoot)) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, RepairReceiptJson);
        if (bytes.Length is <= 0 or > MaxRepairReceiptBytes) return false;
        var path = Path.Combine(receiptRoot, receipt.CohortId + ".json");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            if (!ProtectCohort(receiptRoot)) return false;
            var persisted = File.ReadAllBytes(path);
            return persisted.Length == bytes.Length && persisted.SequenceEqual(bytes);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool TryReadRepairReceipt(
        string cohortsRoot,
        string cohortId,
        out BrainRepairReceipt? receipt)
    {
        receipt = null;
        var receiptRoot = Path.Combine(cohortsRoot, RepairReceiptsDirectoryName);
        if (!Directory.Exists(receiptRoot) || !VerifyCohortAcl(receiptRoot)) return false;
        var path = Path.Combine(receiptRoot, cohortId + ".json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxRepairReceiptBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        receipt = JsonSerializer.Deserialize<BrainRepairReceipt>(
            File.ReadAllText(path, Encoding.UTF8),
            RepairReceiptJson);
        return receipt is not null;
    }

    private static bool IsAdministratorForRepair()
    {
        if (!OperatingSystem.IsWindows()) return true;
        return IsWindowsAdministratorForRepair();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministratorForRepair()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
