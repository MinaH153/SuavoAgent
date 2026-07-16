using SuavoAgent.Setup.Maintenance;
using System.Text.Json;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeInstallCoordinatorTests
{
    private sealed class FakeServices : IWindowsServiceControl
    {
        public Dictionary<string, NativeServiceState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public string? FailStop { get; set; }
        public string? FailConfigure { get; set; }
        public string? FailStart { get; set; }
        public Action<string>? OnStart { get; set; }

        public FakeServices(NativeServiceState initial = NativeServiceState.Running)
        {
            foreach (var spec in NativeServiceSpecs.All) States[spec.Name] = initial;
        }

        public NativeServiceState Query(string serviceName)
        {
            Calls.Add("query:" + serviceName);
            return States.GetValueOrDefault(serviceName, NativeServiceState.Unknown);
        }

        public bool StopAndWait(string serviceName, TimeSpan timeout)
        {
            Calls.Add("stop:" + serviceName);
            if (serviceName == FailStop) return false;
            if (States[serviceName] != NativeServiceState.NotInstalled)
                States[serviceName] = NativeServiceState.Stopped;
            return true;
        }

        public bool EnsureConfigured(NativeServiceSpec spec, string installDir)
        {
            Calls.Add("configure:" + spec.Name);
            if (spec.Name == FailConfigure) return false;
            States[spec.Name] = NativeServiceState.Stopped;
            return true;
        }

        public bool StartAndWait(string serviceName, TimeSpan timeout)
        {
            Calls.Add("start:" + serviceName);
            if (serviceName == FailStart) return false;
            States[serviceName] = NativeServiceState.Running;
            OnStart?.Invoke(serviceName);
            return true;
        }
    }

    [Fact]
    public void Preparation_is_same_volume_sibling_and_transaction_bound()
    {
        var tx = new string('a', 32);
        var preparation = NativeInstallCoordinator.CreatePreparation(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "suavo", "Agent")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "suavo-data")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "suavo-maint")),
            tx);

        Assert.Equal(preparation.LiveDirectory + ".staging-" + tx, preparation.StagingDirectory);
        Assert.Equal(
            Path.GetDirectoryName(preparation.LiveDirectory),
            Path.GetDirectoryName(preparation.StagingDirectory));
        Assert.StartsWith(preparation.MaintenanceRoot, preparation.PreparedManifestPath);
        Assert.Equal(Path.Combine(preparation.DataDirectory, "binaries.manifest"), preparation.DataManifestPath);
    }

    [Fact]
    public void Next_run_reclaims_only_exact_abandoned_stages_and_temp_journals()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "suavo-preparation-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var live = Path.Combine(root, "ProgramFiles", "Agent");
            var maintenance = Path.Combine(root, "ProgramData", "Maintenance");
            var abandoned = live + ".staging-" + new string('a', 32);
            var lookalike = live + ".staging-not-owned";
            Directory.CreateDirectory(abandoned);
            Directory.CreateDirectory(lookalike);
            File.WriteAllText(Path.Combine(abandoned, "partial.bin"), "partial");
            var victim = Path.Combine(root, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
            try
            {
                Directory.CreateSymbolicLink(
                    Path.Combine(abandoned, "nested-link"),
                    victim);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       IOException or
                                       PlatformNotSupportedException)
            {
                // The exact-name cleanup assertions remain valid on hosts that
                // cannot grant the symlink privilege to the test process.
            }
            Directory.CreateDirectory(maintenance);
            var manifestTmp = Path.Combine(
                maintenance,
                "binaries.manifest.new-" + new string('b', 32));
            var journalTmp = Path.Combine(
                maintenance,
                "install-transaction.json.tmp-" + new string('c', 32));
            var evidence = Path.Combine(maintenance, "binaries.manifest.rollback-evidence");
            File.WriteAllText(manifestTmp, "partial");
            File.WriteAllText(journalTmp, "partial");
            File.WriteAllText(evidence, "keep");

            var cleaned = NativeInstallCoordinator.CleanupAbandonedPreparationArtifacts(
                live,
                maintenance);

            Assert.True(cleaned);
            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(lookalike));
            Assert.False(File.Exists(manifestTmp));
            Assert.False(File.Exists(journalTmp));
            Assert.Equal("keep", File.ReadAllText(evidence));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Abandoned_stage_root_link_is_removed_without_touching_its_target()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "suavo-stage-link-cleanup-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "ProgramFiles", "Agent");
        var maintenance = Path.Combine(root, "ProgramData", "Maintenance");
        var victim = Path.Combine(root, "victim");
        var stageLink = live + ".staging-" + new string('a', 32);
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        try
        {
            try { Directory.CreateSymbolicLink(stageLink, victim); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       IOException or
                                       PlatformNotSupportedException)
            {
                return;
            }

            var cleaned = NativeInstallCoordinator.CleanupAbandonedPreparationArtifacts(
                live,
                maintenance);

            Assert.True(cleaned);
            Assert.False(Directory.Exists(stageLink));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
        }
        finally
        {
            try { if (Directory.Exists(stageLink)) Directory.Delete(stageLink); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Quiesce_is_strict_watchdog_broker_core_order_and_kills_lingering_processes()
    {
        var services = new FakeServices();
        var kills = 0;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => { kills++; return true; });

        var result = coordinator.Quiesce();

        Assert.True(result);
        Assert.Equal(
            [
                "stop:SuavoAgent.Watchdog",
                "stop:SuavoAgent.Broker",
                "stop:SuavoAgent.Core",
            ],
            services.Calls);
        Assert.Equal(1, kills);
    }

    [Fact]
    public void Quiesce_fails_closed_before_process_kill_when_any_service_cannot_stop()
    {
        var services = new FakeServices { FailStop = NativeServiceSpecs.Broker.Name };
        var kills = 0;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => { kills++; return true; });

        Assert.False(coordinator.Quiesce());
        Assert.Equal(0, kills);
        Assert.DoesNotContain("stop:SuavoAgent.Core", services.Calls);
    }

    [Fact]
    public void Legacy_lifecycle_is_retired_only_after_services_and_processes_are_quiesced()
    {
        var services = new FakeServices();
        var ordered = services.Calls;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => { ordered.Add("kill"); return true; },
            retireLegacyLifecycle: (_, _) =>
            {
                ordered.Add("legacy-migration");
                return true;
            });

        var result = coordinator.QuiesceAndRetireLegacyLifecycle(
            @"C:\Program Files\Suavo\Agent",
            @"C:\ProgramData\SuavoAgent");

        Assert.True(result);
        Assert.Equal(
        [
            "stop:SuavoAgent.Watchdog",
            "stop:SuavoAgent.Broker",
            "stop:SuavoAgent.Core",
            "kill",
            "legacy-migration",
        ], ordered);
    }

    [Fact]
    public void Failed_legacy_migration_keeps_activation_gate_closed()
    {
        var services = new FakeServices();
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => true,
            retireLegacyLifecycle: (_, _) => false);

        Assert.False(coordinator.QuiesceAndRetireLegacyLifecycle("install", "data"));
        Assert.All(services.States, state =>
            Assert.Equal(NativeServiceState.Stopped, state.Value));
    }

    [Fact]
    public void Activate_reasserts_acls_configures_exact_specs_and_requires_all_running()
    {
        var services = new FakeServices(NativeServiceState.NotInstalled);
        var aclCalls = 0;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => { aclCalls++; services.Calls.Add("acl"); return true; },
            () => true);

        var result = coordinator.Activate("/install", "/data");

        Assert.True(result);
        Assert.Equal(1, aclCalls);
        Assert.Equal(
            NativeServiceSpecs.All.Select(x => "configure:" + x.Name),
            services.Calls.Where(x => x.StartsWith("configure:", StringComparison.Ordinal)));
        Assert.Equal(
            NativeServiceSpecs.All.Select(x => "start:" + x.Name),
            services.Calls.Where(x => x.StartsWith("start:", StringComparison.Ordinal)));
        Assert.True(services.Calls.IndexOf("configure:SuavoAgent.Core") < services.Calls.IndexOf("acl"));
        Assert.True(services.Calls.IndexOf("acl") < services.Calls.IndexOf("start:SuavoAgent.Core"));
    }

    [Theory]
    [InlineData("acl")]
    [InlineData("configure")]
    [InlineData("start")]
    public void Activate_fails_closed_on_each_privileged_boundary(string boundary)
    {
        var services = new FakeServices(NativeServiceState.NotInstalled);
        if (boundary == "configure") services.FailConfigure = NativeServiceSpecs.Broker.Name;
        if (boundary == "start") services.FailStart = NativeServiceSpecs.Broker.Name;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => boundary != "acl",
            () => true);

        Assert.False(coordinator.Activate("/install", "/data"));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void ExecuteRejectsAuthorityModeAndCallbackMismatch(
        bool hasPromote,
        bool hasFinalize,
        bool requiresAuthority)
    {
        var services = new FakeServices();
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => true);
        var preparation = NativeInstallCoordinator.CreatePreparation(
            Path.Combine(Path.GetTempPath(), "suavo-mode", "Agent"),
            Path.Combine(Path.GetTempPath(), "suavo-mode-data"),
            Path.Combine(Path.GetTempPath(), "suavo-mode-maint"),
            new string('a', 32));

        Assert.Throws<ArgumentException>(() => coordinator.Execute(
            preparation,
            () => true,
            promoteAuthority: hasPromote
                ? () => AuthorityPromotionOutcome.Promoted
                : null,
            finalizeAuthority: hasFinalize ? () => true : null,
            requiresAuthorityPromotion: requiresAuthority));
    }

    [Fact]
    public void RestartPromotedCohortRequiresFreshActiveCloudAndFullWorkstationComposite()
    {
        using var fixture = new RestartHealthFixture();

        Assert.True(fixture.Restart());
        Assert.Contains("stop:SuavoAgent.Core", fixture.Services.Calls);
        Assert.Contains("start:SuavoAgent.Core", fixture.Services.Calls);
    }

    [Fact]
    public void InstalledCohortStartNeverCreatesOrReconfiguresMsiOwnedServices()
    {
        var services = new FakeServices(NativeServiceState.Stopped);
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => true);

        Assert.True(coordinator.StartInstalledCohort("install", "data"));
        Assert.DoesNotContain(
            services.Calls,
            call => call.StartsWith("configure:", StringComparison.Ordinal));
        Assert.Equal(
            NativeServiceSpecs.All.Select(spec => "start:" + spec.Name),
            services.Calls.Where(call => call.StartsWith("start:", StringComparison.Ordinal)));
    }

    [Fact]
    public void InstalledCohortStartFailsClosedWhenAnyMsiServiceIsAbsent()
    {
        var services = new FakeServices(NativeServiceState.Stopped);
        services.States[NativeServiceSpecs.Broker.Name] = NativeServiceState.NotInstalled;
        var coordinator = new NativeInstallCoordinator(
            services,
            (_, _) => true,
            () => true);

        Assert.False(coordinator.StartInstalledCohort("install", "data"));
        Assert.DoesNotContain(
            services.Calls,
            call => call.StartsWith("configure:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            services.Calls,
            call => call.StartsWith("start:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("helper")]
    [InlineData("ipc")]
    [InlineData("actuation")]
    [InlineData("sql")]
    [InlineData("schema")]
    [InlineData("pms")]
    public void RestartPromotedCohortFailsClosedForEveryPostRestartHealthBoundary(
        string failedBoundary)
    {
        using var fixture = new RestartHealthFixture(failedBoundary);

        Assert.False(fixture.Restart());
    }

    private sealed class RestartHealthFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "suavo-active-restart-" + Guid.NewGuid().ToString("N"));
        private readonly string? _failed;
        public FakeServices Services { get; } = new();

        public RestartHealthFixture(string? failed = null)
        {
            _failed = failed;
            Directory.CreateDirectory(Data);
            File.WriteAllText(Path.Combine(Data, "cloud-auth-health.json"), "stale");
            File.WriteAllText(Path.Combine(Data, "activation-readiness.json"), "stale");
            Services.OnStart = service =>
            {
                if (service != NativeServiceSpecs.Watchdog.Name) return;
                var now = DateTimeOffset.UtcNow.AddMilliseconds(1).ToString("o");
                File.WriteAllText(
                    Path.Combine(Data, "cloud-auth-health.json"),
                    JsonSerializer.Serialize(new
                    {
                        status = _failed == "cloud" ? "error" : "ok",
                        lastSuccessAt = now,
                        consecutiveFailures = 0,
                        lastErrorKind = (string?)null,
                        restartRequested = false,
                    }));
                File.WriteAllText(
                    Path.Combine(Data, "activation-readiness.json"),
                    JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        provisioningId = (string?)null,
                        checkedAt = now,
                        helperAttached = _failed != "helper",
                        ipcConnected = _failed != "ipc",
                        actuationReady = _failed != "actuation",
                        sqlConnected = _failed != "sql",
                        schemaCanaryGreen = _failed != "schema",
                        pmsCode = _failed == "pms" ? "pms_db_unreachable" : "pms_operational",
                        deviceProof = (object?)null,
                    }));
            };
        }

        private string Data => Path.Combine(_root, "data");
        private string Install => Path.Combine(_root, "install");

        public bool Restart()
        {
            var coordinator = new NativeInstallCoordinator(
                Services,
                (_, _) => true,
                () => true,
                delay: _ => { });
            return coordinator.RestartPromotedCohort(
                Install,
                Data,
                TimeSpan.FromMilliseconds(20));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
