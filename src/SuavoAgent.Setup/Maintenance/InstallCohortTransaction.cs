using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Setup.Maintenance;

internal enum InstallTransactionPhase
{
    Prepared = 0,
    Quiesced = 1,
    PriorMoved = 2,
    NewMoved = 3,
    ManifestCommitted = 4,
    Activated = 5,
    Healthy = 6,
    Committed = 7,
    RollingBack = 8,
    RolledBack = 9,
    AuthorityPromotionStarted = 10,
    AuthorityPromoted = 11,
    PriorRestored = 12,
}

internal enum AuthorityPromotionOutcome
{
    Promoted,
    Rejected,
    Unknown,
}

internal sealed record InstallTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    string LiveDirectory,
    string StagingDirectory,
    string BackupDirectory,
    string DataManifestPath,
    string PreparedManifestPath,
    string BackupManifestPath,
    bool HadPriorInstall,
    bool HadPriorManifest,
    bool RequiresAuthorityPromotion,
    string? PriorDirectoryDigest,
    string? PriorManifestDigest,
    InstallTransactionPhase Phase,
    DateTimeOffset UpdatedAtUtc);

internal sealed record InstallTransactionCallbacks(
    Func<bool> Quiesce,
    Func<bool> Activate,
    Func<bool> VerifyHealthy,
    Func<bool> ReactivatePrior,
    Action<InstallTransactionPhase>? AfterCheckpoint = null,
    Func<AuthorityPromotionOutcome>? PromoteAuthority = null,
    Func<bool>? FinalizeAuthority = null,
    Action<InstallTransactionPhase>? BeforeCheckpoint = null,
    Action? AfterJournalPrepared = null);

internal sealed record InstallTransactionResult(bool Succeeded, bool RolledBack, string Code)
{
    public static InstallTransactionResult Success() => new(true, false, "committed");
    public static InstallTransactionResult Failed(string code, bool rolledBack) =>
        new(false, rolledBack, code);
}

/// <summary>Test-only process-death seam; real process termination never throws.</summary>
internal sealed class InstallTransactionProcessCrashException : Exception;

/// <summary>
/// Durable same-volume install-directory swap. The replacement cohort is fully
/// staged and signed before this class is entered. The old directory remains an
/// intact rollback unit until the replacement reaches a caller-supplied health
/// milestone; a bounded journal outside the live directory makes every crash
/// point recoverable by the native maintenance runner.
/// </summary>
internal sealed class InstallCohortTransaction
{
    internal const int JournalSchemaVersion = 3;
    internal const int MaxJournalBytes = 64 * 1024;
    internal const int MaxRollbackFiles = 4_096;
    internal const long MaxRollbackManifestBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _liveDirectory;
    private readonly string _stagingDirectory;
    private readonly string _maintenanceRoot;
    private readonly string _dataManifestPath;
    private readonly string _preparedManifestPath;
    private readonly string _transactionId;
    private readonly string _backupDirectory;
    private readonly string _backupManifestPath;
    private readonly string _journalPath;
    private readonly InstallTransactionCallbacks _callbacks;
    private readonly bool _requiresAuthorityPromotion;

    public InstallCohortTransaction(
        string liveDirectory,
        string stagingDirectory,
        string maintenanceRoot,
        string dataManifestPath,
        string preparedManifestPath,
        string transactionId,
        InstallTransactionCallbacks callbacks,
        bool requiresAuthorityPromotion = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(callbacks);

        _liveDirectory = CanonicalDirectory(liveDirectory);
        _stagingDirectory = CanonicalDirectory(stagingDirectory);
        _maintenanceRoot = CanonicalDirectory(maintenanceRoot);
        _dataManifestPath = Path.GetFullPath(dataManifestPath);
        _preparedManifestPath = Path.GetFullPath(preparedManifestPath);
        _transactionId = ValidateTransactionId(transactionId);
        _backupDirectory = _liveDirectory + ".rollback-" + _transactionId;
        _backupManifestPath = Path.Combine(
            _maintenanceRoot,
            "binaries.manifest.rollback-" + _transactionId);
        _journalPath = Path.Combine(_maintenanceRoot, "install-transaction.json");
        _callbacks = callbacks;
        _requiresAuthorityPromotion = requiresAuthorityPromotion;
        if (!AuthorityCallbackShapeIsValid(
                _requiresAuthorityPromotion,
                _callbacks))
            throw new ArgumentException(
                "Install authority mode does not match its promotion/finalization callbacks.");

        ValidateStaticPaths();
    }

