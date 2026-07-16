using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class MsiServiceHardeningTests
{
    private const string ProductCode =
        "{A1111111-B222-C333-D444-E55555555555}";
    private static readonly InstallerServiceConfiguration Original = new(false, 0);
    private static readonly string InvocationData = MsiInstallerInvocation.BuildForTests(
        ProductCode,
        "restart-manager-session-a",
        @"C:\rehearsal\SuavoAgent.msi");
    private static readonly string InvocationId = ParseId(InvocationData);
    private static readonly string OtherInvocationId = ParseId(
        MsiInstallerInvocation.BuildForTests(
            ProductCode,
            "restart-manager-session-b",
            @"C:\rehearsal\SuavoAgent.msi"));

    [Fact]
    public void Execute_is_idempotent_when_all_services_already_match()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal();
        var activation = Active(InvocationId);

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            activation).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Empty(session.WriteCalls);
        Assert.Null(journal.State);
    }

    [Fact]
    public void Execute_applies_and_persists_pending_snapshot_for_current_invocation()
    {
        using var session = new FakeSession(Original);
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            Active(InvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(
            MsiServiceHardeningTransaction.ServiceNames,
            session.WriteCalls.Select(static call => call.Name));
        Assert.All(session.States.Values, state =>
            Assert.Equal(MsiServiceHardeningTransaction.Target, state));
        Assert.Equal(InvocationId, journal.State?.InvocationId);
        Assert.Equal(InstallerTransactionJournalPhase.Pending, journal.State?.Phase);
        Assert.All(journal.State!.Snapshots.Values, state => Assert.Equal(Original, state));
    }

    [Fact]
    public void Execute_fails_before_mutation_when_active_token_does_not_match()
    {
        using var session = new FakeSession(Original);
        var journal = new FakeJournal
        {
            State = State(OtherInvocationId, InstallerTransactionJournalPhase.Pending),
        };

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            Active(OtherInvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.Equal(OtherInvocationId, journal.State.InvocationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_never_mutates_services_for_any_stale_pending_journal(
        bool journalMatchesCurrentInvocation)
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var staleId = journalMatchesCurrentInvocation ? InvocationId : OtherInvocationId;
        var journal = new FakeJournal
        {
            State = State(staleId, InstallerTransactionJournalPhase.Pending),
        };

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            Active(InvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.Equal(staleId, journal.State.InvocationId);
        Assert.Equal(InstallerTransactionJournalPhase.Pending, journal.State.Phase);
    }

    [Fact]
    public void Execute_deletes_valid_committed_tombstone_without_restoring_it()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal
        {
            State = State(OtherInvocationId, InstallerTransactionJournalPhase.Committed),
        };

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            Active(InvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Empty(session.WriteCalls);
        Assert.Null(journal.State);
        Assert.Contains("delete:committed", journal.Events);
    }

    [Fact]
    public void Execute_rolls_back_current_partial_write_in_reverse_order()
    {
        using var session = new FakeSession(Original)
        {
            ThrowAfterFirstTargetWriteService = "SuavoAgent.Broker",
        };
        var journal = new FakeJournal();

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            Active(InvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.ApplyFailedRolledBack, result);
        Assert.Equal(
            [
                "SuavoAgent.Core:target",
                "SuavoAgent.Broker:target",
                "SuavoAgent.Broker:original",
                "SuavoAgent.Core:original",
            ],
            session.WriteCalls.Select(Describe));
        Assert.All(session.States.Values, state => Assert.Equal(Original, state));
        Assert.Null(journal.State);
    }

    [Fact]
    public void Execute_fails_before_first_mutation_when_snapshot_or_journal_fails()
    {
        using var readFailure = new FakeSession(Original)
        {
            ThrowOnReadService = "SuavoAgent.Broker",
        };
        var snapshotResult = new MsiServiceHardeningTransaction(
            readFailure,
            new FakeJournal(),
            Active(InvocationId)).Execute(InvocationId);

        using var saveFailure = new FakeSession(Original);
        var journalResult = new MsiServiceHardeningTransaction(
            saveFailure,
            new FakeJournal { ThrowOnSave = true },
            Active(InvocationId)).Execute(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.SnapshotFailed, snapshotResult);
        Assert.Equal(MsiServiceHardeningExitCode.JournalFailed, journalResult);
        Assert.Empty(readFailure.WriteCalls);
        Assert.Empty(saveFailure.WriteCalls);
    }

    [Fact]
    public void Rollback_restores_only_current_pending_journal_and_leaves_finalization_to_runner()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = Active(InvocationId);

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            activation).ExecutePersistedRollback(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(
            MsiServiceHardeningTransaction.ServiceNames.Reverse(),
            session.WriteCalls.Select(static call => call.Name));
        Assert.All(session.States.Values, state => Assert.Equal(Original, state));
        Assert.Null(journal.State);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_refuses_cross_run_or_committed_journal_without_mutation(
        bool committed)
    {
        var phase = committed
            ? InstallerTransactionJournalPhase.Committed
            : InstallerTransactionJournalPhase.Pending;
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal { State = State(OtherInvocationId, phase) };
        var activation = Active(InvocationId);

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            activation).ExecutePersistedRollback(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.RollbackFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.NotNull(journal.State);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Fact]
    public void Rollback_cannot_replay_stale_journal_when_current_arm_never_launched()
    {
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);
        var journal = new FakeJournal
        {
            State = State(OtherInvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var staleActivation = Active(OtherInvocationId);

        var result = new MsiServiceHardeningTransaction(
            session,
            journal,
            staleActivation).ExecutePersistedRollback(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.RollbackFailed, result);
        Assert.Empty(session.WriteCalls);
        Assert.Equal(OtherInvocationId, staleActivation.CurrentInvocationId);
        Assert.Equal(OtherInvocationId, journal.State.InvocationId);
    }

    [Fact]
    public void Rollback_noops_when_forward_never_created_journal()
    {
        using var session = new FakeSession(Original);
        var activation = Active(InvocationId);

        var result = new MsiServiceHardeningTransaction(
            session,
            new FakeJournal(),
            activation).ExecutePersistedRollback(InvocationId);

        Assert.Equal(MsiServiceHardeningExitCode.Success, result);
        Assert.Empty(session.WriteCalls);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Runner_rejects_every_non_exact_argument_shape(string[]? arguments)
    {
        var factoryCalled = false;
        var result = MsiServiceHardeningRunner.Run(
            arguments,
            isWindows: true,
            () => { factoryCalled = true; return new FakeSession(Original); },
            _ => { factoryCalled = true; return new FakeJournal(); },
            _ => { factoryCalled = true; return new FakeActivation(); },
            () => { factoryCalled = true; return new FakeGate(); },
            () => { factoryCalled = true; });

        Assert.Equal((int)MsiServiceHardeningExitCode.InvalidArguments, result);
        Assert.False(factoryCalled);
    }

    [Fact]
    public void Runner_arm_settles_journals_before_writing_derived_invocation()
    {
        var sessionCalled = false;
        var journalCalled = false;
        var activation = new FakeActivation();
        var gate = new FakeGate();
        var markerSettled = false;

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ArmSwitch, InvocationData],
            isWindows: true,
            () => { sessionCalled = true; return new FakeSession(Original); },
            _ => { journalCalled = true; return new FakeJournal(); },
            installDirectory =>
            {
                Assert.Equal(@"C:\Program Files\Suavo\Agent\", installDirectory);
                return activation;
            },
            () => gate,
            () => markerSettled = true);

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
        Assert.False(sessionCalled);
        Assert.True(journalCalled);
        Assert.True(markerSettled);
        Assert.True(gate.Disposed);
    }

    [Fact]
    public void Runner_arm_refuses_existing_token_before_cleaning_any_journal()
    {
        var journal = new FakeJournal
        {
            State = State(OtherInvocationId, InstallerTransactionJournalPhase.Committed),
        };
        var activation = Active(OtherInvocationId);
        var markerCalled = false;

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ArmSwitch, InvocationData],
            isWindows: true,
            () => throw new InvalidOperationException(),
            _ => journal,
            _ => activation,
            () => new FakeGate(),
            () => markerCalled = true);

        Assert.Equal((int)MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Equal(OtherInvocationId, activation.CurrentInvocationId);
        Assert.NotNull(journal.State);
        Assert.False(markerCalled);
    }

    [Fact]
    public void Runner_arm_refuses_pending_service_journal_without_writing_token()
    {
        var journal = new FakeJournal
        {
            State = State(OtherInvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = new FakeActivation();
        var markerCalled = false;

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ArmSwitch, InvocationData],
            isWindows: true,
            () => throw new InvalidOperationException(),
            _ => journal,
            _ => activation,
            () => new FakeGate(),
            () => markerCalled = true);

        Assert.Equal((int)MsiServiceHardeningExitCode.JournalFailed, result);
        Assert.Null(activation.CurrentInvocationId);
        Assert.NotNull(journal.State);
        Assert.False(markerCalled);
    }

    [Fact]
    public void Runner_rejects_non_windows_before_factories()
    {
        var called = false;
        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.ApplySwitch, InvocationData],
            isWindows: false,
            () => { called = true; return new FakeSession(Original); },
            _ => { called = true; return new FakeJournal(); },
            _ => { called = true; return Active(InvocationId); },
            () => { called = true; return new FakeGate(); },
            () => called = true);

        Assert.Equal((int)MsiServiceHardeningExitCode.UnsupportedHost, result);
        Assert.False(called);
    }

    [Fact]
    public void Runner_commit_seals_before_delete_and_disarms_last()
    {
        var events = new List<string>();
        var journal = new FakeJournal(events)
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = new FakeActivation(events) { CurrentInvocationId = InvocationId };
        var gate = new FakeGate();

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.CommitSwitch, InvocationData],
            isWindows: true,
            () => throw new InvalidOperationException("SCM must not open"),
            _ => journal,
            _ => activation,
            () => gate,
            () => events.Add("marker-settled"));

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(
            ["mark-committed", "delete:committed", "marker-settled", "disarm"],
            events);
        Assert.Null(journal.State);
        Assert.Null(activation.CurrentInvocationId);
        Assert.True(gate.Disposed);
    }

    [Fact]
    public void Runner_commit_never_disarms_when_journal_seal_fails()
    {
        var journal = new FakeJournal
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
            ThrowOnMarkCommitted = true,
        };
        var activation = Active(InvocationId);

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.CommitSwitch, InvocationData],
            isWindows: true,
            () => throw new InvalidOperationException(),
            _ => journal,
            _ => activation,
            () => throw new InvalidOperationException("Finalizer must not run"),
            () => throw new InvalidOperationException("Finalizer must not run"));

        Assert.Equal((int)MsiServiceHardeningExitCode.CommitFailed, result);
        Assert.Equal(InstallerTransactionJournalPhase.Pending, journal.State?.Phase);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Fact]
    public void Runner_commit_never_disarms_when_marker_journal_remains_pending()
    {
        var journal = new FakeJournal
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = Active(InvocationId);

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.CommitSwitch, InvocationData],
            isWindows: true,
            () => throw new InvalidOperationException(),
            _ => journal,
            _ => activation,
            () => new FakeGate(),
            () => throw new InvalidDataException("Marker journal is pending."));

        Assert.Equal((int)MsiServiceHardeningExitCode.CommitFailed, result);
        Assert.Null(journal.State);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Fact]
    public void Runner_rollback_never_disarms_when_marker_rollback_did_not_settle()
    {
        var journal = new FakeJournal
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = Active(InvocationId);
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.RollbackSwitch, InvocationData],
            isWindows: true,
            () => session,
            _ => journal,
            _ => activation,
            () => new FakeGate(),
            () => throw new InvalidDataException("Marker journal is pending."));

        Assert.Equal((int)MsiServiceHardeningExitCode.RollbackFailed, result);
        Assert.Null(journal.State);
        Assert.Equal(InvocationId, activation.CurrentInvocationId);
    }

    [Fact]
    public void Runner_rollback_disarms_only_after_both_journals_are_absent()
    {
        var events = new List<string>();
        var journal = new FakeJournal(events)
        {
            State = State(InvocationId, InstallerTransactionJournalPhase.Pending),
        };
        var activation = new FakeActivation(events)
        {
            CurrentInvocationId = InvocationId,
        };
        using var session = new FakeSession(MsiServiceHardeningTransaction.Target);

        var result = MsiServiceHardeningRunner.Run(
            [MsiServiceHardeningRunner.RollbackSwitch, InvocationData],
            isWindows: true,
            () => session,
            _ => journal,
            _ => activation,
            () => new FakeGate(),
            () => events.Add("marker-settled"));

        Assert.Equal((int)MsiServiceHardeningExitCode.Success, result);
        Assert.Equal(["delete:pending", "marker-settled", "disarm"], events);
        Assert.Null(journal.State);
        Assert.Null(activation.CurrentInvocationId);
    }

    public static TheoryData<string[]?> InvalidArguments => new()
    {
        null,
        Array.Empty<string>(),
        new[] { MsiServiceHardeningRunner.ApplySwitch },
        new[] { MsiServiceHardeningRunner.ApplySwitch, "malformed" },
        new[] { MsiServiceHardeningRunner.ApplySwitch, InvocationData, "unexpected" },
        new[] { "--doctor", InvocationData },
    };

    private static string ParseId(string data)
    {
        Assert.True(MsiInstallerInvocation.TryParse(data, out var parsed));
        return parsed.InvocationId;
    }

    private static FakeActivation Active(string invocationId) =>
        new() { CurrentInvocationId = invocationId };

    private static InstallerServiceHardeningJournalState State(
        string invocationId,
        InstallerTransactionJournalPhase phase) =>
        new(invocationId, phase, OriginalSnapshots());

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

        internal FakeSession(InstallerServiceConfiguration initial) =>
            States = MsiServiceHardeningTransaction.ServiceNames.ToDictionary(
                static name => name,
                _ => initial,
                StringComparer.Ordinal);

        internal Dictionary<string, InstallerServiceConfiguration> States { get; }
        internal List<ServiceWrite> WriteCalls { get; } = [];
        internal string? ThrowOnReadService { get; init; }
        internal string? ThrowAfterFirstTargetWriteService { get; init; }

        public InstallerServiceConfiguration Read(string serviceName)
        {
            if (serviceName == ThrowOnReadService)
                throw new InvalidOperationException("Injected read failure.");
            return States[serviceName];
        }

        public void Write(string serviceName, InstallerServiceConfiguration configuration)
        {
            WriteCalls.Add(new(serviceName, configuration));
            States[serviceName] = configuration;
            if (configuration == MsiServiceHardeningTransaction.Target &&
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
        internal FakeJournal(List<string>? events = null) => Events = events ?? [];

        internal InstallerServiceHardeningJournalState? State { get; set; }
        internal bool ThrowOnSave { get; init; }
        internal bool ThrowOnMarkCommitted { get; init; }
        internal List<string> Events { get; }

        public void SavePending(
            string invocationId,
            IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
        {
            if (ThrowOnSave) throw new IOException("Injected save failure.");
            State = new(
                invocationId,
                InstallerTransactionJournalPhase.Pending,
                snapshots.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal));
        }

        public InstallerServiceHardeningJournalState? Load() => State;

        public void MarkCommitted(string invocationId)
        {
            Events.Add("mark-committed");
            if (ThrowOnMarkCommitted) throw new IOException("Injected seal failure.");
            if (State is null || State.InvocationId != invocationId ||
                State.Phase != InstallerTransactionJournalPhase.Pending)
                throw new InvalidDataException();
            State = State with { Phase = InstallerTransactionJournalPhase.Committed };
        }

        public void Delete(string invocationId, InstallerTransactionJournalPhase phase)
        {
            Events.Add("delete:" + phase.ToString().ToLowerInvariant());
            if (State is null || State.InvocationId != invocationId || State.Phase != phase)
                throw new InvalidDataException();
            State = null;
        }
    }

    private sealed class FakeActivation : IMsiInstallerTransactionActivation
    {
        private readonly List<string> _events;

        internal FakeActivation(List<string>? events = null) => _events = events ?? [];
        internal string? CurrentInvocationId { get; set; }

        public void RequireAbsent()
        {
            if (CurrentInvocationId is not null)
                throw new IOException("Activation already exists.");
        }

        public void Arm(string invocationId)
        {
            RequireAbsent();
            _events.Add("arm");
            CurrentInvocationId = invocationId;
        }

        public void RequireCurrent(string invocationId)
        {
            if (!string.Equals(CurrentInvocationId, invocationId, StringComparison.Ordinal))
                throw new InvalidDataException("Activation mismatch.");
        }

        public void Disarm(string invocationId)
        {
            RequireCurrent(invocationId);
            _events.Add("disarm");
            CurrentInvocationId = null;
        }
    }

    private sealed class FakeGate : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed record ServiceWrite(
        string Name,
        InstallerServiceConfiguration Configuration);
}
