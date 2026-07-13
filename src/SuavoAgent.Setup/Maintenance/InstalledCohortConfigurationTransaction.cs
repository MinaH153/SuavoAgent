using System.Text.Json;
using SuavoAgent.Setup.Security;

namespace SuavoAgent.Setup.Maintenance;

internal enum InstalledConfigurationStage
{
    Prepared,
    Applying,
    Applied,
    ProbationHealthy,
    AuthorityUnknown,
    AuthorityPromoted,
    ActiveHealthy,
    RollingBack,
}

internal sealed record InstalledConfigurationJournal(
    int SchemaVersion,
    string TransactionId,
    InstalledConfigurationStage Stage,
    string InstallDirectory,
    string DataDirectory,
    string BackupDirectory,
    IReadOnlyList<string> OriginallyPresent);

internal sealed record InstalledConfigurationResult(
    bool Succeeded,
    bool RecoveryRequired,
    bool RolledBack,
    string Code)
{
    internal static InstalledConfigurationResult Success() =>
        new(true, false, false, "configured");

    internal static InstalledConfigurationResult Recover(string code) =>
        new(false, true, false, code);

    internal static InstalledConfigurationResult Failed(
        string code,
        bool rolledBack = false) =>
        new(false, false, rolledBack, code);
}

internal sealed record InstalledConfigurationCallbacks(
    Func<bool> ValidateCohort,
    Func<bool> Quiesce,
    Action ApplyConfigurationAndStageAuthority,
    Action PreserveAuthorityForRecovery,
    Func<bool> StartInstalledCohort,
    Func<bool> VerifyProbationHealth,
    Func<AuthorityPromotionOutcome> PromoteAuthority,
    Func<bool> FinalizeAuthority,
    Func<bool> RestartPromotedCohort,
    Func<bool> CompleteAuthority,
    Func<bool> AbortAuthority);

/// <summary>
/// Durable configuration-only transaction for a cohort already owned by MSI.
/// It snapshots only four allowlisted non-executable configuration artifacts,
/// never replaces binaries or service registrations, and treats an ambiguous
/// cloud authority result as forward-only recovery.
/// </summary>
internal sealed class InstalledCohortConfigurationTransaction
{
    internal const string JournalFileName = "configuration-transaction.json";
    private const int SchemaVersion = 1;
    private const int MaxJournalBytes = 64 * 1024;

    private sealed record BackupSpec(
        string Key,
        bool InInstallDirectory,
        string FileName,
        int MaximumBytes);

    private static readonly IReadOnlyList<BackupSpec> BackupSpecs =
    [
        new("appsettings", true, "appsettings.json", 2 * 1024 * 1024),
        new("consent", false, "consent-receipt.json", 1024 * 1024),
        new("compliance", false, "vertical-compliance-lkg.json", 64 * 1024),
        new(
            "sql-certificate",
            false,
            SqlServerCertificateEnrollment.InstalledFileName,
            1024 * 1024),
    ];

    private readonly string _installDirectory;
    private readonly string _dataDirectory;
    private readonly string _maintenanceDirectory;
    private readonly InstalledConfigurationCallbacks _callbacks;
    private readonly Action<string> _lockdownMaintenanceDirectory;
    private readonly Func<string, string, bool> _reassertAcls;

    internal InstalledCohortConfigurationTransaction(
        string installDirectory,
        string dataDirectory,
        string maintenanceDirectory,
        InstalledConfigurationCallbacks callbacks,
        Action<string>? lockdownMaintenanceDirectory = null,
        Func<string, string, bool>? reassertAcls = null)
    {
        _installDirectory = CanonicalDirectory(installDirectory);
        _dataDirectory = CanonicalDirectory(dataDirectory);
        _maintenanceDirectory = CanonicalDirectory(maintenanceDirectory);
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _lockdownMaintenanceDirectory = lockdownMaintenanceDirectory ??
            ServiceInstaller.LockdownMaintenanceDirectoryAcl;
        _reassertAcls = reassertAcls ?? ServiceInstaller.ReassertMaintenanceAcls;
    }

    internal string JournalPath => Path.Combine(
        _maintenanceDirectory,
        JournalFileName);

