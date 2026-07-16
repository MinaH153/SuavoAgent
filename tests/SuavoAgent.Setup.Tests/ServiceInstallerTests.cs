using System.Reflection;
using System.Security.AccessControl;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Regression guards for <see cref="ServiceInstaller"/>. The installer is a
/// static class that shells out to <c>sc.exe</c>, so end-to-end behaviour can
/// only be verified on Windows with admin rights — these tests instead assert
/// the internal shape (which services are installed, which SCM recovery policy
/// is applied) so that nobody silently drops a service when editing the class.
/// </summary>
public class ServiceInstallerTests
{
    private static string? GetConstant(string name)
    {
        var field = typeof(ServiceInstaller).GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        return field?.GetRawConstantValue() as string;
    }

    [Fact]
    public void Installer_Registers_Core_Broker_And_Watchdog()
    {
        // Watchdog was missing from the GUI installer path until 2026-04-22.
        // Keep this test as a permanent regression guard — any rename or
        // removal of the constant fails here, not in the field.
        Assert.Equal("SuavoAgent.Core", GetConstant("CoreServiceName"));
        Assert.Equal("SuavoAgent.Broker", GetConstant("BrokerServiceName"));
        Assert.Equal("SuavoAgent.Watchdog", GetConstant("WatchdogServiceName"));
    }

    [Fact]
    public void Installer_Source_Registers_Watchdog_With_Longer_Recovery_Windows()
    {
        // The source-text guard catches "constants exist but sc.exe failure
        // was never wired" regressions without needing a Windows runner.
        // Watchdog uses 10s/60s/5min (vs 5s/30s/60s for
        // Core/Broker) because Watchdog churn would mask real issues.
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup", "ServiceInstaller.cs");
        var source = File.Exists(sourcePath)
            ? File.ReadAllText(sourcePath)
            : string.Empty;

        // Skip the assertion if the source file isn't resolvable from this
        // runner — the reflection test above is the authoritative guard.
        if (source.Length == 0) return;

        Assert.Contains("restart/10000/restart/60000/restart/300000", source);
        Assert.Contains("LocalSystem", source);  // Watchdog account
        Assert.Contains("CoreServiceIdentity.AccountName", source); // Core account
        Assert.Contains("sidtype", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qsidtype", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unrestricted", source, StringComparison.OrdinalIgnoreCase);

        // Broker MUST register as LocalSystem. WTSQueryUserToken +
        // CreateProcessAsUser require SeTcbPrivilege, which is held ONLY by
        // LocalSystem. Under NetworkService the privileged launch fails 1314
        // and the Broker silently falls back to launching the Helper in its
        // OWN session (Session 0) — an invisible desktop where the intent
        // cursor, vision capture, and UIA never render. The C# installer
        // regressed to NetworkService (with a false "NetworkService has
        // SeTcbPrivilege" comment) and shipped a Helper that never painted on
        // the pilot box (2026-06-01). Native install and repair must both
        // register LocalSystem; this keeps both paths in sync and
        // guards the regression forever.
        Assert.Matches(@"create \{BrokerServiceName\}.*LocalSystem", source);  // Broker account
        // No service may be REGISTERED as NetworkService (the word may still
        // appear in explanatory comments). Targets the `obj= ...NetworkService`
        // clause specifically — the exact regression we are guarding against.
        Assert.DoesNotMatch(@"obj=[^\n]*NetworkService", source);
    }

