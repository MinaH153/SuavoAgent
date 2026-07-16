using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeOtaActivationRunTests
{
    [Theory]
    [InlineData(NativeOtaActivationCoordinator.UnsupportedHost)]
    [InlineData(NativeOtaActivationCoordinator.UntrustedHost)]
    public void Runner_rejects_host_before_touching_durable_claim(int hostExitCode)
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.RunnerHost = new(hostExitCode, null);

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(hostExitCode, result);
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
    }

    [Fact]
    public void Runner_rejects_path_other_than_active_durable_request()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator().RunDurableClaim(
            Path.Combine(Path.GetDirectoryName(pointer.RequestPath)!, "other.json"));

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
    }

    [Fact]
    public void Runner_exits_idempotently_when_another_live_runner_owns_lease()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.LeaseAvailable = false;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
    }

    [Fact]
    public void Real_runner_lease_allows_one_owner_then_releases_after_dispose()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        var coordinator = fixture.CreateCoordinator();

        using (var first = coordinator.TryAcquireRunnerLease(pointer))
        {
            Assert.NotNull(first);
            Assert.Null(coordinator.TryAcquireRunnerLease(pointer));
        }

        using var resumed = coordinator.TryAcquireRunnerLease(pointer);
        Assert.NotNull(resumed);
    }

    [Fact]
    public void Real_stale_runner_scan_succeeds_when_exact_process_is_absent()
    {
        var impossiblePath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            MaintenanceContract.ExecutableName);

        Assert.True(NativeOtaActivationCoordinator.TerminateExactStaleRunner(impossiblePath));
    }

    [Fact]
    public void Real_current_cohort_health_fails_before_wait_when_manifest_is_absent()
    {
        using var fixture = new NativeOtaActivationTestHarness();

        Assert.False(fixture.CreateCoordinator().IsCurrentCohortHealthy());
    }

    [Fact]
    public void Runner_lock_timeout_returns_activation_failure_without_mutation()
    {
        using var fixture = new NativeOtaActivationTestHarness
        {
            AcquireTransactionLock = () => throw new TimeoutException("held"),
        };
        var (_, pointer) = fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Runner_oversized_pointer_fails_closed_instead_of_crashing_recovery()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        File.WriteAllText(
            fixture.PointerStore.PointerPath,
            new string('x', UpdateActivationContract.MaxClaimPointerBytes + 1));

        var result = fixture.CreateCoordinator().RunDurableClaim(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
    }

    [Fact]
    public void Missing_installed_identity_terminally_fails_and_cleans_payloads()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        File.Delete(Path.Combine(
            fixture.InstallDirectory,
            MaintenanceContract.InstallStateFileName));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.IdentityInvalid, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
    }

    [Fact]
    public void Tampered_durable_payload_is_rejected_and_can_never_activate()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        File.AppendAllText(
            Path.Combine(pointer.PayloadDirectory, "SuavoAgent.Core.exe"),
            "tamper");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "rejected");
    }

    [Fact]
    public void Missing_authoritative_replay_never_executes_or_discards_claim_evidence()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        File.Delete(Path.Combine(
            fixture.MaintenanceRoot,
            UpdateActivationContract.ReplayLedgerFileName));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
    }

    [Fact]
    public void Runner_rejects_pointer_replay_not_bound_to_signed_request()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        var forged = pointer with { ReplayId = new string('f', 64) };
        File.WriteAllText(
            fixture.PointerStore.PointerPath,
            UpdateActivationContract.Serialize(forged));

        var result = fixture.CreateCoordinator().RunDurableClaim(forged.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
    }

    [Theory]
    [InlineData((int)AuthoritativeReplayState.Completed, "committed", NativeOtaActivationCoordinator.Success)]
    [InlineData((int)AuthoritativeReplayState.RolledBack, "rolled_back", NativeOtaActivationCoordinator.ActivationFailed)]
    [InlineData((int)AuthoritativeReplayState.Failed, "failed", NativeOtaActivationCoordinator.ActivationFailed)]
    public void Terminal_replay_is_idempotently_receipted_without_reapplying(
        int stateValue,
        string outcome,
        int expectedExitCode)
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        var state = (AuthoritativeReplayState)stateValue;
        Assert.True(fixture.Ledger.TryTransition(
            pointer.ReplayId,
            AuthoritativeReplayState.Claimed,
            state,
            fixture.CurrentTime));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(expectedExitCode, result);
        AssertTerminal(fixture, pointer, state, outcome);
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
        Assert.Equal(0, fixture.Runtime.AssemblyCalls);
        Assert.Equal(0, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Prior_commit_crash_window_closes_only_with_durable_and_live_health_proofs()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.WriteIdentity("2.0.0");
        fixture.Runtime.Health.DurableMilestone = true;
        fixture.Runtime.CurrentCohortHealthy = true;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Completed, "committed");
        Assert.Equal(0, fixture.Runtime.RecoveryCalls);
        Assert.Equal(0, fixture.Runtime.AssemblyCalls);
        Assert.Equal(0, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Already_activating_replay_resumes_without_requiring_claim_transition()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        Assert.True(fixture.Ledger.TryTransition(
            pointer.ReplayId,
            AuthoritativeReplayState.Claimed,
            AuthoritativeReplayState.Activating,
            fixture.CurrentTime));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Completed, "committed");
        Assert.Equal(1, fixture.Runtime.RecoveryCalls);
        Assert.Equal(1, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Target_version_without_durable_milestone_runs_full_recovery_path()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.WriteIdentity("2.0.0");
        fixture.Runtime.Health.DurableMilestone = false;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.Equal(1, fixture.Runtime.RecoveryCalls);
        Assert.Equal(1, fixture.Runtime.AssemblyCalls);
        Assert.Equal(1, fixture.Runtime.ExecuteCalls);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Completed, "committed");
    }

    [Fact]
    public void Durable_milestone_alone_does_not_skip_recovery_or_activation()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.WriteIdentity("2.0.0");
        fixture.Runtime.Health.DurableMilestone = true;
        fixture.Runtime.CurrentCohortHealthy = false;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.Equal(1, fixture.Runtime.RecoveryCalls);
        Assert.Equal(1, fixture.Runtime.AssemblyCalls);
        Assert.Equal(1, fixture.Runtime.ExecuteCalls);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Completed, "committed");
    }

    [Theory]
    [InlineData(true, (int)AuthoritativeReplayState.RolledBack, "rolled_back")]
    [InlineData(false, (int)AuthoritativeReplayState.Failed, "failed")]
    public void Failed_incomplete_transaction_recovery_records_exact_terminal_state(
        bool rolledBack,
        int expectedStateValue,
        string outcome)
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        var expectedState = (AuthoritativeReplayState)expectedStateValue;
        fixture.Runtime.RecoveryResult = InstallTransactionResult.Failed(
            "recovery_failed",
            rolledBack);

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, expectedState, outcome);
        Assert.Equal(0, fixture.Runtime.AssemblyCalls);
        Assert.Equal(0, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Recovery_exception_is_terminally_failed_and_receipted()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.RecoverException = new IOException("disk fault");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
        Assert.Equal(0, fixture.Runtime.AssemblyCalls);
    }

    [Fact]
    public void Assembly_failure_never_enters_live_swap()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.AssemblyResult = OtaCohortAssemblyResult.Fail("cohort_incomplete");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
        Assert.Equal(0, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Assembly_exception_is_terminally_failed_before_live_swap()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.AssemblyException = new UnauthorizedAccessException("stage denied");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
        Assert.Equal(0, fixture.Runtime.ExecuteCalls);
    }

    [Fact]
    public void Healthy_target_completes_five_minute_probation_and_cleans_runtime_proofs()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Completed, "committed");
        Assert.Equal(1, fixture.Runtime.RecoveryCalls);
        Assert.Equal(1, fixture.Runtime.AssemblyCalls);
        Assert.Equal(1, fixture.Runtime.ExecuteCalls);
        Assert.Equal(1, fixture.Runtime.Health.IssueCalls);
        Assert.Equal(1, fixture.Runtime.Health.WaitCalls);
        Assert.Equal(TimeSpan.FromMinutes(5), fixture.Runtime.Health.ObservedTimeout);
        Assert.Equal(1, fixture.Runtime.Health.CleanupCalls);
        AssertRuntimeProofsRemoved(fixture);
    }

    [Fact]
    public void Health_probation_timeout_rolls_back_and_never_commits_target()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.Health.Passed = false;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.RolledBack, "rolled_back");
        Assert.Equal(1, fixture.Runtime.Health.WaitCalls);
        Assert.Equal(TimeSpan.FromMinutes(5), fixture.Runtime.Health.ObservedTimeout);
        AssertRuntimeProofsRemoved(fixture);
    }

    [Fact]
    public void Missing_activation_challenge_fails_health_gate_and_rolls_back()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.InvokeBeforeActivate = false;

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.RolledBack, "rolled_back");
        Assert.Equal(0, fixture.Runtime.Health.IssueCalls);
        Assert.Equal(0, fixture.Runtime.Health.WaitCalls);
        Assert.Equal(1, fixture.Runtime.Health.CleanupCalls);
    }

    [Fact]
    public void Transaction_rollback_records_rollback_even_after_health_passes()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.TransactionResult = InstallTransactionResult.Failed(
            "post_health_failure",
            rolledBack: true);

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.RolledBack, "rolled_back");
        AssertRuntimeProofsRemoved(fixture);
    }

    [Fact]
    public void Unfinished_nonrollback_swap_keeps_claim_and_activating_ledger_for_resume()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.TransactionResult = InstallTransactionResult.Failed(
            "rollback_artifacts_authoritative",
            rolledBack: false);

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(
            AuthoritativeReplayState.Activating,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.False(File.Exists(fixture.PointerStore.CompletionPath));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
        AssertRuntimeProofsRemoved(fixture);
    }

    [Fact]
    public void Execute_exception_cleans_runtime_proofs_then_terminally_fails()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.ExecuteException = new IOException("service control fault");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(1, fixture.Runtime.Health.IssueCalls);
        Assert.Equal(1, fixture.Runtime.Health.CleanupCalls);
        AssertRuntimeProofsRemoved(fixture);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
    }

    [Fact]
    public void Health_wait_exception_cleans_runtime_proofs_then_terminally_fails()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.Health.WaitException = new TimeoutException("health read failed");

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(1, fixture.Runtime.Health.WaitCalls);
        Assert.Equal(1, fixture.Runtime.Health.CleanupCalls);
        AssertRuntimeProofsRemoved(fixture);
        AssertTerminal(fixture, pointer, AuthoritativeReplayState.Failed, "failed");
    }

    [Fact]
    public void Missing_pointer_at_terminal_receipt_returns_failure_and_preserves_ledger_evidence()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.BeforeExecuteReturn = () =>
            File.Delete(fixture.PointerStore.PointerPath);

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(
            AuthoritativeReplayState.Completed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.False(File.Exists(fixture.PointerStore.CompletionPath));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
    }

    [Fact]
    public void Missing_ledger_at_terminal_receipt_returns_failure_and_preserves_claim()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.BeforeExecuteReturn = () => File.Delete(Path.Combine(
            fixture.MaintenanceRoot,
            UpdateActivationContract.ReplayLedgerFileName));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.False(File.Exists(fixture.PointerStore.CompletionPath));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
    }

    [Fact]
    public void Diverged_terminal_ledger_refuses_conflicting_completion_receipt()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.Runtime.BeforeExecuteReturn = () => Assert.True(fixture.Ledger.TryTransition(
            pointer.ReplayId,
            AuthoritativeReplayState.Activating,
            AuthoritativeReplayState.RolledBack,
            fixture.CurrentTime));

        var result = fixture.CreateCoordinator().RunDurableClaim(pointer.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ActivationFailed, result);
        Assert.Equal(
            AuthoritativeReplayState.RolledBack,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.False(File.Exists(fixture.PointerStore.CompletionPath));
        Assert.True(Directory.Exists(pointer.PayloadDirectory));
    }

    [Fact]
    public void Real_runner_host_validation_rejects_nonrunner_process()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator(useRuntime: false)
            .RunDurableClaim(pointer.RequestPath);

        Assert.Contains(
            result,
            new[]
            {
                NativeOtaActivationCoordinator.UnsupportedHost,
                NativeOtaActivationCoordinator.UntrustedHost,
            });
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    private static void AssertTerminal(
        NativeOtaActivationTestHarness fixture,
        UpdateActivationClaimPointer pointer,
        AuthoritativeReplayState expectedState,
        string expectedOutcome)
    {
        Assert.Equal(expectedState, fixture.Ledger.Find(pointer.ReplayId)!.State);
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Equal(expectedOutcome, fixture.ReadCompletion().Outcome);
        Assert.False(Directory.Exists(pointer.PayloadDirectory));
        Assert.False(Directory.Exists(fixture.PayloadDirectory));
    }

    private static void AssertRuntimeProofsRemoved(NativeOtaActivationTestHarness fixture)
    {
        Assert.False(File.Exists(
            UpdateActivationContract.DefaultHealthChallengePath(fixture.UpdateRoot)));
        Assert.False(File.Exists(
            UpdateActivationContract.DefaultHealthMilestonePath(fixture.UpdateRoot)));
    }
}
