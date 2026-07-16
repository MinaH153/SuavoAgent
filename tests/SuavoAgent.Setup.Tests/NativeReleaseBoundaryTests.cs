using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Source guards for the native customer boundary and internal release
/// engineering. Internal probes may be written in PowerShell; no customer or
/// runtime path may depend on them.
/// </summary>
public sealed class NativeReleaseBoundaryTests
{
    [Fact]
    public void Customer_repository_surface_contains_no_legacy_lifecycle_scripts()
    {
        var root = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     "bootstrap.ps1",
                     "install.ps1",
                     "suavo-check.ps1",
                     "upgrade.ps1",
                     Path.Combine("scripts", "quick-install.ps1"),
                 })
            Assert.False(File.Exists(Path.Combine(root, relativePath)), relativePath);
    }

    [Fact]
    public void Native_migration_may_recognize_old_names_but_cannot_launch_a_script_host()
    {
        var source = ReadRepoFile(
            "src/SuavoAgent.Setup/Maintenance/LegacyLifecycleMigration.cs");

        Assert.Contains("bootstrap.ps1", source);
        Assert.Contains("quick-install.ps1", source);
        Assert.Contains("TrustedWindowsSystemBinary.Resolve(executable)", source);
        Assert.Contains("\"schtasks.exe\"", source);
        Assert.Contains("[\"/Change\", \"/TN\", taskName, \"/Disable\"]", source);
        Assert.Contains("[\"/End\", \"/TN\", taskName]", source);
        Assert.Contains("[\"/Delete\", \"/TN\", taskName, \"/F\"]", source);
        Assert.DoesNotContain("\"powershell.exe\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"pwsh.exe\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseShellExecute = true", source);

        var uninstall = ReadRepoFile("src/SuavoAgent.Setup/UninstallTerminalCleanup.cs");
        Assert.Contains("[\"/Change\", \"/TN\", taskName, \"/Disable\"]", uninstall);
        Assert.Contains("[\"/End\", \"/TN\", taskName]", uninstall);
        Assert.Contains("[\"/Delete\", \"/TN\", taskName, \"/F\"]", uninstall);
    }

    [Fact]
    public void Native_customer_installer_has_no_secret_file_or_cli_ingress()
    {
        var program = ReadRepoFile("src/SuavoAgent.Setup/Program.cs");
        var app = ReadRepoFile("src/SuavoAgent.Setup/Gui/App.axaml.cs");
        var setupConfig = ReadRepoFile("src/SuavoAgent.Setup/SetupConfig.cs");

        Assert.DoesNotContain("ConsoleInstaller", program);
        Assert.DoesNotContain("SetupConfig.Load", app);
        Assert.DoesNotContain("setup.json", setupConfig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--api-key", setupConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device-code pairing", setupConfig, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installed_connection_entry_is_configuration_only_and_uses_msi_owned_services()
    {
        var program = ReadRepoFile("src/SuavoAgent.Setup/Program.cs");
        var orchestrator = ReadRepoFile(
            "src/SuavoAgent.Setup/Gui/Services/InstalledCohortConfigurationOrchestrator.cs");
        var coordinator = ReadRepoFile(
            "src/SuavoAgent.Setup/Maintenance/NativeInstallCoordinator.cs");

        Assert.Contains("--connect-installed", program);
        Assert.Contains("InstalledCohortConfigurationTransaction", orchestrator);
        Assert.Contains("StartInstalledCohort", orchestrator);
        Assert.Contains("RestartPromotedInstalledCohort", orchestrator);
        Assert.DoesNotContain("BinaryDownloader", orchestrator);
        Assert.DoesNotContain("EnsureConfigured", orchestrator);
        Assert.DoesNotContain("RegisterUninstallEntry", orchestrator);
        Assert.DoesNotContain("RegisterServices", orchestrator);
        var installedStart = coordinator[
            coordinator.IndexOf("internal bool StartInstalledCohort", StringComparison.Ordinal)..coordinator.IndexOf("internal bool RestartPromotedCohort", StringComparison.Ordinal)];
        var installedRestart = coordinator[
            coordinator.IndexOf(
                "internal bool RestartPromotedInstalledCohort",
                StringComparison.Ordinal)..coordinator.IndexOf(
                "private static bool HasFreshActiveHeartbeat",
                StringComparison.Ordinal)];
        Assert.DoesNotContain("_services.EnsureConfigured", installedStart);
        Assert.DoesNotContain("_services.EnsureConfigured", installedRestart);
        Assert.Contains("StartInstalledCohort", installedRestart);
    }

    [Fact]
    public void Native_setup_uses_the_windows_platform_font_without_an_embedded_Inter_payload()
    {
        var project = ReadRepoFile("src/SuavoAgent.Setup/SuavoAgent.Setup.csproj");
        var program = ReadRepoFile("src/SuavoAgent.Setup/Program.cs");

        Assert.DoesNotContain("Avalonia.Fonts.Inter", project, StringComparison.Ordinal);
        Assert.DoesNotContain("WithInterFont", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Internal_publish_script_outputs_every_runtime_binary_required_by_field_release()
    {
        var source = ReadRepoFile("publish.ps1");

        Assert.Contains("SuavoAgent.Core", source);
        Assert.Contains("SuavoAgent.Broker", source);
        Assert.Contains("SuavoAgent.Helper", source);
        Assert.Contains("SuavoAgent.Watchdog", source);
        Assert.Contains("SuavoAgent.Setup", source);
        Assert.Contains("SuavoSetup.exe", source);
        Assert.Contains("SuavoAgent.Watchdog.exe", source);
    }

    [Fact]
    public void Release_probe_validates_no_phi_install_and_release_smoke_paths()
    {
        var source = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");

        Assert.Contains("ReleaseArtifact", source);
        Assert.Contains("Installed", source);
        Assert.Contains("SuavoAgent.Core.exe", source);
        Assert.Contains("SuavoAgent.Broker.exe", source);
        Assert.Contains("SuavoAgent.Helper.exe", source);
        Assert.Contains("SuavoAgent.Watchdog.exe", source);
        Assert.Contains("SuavoSetup.exe", source);
        Assert.Contains("$releaseBinaries = @($runtimeBinaries) + @(\"SuavoSetup.exe\")", source);
        Assert.Contains("$installedBinaries = @($runtimeBinaries) + @(\"SuavoAgent.Maintenance.exe\")", source);
        Assert.Contains("Test-Binaries", source);
        Assert.Contains("-Names $releaseBinaries", source);
        Assert.Contains("-Names $installedBinaries", source);
        Assert.Contains("-RequireValidSignature $true", source);
        Assert.Contains("Test-InstalledCohortIntegrity", source);
        Assert.Contains("install-state.json", source);
        Assert.Contains("binaries.manifest", source);
        Assert.Contains("manifest-hash:$binary", source);
        Assert.Contains("five_entries", source);
        Assert.Contains("Get-FileHash", source);
        Assert.Contains("maintenance:host", source);
        Assert.DoesNotContain("bootstrap:repair-path", source);
        Assert.Contains("heartbeat:config", source);
        Assert.Contains("Test-CrashLogMarkers", source);
        Assert.Contains("Test-HelperAttestation", source);
        Assert.Contains("\"crash-log:$name\"", source);
        Assert.Contains("\"helper:attestation\"", source);
        Assert.Contains("valuesRedacted = $true", source);
        Assert.Contains("\"broker-crash.log\"", source);
        Assert.Contains("\"watchdog-crash.log\"", source);
        Assert.Contains("sha256Prefix", source);
        Assert.Contains("valuesRedacted", source);
        Assert.DoesNotContain("Last 10 lines", source);
        Assert.DoesNotContain("Get-Content $latestLog", source);
    }

    [Fact]
    public void Broker_and_watchdog_do_not_autoload_core_appsettings()
    {
        var broker = ReadRepoFile("src/SuavoAgent.Broker/Program.cs");
        var sessionWatcher = ReadRepoFile("src/SuavoAgent.Broker/SessionWatcher.cs");
        var watchdog = ReadRepoFile("src/SuavoAgent.Watchdog/Program.cs");

        foreach (var source in new[] { broker, watchdog })
        {
            Assert.Contains("Host.CreateEmptyApplicationBuilder", source);
            Assert.Contains("ContentRootPath = exeDir", source);
            Assert.DoesNotContain("Host.CreateApplicationBuilder(", source);
            Assert.Contains("WriteTo.File", source);
        }

        Assert.Contains("broker-crash.log", broker);
        Assert.Contains("watchdog-crash.log", watchdog);
        Assert.Contains("RefreshHelperAttestations", sessionWatcher);
        Assert.Contains("TimeSpan.FromMinutes(1)", sessionWatcher);
        Assert.Contains("IpcPeerAttestationStore.Write", sessionWatcher);
    }

    [Fact]
    public void Service_logs_are_written_with_utf8_bom()
    {
        var sources = new[]
        {
            ReadRepoFile("src/SuavoAgent.Core/Program.cs"),
            ReadRepoFile("src/SuavoAgent.Broker/Program.cs"),
            ReadRepoFile("src/SuavoAgent.Watchdog/Program.cs"),
            ReadRepoFile("src/SuavoAgent.Helper/Program.cs"),
        };

        foreach (var source in sources)
        {
            Assert.Contains("WriteTo.File", source);
            Assert.Contains("encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)", source);
        }

        Assert.Contains(
            "File.AppendAllText(\n            Path.Combine(CoreCrashDir(), \"startup-crash.log\"),\n            line,\n            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true))",
            sources[0].Replace("\r\n", "\n"));
        Assert.Contains(
            "File.AppendAllText(\n            Path.Combine(BrokerCrashDir(), \"broker-crash.log\"),\n            line,\n            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true))",
            sources[1].Replace("\r\n", "\n"));
        Assert.Contains(
            "File.AppendAllText(\n            Path.Combine(WatchdogCrashDir(), \"watchdog-crash.log\"),\n            line,\n            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true))",
            sources[2].Replace("\r\n", "\n"));
    }

    [Fact]
    public void Workflows_gate_release_on_windows_release_probe()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var release = ReadRepoFile(".github/workflows/release.yml");
        var hotfix = ReadRepoFile(".github/workflows/hotfix.yml");
        var productionRelease = ReadRepoFile(
            ".github/workflows/production-release-signing.yml");
        var releaseBoundary = release + "\n" + productionRelease;
        var releaseGate = ReadRepoFile("docs/hardening/release-gate.md");
        var sharedBuild = ReadRepoFile("Directory.Build.props");

        Assert.Contains("verify-production-shell-boundary.sh", ci);
        Assert.Contains("production-shell-boundary", ci);
        Assert.Contains("Agent Merge Gate", ci);
        Assert.Contains("ota-manifest-compatibility", ci);
        Assert.Contains("verify-ota-manifest-compatibility.sh", ci);
        Assert.Contains(
            "needs: [production-shell-boundary, ota-manifest-compatibility, build-and-test, windows-coverage, coverage-report]",
            ci);
        Assert.Contains("Require agent checks before merge", ci);
        Assert.Contains("full-windows-suite", release);
        Assert.Contains("full-windows-suite", hotfix);
        Assert.Contains(
            "needs: [release-signing-preflight, production-shell-boundary, full-windows-suite]",
            release);
        Assert.Contains(
            "needs: [release-signing-preflight, production-shell-boundary, full-windows-suite]",
            hotfix);
        Assert.Contains("ref: ${{ github.sha }}", release);
        Assert.Contains("ref: ${{ github.sha }}", hotfix);
        Assert.Contains("<NuGetAudit>true</NuGetAudit>", sharedBuild);
        Assert.Contains("<NuGetAuditMode>all</NuGetAuditMode>", sharedBuild);
        Assert.Contains("NU1901;NU1902;NU1903;NU1904", sharedBuild);
        // Signing is gated by SIGNING_ENABLED, four eSigner cloud secrets,
        // an exact Authenticode certificate digest, and the independent
        // AWS-KMS signed-checksum/OTA manifest key;
        // see docs/signing.md for the activation walkthrough.
        Assert.Contains("Require eSigner cloud-signing configuration", release);
        Assert.Contains("SIGNING_ENABLED must be set to true", release);
        Assert.Contains("Missing release-signing secret(s)", release);
        Assert.Contains("ES_USERNAME", release);
        Assert.Contains("ES_PASSWORD", release);
        Assert.Contains("ES_CREDENTIAL_ID", release);
        Assert.Contains("ES_TOTP_SECRET", release);
        Assert.Contains("environment: suavoagent-production-signing", release);
        Assert.Contains("AUTHENTICODE_SIGNER_SHA256", release);
        Assert.Contains("OTA_KMS_KEY_ID", release);
        Assert.Contains("AWS_SIGNING_ROLE_ARN", release);
        Assert.Contains("id-token: write", release);
        Assert.Contains("aws-kms-sign-ecdsa-p256.sh", releaseBoundary);
        Assert.Contains("actions/attest-build-provenance@", releaseBoundary);
        Assert.DoesNotContain("SIGNING_KEY_PEM", releaseBoundary);
        Assert.Contains("scripts/esigner-codesign-hardened.sh", release);
        Assert.Contains("actions/setup-java@0f481fcb613427c0f801b606911222b5b6f3083a", release);
        Assert.Contains("verify-signature: true", release);
        Assert.DoesNotContain("sslcom/actions-codesigner@", release);
        Assert.DoesNotContain("sign_passthrough:", release);
        Assert.DoesNotContain("Upload as final (unsigned)", release);
        Assert.DoesNotContain("MODE=unsigned", release);
        // Self-hosted Yubikey runner is no longer required; cloud HSM signing
        // runs on ubuntu-latest. Guard against accidental regression.
        Assert.DoesNotContain("[self-hosted, windows, yubikey]", release);
        Assert.Contains("Cloud signing key", releaseGate);
        Assert.Contains("hardened eSigner wrapper", releaseGate);
        Assert.Contains("windows-release-smoke", release);
        Assert.Contains("Test-SuavoAgentReleaseProbe.ps1", release);
        Assert.Contains("-RequireAuthenticodeSignature", release);
        Assert.Contains("suavoagent-smoked-zip", release);
        Assert.Contains("windows-release-smoke.result == 'success'", release);
        Assert.Contains("Get-ChildItem -LiteralPath release -Filter *.exe -File", release);
        Assert.Contains("Compress-Archive -Path (Join-Path release '*') -DestinationPath $zip -Force", release);
        Assert.DoesNotContain("Compress-Archive -LiteralPath release\\*.exe", release);
        Assert.Contains("Native installer URL", releaseGate);
        Assert.Contains("unsigned passthrough is not a Queen/field release", releaseGate);
        Assert.Contains("checksums.sha256.sig", releaseGate);
        Assert.Contains("update-manifest-vX.Y.Z.sig", releaseGate);
        Assert.Contains("Production migration evidence", releaseGate);
        Assert.Contains("OTA_FULL_COHORT_MANIFEST: ${{ vars.OTA_FULL_COHORT_MANIFEST }}", release);
        Assert.Contains("compose-ota-manifest.sh", releaseBoundary);
        AssertNormalReleaseCallerBoundary(release);
    }

    [Fact]
    public void Hotfix_workflow_preserves_field_release_evidence_gate()
    {
        var hotfix = ReadRepoFile(".github/workflows/hotfix.yml");
        var productionRelease = ReadRepoFile(
            ".github/workflows/production-release-signing.yml");
        var releaseBoundary = hotfix + "\n" + productionRelease;

        Assert.Contains("contents: read", hotfix);
        // Hotfixes use the same eSigner cloud signing gate as releases.
        Assert.Contains("Require eSigner cloud-signing configuration", hotfix);
        Assert.Contains("Missing release-signing secret(s)", hotfix);
        Assert.Contains("ES_USERNAME", hotfix);
        Assert.Contains("ES_PASSWORD", hotfix);
        Assert.Contains("ES_CREDENTIAL_ID", hotfix);
        Assert.Contains("ES_TOTP_SECRET", hotfix);
        Assert.Contains("environment: suavoagent-production-signing", hotfix);
        Assert.Contains("AUTHENTICODE_SIGNER_SHA256", hotfix);
        Assert.Contains("OTA_KMS_KEY_ID", hotfix);
        Assert.Contains("AWS_SIGNING_ROLE_ARN", hotfix);
        Assert.Contains("id-token: write", hotfix);
        Assert.Contains("aws-kms-sign-ecdsa-p256.sh", releaseBoundary);
        Assert.Contains("actions/attest-build-provenance@", releaseBoundary);
        Assert.DoesNotContain("SIGNING_KEY_PEM", releaseBoundary);
        Assert.Contains("scripts/esigner-codesign-hardened.sh", hotfix);
        Assert.Contains("actions/setup-java@0f481fcb613427c0f801b606911222b5b6f3083a", hotfix);
        Assert.Contains("verify-signature: true", hotfix);
        Assert.DoesNotContain("sslcom/actions-codesigner@", hotfix);
        Assert.DoesNotContain("sign_passthrough:", hotfix);
        Assert.DoesNotContain("Upload as final (unsigned)", hotfix);
        Assert.DoesNotContain("MODE=unsigned", hotfix);
        Assert.DoesNotContain("[self-hosted, windows, yubikey]", hotfix);
        Assert.Contains("windows-release-smoke", hotfix);
        Assert.Contains("Test-SuavoAgentReleaseProbe.ps1", hotfix);
        Assert.Contains("-RequireAuthenticodeSignature", hotfix);
        Assert.Contains("suavoagent-hotfix-smoked-zip", hotfix);
        Assert.Contains("needs.windows-release-smoke.result == 'success'", hotfix);
        Assert.Contains("field-release-receipt.json", releaseBoundary);
        Assert.Contains("\"rollbackArtifact\"", releaseBoundary);
        Assert.Contains("track2QueenValidation", releaseBoundary);
        Assert.Contains("SuavoSetup.exe \"SuavoAgent-${VERSION}-win-x64.msi\"", productionRelease);
        Assert.Contains("SuavoAgent-Setup.exe suavoagent.spdx.json field-release-receipt.json", productionRelease);
        Assert.Contains("publication-paths", productionRelease);
        Assert.Contains("validate-release-assets", productionRelease);
        Assert.Contains("Authenticode, installer smoke", productionRelease);
        Assert.Contains("OTA_FULL_COHORT_MANIFEST: ${{ vars.OTA_FULL_COHORT_MANIFEST }}", hotfix);
        Assert.Contains("compose-ota-manifest.sh", releaseBoundary);
        Assert.DoesNotContain("Unsigned (SmartScreen warning expected)", hotfix);
        AssertNormalReleaseCallerBoundary(hotfix);
    }

    [Fact]
    public void Release_workflows_bind_setup_source_manifest_and_signed_rollback_receipts()
    {
        var productionRelease = ReadRepoFile(
            ".github/workflows/production-release-signing.yml");
        foreach (var caller in new[]
                 {
                     ReadRepoFile(".github/workflows/release.yml"),
                     ReadRepoFile(".github/workflows/hotfix.yml"),
                 })
        {
            var workflow = caller + "\n" + productionRelease;
            Assert.Contains("ARTIFACT=\"SuavoAgent-Setup.exe\"", workflow);
            Assert.Contains("\"artifact\": os.environ[\"ARTIFACT\"]", workflow);
            Assert.Contains("\"sourceCommit\": os.environ[\"GITHUB_SHA\"]", workflow);
            Assert.DoesNotContain("ROLLBACK_ARTIFACT=\"SuavoAgent-Setup-${ROLLBACK_TAG}-win-x64.exe\"", workflow);
            Assert.Contains("--pattern checksums.sha256.sig", workflow);
            Assert.Contains("--pattern field-release-receipt.json", workflow);
            Assert.Contains("--signature \"$rollback/checksums.sha256.sig\"", workflow);
            Assert.Contains("--bridge-release-tag \"$BRIDGE_TAG\"", workflow);
            Assert.Contains("--bridge-source-sha \"$BRIDGE_SOURCE_SHA\"", workflow);
            Assert.Contains("--bridge-receipt-sha256 \"$BRIDGE_RECEIPT_SHA\"", workflow);
            Assert.Contains("\"otaSigningKeyId\": \"ota-update-v2\"", workflow);
            Assert.Contains("--require-key-id ota-update-v2", workflow);
            Assert.Contains("assert-signing-public-key", workflow);
            Assert.Contains("select-release-rollback-tag.py", workflow);
            Assert.Contains("resolve-release-rollback-evidence.py", workflow);
            Assert.Contains("ROLLBACK_ARTIFACT=\"${evidence[0]}\"", workflow);
            Assert.Contains("sha256sum \"$rollback/$ROLLBACK_ARTIFACT\"", workflow);
            Assert.Contains("No signed stable rollback exists", workflow);
            Assert.Contains("update-manifest-${VERSION}.txt\" \"update-manifest-${VERSION}.sig\" > checksums.sha256", workflow);
            Assert.True(
                workflow.IndexOf("Generate exact full-cohort OTA manifest bytes", StringComparison.Ordinal) <
                workflow.IndexOf("Generate exact checksum payload", StringComparison.Ordinal));
            AssertNormalReleaseCallerBoundary(caller);
        }

        var probe = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");
        Assert.Contains("$expectedPublisher = \"MKM TECHNOLOGIES LLC\"", probe);
        Assert.Contains("$publisher -ceq $expectedPublisher", probe);
    }

    [Fact]
    public void Release_and_hotfix_publish_only_signed_native_customer_installers()
    {
        var productionRelease = ReadRepoFile(
            ".github/workflows/production-release-signing.yml");
        foreach (var caller in new[]
                 {
                     ReadRepoFile(".github/workflows/release.yml"),
                     ReadRepoFile(".github/workflows/hotfix.yml"),
                 })
        {
            var workflow = caller + "\n" + productionRelease;
            Assert.Contains("build_msi:", workflow);
            Assert.Contains("sign_msi:", workflow);
            Assert.Contains("build_bundle:", workflow);
            Assert.Contains("sign_bundle:", workflow);
            Assert.Contains("WIX_OSMF_EULA_ACCEPTED", workflow);
            Assert.Contains("VC_REDIST_X64_URL", workflow);
            Assert.Contains("cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b", workflow);
            Assert.Contains("-p:BuildProjectReferences=false", workflow);
            Assert.Contains("Get-AuthenticodeSignature", workflow);
            Assert.Contains("Test-InstallerAuthenticode.ps1", workflow);
            Assert.Contains("Invoke-SuavoAgentInstallerRehearsal.ps1", workflow);
            Assert.Contains("-InstallerKind Msi", workflow);
            Assert.Contains("-InstallerKind Bundle", workflow);
            Assert.Contains("-MsiPath", workflow);
            Assert.Contains("installer-rehearsal-evidence/", workflow);
            Assert.Contains("publication-paths", productionRelease);
            Assert.Contains("SuavoAgent-${VERSION}-win-x64.msi", productionRelease);
            Assert.Contains("SuavoAgent-Setup.exe", productionRelease);
            Assert.DoesNotContain("release/suavoagent-*-win-x64.zip", workflow);
            AssertNormalReleaseCallerBoundary(caller);
        }
    }

    [Fact]
    public void Ota_manifest_rollout_defaults_to_previous_stable_shape_and_gates_full_cohort()
    {
        var composer = ReadRepoFile("scripts/compose-ota-manifest.sh");
        var compatibility = ReadRepoFile("scripts/verify-ota-manifest-compatibility.sh");

        Assert.Contains("${OTA_FULL_COHORT_MANIFEST:-}", composer);
        Assert.Contains("== \"true\"", composer);
        Assert.Contains("SuavoAgent.Watchdog.exe", composer);
        Assert.Contains("SuavoSetup.exe", composer);
        Assert.Contains("default=11, full-cohort=13", compatibility);
        Assert.Contains("assert_previous_stable_parse", compatibility);
        Assert.Contains("OTA_FULL_COHORT_MANIFEST=TRUE", compatibility);
        Assert.Contains("OTA_FULL_COHORT_MANIFEST=true", compatibility);
    }

    [Fact]
    public void Release_probe_checks_appsettings_acl_for_dpapi_sealing()
    {
        var source = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");

        Assert.Contains("function Test-AppSettingsAcl", source);
        Assert.Contains("appsettings:acl-localservice-modify", source);
        Assert.Contains("appsettings:acl-networkservice-readonly", source);
        Assert.Contains("Test-AppSettingsAcl -Directory $InstallDir", source);
    }

    [Fact]
    public void Release_probe_verifies_installed_cloud_auth_without_printing_secrets()
    {
        var source = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");

        Assert.Contains("function Test-AgentCloudAuth", source);
        Assert.Contains("cloud-auth:agent-config", source);
        Assert.Contains("x-agent-api-key", source);
        Assert.Contains("ProtectedData]::Unprotect", source);
        Assert.Contains("Test-AgentCloudAuth -Directory $InstallDir", source);
        Assert.Contains("function Get-CloudAuthFailureDetail", source);
        Assert.Contains("GetResponseStream", source);
        Assert.Contains("http_$status`_$reason", source);
        Assert.Contains("valuesRedacted = $true", source);
        Assert.DoesNotContain("Write-Host $apiKey", source);
        Assert.DoesNotContain("Write-Host $body", source);
    }

    [Fact]
    public void Release_probe_reports_cloud_auth_health_evidence_without_printing_bodies()
    {
        var source = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");

        Assert.Contains("cloud-auth-health.json", source);
        Assert.Contains("heartbeat:cloud-auth-health", source);
        Assert.Contains("recoveryOutcome", source);
        Assert.Contains("restartRequested", source);
        Assert.DoesNotContain("rawBody", source);
    }

    [Fact]
    public void Release_probe_fails_missing_runtime_health_evidence_on_running_services()
    {
        var source = ReadRepoFile("scripts/Test-SuavoAgentReleaseProbe.ps1");

        Assert.Contains("config-sync-health.json", source);
        Assert.Contains("cloud-auth-health.json", source);
        Assert.Contains("watchdog-health.json", source);
        Assert.Contains("heartbeat:config-sync-health", source);
        Assert.Contains("heartbeat:cloud-auth-health", source);
        Assert.Contains("heartbeat:watchdog-health", source);
        Assert.Contains("missing_after_service_running", source);
        Assert.DoesNotContain("not_yet_written", source);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var candidate = Path.Combine(FindRepositoryRoot(), relativePath);
        return File.Exists(candidate)
            ? File.ReadAllText(candidate)
            : throw new FileNotFoundException(
                $"Could not locate repository file: {relativePath}");
    }

    private static void AssertNormalReleaseCallerBoundary(string caller)
    {
        const string marker = "\n  release:\n";
        var start = caller.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "normal release caller job is missing");
        var releaseJob = caller[start..];
        Assert.Contains(
            "needs: [build, sign_windows, sign_msi, sign_bundle, windows-release-smoke]",
            releaseJob);
        Assert.Contains("uses: ./.github/workflows/production-release-signing.yml", releaseJob);
        Assert.Contains("version: ${{ inputs.version }}", releaseJob);
        Assert.Contains("actions: read", releaseJob);
        Assert.Contains("attestations: write", releaseJob);
        Assert.Contains("contents: write", releaseJob);
        Assert.Contains("id-token: write", releaseJob);
        Assert.DoesNotContain("${{ vars.", releaseJob);
        Assert.DoesNotContain("${{ secrets.", releaseJob);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SuavoAgent.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the SuavoAgent repository root.");
    }
}