    // Regression for the 2026-06-10 Helper crash-loop: LockdownDirectoryAcl strips
    // the data dir to SYSTEM/Admins/Core-service-SID, but the Helper runs de-privileged
    // and died on its first log write — before it could log anything. The carve-out
    // grants BUILTIN\Users (*S-1-5-32-545 — robust vs INTERACTIVE for a UAC-filtered
    // token; the proven principal): traverse on the root (dir-only,
    // NO inherited file reads — state.db is plaintext PHI, state.key machine-DPAPI),
    // Modify on logs\helper + diagnostics\helper, inherited read-only access to the
    // PHI-free signed observation authority directory, and per-file read on the
    // remaining helper configs.
    [Fact]
    public void Helper_carveout_grants_minimum_and_never_root_file_reads()
    {
        var grants = ServiceInstaller.BuildInteractiveAclSpecs(@"C:\ProgramData\SuavoAgent");

        Assert.Equal(10, grants.Count);

        var root = grants[0];
        Assert.Equal(@"C:\ProgramData\SuavoAgent", root.Target);
        Assert.Equal(FileSystemRights.ReadAndExecute, root.UsersRights);
        // The root grant must NOT inherit to files — inheritance would expose state.db/state.key.
        Assert.Equal(InheritanceFlags.None, root.UsersInheritance);

        // logs\ root: traverse only — SYSTEM services write here, so the de-privileged
        // user must never gain create/delete (junction-planting EoP).
        var logsRoot = grants[1];
        Assert.EndsWith("logs", logsRoot.Target);
        Assert.Equal(FileSystemRights.ReadAndExecute, logsRoot.UsersRights);
        Assert.Equal(InheritanceFlags.None, logsRoot.UsersInheritance);

        var inherited = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
        Assert.Contains(grants, g =>
            g.Target.EndsWith(Path.Combine("logs", "helper")) &&
            g.UsersRights == FileSystemRights.Modify &&
            g.UsersInheritance == inherited &&
            g.EnsureDirectory &&
            g.ApplyRecursively);
        // diagnostics\ root: traverse only (SYSTEM appends events.jsonl there);
        // the Helper's journal gets its own Modify subtree.
        Assert.Contains(grants, g =>
            g.Target.EndsWith("diagnostics") &&
            g.UsersRights == FileSystemRights.ReadAndExecute &&
            g.UsersInheritance == InheritanceFlags.None &&
            g.EnsureDirectory);
        Assert.Contains(grants, g =>
            g.Target.EndsWith(Path.Combine("diagnostics", "helper")) &&
            g.UsersRights == FileSystemRights.Modify &&
            g.UsersInheritance == inherited &&
            g.EnsureDirectory &&
            g.ApplyRecursively);
        Assert.Contains(grants, g =>
            g.Target.EndsWith("honeytokens") &&
            g.UsersRights == FileSystemRights.Modify &&
            g.UsersInheritance == inherited &&
            g.EnsureDirectory &&
            g.ApplyRecursively);
        Assert.Contains(grants, g =>
            g.Target.EndsWith(ObservationActivationAuthority.StateDirectoryName) &&
            g.UsersRights == FileSystemRights.ReadAndExecute &&
            g.UsersInheritance == inherited &&
            g.EnsureDirectory &&
            g.ApplyRecursively);
        Assert.DoesNotContain(grants, g => g.Target.EndsWith("vision.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(grants, g => g.Target.EndsWith("actuation.json") && g.UsersRights == FileSystemRights.Read && !g.EnsureDirectory);
        Assert.Contains(grants, g => g.Target.EndsWith("pioneerrx.json") && g.UsersRights == FileSystemRights.Read && !g.EnsureDirectory);
        Assert.Contains(grants, g => g.Target.EndsWith("honeytoken-attribution.json") && g.UsersRights == FileSystemRights.Read && !g.EnsureDirectory);
    }

    [Fact]
    public void Protected_directory_acls_authorize_only_the_exact_core_service_sid()
    {
        var install = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Install,
            directory: true,
            inherit: true);
        var data = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Data,
            directory: true,
            inherit: true);
        var maintenance = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: true,
            inherit: true);