    internal bool HasPendingJournal => File.Exists(JournalPath);

    internal InstalledConfigurationResult Execute()
    {
        if (HasPendingJournal)
            return InstalledConfigurationResult.Recover("prior_configuration_recovery_required");
        if (!_callbacks.ValidateCohort())
            return InstalledConfigurationResult.Failed("installed_cohort_invalid");

        InstalledConfigurationJournal journal;
        try
        {
            journal = PrepareSnapshot();
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return InstalledConfigurationResult.Failed("configuration_snapshot_failed");
        }

        if (!_callbacks.Quiesce())
            return RollBack(journal, "installed_cohort_quiesce_failed");

        try
        {
            journal = Advance(journal, InstalledConfigurationStage.Applying);
            _callbacks.ApplyConfigurationAndStageAuthority();
            _callbacks.PreserveAuthorityForRecovery();
            journal = Advance(journal, InstalledConfigurationStage.Applied);
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return RollBack(journal, "configuration_apply_failed");
        }

        try
        {
            if (!_callbacks.StartInstalledCohort())
                return RollBack(journal, "installed_cohort_start_failed");
            if (!_callbacks.VerifyProbationHealth())
                return RollBack(journal, "probation_health_failed");
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return RollBack(journal, "probation_health_unavailable");
        }
        journal = Advance(journal, InstalledConfigurationStage.ProbationHealthy);

        // Crossing the cloud authority boundary is irreversible from the
        // workstation's point of view. Persist the forward-recovery state
        // before sending the confirmation so a process or power loss after
        // server acceptance can never be mistaken for a pre-authority state.
        journal = Advance(journal, InstalledConfigurationStage.AuthorityUnknown);

        AuthorityPromotionOutcome promotion;
        try
        {
            promotion = _callbacks.PromoteAuthority();
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            promotion = AuthorityPromotionOutcome.Unknown;
        }

        if (promotion == AuthorityPromotionOutcome.Rejected)
            return RollBack(journal, "authority_rejected");
        if (promotion == AuthorityPromotionOutcome.Unknown)
            return InstalledConfigurationResult.Recover("authority_confirmation_unknown");

        journal = Advance(journal, InstalledConfigurationStage.AuthorityPromoted);
        return CompleteForward(journal);
    }

    internal InstalledConfigurationResult Recover()
    {
        InstalledConfigurationJournal journal;
        try
        {
            journal = ReadAndValidateJournal();
        }
        catch (FileNotFoundException)
        {
            return InstalledConfigurationResult.Success();
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return InstalledConfigurationResult.Recover("configuration_journal_invalid");
        }

        if (!_callbacks.ValidateCohort())
            return InstalledConfigurationResult.Recover("installed_cohort_invalid");

        if (journal.Stage == InstalledConfigurationStage.RollingBack)
            return RollBack(journal, "configuration_rollback_recovered");

        if (journal.Stage < InstalledConfigurationStage.AuthorityUnknown)
            return RollBack(journal, "pre_authority_configuration_recovered");

        if (journal.Stage == InstalledConfigurationStage.AuthorityUnknown)
        {
            AuthorityPromotionOutcome promotion;
            try
            {
                promotion = _callbacks.PromoteAuthority();
            }
            catch (Exception ex) when (IsSafeBoundaryException(ex))
            {
                promotion = AuthorityPromotionOutcome.Unknown;
            }
            if (promotion == AuthorityPromotionOutcome.Rejected)
                return RollBack(journal, "recovered_authority_rejected");
            if (promotion == AuthorityPromotionOutcome.Unknown)
                return InstalledConfigurationResult.Recover(
                    "authority_confirmation_still_unknown");
            journal = Advance(journal, InstalledConfigurationStage.AuthorityPromoted);
        }

        return CompleteForward(journal);
    }

