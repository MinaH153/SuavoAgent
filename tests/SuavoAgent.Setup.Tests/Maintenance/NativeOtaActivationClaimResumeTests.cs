using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeOtaActivationClaimResumeTests
{
    [Fact]
    public void Public_modes_reject_missing_or_wrong_path_arguments_before_default_runtime_starts()
    {
        var wrong = Path.Combine(Path.GetTempPath(), "not-the-fixed-activation-path.json");

        Assert.Equal(
            NativeOtaActivationCoordinator.InvalidArguments,
            NativeOtaActivationCoordinator.RunInitial([]));
        Assert.Equal(
            NativeOtaActivationCoordinator.InvalidArguments,
            NativeOtaActivationCoordinator.RunInitial(
                [UpdateActivationContract.RequestPathSwitch, wrong]));
        Assert.Equal(
            NativeOtaActivationCoordinator.InvalidArguments,
            NativeOtaActivationCoordinator.RunResume([]));
        Assert.Equal(
            NativeOtaActivationCoordinator.InvalidArguments,
            NativeOtaActivationCoordinator.RunResume(
                [UpdateActivationContract.ClaimPathSwitch, wrong]));
        Assert.Equal(
            NativeOtaActivationCoordinator.InvalidArguments,
            NativeOtaActivationCoordinator.RunRunner([]));
    }

    [Fact]
    public void Path_parser_requires_one_absolute_value()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "one.json");

        Assert.False(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            ["--other", absolute],
            UpdateActivationContract.RequestPathSwitch,
            out _));
        Assert.False(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [UpdateActivationContract.RequestPathSwitch, "relative.json"],
            UpdateActivationContract.RequestPathSwitch,
            out _));
        Assert.False(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [
                UpdateActivationContract.RequestPathSwitch,
                absolute,
                UpdateActivationContract.RequestPathSwitch,
                Path.Combine(Path.GetTempPath(), "two.json"),
            ],
            UpdateActivationContract.RequestPathSwitch,
            out _));
        Assert.True(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [UpdateActivationContract.RequestPathSwitch, absolute],
            UpdateActivationContract.RequestPathSwitch,
            out var value));
        Assert.Equal(absolute, value);
    }

    [Fact]
    public void Claim_launches_exact_durable_request_before_deleting_untrusted_source()
    {
        using var fixture = new NativeOtaActivationTestHarness();

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.False(File.Exists(fixture.RequestPath));
        var pointer = fixture.PointerStore.TryReadPointer(fixture.CurrentTime);
        Assert.NotNull(pointer);
        Assert.True(File.Exists(pointer!.RequestPath));
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(pointer.ReplayId)!.State);
        var launch = Assert.Single(fixture.RunnerLaunches);
        Assert.Equal(UpdateActivationContract.RunnerSwitch, launch.ArgumentList[0]);
        Assert.Equal(UpdateActivationContract.RequestPathSwitch, launch.ArgumentList[1]);
        Assert.Equal(pointer.RequestPath, launch.ArgumentList[2]);
    }

    [Theory]
    [InlineData(NativeOtaActivationCoordinator.UnsupportedHost)]
    [InlineData(NativeOtaActivationCoordinator.UntrustedHost)]
    public void Claim_rejects_host_before_creating_any_durable_state(int hostExitCode)
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.Runtime.InstalledHost = new(hostExitCode, null);

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(hostExitCode, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Claim_rejects_missing_bound_installed_identity()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        File.Delete(Path.Combine(
            fixture.InstallDirectory,
            MaintenanceContract.InstallStateFileName));

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.IdentityInvalid, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Claim_rejects_malformed_request_without_consulting_payload()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        File.WriteAllText(fixture.RequestPath, "not-json");

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.True(Directory.Exists(fixture.PayloadDirectory));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Claim_rejects_oversized_request_without_throwing()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        File.WriteAllText(
            fixture.RequestPath,
            new string('x', UpdateActivationContract.MaxRequestBytes + 1));

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Claim_rejects_signed_request_when_source_payload_hash_changed()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        File.AppendAllText(
            Path.Combine(fixture.PayloadDirectory, "SuavoAgent.Core.exe"),
            "tamper");

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Existing_terminal_receipt_for_same_replay_blocks_new_active_pointer()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        File.WriteAllText(
            fixture.PointerStore.CompletionPath,
            UpdateActivationContract.Serialize(new UpdateActivationCompletion(
                UpdateActivationContract.SchemaVersion,
                UpdateActivationContract.ComputeReplayId(fixture.Request),
                fixture.Request.StagingId,
                fixture.Manifest.Version,
                "failed",
                fixture.CurrentTime.ToString("O"),
                fixture.CurrentTime.ToString("O"))));

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Claim_preserves_source_and_active_pointer_when_runner_launch_fails()
    {
        using var fixture = new NativeOtaActivationTestHarness
        {
            RunnerLaunchSucceeds = false,
        };

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.RunnerLaunchFailed, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Single(fixture.RunnerLaunches);
    }

    [Fact]
    public void Claim_preserves_source_when_runner_cannot_be_staged()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        File.Delete(fixture.InstalledMaintenancePath);

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.RunnerLaunchFailed, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Claim_lock_timeout_fails_closed_without_throwing_or_mutating_state()
    {
        using var fixture = new NativeOtaActivationTestHarness
        {
            AcquireTransactionLock = () => throw new TimeoutException("held"),
        };

        var result = fixture.CreateCoordinator().ClaimAndLaunch(fixture.RequestPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.True(File.Exists(fixture.RequestPath));
        Assert.Null(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_does_not_launch_duplicate_runner_while_heartbeat_lease_is_fresh()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();
        fixture.CurrentTime = NativeOtaActivationTestHarness.Now.AddSeconds(90);

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.Equal(0, fixture.Runtime.TerminateCalls);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_rejects_missing_identity_without_relaunching()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();
        File.Delete(Path.Combine(
            fixture.InstallDirectory,
            MaintenanceContract.InstallStateFileName));

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.IdentityInvalid, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_rejects_untrusted_host_before_reading_active_claim()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();
        fixture.Runtime.InstalledHost = new(
            NativeOtaActivationCoordinator.UntrustedHost,
            null);

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_lock_timeout_fails_closed_without_relaunching()
    {
        using var fixture = new NativeOtaActivationTestHarness
        {
            AcquireTransactionLock = () => throw new TimeoutException("held"),
        };
        fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_rejects_tampered_claim_without_relaunching()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        File.AppendAllText(
            Path.Combine(pointer.PayloadDirectory, "SuavoAgent.Core.exe"),
            "tamper");

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_rejects_pointer_replay_not_bound_to_signed_request()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        File.WriteAllText(
            fixture.PointerStore.PointerPath,
            UpdateActivationContract.Serialize(pointer with
            {
                ReplayId = new string('f', 64),
            }));

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_reports_stage_failure_and_keeps_claim_recoverable()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();
        File.Delete(fixture.InstalledMaintenancePath);

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.RunnerLaunchFailed, result);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_terminates_only_stale_runner_then_relaunches_exact_claim()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        var (_, pointer) = fixture.ClaimAndBegin();
        fixture.CurrentTime = NativeOtaActivationTestHarness.Now.AddMinutes(3);

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.Success, result);
        Assert.Equal(1, fixture.Runtime.TerminateCalls);
        var launch = Assert.Single(fixture.RunnerLaunches);
        Assert.Equal(pointer.RequestPath, launch.ArgumentList[2]);
    }

    [Fact]
    public void Resume_refuses_relaunch_when_stale_runner_cannot_be_terminated()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();
        fixture.CurrentTime = NativeOtaActivationTestHarness.Now.AddMinutes(3);
        fixture.Runtime.TerminateResult = false;

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.RunnerLaunchFailed, result);
        Assert.Equal(1, fixture.Runtime.TerminateCalls);
        Assert.Empty(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_reports_launch_failure_and_keeps_claim_recoverable()
    {
        using var fixture = new NativeOtaActivationTestHarness
        {
            RunnerLaunchSucceeds = false,
        };
        fixture.ClaimAndBegin();
        fixture.CurrentTime = NativeOtaActivationTestHarness.Now.AddMinutes(3);

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.RunnerLaunchFailed, result);
        Assert.Single(fixture.RunnerLaunches);
        Assert.NotNull(fixture.PointerStore.TryReadPointer(fixture.CurrentTime));
    }

    [Fact]
    public void Resume_rejects_wrong_pointer_path_before_reading_claim()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        fixture.ClaimAndBegin();

        var result = fixture.CreateCoordinator().ResumeAndLaunch(
            Path.Combine(fixture.MaintenanceRoot, "other.json"));

        Assert.Equal(NativeOtaActivationCoordinator.InvalidArguments, result);
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Resume_rejects_malformed_path_without_throwing()
    {
        using var fixture = new NativeOtaActivationTestHarness();

        var result = fixture.CreateCoordinator().ResumeAndLaunch("\0bad-path");

        Assert.Equal(NativeOtaActivationCoordinator.InvalidArguments, result);
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Resume_oversized_pointer_fails_closed_instead_of_crashing_maintenance()
    {
        using var fixture = new NativeOtaActivationTestHarness();
        Directory.CreateDirectory(fixture.MaintenanceRoot);
        File.WriteAllText(
            fixture.PointerStore.PointerPath,
            new string('x', UpdateActivationContract.MaxClaimPointerBytes + 1));

        var result = fixture.CreateCoordinator().ResumeAndLaunch(fixture.PointerStore.PointerPath);

        Assert.Equal(NativeOtaActivationCoordinator.ClaimFailed, result);
        Assert.Empty(fixture.RunnerLaunches);
    }

    [Fact]
    public void Real_host_validation_rejects_noninstalled_or_non_system_process()
    {
        using var fixture = new NativeOtaActivationTestHarness();

        var result = fixture.CreateCoordinator(useRuntime: false)
            .ClaimAndLaunch(fixture.RequestPath);

        Assert.Contains(
            result,
            new[]
            {
                NativeOtaActivationCoordinator.UnsupportedHost,
                NativeOtaActivationCoordinator.UntrustedHost,
            });
        Assert.True(File.Exists(fixture.RequestPath));
    }

    [Fact]
    public void Installed_host_boundary_requires_windows_system_exact_path_and_trust()
    {
        var install = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Agent");
        var exact = Path.Combine(install, MaintenanceContract.ExecutableName);
        var trustCalls = 0;
        MaintenanceHostTrustResult Trust(bool trusted) => new(
            trusted,
            MaintenanceTrustSource.SignedOtaManifest,
            trusted ? "trusted" : "bad_signature");

        var nonWindows = NativeOtaActivationCoordinator.ValidateInstalledHostForEnvironment(
            false,
            true,
            exact,
            install,
            _ => { trustCalls++; return Trust(true); });
        var nonSystem = NativeOtaActivationCoordinator.ValidateInstalledHostForEnvironment(
            true,
            false,
            exact,
            install,
            _ => { trustCalls++; return Trust(true); });
        var wrongPath = NativeOtaActivationCoordinator.ValidateInstalledHostForEnvironment(
            true,
            true,
            Path.Combine(install, "other.exe"),
            install,
            _ => { trustCalls++; return Trust(true); });
        var untrusted = NativeOtaActivationCoordinator.ValidateInstalledHostForEnvironment(
            true,
            true,
            exact,
            install,
            _ => { trustCalls++; return Trust(false); });
        var trusted = NativeOtaActivationCoordinator.ValidateInstalledHostForEnvironment(
            true,
            true,
            exact,
            install,
            _ => { trustCalls++; return Trust(true); });

        Assert.Equal(NativeOtaActivationCoordinator.UnsupportedHost, nonWindows.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.UnsupportedHost, nonSystem.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, wrongPath.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, untrusted.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.Success, trusted.ExitCode);
        Assert.Equal(exact, trusted.ProcessPath);
        Assert.Equal(2, trustCalls);
    }

    [Fact]
    public void Runner_host_boundary_requires_exact_name_runner_root_and_trust()
    {
        var maintenance = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "maintenance");
        var exact = Path.Combine(
            maintenance,
            UpdateActivationContract.RunnerDirectoryName,
            new string('a', 64),
            MaintenanceContract.ExecutableName);
        var outside = Path.Combine(
            Path.GetDirectoryName(maintenance)!,
            "other",
            MaintenanceContract.ExecutableName);
        var trustCalls = 0;
        MaintenanceHostTrustResult Trust(bool trusted) => new(
            trusted,
            MaintenanceTrustSource.SignedOtaManifest,
            trusted ? "trusted" : "bad_signature");

        var wrongName = NativeOtaActivationCoordinator.ValidateRunnerHostForEnvironment(
            true,
            true,
            Path.Combine(Path.GetDirectoryName(exact)!, "other.exe"),
            maintenance,
            _ => { trustCalls++; return Trust(true); });
        var wrongRoot = NativeOtaActivationCoordinator.ValidateRunnerHostForEnvironment(
            true,
            true,
            outside,
            maintenance,
            _ => { trustCalls++; return Trust(true); });
        var untrusted = NativeOtaActivationCoordinator.ValidateRunnerHostForEnvironment(
            true,
            true,
            exact,
            maintenance,
            _ => { trustCalls++; return Trust(false); });
        var trusted = NativeOtaActivationCoordinator.ValidateRunnerHostForEnvironment(
            true,
            true,
            exact,
            maintenance,
            _ => { trustCalls++; return Trust(true); });

        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, wrongName.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, wrongRoot.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.UntrustedHost, untrusted.ExitCode);
        Assert.Equal(NativeOtaActivationCoordinator.Success, trusted.ExitCode);
        Assert.Equal(exact, trusted.ProcessPath);
        Assert.Equal(2, trustCalls);
    }
}