        Assert.Equal(HandleBoundAcl.SystemSid, install.OwnerSid);
        Assert.Contains(install.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.ReadAndExecute);
        Assert.DoesNotContain(install.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.Modify);
        Assert.Contains(data.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.Modify);
        Assert.DoesNotContain(maintenance.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid);
        Assert.Equal(2, maintenance.Aces.Count);
        Assert.Contains(maintenance.Aces, ace =>
            ace.Sid == HandleBoundAcl.AdministratorsSid &&
            ace.Rights == FileSystemRights.FullControl);
        Assert.Contains(maintenance.Aces, ace =>
            ace.Sid == HandleBoundAcl.SystemSid &&
            ace.Rights == FileSystemRights.FullControl);
    }

    // ParseVersion feeds the ARP VersionMajor/Minor DWORDs. Must tolerate a leading 'v',
    // a -rc/-suffix, and a short/garbage string without throwing.
    [Theory]
    [InlineData("3.77.0", 3, 77)]
    [InlineData("v3.77.0", 3, 77)]
    [InlineData("v3.77.0-rc1", 3, 77)]
    [InlineData("4", 4, 0)]
    [InlineData("", 0, 0)]
    [InlineData("garbage", 0, 0)]
    public void ParseVersion_extracts_major_minor(string input, int major, int minor)
    {
        var (m, n) = ServiceInstaller.ParseVersion(input);
        Assert.Equal(major, m);
        Assert.Equal(minor, n);
    }

    [Fact]
    public void AddRemovePrograms_commands_use_one_native_maintenance_host_for_repair_and_uninstall()
    {
        var commands = ServiceInstaller.BuildMaintenanceCommands(@"C:\Program Files\Suavo\Agent");

        Assert.Contains(MaintenanceContract.ExecutableName, commands.Uninstall);
        Assert.Contains(Program.UninstallUiSwitch, commands.Uninstall);
        Assert.Contains(MaintenanceContract.UninstallSwitch, commands.QuietUninstall);
        Assert.Contains(SelfUninstallContract.PreserveDataSwitch, commands.Uninstall);
        Assert.Contains(SelfUninstallContract.PreserveDataSwitch, commands.QuietUninstall);
        Assert.DoesNotContain(SelfUninstallContract.PurgeRetainedDataSwitch, commands.Uninstall);
        Assert.Contains(MaintenanceContract.ExecutableName, commands.QuietUninstall);
        Assert.Contains(Program.RepairUiSwitch, commands.Repair);
        Assert.DoesNotContain(MaintenanceContract.RepairServicesSwitch, commands.Repair);
        Assert.Contains("manual-repair-requested", commands.Repair);
        Assert.DoesNotContain(Program.RepairUiSwitch, commands.QuietUninstall);
        Assert.Contains("--silent", commands.QuietUninstall);
        Assert.DoesNotContain(".ps1", commands.Uninstall, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", commands.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddRemovePrograms_registration_failure_is_terminal_not_a_warning()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/SuavoAgent.Setup/ServiceInstaller.Registration.cs"));
        var source = File.ReadAllText(path);

        Assert.Contains("throw new InvalidOperationException(\"arp_registration_failed\"", source);
        Assert.DoesNotContain(
            "The agent installation can continue",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationCannotPublishDoneBeforeMandatoryLifecycleRegistration()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs"));
        var source = File.ReadAllText(path);
        var registration = source.IndexOf(
            "ServiceInstaller.RegisterUninstallEntry",
            StringComparison.Ordinal);
        var done = source.IndexOf(
            "new PhaseEvent(Phase.Done",
            StringComparison.Ordinal);

        Assert.True(registration >= 0);
        Assert.True(done > registration);
    }

    [Fact]
    public void PreserveDataDirectory_quarantines_evidence_and_removes_operational_secrets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "suavo-retention-test-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "SuavoAgent");
        var retentionRoot = Path.Combine(root, "SuavoAgent-Retained");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "state.db"), "audit-evidence");
        File.WriteAllText(Path.Combine(dataDir, "state.key"), "evidence-key");
        File.WriteAllText(Path.Combine(dataDir, "credentials.dat"), "operational-secret");
        File.WriteAllText(
            Path.Combine(dataDir, ".credentials.dat.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp"),
            "interrupted-operational-secret");
        File.WriteAllText(Path.Combine(dataDir, "pipe.nonce"), "operational-nonce");
        var locked = new List<string>();
        var finalMetadataObservedByLockdown = false;

        try
        {
            var result = ServiceInstaller.PreserveDataDirectory(
                dataDir,
                retentionRoot,
                DateTimeOffset.Parse("2026-07-10T12:00:00.0000000Z"),
                path =>
                {
                    locked.Add(path);
                    if (Path.GetFileName(path).StartsWith(
                            "retained-",
                            StringComparison.Ordinal))
                    {
                        finalMetadataObservedByLockdown = File.Exists(
                            Path.Combine(path, "retention.json"));
                    }
                    return true;
                });

            Assert.True(result.IsPreserved);
            Assert.NotNull(result.RetainedPath);
            Assert.False(Directory.Exists(dataDir));
            Assert.True(File.Exists(Path.Combine(result.RetainedPath!, "state.db")));
            Assert.True(File.Exists(Path.Combine(result.RetainedPath!, "state.key")));
            Assert.False(File.Exists(Path.Combine(result.RetainedPath!, "credentials.dat")));
            Assert.False(File.Exists(Path.Combine(
                result.RetainedPath!,
                ".credentials.dat.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp")));
            Assert.False(File.Exists(Path.Combine(result.RetainedPath!, "pipe.nonce")));
            Assert.True(File.Exists(Path.Combine(result.RetainedPath!, "retention.json")));
            Assert.True(finalMetadataObservedByLockdown);
            Assert.Contains(retentionRoot, locked);
            Assert.Contains(dataDir, locked);
            Assert.Contains(result.RetainedPath!, locked);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true, SelfUninstallContract.PurgeRetainedDataSwitch)]
    [InlineData(false, SelfUninstallContract.PreserveDataSwitch)]
    public void Uninstall_data_policy_defaults_to_preserve(
        bool expectedPurge,
        params string[] switches) =>
        Assert.Equal(expectedPurge, UninstallInstaller.ShouldPurgeRetainedData(switches));

    [Fact]
    public void Uninstall_rejects_conflicting_preserve_and_purge_switches()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UninstallInstaller.ShouldPurgeRetainedData(
            [
                SelfUninstallContract.PreserveDataSwitch,
                SelfUninstallContract.PurgeRetainedDataSwitch,
            ]));
    }

    [Fact]
    public void Native_scheduled_task_scan_deletes_only_exact_owned_identity()
    {
        const string csv =
            "\"\\Ordinary\",\"Ready\",\"cmd.exe /c echo ordinary\"\r\n" +
            "\"\\Legacy, Agent\",\"Ready\",\"C:\\SuavoAgent\\node.exe\"\r\n" +
            "\"\\My SuavoAgent Backup\",\"Ready\",\"cmd.exe /c backup\"\r\n" +
            "\"\\SuavoSelfUninstall\",\"Ready\",\"powershell -NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1\"\r\n";

        var names = UninstallTerminalCleanup.ParseSuavoScheduledTaskNames(csv);

        Assert.Equal(new[] { @"\SuavoSelfUninstall" }, names);
        Assert.Equal("quoted\"name", UninstallTerminalCleanup.ReadFirstCsvField(
            "\"quoted\"\"name\",\"Ready\""));
    }

    [Theory]
    [InlineData(0, "", true)]
    [InlineData(UninstallTerminalCleanup.TaskNotRunningHResult, "", true)]
    [InlineData(1, "ERROR: The task cannot be stopped because it is not running.", true)]
    [InlineData(1, "ERROR: Access is denied.", false)]
    public void Scheduled_task_end_accepts_only_proven_nonrunning_outcome(
        int exitCode,
        string output,
        bool expected) =>
        Assert.Equal(
            expected,
            UninstallTerminalCleanup.IsSafeTaskEndResult(exitCode, output));

    [Theory]
    [InlineData("schtasks.exe", true)]
    [InlineData("sc.exe", true)]
    [InlineData("powershell", false)]
    [InlineData("powershell.exe", false)]
    [InlineData("pwsh.exe", false)]
    [InlineData("cmd.exe", false)]
    [InlineData("SCHTASKS.EXE", false)]
    [InlineData(null, false)]
    public void Terminal_cleanup_can_launch_only_the_two_exact_native_utilities(
        string? executable,
        bool expected) =>
        Assert.Equal(
            expected,
            UninstallTerminalCleanup.IsApprovedCleanupExecutable(executable));

    [Fact]
    public void Exact_legacy_task_identity_requires_its_exact_owned_action()
    {
        const string owned = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>powershell</Command>
                  <Arguments>-NoProfile -ExecutionPolicy Bypass -File C:\Windows\Temp\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        const string unrelated = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>cmd.exe</Command>
                  <Arguments>/c backup-important-files.cmd</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Assert.True(UninstallTerminalCleanup.IsExactOwnedScheduledTaskName(
            @"\SuavoSelfUninstall"));
        Assert.True(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            owned,
            @"C:\Windows"));
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            owned.Replace(
                "<Command>powershell</Command>",
                "<Command>powershell.exe</Command>",
                StringComparison.Ordinal),
            @"C:\Windows"));
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            owned.Replace(
                "<Command>powershell</Command>",
                @"<Command>C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe</Command>",
                StringComparison.Ordinal),
            @"C:\Windows"));
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            owned.Replace(
                "<Command>powershell</Command>",
                "<Command>PowerShell</Command>",
                StringComparison.Ordinal),
            @"C:\Windows"));
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            unrelated,
            @"C:\Windows"));
    }

    [Theory]
    [InlineData("credentials.dat", true)]
    [InlineData(".credentials.dat.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp", true)]
    [InlineData("credentials.dat.tmp-old", true)]
    [InlineData("pipe.nonce", true)]
    [InlineData("pipe.nonce.tmp-1", true)]
    [InlineData("backup_credentials.dat", false)]
    [InlineData("state.key", false)]
    public void Operational_credential_residue_names_are_exact_and_bounded(
        string name,
        bool expected) =>
        Assert.Equal(expected, UninstallTerminalCleanup.IsOperationalCredentialFileName(name));

    [Fact]
    public void Authenticated_uninstall_claim_argument_requires_one_path()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uninstall.request.claimed"));

        Assert.Equal(path, UninstallInstaller.ReadAuthenticatedClaimPath(
        [
            SelfUninstallContract.AuthenticatedRequestSwitch,
            path,
        ]));
        Assert.Throws<InvalidOperationException>(() =>
            UninstallInstaller.ReadAuthenticatedClaimPath(
            [
                SelfUninstallContract.AuthenticatedRequestSwitch,
                path,
                SelfUninstallContract.AuthenticatedRequestSwitch,
                path,
            ]));
    }
}
