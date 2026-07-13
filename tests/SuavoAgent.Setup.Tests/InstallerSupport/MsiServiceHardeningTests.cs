using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class MsiServiceHardeningTests
{
    private static readonly InstallerServiceConfiguration Original = new(
        DelayedAutoStart: false,
        ServiceSidType: 0);

    [Fact]
    public void Execute_is_idempotent_when_all_services_already_match()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Empty(session.WriteCalls);
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Execute_applies_and_verifies_exact_three_service_cohort()
    {
        using var session = new FakeSession(Original);
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(MsiServiceHardeningTransaction.ServiceNames, session.WriteCalls.Select(static call => call.Name));
        Assert.All(
            MsiServiceHardeningTransaction.ServiceNames,
            name => Assert.Equal(MsiServiceHardeningTransaction.Target, session.States[name]));
        Assert.NotNull(journal.Snapshots);
        Assert.All(
            MsiServiceHardeningTransaction.ServiceNames,
            name => Assert.Equal(Original, journal.Snapshots[name]));
    }

    [Fact]
    public void Execute_fails_before_first_mutation_when_snapshot_is_incomplete()
    {
        using var session = new FakeSession(Original)
        {
            ThrowOnReadService = "SuavoAgent.Broker",
        };
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.SnapshotFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.All(
            MsiServiceHardeningTransaction.ServiceNames,
            name => Assert.Equal(Original, session.States[name]));
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Execute_rolls_back_current_partial_write_and_prior_services_in_reverse_order()
    {
        using var session = new FakeSession(Original)
        {
            ThrowAfterFirstTargetWriteService = "SuavoAgent.Broker",
        };
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.ApplyFailedRolledBack, result);
        Assert.Equal(
            [
                "SuavoAgent.Core:target",
                "SuavoAgent.Broker:target",
                "SuavoAgent.Broker:original",
                "SuavoAgent.Core:original",
            ],
            session.WriteCalls.Select(Describe));
        Assert.All(
            MsiServiceHardeningTransaction.ServiceNames,
            name => Assert.Equal(Original, session.States[name]));
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Execute_reports_rollback_failure_but_continues_restoring_earlier_services()
    {
        using var session = new FakeSession(Original)
        {
            ThrowAfterFirstTargetWriteService = "SuavoAgent.Broker",
            ThrowBeforeRollbackService = "SuavoAgent.Broker",
        };
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.RollbackFailed, result);
        Assert.Equal(Original, session.States["SuavoAgent.Core"]);
        Assert.Equal(MsiServiceHardeningTransaction.Target, session.States["SuavoAgent.Broker"]);
        Assert.Contains(
            session.WriteCalls,
            call => call.Name == "SuavoAgent.Core" && call.Configuration == Original);
        Assert.NotNull(journal.Snapshots);
    }

    [Fact]
    public void Execute_treats_post_write_verification_mismatch_as_failure_and_rolls_back()
    {
        using var session = new FakeSession(Original)
        {
            IgnoreFirstTargetWriteService = "SuavoAgent.Broker",
        };
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.ApplyFailedRolledBack, result);
        Assert.All(
            MsiServiceHardeningTransaction.ServiceNames,
            name => Assert.Equal(Original, session.States[name]));
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Execute_fails_before_mutation_when_durable_journal_cannot_be_written()
    {
        using var session = new FakeSession(Original);
        var journal = new FakeJournal { ThrowOnSave = true };

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.All(session.States.Values, state => Assert.Equal(Original, state));
    }

    [Fact]
    public void Execute_fails_closed_on_stale_journal_even_when_services_already_match()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal { Snapshots = OriginalSnapshots() };

        var result = new MsiServiceHardeningTransaction(session, journal).Execute();

        Assert.Equal(MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.NotNull(journal.Snapshots);
    }

    [Fact]
    public void Persisted_rollback_restores_exact_snapshot_in_reverse_order_and_deletes_journal()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal { Snapshots = OriginalSnapshots() };

        var result = new MsiServiceHardeningTransaction(
            session,
            journal).ExecutePersistedRollback();

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(
            MsiServiceHardeningTransaction.ServiceNames.Reverse(),
            session.WriteCalls.Select(static call => call.Name));
        Assert.All(session.States.Values, state => Assert.Equal(Original, state));
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Persisted_rollback_is_safe_noop_when_forward_action_never_created_journal()
    {
        using var session = new FakeSession(Original);
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(
            session,
            journal).ExecutePersistedRollback();

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Empty(session.WriteCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Runner_rejects_every_argument_shape_except_the_single_fixed_switch(string[]? arguments)
    {
        var sessionFactoryCalled = false;
        var journalFactoryCalled = false;

        var result = MsiServiceHardeningRunner.Run(
            arguments,
            isWindows: true,
            () =>
            {
                sessionFactoryCalled = true;
                return new FakeSession(Original);
            },
            () =>
            {
                journalFactoryCalled = true;
                return new FakeJournal();
            });

        Assert.Equal((int)MsiServiceHardeningExitCode.InvalidArguments, result);
        Assert.False(sessionFactoryCalled);
        Assert.False(journalFactoryCalled);
    }

    [Fact]
    public void Runner_rejects_non_windows_host_before_native_session_creation()
    {
        var sessionFactoryCalled = false;
        var journalFactoryCalled = false;

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ApplySwitch],
            isWindows: false,
            () =>
            {
                sessionFactoryCalled = true;
                return new FakeSession(Original);
            },
            () =>
            {
                journalFactoryCalled = true;
                return new FakeJournal();
            });

        Assert.Equal((int)MsiServiceHardeningExitCode.UnsupportedHost, result);
        Assert.False(sessionFactoryCalled);
        Assert.False(journalFactoryCalled);
    }

    [Fact]
    public void Runner_executes_valid_fixed_switch_without_sensitive_parameters()
    {
        var journal = new FakeJournal();
        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ApplySwitch],
            isWindows: true,
            () => new FakeSession(Original),
            () => journal);

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.True(MsiServiceHardeningRunner.IsRequested(["--MSI-APPLY-SERVICE-HARDENING"]));
        Assert.True(MsiServiceHardeningRunner.IsRequested(["--MSI-ROLLBACK-SERVICE-HARDENING"]));
        Assert.True(MsiServiceHardeningRunner.IsRequested(["--MSI-COMMIT-SERVICE-HARDENING"]));
        Assert.False(MsiServiceHardeningRunner.IsRequested(["--doctor"]));
    }

    [Fact]
    public void Runner_commit_deletes_journal_without_opening_service_manager()
    {
        var sessionFactoryCalled = false;
        var journal = new FakeJournal { Snapshots = OriginalSnapshots() };

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.CommitSwitch],
            isWindows: true,
            () =>
            {
                sessionFactoryCalled = true;
                return new FakeSession(Original);
            },
            () => journal);

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.False(sessionFactoryCalled);
        Assert.Null(journal.Snapshots);
    }

    [Fact]
    public void Runner_rollback_restores_durable_snapshot()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal { Snapshots = OriginalSnapshots() };

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.RollbackSwitch],
            isWindows: true,
            () => session,
            () => journal);

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.All(session.States.Values, state => Assert.Equal(Original, state));
        Assert.Null(journal.Snapshots);
    }

    public static TheoryData<string[]?> InvalidArguments => new()
    {
        null,
        Array.Empty<string>(),
        new[] { MsiServiceHardeningRunner.ApplySwitch, "unexpected" },
        new[] { MsiServiceHardeningRunner.RollbackSwitch, "unexpected" },
        new[] { MsiServiceHardeningRunner.CommitSwitch, "unexpected" },
        new[] { "--doctor" },
    };

    private static IReadOnlyDictionary<string, InstallerServiceConfiguration>
        OriginalSnapshots() =>
        MsiServiceHardeningTransaction.ServiceNames.ToDictionary(
            static name => name,
            _ => Original,
            StringComparer.Ordinal);

    private static string Describe(ServiceWrite call) =>
        $"{call.Name}:{(call.Configuration == MsiServiceHardeningTransaction.Target ? "target" : "original")}";

    private sealed class FakeSession : IInstallerServiceConfigurationSession
    {
        private bool _threwAfterTargetWrite;
        private bool _ignoredTargetWrite;

        internal FakeSession(InstallerServiceConfiguration initial)
        {
            States = MsiServiceHardeningTransaction.ServiceNames.ToDictionary(
                static name => name,
                _ => initial,
                StringComparer.Ordinal);
        }

        internal Dictionary<string, InstallerServiceConfiguration> States { get; }
        internal List<ServiceWrite> WriteCalls { get; } = [];
        internal string? ThrowOnReadService { get; init; }
        internal string? ThrowAfterFirstTargetWriteService { get; init; }
        internal string? ThrowBeforeRollbackService { get; init; }
        internal string? IgnoreFirstTargetWriteService { get; init; }

        public InstallerServiceConfiguration Read(string serviceName)
        {
            if (serviceName == ThrowOnReadService)
                throw new InvalidOperationException("Injected read failure.");
            return States[serviceName];
        }

        public void Write(string serviceName, InstallerServiceConfiguration configuration)
        {
            WriteCalls.Add(new ServiceWrite(serviceName, configuration));
            var isTarget = configuration == MsiServiceHardeningTransaction.Target;
            if (!isTarget && serviceName == ThrowBeforeRollbackService)
                throw new InvalidOperationException("Injected rollback failure.");
            if (isTarget &&
                serviceName == IgnoreFirstTargetWriteService &&
                !_ignoredTargetWrite)
            {
                _ignoredTargetWrite = true;
                return;
            }

            States[serviceName] = configuration;
            if (isTarget &&
                serviceName == ThrowAfterFirstTargetWriteService &&
                !_threwAfterTargetWrite)
            {
                _threwAfterTargetWrite = true;
                throw new InvalidOperationException("Injected partial native write failure.");
            }
        }

        public void Dispose() { }
    }

    private sealed class FakeJournal : IInstallerServiceHardeningJournal
    {
        internal IReadOnlyDictionary<string, InstallerServiceConfiguration>? Snapshots { get; set; }
        internal bool ThrowOnSave { get; init; }
        internal bool ThrowOnLoad { get; init; }
        internal bool ThrowOnDelete { get; init; }

        public void Save(
            IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
        {
            if (ThrowOnSave)
                throw new IOException("Injected journal save failure.");
            Snapshots = snapshots.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, InstallerServiceConfiguration>? Load()
        {
            if (ThrowOnLoad)
                throw new IOException("Injected journal load failure.");
            return Snapshots;
        }

        public void Delete()
        {
            if (ThrowOnDelete)
                throw new IOException("Injected journal delete failure.");
            Snapshots = null;
        }
    }

    private sealed record ServiceWrite(
        string Name,
        InstallerServiceConfiguration Configuration);
}