    internal string JournalPath => _journalPath;
    internal string BackupDirectory => _backupDirectory;

    public InstallTransactionResult Execute()
    {
        if (File.Exists(_journalPath))
            return InstallTransactionResult.Failed("unfinished_transaction_present", false);
        if (!Directory.Exists(_stagingDirectory))
            return InstallTransactionResult.Failed("staging_directory_missing", false);
        if (!File.Exists(_preparedManifestPath))
            return InstallTransactionResult.Failed("prepared_manifest_missing", false);
        if (Directory.Exists(_backupDirectory) || File.Exists(_backupManifestPath))
            return InstallTransactionResult.Failed("rollback_artifact_collision", false);

        Directory.CreateDirectory(_maintenanceRoot);
        var journal = NewJournal(
            Directory.Exists(_liveDirectory),
            File.Exists(_dataManifestPath),
            InstallTransactionPhase.Prepared);
        WriteJournal(journal);
        _callbacks.AfterJournalPrepared?.Invoke();

        try
        {
            if (!_callbacks.Quiesce())
            {
                // No filesystem or service-definition mutation has happened.
                // Do not touch the live cohort or its manifest merely because
                // the running services refused to quiesce.
                CleanupRolledBack(journal);
                return InstallTransactionResult.Failed("quiesce_failed", false);
            }

            journal = Checkpoint(journal, InstallTransactionPhase.Quiesced);

            if (journal.HadPriorInstall)
                Directory.Move(_liveDirectory, _backupDirectory);
            journal = Checkpoint(journal, InstallTransactionPhase.PriorMoved);

            Directory.Move(_stagingDirectory, _liveDirectory);
            journal = Checkpoint(journal, InstallTransactionPhase.NewMoved);

            Directory.CreateDirectory(Path.GetDirectoryName(_dataManifestPath)!);
            if (journal.HadPriorManifest)
                File.Copy(_dataManifestPath, _backupManifestPath, overwrite: false);
            AtomicMoveFile(_preparedManifestPath, _dataManifestPath);
            journal = Checkpoint(journal, InstallTransactionPhase.ManifestCommitted);

            if (!_callbacks.Activate())
                return RollBack(journal, "activation_failed", priorMayNeedActivation: true);
            journal = Checkpoint(journal, InstallTransactionPhase.Activated);

            if (!_callbacks.VerifyHealthy())
                return RollBack(journal, "health_milestone_failed", priorMayNeedActivation: true);
            journal = Checkpoint(journal, InstallTransactionPhase.Healthy);

            // Persist uncertainty before the external distributed commit. From
            // this point onward a transport failure can mean "committed, reply
            // lost" and therefore must never restore the predecessor.
            journal = Checkpoint(journal, InstallTransactionPhase.AuthorityPromotionStarted);
            AuthorityPromotionOutcome promotion;
            try
            {
                promotion = _requiresAuthorityPromotion
                    ? _callbacks.PromoteAuthority?.Invoke()
                      ?? AuthorityPromotionOutcome.Unknown
                    : AuthorityPromotionOutcome.Promoted;
            }
            catch (Exception ex) when (ex is not InstallTransactionProcessCrashException)
            {
                return InstallTransactionResult.Failed(
                    "authority_promotion_unknown:" + ex.GetType().Name,
                    rolledBack: false);
            }
            if (promotion == AuthorityPromotionOutcome.Rejected)
                return RollBack(
                    journal,
                    "authority_promotion_failed",
                    priorMayNeedActivation: true);
            if (promotion == AuthorityPromotionOutcome.Unknown)
                return InstallTransactionResult.Failed(
                    "authority_promotion_unknown",
                    rolledBack: false);
            journal = Checkpoint(journal, InstallTransactionPhase.AuthorityPromoted);

            // Cloud authority is now forward-only: the predecessor may already
            // be revoked. Local credential/key finalization is retryable and
            // must never compensate by restoring that predecessor.
            if (_requiresAuthorityPromotion &&
                !(_callbacks.FinalizeAuthority?.Invoke() ?? false))
                return InstallTransactionResult.Failed(
                    "authority_finalization_failed",
                    rolledBack: false);

            journal = Checkpoint(journal, InstallTransactionPhase.Committed);
            CleanupCommitted(journal);
            return InstallTransactionResult.Success();
        }
        catch (Exception ex) when (ex is not InstallTransactionProcessCrashException)
        {
            if (DurablePhase(journal) is
                InstallTransactionPhase.AuthorityPromotionStarted or
                InstallTransactionPhase.AuthorityPromoted or
                InstallTransactionPhase.Committed)
                return InstallTransactionResult.Failed(
                    "forward_recovery_required:" + ex.GetType().Name,
                    rolledBack: false);
            return RollBack(journal, "transaction_io_failed:" + ex.GetType().Name, priorMayNeedActivation: true);
        }
    }