    private InstalledConfigurationResult CompleteForward(
        InstalledConfigurationJournal journal)
    {
        try
        {
            if (journal.Stage < InstalledConfigurationStage.ActiveHealthy)
            {
                if (!_callbacks.FinalizeAuthority())
                    return InstalledConfigurationResult.Recover(
                        "authority_finalization_incomplete");
                if (!_callbacks.RestartPromotedCohort())
                    return InstalledConfigurationResult.Recover(
                        "active_health_recovery_required");
                journal = Advance(journal, InstalledConfigurationStage.ActiveHealthy);
            }
            if (!_callbacks.CompleteAuthority())
                return InstalledConfigurationResult.Recover(
                    "authority_cleanup_incomplete");
            if (!DeleteTransactionArtifacts(journal))
                return InstalledConfigurationResult.Recover(
                    "configuration_journal_cleanup_failed");
            return InstalledConfigurationResult.Success();
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return InstalledConfigurationResult.Recover(
                "forward_configuration_recovery_required");
        }
    }

    private InstalledConfigurationResult RollBack(
        InstalledConfigurationJournal journal,
        string code)
    {
        try
        {
            // Rollback intent is durable before the first compensating write.
            // A crash after restoring files or aborting the pending key must
            // resume compensation, never re-enter cloud promotion.
            if (journal.Stage != InstalledConfigurationStage.RollingBack)
                journal = Advance(
                    journal,
                    InstalledConfigurationStage.RollingBack);
            if (!_callbacks.Quiesce())
                return InstalledConfigurationResult.Recover(
                    "configuration_rollback_quiesce_failed");
            if (!RestoreSnapshot(journal))
                return InstalledConfigurationResult.Recover(
                    "configuration_rollback_restore_failed");
            if (!_callbacks.AbortAuthority())
                return InstalledConfigurationResult.Recover(
                    "configuration_rollback_authority_failed");
            if (!_callbacks.StartInstalledCohort())
                return InstalledConfigurationResult.Recover(
                    "configuration_rollback_restart_failed");
            if (!DeleteTransactionArtifacts(journal))
                return InstalledConfigurationResult.Recover(
                    "configuration_rollback_cleanup_failed");
            return InstalledConfigurationResult.Failed(code, rolledBack: true);
        }
        catch (Exception ex) when (IsSafeBoundaryException(ex))
        {
            return InstalledConfigurationResult.Recover(
                "configuration_rollback_incomplete");
        }
    }

    private InstalledConfigurationJournal PrepareSnapshot()
    {
        EnsureSafeDirectory(_installDirectory, mustExist: true);
        EnsureSafeDirectory(_dataDirectory, mustExist: true);
        Directory.CreateDirectory(_maintenanceDirectory);
        EnsureSafeDirectory(_maintenanceDirectory, mustExist: true);
        _lockdownMaintenanceDirectory(_maintenanceDirectory);

        var transactionId = Guid.NewGuid().ToString("N");
        var backupDirectory = Path.Combine(
            _maintenanceDirectory,
            "configuration-backup-" + transactionId);
        Directory.CreateDirectory(backupDirectory);
        EnsureSafeDirectory(backupDirectory, mustExist: true);
        _lockdownMaintenanceDirectory(backupDirectory);

        var present = new List<string>();
        foreach (var spec in BackupSpecs)
        {
            var source = SourcePath(spec);
            if (!File.Exists(source)) continue;
            EnsureRegularBoundedFile(source, spec.MaximumBytes);
            var destination = Path.Combine(backupDirectory, spec.Key + ".bak");
            File.Copy(source, destination, overwrite: false);
            EnsureRegularBoundedFile(destination, spec.MaximumBytes);
            present.Add(spec.Key);
        }

        var journal = new InstalledConfigurationJournal(
            SchemaVersion,
            transactionId,
            InstalledConfigurationStage.Prepared,
            _installDirectory,
            _dataDirectory,
            backupDirectory,
            present.AsReadOnly());
        WriteJournal(journal);
        return journal;
    }

    private bool RestoreSnapshot(InstalledConfigurationJournal journal)
    {
        ValidateJournalIdentity(journal);
        var present = journal.OriginallyPresent.ToHashSet(StringComparer.Ordinal);
        foreach (var spec in BackupSpecs)
        {
            var destination = SourcePath(spec);
            if (!present.Contains(spec.Key))
            {
                if (Path.Exists(destination))
                {
                    if (Directory.Exists(destination)) return false;
                    File.Delete(destination);
                }
                continue;
            }

            var source = Path.Combine(journal.BackupDirectory, spec.Key + ".bak");
            EnsureRegularBoundedFile(source, spec.MaximumBytes);
            var temporary = destination + ".restore-" + journal.TransactionId;
            if (File.Exists(temporary)) File.Delete(temporary);
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
            EnsureRegularBoundedFile(destination, spec.MaximumBytes);
        }
        if (!_reassertAcls(_installDirectory, _dataDirectory))
            return false;
        return true;
    }

