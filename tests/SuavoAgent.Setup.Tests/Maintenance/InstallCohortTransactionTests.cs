using System.Text.Json;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class InstallCohortTransactionTests
{
    [Fact]
    public void Healthy_reinstall_commits_new_directory_and_manifest_as_one_cohort()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var events = new List<string>();
        var transaction = fixture.Build(
            quiesce: () => { events.Add("quiesce"); return true; },
            activate: () => { events.Add("activate"); return true; },
            healthy: () => { events.Add("healthy"); return true; },
            reactivate: () => { events.Add("reactivate"); return true; });

        var result = transaction.Execute();

        Assert.True(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.Equal("new-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.Equal(["quiesce", "activate", "healthy"], events);
        Assert.False(File.Exists(transaction.JournalPath));
        Assert.False(Directory.Exists(transaction.BackupDirectory));
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Fact]
    public void TransactionConstructorRejectsAuthorityModeCallbackMismatch()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);

        Assert.Throws<ArgumentException>(() => fixture.Build(
            requiresAuthorityPromotion: true));
        Assert.Throws<ArgumentException>(() => fixture.Build(
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => true));
        Assert.Throws<ArgumentException>(() => fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted));
    }

    [Theory]
    [InlineData(false, true, "activation_failed")]
    [InlineData(true, false, "health_milestone_failed")]
    public void Activation_or_health_failure_restores_exact_prior_cohort(
        bool activationSucceeds,
        bool healthSucceeds,
        string expectedCode)
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var reactivated = 0;
        var transaction = fixture.Build(
            activate: () => activationSucceeds,
            healthy: () => healthSucceeds,
            reactivate: () => { reactivated++; return true; });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.Equal(1, reactivated);
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Quiesce_failure_never_touches_live_directory_or_manifest()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(quiesce: () => false);

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("quiesce_failed", result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Pending_authority_is_released_from_process_teardown_only_after_durable_journal()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        string? journalAtCallback = null;
        var transaction = fixture.Build(
            quiesce: () => false,
            afterJournalPrepared: () =>
                journalAtCallback = File.ReadAllText(Path.Combine(
                    fixture.MaintenanceRoot,
                    "install-transaction.json")));

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.NotNull(journalAtCallback);
        Assert.Contains("\"phase\":\"Prepared\"", journalAtCallback);
    }

    [Fact]
    public void Crash_during_post_journal_authority_handoff_is_recoverable_without_live_mutation()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            afterJournalPrepared: () =>
                throw new InstallTransactionProcessCrashException());

        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());
        Assert.True(File.Exists(transaction.JournalPath));
        Assert.Equal("old", fixture.ReadLiveMarker());

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(recovery.Succeeded);
        Assert.True(recovery.RolledBack);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Failed_fresh_install_removes_partial_new_cohort_and_manifest()
    {
        using var fixture = TransactionFixture.Create(withPrior: false);
        var transaction = fixture.Build(activate: () => false);

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.False(Directory.Exists(fixture.Live));
        Assert.False(File.Exists(fixture.DataManifest));
    }

    [Fact]
    public void Crash_after_new_directory_move_is_recovered_to_prior_cohort()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(afterCheckpoint: phase =>
        {
            if (phase == InstallTransactionPhase.NewMoved)
                throw new InstallTransactionProcessCrashException();
        });

        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(recovery.Succeeded);
        Assert.True(recovery.RolledBack);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Theory]
    [InlineData((int)InstallTransactionPhase.PriorMoved)]
    [InlineData((int)InstallTransactionPhase.NewMoved)]
    [InlineData((int)InstallTransactionPhase.ManifestCommitted)]
    [InlineData((int)InstallTransactionPhase.Activated)]
    [InlineData((int)InstallTransactionPhase.Healthy)]
    public void EveryPreAuthorityCrashPhaseRestoresExactPriorCohort(
        int crashAtValue)
    {
        var crashAt = (InstallTransactionPhase)crashAtValue;
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(afterCheckpoint: phase =>
        {
            if (phase == crashAt)
                throw new InstallTransactionProcessCrashException();
        });
        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(recovery.Succeeded);
        Assert.True(recovery.RolledBack);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void CrashAfterBackupMovesBackToLiveReprovesExactPriorAndFinishesRollback()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            activate: () => false,
            beforeCheckpoint: phase =>
            {
                if (phase == InstallTransactionPhase.PriorRestored)
                    throw new InstallTransactionProcessCrashException();
            });

        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.False(Directory.Exists(transaction.BackupDirectory));
        Assert.True(File.Exists(transaction.JournalPath));

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(recovery.Succeeded);
        Assert.True(recovery.RolledBack);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Theory]
    [InlineData((int)InstallTransactionPhase.NewMoved)]
    [InlineData((int)InstallTransactionPhase.Healthy)]
    public void MissingPriorBackupNeverReactivatesOrReportsRollbackSuccess(
        int deleteAtValue)
    {
        var deleteAt = (InstallTransactionPhase)deleteAtValue;
        using var fixture = TransactionFixture.Create(withPrior: true);
        var reactivated = 0;
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Rejected,
            finalizeAuthority: () => true,
            reactivate: () =>
            {
                reactivated++;
                return true;
            },
            afterCheckpoint: phase =>
            {
                if (phase == deleteAt)
                    Directory.Delete(fixture.Live + ".rollback-" + fixture.TransactionId, true);
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Contains("rollback_failed:InvalidDataException", result.Code);
        Assert.Equal(0, reactivated);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));
    }

    [Theory]
    [InlineData((int)InstallTransactionPhase.AuthorityPromotionStarted)]
    [InlineData((int)InstallTransactionPhase.AuthorityPromoted)]
    public void SameCredentialOtaCrashSkipsCredentialReplayUsingDurableTransactionKind(
        int crashAtValue)
    {
        var crashAt = (InstallTransactionPhase)crashAtValue;
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(afterCheckpoint: phase =>
        {
            if (phase == crashAt)
                throw new InstallTransactionProcessCrashException();
        });
        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());

        var mismatchedCallbacks = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => throw new InvalidOperationException(
                    "OTA must not replay device credentials"),
                finalizeAuthority: () => throw new InvalidOperationException(
                    "OTA must not finalize device credentials")));

        Assert.False(mismatchedCallbacks.Succeeded);
        Assert.Equal("authority_callbacks_invalid", mismatchedCallbacks.Code);
        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.True(recovery.Succeeded);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Theory]
    [InlineData((int)InstallTransactionPhase.AuthorityPromotionStarted, "authority_promotion_unknown")]
    [InlineData((int)InstallTransactionPhase.AuthorityPromoted, "authority_finalization_failed")]
    public void DeviceReplacementCrashCannotInferNoOpFromMissingPendingMetadata(
        int crashAtValue,
        string expectedCode)
    {
        var crashAt = (InstallTransactionPhase)crashAtValue;
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => true,
            afterCheckpoint: phase =>
            {
                if (phase == crashAt)
                    throw new InstallTransactionProcessCrashException();
            });
        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => AuthorityPromotionOutcome.Unknown,
                finalizeAuthority: () => false));

        Assert.False(recovery.Succeeded);
        Assert.False(recovery.RolledBack);
        Assert.Equal(expectedCode, recovery.Code);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Crash_after_authority_promotion_recovers_forward_without_revoked_predecessor()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => true,
            afterCheckpoint: phase =>
            {
                if (phase == InstallTransactionPhase.AuthorityPromoted)
                    throw new InstallTransactionProcessCrashException();
            });

        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());
        Assert.Equal("new", fixture.ReadLiveMarker());

        var finalized = 0;
        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => AuthorityPromotionOutcome.Unknown,
                finalizeAuthority: () =>
                {
                    finalized++;
                    return true;
                }));

        Assert.True(recovery.Succeeded);
        Assert.Equal(1, finalized);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.Equal("new-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.False(Directory.Exists(transaction.BackupDirectory));
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void AuthorityPromotedWithoutPendingTargetProofStaysForwardOnlyAndFailsClosed()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => true,
            afterCheckpoint: phase =>
            {
                if (phase == InstallTransactionPhase.AuthorityPromoted)
                    throw new InstallTransactionProcessCrashException();
            });
        Assert.Throws<InstallTransactionProcessCrashException>(() => transaction.Execute());

        var reactivated = 0;
        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                reactivate: () =>
                {
                    reactivated++;
                    return true;
                },
                promoteAuthority: () => AuthorityPromotionOutcome.Unknown,
                finalizeAuthority: () => false));

        Assert.False(recovery.Succeeded);
        Assert.False(recovery.RolledBack);
        Assert.Equal("authority_finalization_failed", recovery.Code);
        Assert.Equal(0, reactivated);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Persistent_authority_promotion_failure_restores_prior_online_cohort()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var reactivated = 0;
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Rejected,
            finalizeAuthority: () => true,
            reactivate: () =>
            {
                reactivated++;
                return true;
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("authority_promotion_failed", result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.Equal(1, reactivated);
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Lost_cloud_commit_responses_preserve_new_cohort_for_exact_replay()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var reactivated = 0;
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => throw new HttpRequestException(
                "cloud committed but every response was lost"),
            finalizeAuthority: () => true,
            reactivate: () =>
            {
                reactivated++;
                return true;
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("authority_promotion_unknown:HttpRequestException", result.Code);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.Equal("new-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.Equal(0, reactivated);
        Assert.True(File.Exists(transaction.JournalPath));

        var replay = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
                finalizeAuthority: () => true));

        Assert.True(replay.Succeeded);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Checkpoint_write_failure_after_cloud_success_recovers_forward()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => true,
            beforeCheckpoint: phase =>
            {
                if (phase == InstallTransactionPhase.AuthorityPromoted)
                    throw new IOException("simulated checkpoint write failure");
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("forward_recovery_required:IOException", result.Code);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
                finalizeAuthority: () => true));

        Assert.True(recovery.Succeeded);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Local_finalization_failure_keeps_new_cohort_and_forward_journal()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var transaction = fixture.Build(
            requiresAuthorityPromotion: true,
            promoteAuthority: () => AuthorityPromotionOutcome.Promoted,
            finalizeAuthority: () => false);

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("authority_finalization_failed", result.Code);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.True(File.Exists(transaction.JournalPath));

        var recovery = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks(
                promoteAuthority: () => AuthorityPromotionOutcome.Unknown,
                finalizeAuthority: () => true));

        Assert.True(recovery.Succeeded);
        Assert.Equal("new", fixture.ReadLiveMarker());
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Unexpected_checkpoint_callback_failure_rolls_back_in_process()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        var reactivated = 0;
        var transaction = fixture.Build(
            reactivate: () => { reactivated++; return true; },
            afterCheckpoint: phase =>
            {
                if (phase == InstallTransactionPhase.NewMoved)
                    throw new InvalidOperationException("unexpected callback failure");
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("transaction_io_failed:InvalidOperationException", result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
        Assert.Equal(1, reactivated);
        Assert.False(File.Exists(transaction.JournalPath));
    }

    [Fact]
    public void Recovery_rejects_oversized_journal_without_allocating_or_mutating_live_cohort()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        File.WriteAllBytes(
            Path.Combine(fixture.MaintenanceRoot, "install-transaction.json"),
            new byte[InstallCohortTransaction.MaxJournalBytes + 1]);

        var result = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("journal_invalid:InvalidDataException", result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("old-manifest", File.ReadAllText(fixture.DataManifest));
    }

    [Theory]
    [InlineData(7)] // legacy v1 Committed
    [InlineData(8)] // legacy v1 RollingBack
    public void LegacyV1NumericPhase_IsNeverReinterpretedAsV2AuthorityState(int legacyPhase)
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        var backup = fixture.Live + ".rollback-" + fixture.TransactionId;
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "keep.txt"), "rollback-evidence");
        var legacy = new
        {
            schemaVersion = 1,
            transactionId = fixture.TransactionId,
            liveDirectory = fixture.Live,
            stagingDirectory = fixture.Staging,
            backupDirectory = backup,
            dataManifestPath = fixture.DataManifest,
            preparedManifestPath = fixture.PreparedManifest,
            backupManifestPath = Path.Combine(
                fixture.MaintenanceRoot,
                "binaries.manifest.rollback-" + fixture.TransactionId),
            hadPriorInstall = true,
            hadPriorManifest = true,
            phase = legacyPhase,
            updatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            Path.Combine(fixture.MaintenanceRoot, "install-transaction.json"),
            JsonSerializer.Serialize(legacy));

        var result = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("journal_invalid:InvalidDataException", result.Code);
        Assert.Equal("old", fixture.ReadLiveMarker());
        Assert.Equal("rollback-evidence", File.ReadAllText(Path.Combine(backup, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(
            fixture.MaintenanceRoot,
            "install-transaction.json")));
    }

    [Fact]
    public void Recovery_rejects_journal_path_injection_without_mutation()
    {
        using var fixture = TransactionFixture.Create(withPrior: true);
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        var evil = Path.Combine(fixture.Root, "victim");
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "keep.txt"), "keep");
        var journal = new InstallTransactionJournal(
            InstallCohortTransaction.JournalSchemaVersion,
            fixture.TransactionId,
            fixture.Live,
            evil,
            fixture.Live + ".rollback-" + fixture.TransactionId,
            fixture.DataManifest,
            fixture.PreparedManifest,
            Path.Combine(fixture.MaintenanceRoot, "binaries.manifest.rollback-" + fixture.TransactionId),
            true,
            true,
            false,
            new string('a', 64),
            new string('b', 64),
            InstallTransactionPhase.NewMoved,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(fixture.MaintenanceRoot, "install-transaction.json"),
            JsonSerializer.Serialize(journal, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        var result = InstallCohortTransaction.Recover(
            fixture.Live,
            fixture.MaintenanceRoot,
            fixture.DataManifest,
            fixture.Callbacks());

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.StartsWith("journal_invalid:", result.Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(evil, "keep.txt")));
        Assert.Equal("old", fixture.ReadLiveMarker());
    }

    private sealed class TransactionFixture : IDisposable
    {
        public string Root { get; }
        public string Live { get; }
        public string Staging { get; }
        public string MaintenanceRoot { get; }
        public string DataManifest { get; }
        public string PreparedManifest { get; }
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");

        private TransactionFixture(bool withPrior)
        {
            Root = Path.Combine(Path.GetTempPath(), "suavo-install-tx-" + Guid.NewGuid().ToString("N"));
            Live = Path.Combine(Root, "ProgramFiles", "Agent");
            Staging = Live + ".staging-" + TransactionId;
            MaintenanceRoot = Path.Combine(Root, "ProgramData", "SuavoAgent-Maintenance");
            DataManifest = Path.Combine(Root, "ProgramData", "SuavoAgent", "binaries.manifest");
            PreparedManifest = Path.Combine(MaintenanceRoot, "binaries.manifest.new-" + TransactionId);

            Directory.CreateDirectory(Staging);
            File.WriteAllText(Path.Combine(Staging, "marker.txt"), "new");
            Directory.CreateDirectory(MaintenanceRoot);
            File.WriteAllText(PreparedManifest, "new-manifest");
            if (withPrior)
            {
                Directory.CreateDirectory(Live);
                File.WriteAllText(Path.Combine(Live, "marker.txt"), "old");
                Directory.CreateDirectory(Path.GetDirectoryName(DataManifest)!);
                File.WriteAllText(DataManifest, "old-manifest");
            }
        }

        public static TransactionFixture Create(bool withPrior) => new(withPrior);

        public InstallCohortTransaction Build(
            Func<bool>? quiesce = null,
            Func<bool>? activate = null,
            Func<bool>? healthy = null,
            Func<bool>? reactivate = null,
            Action<InstallTransactionPhase>? afterCheckpoint = null,
            Func<AuthorityPromotionOutcome>? promoteAuthority = null,
            Func<bool>? finalizeAuthority = null,
            Action<InstallTransactionPhase>? beforeCheckpoint = null,
            bool requiresAuthorityPromotion = false,
            Action? afterJournalPrepared = null) =>
            new(
                Live,
                Staging,
                MaintenanceRoot,
                DataManifest,
                PreparedManifest,
                TransactionId,
                Callbacks(
                    quiesce,
                    activate,
                    healthy,
                    reactivate,
                    afterCheckpoint,
                    promoteAuthority,
                    finalizeAuthority,
                    beforeCheckpoint,
                    afterJournalPrepared),
                requiresAuthorityPromotion);

        public InstallTransactionCallbacks Callbacks(
            Func<bool>? quiesce = null,
            Func<bool>? activate = null,
            Func<bool>? healthy = null,
            Func<bool>? reactivate = null,
            Action<InstallTransactionPhase>? afterCheckpoint = null,
            Func<AuthorityPromotionOutcome>? promoteAuthority = null,
            Func<bool>? finalizeAuthority = null,
            Action<InstallTransactionPhase>? beforeCheckpoint = null,
            Action? afterJournalPrepared = null) =>
            new(
                quiesce ?? (() => true),
                activate ?? (() => true),
                healthy ?? (() => true),
                reactivate ?? (() => true),
                afterCheckpoint,
                promoteAuthority,
                finalizeAuthority,
                beforeCheckpoint,
                afterJournalPrepared);

        public string ReadLiveMarker() => File.ReadAllText(Path.Combine(Live, "marker.txt"));

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
