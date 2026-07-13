using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class InstalledCohortConfigurationTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "suavo-installed-config-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private bool _abortCalled;

    private string Install => Path.Combine(_root, "install");
    private string Data => Path.Combine(_root, "data");
    private string Maintenance => Path.Combine(_root, "maintenance");

    public InstalledCohortConfigurationTransactionTests()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Maintenance);
        File.WriteAllText(Path.Combine(Install, "appsettings.json"), "original-settings");
        File.WriteAllText(
            Path.Combine(Data, "vertical-compliance-lkg.json"),
            "original-compliance");
        File.WriteAllText(Path.Combine(Data, "operator-evidence.txt"), "preserve");
    }

    [Fact]
    public void SuccessfulConfigurationCommitsWithoutTouchingUnknownFiles()
    {
        var transaction = CreateTransaction(AuthorityPromotionOutcome.Promoted);

        var result = transaction.Execute();

        Assert.True(result.Succeeded, result.Code);
        Assert.False(result.RecoveryRequired);
        Assert.False(transaction.HasPendingJournal);
        Assert.Equal("new-settings", File.ReadAllText(Path.Combine(Install, "appsettings.json")));
        Assert.Equal("new-consent", File.ReadAllText(Path.Combine(Data, "consent-receipt.json")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "operator-evidence.txt")));
        Assert.Contains("finalize", _calls);
        Assert.Contains("restart-active", _calls);
        Assert.Contains("complete", _calls);
        Assert.False(_abortCalled);
    }

    [Fact]
    public void RejectedAuthorityRestoresEveryAllowlistedArtifactAndAbortsPendingKey()
    {
        var transaction = CreateTransaction(AuthorityPromotionOutcome.Rejected);

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack, result.Code);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(
            "original-settings",
            File.ReadAllText(Path.Combine(Install, "appsettings.json")));
        Assert.Equal(
            "original-compliance",
            File.ReadAllText(Path.Combine(Data, "vertical-compliance-lkg.json")));
        Assert.False(File.Exists(Path.Combine(Data, "consent-receipt.json")));
        Assert.True(_abortCalled);
        Assert.False(transaction.HasPendingJournal);
    }

    [Fact]
    public void PartialApplyFailureRollsBackBecauseApplyingIsDurableBeforeWrites()
    {
        var transaction = CreateTransaction(
            AuthorityPromotionOutcome.Promoted,
            apply: () =>
            {
                File.WriteAllText(Path.Combine(Install, "appsettings.json"), "torn");
                throw new IOException("injected");
            });

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack, result.Code);
        Assert.Equal(
            "original-settings",
            File.ReadAllText(Path.Combine(Install, "appsettings.json")));
        Assert.True(_abortCalled);
    }

    [Fact]
    public void UnknownAuthorityPreservesJournalAndRecoversForwardWithoutReapplyingConfig()
    {
        var first = CreateTransaction(AuthorityPromotionOutcome.Unknown);

        var pending = first.Execute();

        Assert.True(pending.RecoveryRequired, pending.Code);
        Assert.True(first.HasPendingJournal);
        var journal = File.ReadAllText(first.JournalPath);
        Assert.DoesNotContain("api-key", journal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient", journal, StringComparison.OrdinalIgnoreCase);

        _calls.Clear();
        var recovery = CreateTransaction(
            AuthorityPromotionOutcome.Promoted,
            apply: () => throw new InvalidOperationException("must not reapply"));
        var recovered = recovery.Recover();

        Assert.True(recovered.Succeeded);
        Assert.False(recovery.HasPendingJournal);
        Assert.DoesNotContain("apply", _calls);
        Assert.Contains("promote", _calls);
        Assert.Contains("finalize", _calls);
        Assert.Contains("restart-active", _calls);
    }

    [Fact]
    public void AuthorityUnknownIsDurableBeforeConfirmationAndCrashRecoveryNeverCompensates()
    {
        InstalledCohortConfigurationTransaction? first = null;
        var callbacks = Callbacks(
            AuthorityPromotionOutcome.Unknown,
            promote: () =>
            {
                using var journal = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllBytes(first!.JournalPath));
                Assert.Equal(
                    (int)InstalledConfigurationStage.AuthorityUnknown,
                    journal.RootElement.GetProperty("stage").GetInt32());
                throw new IOException("simulated process boundary");
            });
        first = new InstalledCohortConfigurationTransaction(
            Install,
            Data,
            Maintenance,
            callbacks,
            _ => { },
            (_, _) => true);

        var pending = first.Execute();

        Assert.True(pending.RecoveryRequired, pending.Code);
        Assert.False(pending.RolledBack);
        Assert.False(_abortCalled);
        Assert.Equal(
            "new-settings",
            File.ReadAllText(Path.Combine(Install, "appsettings.json")));

        _calls.Clear();
        var recovery = CreateTransaction(AuthorityPromotionOutcome.Promoted);
        var recovered = recovery.Recover();

        Assert.True(recovered.Succeeded);
        Assert.False(_abortCalled);
        Assert.DoesNotContain("apply", _calls);
        Assert.Contains("promote", _calls);
    }

    [Fact]
    public void CrashDuringCompensationResumesRollbackWithoutReenteringCloudAuthority()
    {
        var abortAttempts = 0;
        var firstCallbacks = Callbacks(
            AuthorityPromotionOutcome.Rejected,
            abort: () =>
            {
                abortAttempts++;
                _abortCalled = true;
                throw new IOException("simulated crash after authority abort");
            });
        var first = NewTransaction(firstCallbacks);

        var interrupted = first.Execute();

        Assert.True(interrupted.RecoveryRequired, interrupted.Code);
        Assert.True(first.HasPendingJournal);
        using (var journal = System.Text.Json.JsonDocument.Parse(
                   File.ReadAllBytes(first.JournalPath)))
            Assert.Equal(
                (int)InstalledConfigurationStage.RollingBack,
                journal.RootElement.GetProperty("stage").GetInt32());

        _calls.Clear();
        var recoveryCallbacks = Callbacks(
            AuthorityPromotionOutcome.Promoted,
            promote: () => throw new InvalidOperationException(
                "rollback recovery must not confirm authority"));
        var recovery = NewTransaction(recoveryCallbacks);
        var recovered = recovery.Recover();

        Assert.True(recovered.RolledBack, recovered.Code);
        Assert.DoesNotContain("promote", _calls);
        Assert.False(recovery.HasPendingJournal);
        Assert.Equal(
            "original-settings",
            File.ReadAllText(Path.Combine(Install, "appsettings.json")));
    }

    [Fact]
    public void MissingMsiCohortFailsBeforeSnapshotOrServiceMutation()
    {
        var callbacks = Callbacks(
            AuthorityPromotionOutcome.Promoted,
            validate: () => false);
        var transaction = new InstalledCohortConfigurationTransaction(
            Install,
            Data,
            Maintenance,
            callbacks,
            _ => { },
            (_, _) => true);

        var result = transaction.Execute();

        Assert.False(result.Succeeded);
        Assert.Equal("installed_cohort_invalid", result.Code);
        Assert.Empty(_calls);
        Assert.False(transaction.HasPendingJournal);
    }

    private InstalledCohortConfigurationTransaction CreateTransaction(
        AuthorityPromotionOutcome promotion,
        Action? apply = null) =>
        NewTransaction(Callbacks(promotion, apply: apply));

    private InstalledCohortConfigurationTransaction NewTransaction(
        InstalledConfigurationCallbacks callbacks) =>
        new(
            Install,
            Data,
            Maintenance,
            callbacks,
            _ => { },
            (_, _) => true);

    private InstalledConfigurationCallbacks Callbacks(
        AuthorityPromotionOutcome promotion,
        Action? apply = null,
        Func<bool>? validate = null,
        Func<AuthorityPromotionOutcome>? promote = null,
        Func<bool>? abort = null) =>
        new(
            ValidateCohort: validate ?? (() => true),
            Quiesce: () => Record("quiesce"),
            ApplyConfigurationAndStageAuthority: apply ?? (() =>
            {
                _calls.Add("apply");
                File.WriteAllText(Path.Combine(Install, "appsettings.json"), "new-settings");
                File.WriteAllText(Path.Combine(Data, "consent-receipt.json"), "new-consent");
                File.WriteAllText(
                    Path.Combine(Data, "vertical-compliance-lkg.json"),
                    "new-compliance");
            }),
            PreserveAuthorityForRecovery: () => _calls.Add("preserve"),
            StartInstalledCohort: () => Record("start"),
            VerifyProbationHealth: () => Record("probation-health"),
            PromoteAuthority: () =>
            {
                _calls.Add("promote");
                return (promote ?? (() => promotion))();
            },
            FinalizeAuthority: () => Record("finalize"),
            RestartPromotedCohort: () => Record("restart-active"),
            CompleteAuthority: () => Record("complete"),
            AbortAuthority: abort ?? (() =>
            {
                _abortCalled = true;
                return Record("abort");
            }));

    private bool Record(string value)
    {
        _calls.Add(value);
        return true;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
