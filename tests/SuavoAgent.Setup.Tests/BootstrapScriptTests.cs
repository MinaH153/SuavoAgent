using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class BootstrapScriptTests
{
    [Fact]
    public void Bootstrap_reuses_verified_release_cache_for_binaries()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("'download-cache'", source);
        Assert.Contains("$downloadCacheRoot = Join-Path $dataDir \"download-cache\"", source);
        Assert.Contains("$releaseCacheDir = Join-Path $downloadCacheRoot $ReleaseTag", source);
        Assert.Contains("Test-VerifiedCachedBinary", source);
        Assert.Contains("(Get-FileHash -Path $cachedPath -Algorithm SHA256).Hash.ToLower()", source);
        Assert.Contains("$cachedHash -eq $ExpectedHash", source);
        Assert.Contains("Copy-Item -Path $cachedBinary -Destination $dst -Force", source);
        Assert.Contains("Copy-Item -Path $dst -Destination $cacheDst -Force", source);
    }

    [Fact]
    public void Bootstrap_reuses_existing_registration_and_sql_settings_before_cleanup()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("function Read-ExistingAgentConfig", source);
        Assert.Contains("$configPath = Join-Path $installDir \"appsettings.json\"", source);
        Assert.Contains("$previousAgentConfig = Read-ExistingAgentConfig", source);
        Assert.Contains("if (-not $ApiKey -and $previousAgentConfig.ApiKey)", source);
        Assert.Contains("if (-not $PharmacyId -and $previousAgentConfig.PharmacyId)", source);
        Assert.Contains("if (-not $AgentId -and $previousAgentConfig.AgentId)", source);
        Assert.Contains("Reusing existing cloud registration", source);
        Assert.Contains("if ($previousAgentConfig -and $previousAgentConfig.SqlServer)", source);
        Assert.Contains("$reusePreviousSqlSettings = $true", source);
        Assert.Contains("Reusing previous SQL settings", source);
        Assert.Contains("if (-not $reusePreviousSqlSettings -and -not $sqlUser -and -not $discoveredConnStr)", source);
    }

    [Fact]
    public void Bootstrap_install_token_replaces_stale_existing_registration()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("$hasProvidedInstallToken = -not [string]::IsNullOrWhiteSpace($installTokenPlain)", source);
        Assert.Contains("if ($previousAgentConfig -and -not $hasProvidedInstallToken)", source);
        Assert.Contains("Fresh install token provided; existing cloud registration will be replaced", source);
        Assert.Contains("if ($installTokenPlain) {", source);
        Assert.DoesNotContain("if ($installTokenPlain -and -not $ApiKey)", source);
    }

    [Fact]
    public void Bootstrap_forbids_local_only_cloud_identity_fallback()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("Cloud registration is required but did not produce a complete agent identity", source);
        Assert.Contains("Local-only fallback IDs are not allowed for production installs", source);
        Assert.Contains("Cloud registration did not produce ApiKey, AgentId, and PharmacyId", source);
        Assert.DoesNotContain("\"agent-\" + ([guid]::NewGuid()", source);
        Assert.DoesNotContain("generated local fallback", source);
    }

    [Fact]
    public void Legacy_setup_installer_also_requires_cloud_agent_identity()
    {
        var consoleInstaller = ReadRepoFile("src/SuavoAgent.Setup/ConsoleInstaller.cs");
        var setupConfig = ReadRepoFile("src/SuavoAgent.Setup/SetupConfig.cs");
        var guiInstaller = ReadRepoFile("src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs");

        Assert.Contains("var agentId = config.AgentId;", consoleInstaller);
        Assert.DoesNotContain("\"agent-\" + Guid.NewGuid()", consoleInstaller);
        Assert.Contains("AgentId must be a cloud UUID", setupConfig);
        Assert.Contains("Legacy local setup.json installs are disabled", setupConfig);
        Assert.Contains("_ctx.AgentId ??= _ctx.Config.AgentId", guiInstaller);
        Assert.DoesNotContain("\"agent-\" + Guid.NewGuid()", guiInstaller);
    }

    [Fact]
    public void Bootstrap_preserves_cloud_auth_verify_stage_for_install_telemetry()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("\"cloud_auth_verify\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"cloud_auth_verify\"", source);
    }

    [Fact]
    public void Bootstrap_allows_core_to_dpapi_seal_appsettings_without_broadening_broker_access()
    {
        var source = ReadBootstrapScript();

        Assert.Contains(
            "\"NT AUTHORITY\\LOCAL SERVICE\", \"Modify\", \"None\", \"None\", \"Allow\"",
            source);
        Assert.Contains(
            "\"NT AUTHORITY\\NETWORK SERVICE\", \"Read\", \"None\", \"None\", \"Allow\"",
            source);
        Assert.Contains("LocalService M + NetworkService R", source);
    }

    [Fact]
    public void Bootstrap_uses_windows_powershell_51_compatible_pipeline_helpers()
    {
        var source = ReadBootstrapScript();

        Assert.DoesNotContain("Join-String", source);
        Assert.Contains("-join ', '", source);
    }

    [Fact]
    public void Bootstrap_requires_admin_before_touching_existing_install()
    {
        var source = ReadBootstrapScript();

        var adminCheckIndex = source.IndexOf("# -- Require Admin before touching existing install --", StringComparison.Ordinal);
        var previousConfigIndex = source.IndexOf("$previousAgentConfig = Read-ExistingAgentConfig", StringComparison.Ordinal);

        Assert.NotEqual(-1, adminCheckIndex);
        Assert.NotEqual(-1, previousConfigIndex);
        Assert.True(adminCheckIndex < previousConfigIndex);
    }

    [Fact]
    public void Publish_script_outputs_every_runtime_binary_required_by_bootstrap()
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
    public void Local_install_script_installs_watchdog_and_fails_when_services_are_not_running()
    {
        var source = ReadRepoFile("install.ps1");

        Assert.Contains("$serviceWatchdog = \"SuavoAgent.Watchdog\"", source);
        Assert.Contains("foreach ($sub in @(\"Core\", \"Broker\", \"Helper\", \"Watchdog\", \"Setup\"))", source);
        Assert.Contains("\"SuavoSetup.exe\"", source);
        Assert.Contains("sc.exe create $serviceWatchdog", source);
        Assert.Contains("Start-Service $serviceWatchdog", source);
        Assert.Contains("throw \"$svc failed to reach Running", source);
        Assert.Contains("Run-SuavoAgentSmokeStrict", source);
    }

    [Fact]
    public void Bootstrap_fails_install_when_required_services_do_not_start()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("$installFailed = $false", source);
        Assert.Contains("$installFailed = $true", source);
        Assert.Contains("if ($installFailed)", source);
        Assert.Contains("SuavoAgent install did not pass startup verification", source);
        Assert.Contains("exit 1", source);
    }

    [Fact]
    public void Bootstrap_redacts_postmortem_logs_before_desktop_dump()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("function Redact-PostmortemText", source);
        Assert.Contains("[REDACTED_USER]", source);
        Assert.Contains("[REDACTED_SSN]", source);
        Assert.Contains("[REDACTED_RX]", source);
        Assert.Contains("[REDACTED_PHONE]", source);
        Assert.Contains("[REDACTED_EMAIL]", source);
        Assert.Contains("[REDACTED_MRN]", source);
        Assert.Contains("[REDACTED_DATE]", source);
        Assert.Contains("Redact-PostmortemText -Text ((Get-Content $crashLog -Raw).Trim())", source);
        Assert.Contains("Redact-PostmortemText -Text ((Get-Content $brokerCrashLog -Raw).Trim())", source);
        Assert.Contains("Redact-PostmortemText -Text ((Get-Content $brokerLatest.FullName -Tail 80 | Out-String).Trim())", source);
        Assert.Contains("Redact-PostmortemText -Text ((Get-Content $latest.FullName -Tail 80 | Out-String).Trim())", source);
    }

    [Fact]
    public void Bootstrap_posts_redacted_install_telemetry_on_failure_paths()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("function Send-InstallTelemetry", source);
        Assert.Contains("function Redact-InstallTelemetryText", source);
        Assert.Contains("function Normalize-InstallTelemetryStage", source);
        Assert.Contains("$script:InstallTelemetryToken", source);
        Assert.Contains("$script:InstallTelemetryToken = $installTokenPlain", source);
        Assert.Contains("Invoke-RestMethod `", source);
        Assert.Contains("-Uri \"$cloudBase/api/agent/install-telemetry\" `", source);
        Assert.Contains("install_token = $token", source);
        Assert.Contains("stdout_tail", source);
        Assert.Contains("agent_version_target = $ReleaseTag.TrimStart('v')", source);
        Assert.Contains("machine_name = $env:COMPUTERNAME", source);
        Assert.Contains("os_version = Get-InstallTelemetryOsVersion", source);
        Assert.Contains("metadata = $safeMetadata", source);
        Assert.Contains("[REDACTED_INSTALL_TOKEN]", source);
        Assert.Contains("[REDACTED_SECRET]", source);
        Assert.Contains("Registration failed: $($_.Exception.Message)", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"cloud_register\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"download_binary\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"download_hash\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"service_start_core\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"service_start_broker\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"service_start_watchdog\"", source);
        Assert.Contains("Send-InstallTelemetry -Stage \"service_verify\"", source);
        Assert.Contains("SuavoAgent install did not pass startup verification", source);
        Assert.DoesNotContain("Get-Content -Path $transcriptPath", source);
        Assert.DoesNotContain("Get-Content $transcriptPath", source);
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
        Assert.Contains("Get-FileHash", source);
        Assert.Contains("bootstrap:repair-path", source);
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
    public void Service_logs_are_written_with_utf8_bom_for_windows_powershell_51()
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
    public void Operator_powershell_scripts_keep_status_output_ascii_safe()
    {
        foreach (var path in new[]
        {
            "suavo-check.ps1",
            "install.ps1",
            "publish.ps1",
            "scripts/vm-validate.ps1",
        })
        {
            var source = ReadRepoFile(path);
            Assert.DoesNotContain(" — ", source);
        }
    }

    [Fact]
    public void Workflows_gate_release_on_windows_release_probe()
    {
        var ci = ReadRepoFile(".github/workflows/ci.yml");
        var release = ReadRepoFile(".github/workflows/release.yml");
        var releaseGate = ReadRepoFile("docs/hardening/release-gate.md");

        Assert.Contains("Test-SuavoAgentReleaseProbe.ps1", ci);
        Assert.Contains("ReleaseArtifact", ci);
        Assert.Contains("Agent Merge Gate", ci);
        Assert.Contains("needs: [bootstrap-windows-smoke, build-and-test]", ci);
        Assert.Contains("Require agent checks before merge", ci);
        // Signing is gated by SIGNING_ENABLED + four eSigner cloud secrets;
        // see docs/signing.md for the activation walkthrough.
        Assert.Contains("Require eSigner cloud-signing configuration", release);
        Assert.Contains("SIGNING_ENABLED must be set to true", release);
        Assert.Contains("Missing eSigner cloud-signing secret(s)", release);
        Assert.Contains("ES_USERNAME", release);
        Assert.Contains("ES_PASSWORD", release);
        Assert.Contains("ES_CREDENTIAL_ID", release);
        Assert.Contains("ES_TOTP_SECRET", release);
        Assert.Contains("sslcom/actions-codesigner@develop", release);
        Assert.DoesNotContain("sign_passthrough:", release);
        Assert.DoesNotContain("Upload as final (unsigned)", release);
        Assert.DoesNotContain("MODE=unsigned", release);
        // Self-hosted Yubikey runner is no longer required; cloud HSM signing
        // runs on ubuntu-latest. Guard against accidental regression.
        Assert.DoesNotContain("[self-hosted, windows, yubikey]", release);
        Assert.Contains("Cloud signing key", releaseGate);
        Assert.Contains("sslcom/actions-codesigner", releaseGate);
        Assert.Contains("windows-release-smoke", release);
        Assert.Contains("Test-SuavoAgentReleaseProbe.ps1", release);
        Assert.Contains("-RequireAuthenticodeSignature", release);
        Assert.Contains("suavoagent-smoked-zip", release);
        Assert.Contains("windows-release-smoke.result == 'success'", release);
        Assert.Contains("Get-ChildItem -LiteralPath release -Filter *.exe -File", release);
        Assert.Contains("| Compress-Archive -DestinationPath $zip -Force", release);
        Assert.DoesNotContain("Compress-Archive -LiteralPath release\\*.exe", release);
        Assert.Contains("Installer ZIP URL", releaseGate);
        Assert.Contains("unsigned passthrough is not a Queen/field release", releaseGate);
        Assert.Contains("checksums.sha256.sig", releaseGate);
        Assert.Contains("update-manifest-vX.Y.Z.sig", releaseGate);
        Assert.Contains("Production migration evidence", releaseGate);
    }

    [Fact]
    public void Hotfix_workflow_preserves_field_release_evidence_gate()
    {
        var hotfix = ReadRepoFile(".github/workflows/hotfix.yml");

        Assert.Contains("actions: read", hotfix);
        // Hotfixes use the same eSigner cloud signing gate as releases.
        Assert.Contains("Require eSigner cloud-signing configuration", hotfix);
        Assert.Contains("Missing eSigner cloud-signing secret(s)", hotfix);
        Assert.Contains("ES_USERNAME", hotfix);
        Assert.Contains("ES_PASSWORD", hotfix);
        Assert.Contains("ES_CREDENTIAL_ID", hotfix);
        Assert.Contains("ES_TOTP_SECRET", hotfix);
        Assert.Contains("sslcom/actions-codesigner@develop", hotfix);
        Assert.DoesNotContain("sign_passthrough:", hotfix);
        Assert.DoesNotContain("Upload as final (unsigned)", hotfix);
        Assert.DoesNotContain("MODE=unsigned", hotfix);
        Assert.DoesNotContain("[self-hosted, windows, yubikey]", hotfix);
        Assert.Contains("windows-release-smoke", hotfix);
        Assert.Contains("Test-SuavoAgentReleaseProbe.ps1", hotfix);
        Assert.Contains("-RequireAuthenticodeSignature", hotfix);
        Assert.Contains("suavoagent-hotfix-smoked-zip", hotfix);
        Assert.Contains("needs.windows-release-smoke.result == 'success'", hotfix);
        Assert.Contains("field-release-receipt.json", hotfix);
        Assert.Contains("\"rollbackArtifact\"", hotfix);
        Assert.Contains("track2QueenValidation", hotfix);
        Assert.Contains("SuavoSetup.exe suavoagent-${{ inputs.version }}-win-x64.zip field-release-receipt.json", hotfix);
        Assert.Contains("release/suavoagent-*-win-x64.zip", hotfix);
        Assert.Contains("release/field-release-receipt.json", hotfix);
        Assert.Contains("**Authenticode:** Signed (SSL.com eSigner Cloud EV).", hotfix);
        Assert.DoesNotContain("Unsigned (SmartScreen warning expected)", hotfix);
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

    [Fact]
    public void Bootstrap_verifies_cloud_hmac_before_printing_install_complete()
    {
        var source = ReadBootstrapScript();

        Assert.Contains("function Test-AgentCloudAuth", source);
        Assert.Contains("cloud_auth_verify", source);
        Assert.Contains("Cloud HMAC auth accepted", source);
        Assert.Contains("At least one required service or cloud-auth check failed.", source);
        Assert.Contains("Get-AgentHmacSignature -ApiKey $apiKey -Timestamp $timestamp -Body \"\"", source);
        Assert.Contains("Get-CloudAuthFailureDetail", source);
        Assert.DoesNotContain("Write-Host $apiKey", source);
        Assert.DoesNotContain("Write-Host $body", source);

        var cloudVerifyIndex = source.IndexOf("Verify cloud authentication before declaring install complete", StringComparison.Ordinal);
        var failureIndex = source.IndexOf("SuavoAgent install did not pass startup verification", StringComparison.Ordinal);
        var completeIndex = source.IndexOf("Installation Complete", StringComparison.Ordinal);

        Assert.NotEqual(-1, cloudVerifyIndex);
        Assert.NotEqual(-1, failureIndex);
        Assert.NotEqual(-1, completeIndex);
        Assert.True(cloudVerifyIndex < failureIndex);
        Assert.True(cloudVerifyIndex < completeIndex);
    }

    private static string ReadBootstrapScript()
    {
        return ReadRepoFile("bootstrap.ps1");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate bootstrap.ps1 from test output directory.");
    }
}