    /// <summary>
    /// Recovers the single durable journal for the expected install/data roots.
    /// Journal paths are never trusted: every path must exactly match a path
    /// derived from the caller's canonical roots and journal transaction id.
    /// </summary>
    public static InstallTransactionResult Recover(
        string liveDirectory,
        string maintenanceRoot,
        string dataManifestPath,
        InstallTransactionCallbacks callbacks,
        bool forwardAuthorityOnly = false)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        var canonicalLive = CanonicalDirectory(liveDirectory);
        var canonicalMaintenance = CanonicalDirectory(maintenanceRoot);
        var canonicalManifest = Path.GetFullPath(dataManifestPath);
        var journalPath = Path.Combine(canonicalMaintenance, "install-transaction.json");
        if (!File.Exists(journalPath))
            return InstallTransactionResult.Success();

        InstallTransactionJournal journal;
        try
        {
            journal = ReadJournal(journalPath);
            ValidateRecoveryJournal(
                journal,
                canonicalLive,
                canonicalMaintenance,
                canonicalManifest);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException or
            ArgumentException)
        {
            return InstallTransactionResult.Failed(
                "journal_invalid:" + ex.GetType().Name,
                false);
        }

        if (forwardAuthorityOnly && journal.Phase is not (
                InstallTransactionPhase.AuthorityPromotionStarted or
                InstallTransactionPhase.AuthorityPromoted or
                InstallTransactionPhase.Committed))
            return InstallTransactionResult.Failed(
                "non_authority_transaction_requires_full_recovery",
                rolledBack: false);

        if (!AuthorityCallbackShapeIsValid(
                journal.RequiresAuthorityPromotion,
                callbacks))
            return InstallTransactionResult.Failed(
                "authority_callbacks_invalid",
                rolledBack: false);

        var transaction = new InstallCohortTransaction(
            journal.LiveDirectory,
            journal.StagingDirectory,
            canonicalMaintenance,
            journal.DataManifestPath,
            journal.PreparedManifestPath,
            journal.TransactionId,
            callbacks,
            journal.RequiresAuthorityPromotion);

        if (journal.Phase == InstallTransactionPhase.Committed)
        {
            transaction.CleanupCommitted(journal);
            return InstallTransactionResult.Success();
        }

        if (journal.Phase == InstallTransactionPhase.AuthorityPromotionStarted)
        {
            AuthorityPromotionOutcome promotion;
            try
            {
                promotion = journal.RequiresAuthorityPromotion
                    ? callbacks.PromoteAuthority?.Invoke()
                      ?? AuthorityPromotionOutcome.Unknown
                    : AuthorityPromotionOutcome.Promoted;
            }
            catch (Exception ex) when (ex is not InstallTransactionProcessCrashException)
            {
                return InstallTransactionResult.Failed(
                    "authority_promotion_unknown:" + ex.GetType().Name,
                    rolledBack: false);
            }
            if (promotion == AuthorityPromotionOutcome.Rejected)
                return transaction.RollBack(
                    journal,
                    "authority_promotion_failed",
                    priorMayNeedActivation: true);
            if (promotion != AuthorityPromotionOutcome.Promoted)
                return InstallTransactionResult.Failed(
                    "authority_promotion_unknown",
                    rolledBack: false);
            try
            {
                journal = transaction.Checkpoint(
                    journal,
                    InstallTransactionPhase.AuthorityPromoted);
            }
            catch (Exception ex) when (ex is not InstallTransactionProcessCrashException)
            {
                return InstallTransactionResult.Failed(
                    "forward_recovery_required:" + ex.GetType().Name,
                    rolledBack: false);
            }
        }

