using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeRepairInstallerTests
{
    private sealed class FakeServiceControl : IWindowsServiceControl
    {
        public Dictionary<string, NativeServiceState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public List<NativeServiceSpec> Configured { get; } = [];
        public string? StayStoppedAfterStart { get; set; }

        public FakeServiceControl()
        {
            foreach (var spec in NativeServiceSpecs.All)
                States[spec.Name] = NativeServiceState.Running;
        }

        public NativeServiceState Query(string serviceName)
        {
            Calls.Add("query:" + serviceName);
            return States.GetValueOrDefault(serviceName, NativeServiceState.Unknown);
        }

        public bool StopAndWait(string serviceName, TimeSpan timeout)
        {
            Calls.Add("stop:" + serviceName);
            if (States.GetValueOrDefault(serviceName) != NativeServiceState.NotInstalled)
                States[serviceName] = NativeServiceState.Stopped;
            return true;
        }

        public bool EnsureConfigured(NativeServiceSpec spec, string installDir)
        {
            Calls.Add("configure:" + spec.Name);
            Configured.Add(spec);
            if (States.GetValueOrDefault(spec.Name) == NativeServiceState.NotInstalled)
                States[spec.Name] = NativeServiceState.Stopped;
            return true;
        }

        public bool StartAndWait(string serviceName, TimeSpan timeout)
        {
            Calls.Add("start:" + serviceName);
            if (!string.Equals(serviceName, StayStoppedAfterStart, StringComparison.OrdinalIgnoreCase))
                States[serviceName] = NativeServiceState.Running;
            return true;
        }
    }

    [Fact]
    public void Invalid_cohort_refuses_before_acl_or_service_mutation()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: false);
        var services = new FakeServiceControl();
        var aclCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => { aclCalls++; services.Calls.Add("acl"); return true; },
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance);

        Assert.Equal(NativeRepairInstaller.InvalidCohort, exit);
        Assert.Equal(0, aclCalls);
        Assert.Empty(services.Calls);
    }

    [Fact]
    public void LocalSystemRepair_RunsForwardAuthorityRecoveryBeforeCohortMutation()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl();
        var recoveryCalls = 0;
        var aclCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => { aclCalls++; return true; },
            fixture.PublicKeyDer,
            recoverAuthority: () =>
            {
                recoveryCalls++;
                return InstallTransactionResult.Failed(
                    "authority_promotion_unknown",
                    rolledBack: false);
            },
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance);

        Assert.Equal(NativeRepairInstaller.AuthorityRecoveryPending, exit);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(0, aclCalls);
        Assert.Empty(services.Calls);
    }

    [Fact]
    public void LocalSystemRepairContinuesAfterExactPreAuthorityRollbackRecovery()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl();
        var recoveryCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => true,
            fixture.PublicKeyDer,
            recoverAuthority: () =>
            {
                recoveryCalls++;
                return InstallTransactionResult.Failed(
                    "recovered_incomplete_transaction",
                    rolledBack: true);
            },
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance);

        Assert.Equal(NativeRepairInstaller.Success, exit);
        Assert.Equal(1, recoveryCalls);
        Assert.Contains("stop:SuavoAgent.Core", services.Calls);
        Assert.Contains("start:SuavoAgent.Core", services.Calls);
    }

    [Fact]
    public void Valid_repair_reasserts_full_specs_restarts_core_broker_and_never_stops_watchdog()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl();
        services.States[NativeServiceSpecs.Core.Name] = NativeServiceState.NotInstalled;
        var aclCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => { aclCalls++; services.Calls.Add("acl"); return true; },
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance);

        Assert.Equal(NativeRepairInstaller.Success, exit);
        Assert.Equal(1, aclCalls);
        Assert.Contains("stop:SuavoAgent.Broker", services.Calls);
        Assert.Contains("stop:SuavoAgent.Core", services.Calls);
        Assert.DoesNotContain("stop:SuavoAgent.Watchdog", services.Calls);
        Assert.Equal(NativeServiceSpecs.All, services.Configured);
        Assert.Equal(@"NT AUTHORITY\LocalService", services.Configured.Single(x => x.Name == "SuavoAgent.Core").Account);
        Assert.True(services.Configured.Single(x => x.Name == "SuavoAgent.Core").RequiresUnrestrictedServiceSid);
        Assert.All(
            services.Configured.Where(x => x.Name != "SuavoAgent.Core"),
            spec => Assert.False(spec.RequiresUnrestrictedServiceSid));
        Assert.Equal("LocalSystem", services.Configured.Single(x => x.Name == "SuavoAgent.Broker").Account);
        Assert.Equal("LocalSystem", services.Configured.Single(x => x.Name == "SuavoAgent.Watchdog").Account);
        Assert.Equal("SuavoAgent.Core", services.Configured.Single(x => x.Name == "SuavoAgent.Broker").Dependency);
        Assert.Equal("restart/10000/restart/60000/restart/300000",
            services.Configured.Single(x => x.Name == "SuavoAgent.Watchdog").FailureActions);
        Assert.True(
            services.Calls.IndexOf("configure:SuavoAgent.Core") < services.Calls.IndexOf("acl"),
            "Core must exist with its unrestricted service SID before exact-SID ACLs are applied.");
        Assert.True(
            services.Calls.IndexOf("acl") < services.Calls.IndexOf("start:SuavoAgent.Core"),
            "Exact-SID ACLs must be applied before Core starts.");
        Assert.All(NativeServiceSpecs.All, spec =>
            Assert.Equal(NativeServiceState.Running, services.States[spec.Name]));
    }

    [Fact]
    public void Repair_fails_closed_before_restart_when_legacy_lifecycle_cannot_be_retired()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl();

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => { services.Calls.Add("acl"); return true; },
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance,
            retireLegacyLifecycle: (_, _) =>
            {
                services.Calls.Add("legacy-migration");
                return false;
            });

        Assert.Equal(NativeRepairInstaller.LegacyLifecycleMigrationFailed, exit);
        Assert.True(
            services.Calls.IndexOf("stop:SuavoAgent.Core") <
            services.Calls.IndexOf("legacy-migration"));
        Assert.True(
            services.Calls.IndexOf("acl") <
            services.Calls.IndexOf("legacy-migration"));
        Assert.DoesNotContain(
            services.Calls,
            call => call.StartsWith("start:", StringComparison.Ordinal));
        Assert.Equal(NativeServiceState.Stopped, services.States[NativeServiceSpecs.Core.Name]);
        Assert.Equal(NativeServiceState.Stopped, services.States[NativeServiceSpecs.Broker.Name]);
    }

    [Fact]
    public void Repair_returns_nonzero_when_any_service_is_not_healthy_after_start()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl
        {
            StayStoppedAfterStart = NativeServiceSpecs.Watchdog.Name,
        };
        services.States[NativeServiceSpecs.Watchdog.Name] = NativeServiceState.Stopped;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => true,
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance);

        Assert.Equal(NativeRepairInstaller.CohortUnhealthy, exit);
    }

    [Fact]
    public void RepairIsNotSuccessfulWhenWindowsLifecycleRegistrationFails()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        var services = new FakeServiceControl();
        var registrationCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => true,
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: TrustedMaintenance,
            repairLifecycleRegistration: () =>
            {
                registrationCalls++;
                return false;
            });

        Assert.Equal(NativeRepairInstaller.LifecycleRegistrationFailed, exit);
        Assert.Equal(1, registrationCalls);
        Assert.All(
            NativeServiceSpecs.All,
            spec => Assert.Equal(NativeServiceState.Running, services.States[spec.Name]));
    }

    [Fact]
    public void Missing_signed_host_receipt_refuses_before_acl_or_service_mutation()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        File.Delete(Path.Combine(
            fixture.InstallDir,
            MaintenanceContract.ReleaseChecksumsFileName));
        File.Delete(Path.Combine(
            fixture.InstallDir,
            MaintenanceContract.ReleaseChecksumsSignatureFileName));
        var services = new FakeServiceControl();
        var aclCalls = 0;

        var exit = NativeRepairInstaller.RunCore(
            fixture.InstallDir,
            fixture.DataDir,
            fixture.ManifestPath,
            services,
            (_, _) => { aclCalls++; return true; },
            fixture.PublicKeyDer,
            verifyAuthenticode: TrustedPublisher,
            verifyMaintenanceTrust: path =>
            {
                var directory = Path.GetDirectoryName(path)!;
                return File.Exists(Path.Combine(directory, MaintenanceContract.ReleaseChecksumsFileName)) &&
                       File.Exists(Path.Combine(directory, MaintenanceContract.ReleaseChecksumsSignatureFileName))
                    ? TrustedMaintenance(path)
                    : new(false, MaintenanceTrustSource.None, "signed_receipt_missing");
            });

        Assert.Equal(NativeRepairInstaller.InvalidCohort, exit);
        Assert.Equal(0, aclCalls);
        Assert.Empty(services.Calls);
    }

    [Fact]
    public void Program_routes_repair_switch_before_normal_setup()
    {
        Assert.True(Program.IsRepairServicesMode([MaintenanceContract.RepairServicesSwitch]));
        Assert.True(Program.IsRepairServicesMode(["--silent", "--REPAIR-SERVICES"]));
        Assert.False(Program.IsRepairServicesMode(["--uninstall"]));
    }

    [Fact]
    public void Program_routes_each_native_update_mode_without_entering_setup_ui()
    {
        Assert.True(Program.IsActivateUpdateMode([UpdateActivationContract.ActivateSwitch]));
        Assert.True(Program.IsUpdateRunnerMode([UpdateActivationContract.RunnerSwitch]));
        Assert.True(Program.IsResumeUpdateMode([UpdateActivationContract.ResumeSwitch]));
        Assert.False(Program.IsActivateUpdateMode([UpdateActivationContract.RunnerSwitch]));
        Assert.False(Program.IsUpdateRunnerMode([MaintenanceContract.RepairServicesSwitch]));
        Assert.False(Program.IsResumeUpdateMode(["--uninstall"]));
    }

    [Fact]
    public void Native_update_path_parser_requires_one_absolute_value()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "activation.request.json"));
        Assert.True(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [UpdateActivationContract.RequestPathSwitch, absolute],
            UpdateActivationContract.RequestPathSwitch,
            out var parsed));
        Assert.Equal(absolute, parsed);
        Assert.False(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [UpdateActivationContract.RequestPathSwitch, "relative.json"],
            UpdateActivationContract.RequestPathSwitch,
            out _));
        Assert.False(NativeOtaActivationCoordinator.TryReadSinglePathArgument(
            [
                UpdateActivationContract.RequestPathSwitch, absolute,
                UpdateActivationContract.RequestPathSwitch, absolute,
            ],
            UpdateActivationContract.RequestPathSwitch,
            out _));
    }

    private static AuthenticodePublisherTrust TrustedPublisher(string _) =>
        AuthenticodePublisherTrust.Trusted(AuthenticodePublisherVerifier.ExpectedPublisher);

    private static MaintenanceHostTrustResult TrustedMaintenance(string _) =>
        new(true, MaintenanceTrustSource.SignedReleaseChecksums, "trusted");

    [Fact]
    public void Reason_parser_accepts_shared_contract_and_defaults_closed()
    {
        Assert.Equal(
            MaintenanceReason.ServiceRestartFailed,
            NativeRepairInstaller.ReadReason([
                MaintenanceContract.RepairServicesSwitch,
                MaintenanceContract.ReasonSwitch,
                "service-restart-failed"]));
        Assert.Equal(MaintenanceReason.Unspecified, NativeRepairInstaller.ReadReason(["--reason", "patient-name"]));
    }
}