    private InstalledConfigurationJournal Advance(
        InstalledConfigurationJournal journal,
        InstalledConfigurationStage stage)
    {
        if (stage <= journal.Stage)
            throw new InvalidOperationException("Configuration journal cannot move backward.");
        var advanced = journal with { Stage = stage };
        WriteJournal(advanced);
        return advanced;
    }

    private InstalledConfigurationJournal ReadAndValidateJournal()
    {
        EnsureRegularBoundedFile(JournalPath, MaxJournalBytes);
        var bytes = File.ReadAllBytes(JournalPath);
        var journal = JsonSerializer.Deserialize<InstalledConfigurationJournal>(
            bytes,
            JsonOptions)
            ?? throw new InvalidDataException("Configuration journal is empty.");
        ValidateJournalIdentity(journal);
        return journal;
    }

    private void ValidateJournalIdentity(InstalledConfigurationJournal journal)
    {
        if (journal.SchemaVersion != SchemaVersion ||
            journal.TransactionId.Length != 32 ||
            !journal.TransactionId.All(Uri.IsHexDigit) ||
            !string.Equals(
                journal.InstallDirectory,
                _installDirectory,
                PathComparison) ||
            !string.Equals(
                journal.DataDirectory,
                _dataDirectory,
                PathComparison) ||
            !string.Equals(
                journal.BackupDirectory,
                Path.Combine(
                    _maintenanceDirectory,
                    "configuration-backup-" + journal.TransactionId),
                PathComparison) ||
            journal.OriginallyPresent.Count > BackupSpecs.Count ||
            journal.OriginallyPresent.Distinct(StringComparer.Ordinal).Count() !=
            journal.OriginallyPresent.Count ||
            journal.OriginallyPresent.Any(key =>
                BackupSpecs.All(spec => spec.Key != key)))
            throw new InvalidDataException("Configuration journal identity is invalid.");
        EnsureSafeDirectory(journal.BackupDirectory, mustExist: true);
    }

    private void WriteJournal(InstalledConfigurationJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        if (bytes.Length is <= 0 or > MaxJournalBytes)
            throw new InvalidDataException("Configuration journal exceeds its bound.");
        var temporary = JournalPath + ".tmp-" + journal.TransactionId;
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, JournalPath, overwrite: true);
            EnsureRegularBoundedFile(JournalPath, MaxJournalBytes);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private bool DeleteTransactionArtifacts(InstalledConfigurationJournal journal)
    {
        ValidateJournalIdentity(journal);
        foreach (var spec in BackupSpecs)
        {
            var path = Path.Combine(journal.BackupDirectory, spec.Key + ".bak");
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.EnumerateFileSystemEntries(journal.BackupDirectory).Any())
            return false;
        File.Delete(JournalPath);
        Directory.Delete(journal.BackupDirectory, recursive: false);
        return !Path.Exists(journal.BackupDirectory) && !Path.Exists(JournalPath);
    }

    private string SourcePath(BackupSpec spec) => Path.Combine(
        spec.InInstallDirectory ? _installDirectory : _dataDirectory,
        spec.FileName);

    private static void EnsureRegularBoundedFile(string path, int maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A configuration artifact is untrusted.");
    }

    private static void EnsureSafeDirectory(string path, bool mustExist)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            if (mustExist) throw new DirectoryNotFoundException();
            return;
        }
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A configuration directory is redirected.");
        for (var parent = directory.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Exists &&
                parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "A configuration directory parent is redirected.");
        }
    }

    private static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Configuration directory must be absolute.");
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static bool IsSafeBoundaryException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or InvalidOperationException or
            OperationCanceledException or
            System.Security.SecurityException or System.Security.Cryptography.CryptographicException;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