        if (journal.Phase == InstallTransactionPhase.AuthorityPromoted)
        {
            try
            {
                if (journal.RequiresAuthorityPromotion &&
                    !(callbacks.FinalizeAuthority?.Invoke() ?? false))
                    return InstallTransactionResult.Failed(
                        "authority_finalization_failed",
                        rolledBack: false);
                journal = transaction.Checkpoint(journal, InstallTransactionPhase.Committed);
                transaction.CleanupCommitted(journal);
                return InstallTransactionResult.Success();
            }
            catch (Exception ex) when (ex is not InstallTransactionProcessCrashException)
            {
                return InstallTransactionResult.Failed(
                    "forward_recovery_required:" + ex.GetType().Name,
                    rolledBack: false);
            }
        }

        return transaction.RollBack(
            journal,
            "recovered_incomplete_transaction",
            priorMayNeedActivation: true);
    }

    private InstallTransactionResult RollBack(
        InstallTransactionJournal journal,
        string failureCode,
        bool priorMayNeedActivation)
    {
        try
        {
            if (journal.Phase != InstallTransactionPhase.PriorRestored)
                journal = Checkpoint(journal, InstallTransactionPhase.RollingBack);
            if (!_callbacks.Quiesce())
                throw new InvalidDataException("Replacement cohort could not be quiesced for rollback.");

            RestoreAndProvePrior(journal);
            if (journal.Phase != InstallTransactionPhase.PriorRestored)
                journal = Checkpoint(journal, InstallTransactionPhase.PriorRestored);

            if (journal.HadPriorInstall && priorMayNeedActivation && !_callbacks.ReactivatePrior())
                throw new InvalidDataException("Prior cohort could not be reactivated.");

            journal = Checkpoint(journal, InstallTransactionPhase.RolledBack);
            CleanupRolledBack(journal);
            return InstallTransactionResult.Failed(failureCode, true);
        }
        catch (Exception rollbackEx) when (
            rollbackEx is not InstallTransactionProcessCrashException)
        {
            // Keep the journal and every rollback artifact. A later SYSTEM
            // maintenance run can retry recovery; deleting evidence here would
            // turn a recoverable interrupted swap into an unrecoverable brick.
            return InstallTransactionResult.Failed(
                failureCode + ";rollback_failed:" + rollbackEx.GetType().Name,
                false);
        }
    }

    private InstallTransactionJournal NewJournal(
        bool hadPriorInstall,
        bool hadPriorManifest,
        InstallTransactionPhase phase) =>
        new(
            JournalSchemaVersion,
            _transactionId,
            _liveDirectory,
            _stagingDirectory,
            _backupDirectory,
            _dataManifestPath,
            _preparedManifestPath,
            _backupManifestPath,
            hadPriorInstall,
            hadPriorManifest,
            _requiresAuthorityPromotion,
            hadPriorInstall ? ComputeDirectoryDigest(_liveDirectory) : null,
            hadPriorManifest ? ComputeFileDigest(_dataManifestPath, MaxRollbackManifestBytes) : null,
            phase,
            DateTimeOffset.UtcNow);

    private InstallTransactionJournal Checkpoint(
        InstallTransactionJournal journal,
        InstallTransactionPhase phase)
    {
        var next = journal with { Phase = phase, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _callbacks.BeforeCheckpoint?.Invoke(phase);
        WriteJournal(next);
        _callbacks.AfterCheckpoint?.Invoke(phase);
        return next;
    }

    private InstallTransactionPhase DurablePhase(InstallTransactionJournal fallback)
    {
        try
        {
            return File.Exists(_journalPath)
                ? ReadJournal(_journalPath).Phase
                : fallback.Phase;
        }
        catch
        {
            return fallback.Phase;
        }
    }

    private void WriteJournal(InstallTransactionJournal journal)
    {
        Directory.CreateDirectory(_maintenanceRoot);
        var json = JsonSerializer.Serialize(journal, JournalJson);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxJournalBytes)
            throw new InvalidDataException("Install transaction journal exceeds its size limit.");
        var tempPath = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(json);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _journalPath, overwrite: true);
            using var committed = new FileStream(
                _journalPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            DeleteFile(tempPath);
        }
    }

    private static InstallTransactionJournal ReadJournal(string journalPath)
    {
        return JsonSerializer.Deserialize<InstallTransactionJournal>(
                   BoundedFile.ReadUtf8(journalPath, MaxJournalBytes),
                   JournalJson)
               ?? throw new InvalidDataException("Install transaction journal is null.");
    }

    internal static bool? TryReadRecoveryAuthorityMode(
        string liveDirectory,
        string maintenanceRoot,
        string dataManifestPath)
    {
        var live = CanonicalDirectory(liveDirectory);
        var maintenance = CanonicalDirectory(maintenanceRoot);
        var manifest = Path.GetFullPath(dataManifestPath);
        var journalPath = Path.Combine(maintenance, "install-transaction.json");
        if (!File.Exists(journalPath)) return null;
        try
        {
            var journal = ReadJournal(journalPath);
            ValidateRecoveryJournal(journal, live, maintenance, manifest);
            return journal.RequiresAuthorityPromotion;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreAndProvePrior(InstallTransactionJournal journal)
    {
        if (journal.HadPriorInstall)
        {
            if (Directory.Exists(_backupDirectory))
            {
                DeleteDirectory(_liveDirectory);
                if (Directory.Exists(_liveDirectory))
                    throw new IOException("Replacement cohort could not be removed for rollback.");
                Directory.Move(_backupDirectory, _liveDirectory);
            }

            if (!Directory.Exists(_liveDirectory) ||
                !DigestEquals(
                    journal.PriorDirectoryDigest!,
                    ComputeDirectoryDigest(_liveDirectory)))
                throw new InvalidDataException(
                    "Exact prior install bytes could not be proven after rollback.");
        }
        else
        {
            DeleteDirectory(_liveDirectory);
            if (Directory.Exists(_liveDirectory))
                throw new IOException("Partial fresh-install cohort could not be removed.");
        }

        if (journal.HadPriorManifest)
        {
            if (File.Exists(_backupManifestPath))
                AtomicMoveFile(_backupManifestPath, _dataManifestPath);
            if (!File.Exists(_dataManifestPath) ||
                !DigestEquals(
                    journal.PriorManifestDigest!,
                    ComputeFileDigest(_dataManifestPath, MaxRollbackManifestBytes)))
                throw new InvalidDataException(
                    "Exact prior manifest could not be proven after rollback.");
        }
        else
        {
            DeleteFile(_dataManifestPath);
            DeleteFile(_backupManifestPath);
            if (File.Exists(_dataManifestPath) || File.Exists(_backupManifestPath))
                throw new IOException("Fresh-install manifest rollback could not be proven.");
        }
    }

    private static string ComputeDirectoryDigest(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Rollback cohort directory is missing.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Rollback cohort root must not be a reparse point.");

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "Rollback cohort must not contain reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    files.Add(entry);
                if (files.Count > MaxRollbackFiles)
                    throw new InvalidDataException(
                        "Rollback cohort exceeds the bounded file-count limit.");
            }
        }

        files.Sort((left, right) => string.Compare(
            Path.GetRelativePath(root, left),
            Path.GetRelativePath(root, right),
            StringComparison.Ordinal));
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            BinaryPrimitives.WriteInt64BigEndian(length, new FileInfo(path).Length);
            aggregate.AppendData(length);
            aggregate.AppendData(Convert.FromHexString(
                ComputeFileDigest(path, long.MaxValue)));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeFileDigest(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 0 || info.Length > maxBytes)
            throw new InvalidDataException("Rollback proof file is missing or too large.");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool DigestEquals(string expected, string actual) =>
        expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool AuthorityCallbackShapeIsValid(
        bool requiresAuthorityPromotion,
        InstallTransactionCallbacks callbacks)
    {
        var count = new Delegate?[]
            { callbacks.PromoteAuthority, callbacks.FinalizeAuthority }
            .Count(callback => callback is not null);
        return requiresAuthorityPromotion ? count == 2 : count == 0;
    }

    private static void ValidateRecoveryJournal(
        InstallTransactionJournal journal,
        string expectedLive,
        string expectedMaintenanceRoot,
        string expectedDataManifest)
    {
        if (journal.SchemaVersion != JournalSchemaVersion)
            throw new InvalidDataException("Install transaction journal schema mismatch.");
        if (journal.HadPriorInstall != IsSha256Hex(journal.PriorDirectoryDigest) ||
            journal.HadPriorManifest != IsSha256Hex(journal.PriorManifestDigest))
            throw new InvalidDataException("Install transaction prior-cohort proof is invalid.");
        var transactionId = ValidateTransactionId(journal.TransactionId);
        var expectedBackup = expectedLive + ".rollback-" + transactionId;
        var expectedBackupManifest = Path.Combine(
            expectedMaintenanceRoot,
            "binaries.manifest.rollback-" + transactionId);
        var expectedJournal = Path.Combine(expectedMaintenanceRoot, "install-transaction.json");
        var actualJournal = Path.Combine(
            CanonicalDirectory(expectedMaintenanceRoot),
            "install-transaction.json");

        if (!PathEquals(journal.LiveDirectory, expectedLive) ||
            !PathEquals(journal.BackupDirectory, expectedBackup) ||
            !PathEquals(journal.DataManifestPath, expectedDataManifest) ||
            !PathEquals(journal.BackupManifestPath, expectedBackupManifest) ||
            !IsSafeSiblingStage(journal.StagingDirectory, expectedLive, transactionId) ||
            !IsStrictChild(journal.PreparedManifestPath, expectedMaintenanceRoot) ||
            !PathEquals(expectedJournal, actualJournal))
        {
            throw new InvalidDataException("Install transaction journal path binding failed.");
        }
    }

    private void ValidateStaticPaths()
    {
        if (PathEquals(_liveDirectory, _stagingDirectory) ||
            IsStrictChild(_stagingDirectory, _liveDirectory) ||
            IsStrictChild(_liveDirectory, _stagingDirectory))
            throw new ArgumentException("Live and staging directories must be separate siblings.");
        if (!PathEquals(Path.GetDirectoryName(_liveDirectory), Path.GetDirectoryName(_stagingDirectory)))
            throw new ArgumentException("Staging must be a same-volume sibling of the live directory.");
        if (!PathEquals(Path.GetPathRoot(_liveDirectory), Path.GetPathRoot(_stagingDirectory)))
            throw new ArgumentException("Staging and live directories must share a volume.");
        if (!IsSafeSiblingStage(_stagingDirectory, _liveDirectory, _transactionId))
            throw new ArgumentException("Staging directory name is not bound to the transaction id.");
        if (!IsStrictChild(_preparedManifestPath, _maintenanceRoot) ||
            !IsStrictChild(_backupManifestPath, _maintenanceRoot))
            throw new ArgumentException("Manifest transaction files must remain under the maintenance root.");
        if (IsStrictChild(_journalPath, _liveDirectory))
            throw new ArgumentException("Journal must survive outside the live directory.");
    }

    private void CleanupCommitted(InstallTransactionJournal journal)
    {
        DeleteDirectory(journal.BackupDirectory);
        DeleteDirectory(journal.StagingDirectory);
        DeleteFile(journal.BackupManifestPath);
        DeleteFile(journal.PreparedManifestPath);
        DeleteFile(_journalPath);
    }

    private void CleanupRolledBack(InstallTransactionJournal journal)
    {
        DeleteDirectory(journal.StagingDirectory);
        DeleteDirectory(journal.BackupDirectory);
        DeleteFile(journal.PreparedManifestPath);
        DeleteFile(journal.BackupManifestPath);
        DeleteFile(_journalPath);
    }

    private static void AtomicMoveFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Transaction source file is missing.", sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    private static string CanonicalDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ValidateTransactionId(string transactionId)
    {
        if (transactionId.Length != 32 || !transactionId.All(Uri.IsHexDigit))
            throw new ArgumentException("Transaction id must be 32 hexadecimal characters.");
        return transactionId.ToLowerInvariant();
    }

    private static bool IsSafeSiblingStage(string path, string liveDirectory, string transactionId)
    {
        var expected = liveDirectory + ".staging-" + transactionId;
        return PathEquals(path, expected);
    }

    private static bool IsStrictChild(string path, string root)
    {
        var canonicalPath = Path.GetFullPath(path);
        var canonicalRoot = CanonicalDirectory(root) + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(canonicalRoot, PathComparison);
    }

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
