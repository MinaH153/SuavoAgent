import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "Invoke-SuavoAgentInstallerRehearsal.ps1"


class InstallerRehearsalScriptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SCRIPT.read_text(encoding="utf-8")

    def test_is_bounded_phi_negative_and_requires_disposable_fresh_host(self):
        self.assertLess(len(self.source.splitlines()), 800)
        self.assertIn("phiClassification = 'phi-negative'", self.source)
        self.assertIn("preexisting_service", self.source)
        self.assertIn("preexisting_install_directory", self.source)
        self.assertIn("preexisting_marker_transaction_journal", self.source)
        self.assertIn("preexisting_service_transaction_journal", self.source)
        self.assertIn("preexisting_transaction_active_token", self.source)
        self.assertNotIn("$env:COMPUTERNAME", self.source)
        self.assertNotIn("$env:USERNAME", self.source)
        self.assertNotIn("Exception.Message", self.source)

    def test_verifies_both_bundle_and_exact_msi_identity(self):
        self.assertIn("Test-InstallerAuthenticode.ps1", self.source)
        self.assertIn("installerArtifactSha256", self.source)
        self.assertIn("msiArtifactSha256", self.source)
        self.assertIn("release1_marker:msi_hash", self.source)
        self.assertIn("release1_marker:maintenance_hash", self.source)
        self.assertIn("release1_marker:product_code", self.source)

    def test_refuses_exact_legacy_state_without_mutating_it(self):
        for marker in (
            "Test-ExactLegacyBrokerPath",
            "legacy_process_present",
            "legacy_process_path_unavailable",
            "legacy_shortcut_present",
            "legacy_shortcut_probe_failed",
            "legacy_product_registry_present",
            "legacy_installed_product_present",
            "legacy_product_directory_present",
            "legacy_product_scheduled_task_present",
            "SuavoSelfUninstall",
            "preexisting_service",
        ):
            self.assertIn(marker, self.source)
        refusal_helpers = self.source[
            self.source.index("function Test-ExactLegacyBrokerPath") :
            self.source.index("function Assert-InstalledState")
        ]
        for mutator in ("Stop-Process", "Remove-Item", ".Kill(", "File]::Delete"):
            self.assertNotIn(mutator, refusal_helpers)
        self.assertNotIn("msi-legacy-interactive.rollback", self.source)
        self.assertNotIn("--msi-retire-legacy-interactive", self.source)

    def test_proves_installed_cohort_services_and_manifest(self):
        for name in (
            "SuavoAgent.Core.exe",
            "SuavoAgent.Broker.exe",
            "SuavoAgent.Helper.exe",
            "SuavoAgent.Watchdog.exe",
            "SuavoAgent.Maintenance.exe",
        ):
            self.assertIn(name, self.source)
        for marker in (
            "binaries.manifest",
            "DelayedAutostart",
            "ServiceSidType",
            "FailureActions",
            "DependOnService",
            "Wait-ForServiceState $name 'Running'",
        ):
            self.assertIn(marker, self.source)

    def test_repair_restores_real_damage_without_minting_install_proof(self):
        self.assertIn("Remove-Item -LiteralPath", self.source)
        self.assertIn("'SuavoAgent.Helper.exe'", self.source)
        self.assertIn("'SuavoAgent.Maintenance.exe'", self.source)
        self.assertIn("repair:not_restored:$name", self.source)
        self.assertIn("Invoke-InstallOrRepair $true", self.source)
        self.assertIn("repair:marker_changed", self.source)
        self.assertIn("repair:transaction_changed", self.source)
        self.assertIn("repair-verified-without-new-install-proof", self.source)

    def test_each_install_must_mint_fresh_bounded_marker_even_when_data_is_retained(self):
        for marker in (
            "$preInstallMarkerSha256",
            "$preInstallMarkerTransactionId",
            "$markerFreshnessDeadlineUtc = $scriptStartedAtUtc.AddMinutes(30)",
            "fresh_install:completion_before_rehearsal",
            "fresh_install:completion_after_deadline",
            "fresh_install:completion_in_future",
            "fresh_install:marker_hash_reused",
            "fresh_install:transaction_reused",
        ):
            self.assertIn(marker, self.source)
        self.assertLess(
            self.source.index("$preInstallMarkerSha256 = Get-Sha256 $markerPath"),
            self.source.index("Invoke-InstallOrRepair $false"),
        )
        self.assertLess(
            self.source.index("fresh_install:marker_hash_reused"),
            self.source.index("Invoke-InstallOrRepair $true"),
        )

    def test_uninstall_removes_code_and_services_but_preserves_data(self):
        self.assertIn("'/uninstall'", self.source)
        self.assertIn("uninstall:service_remained", self.source)
        self.assertIn("uninstall:program_files_remained", self.source)
        self.assertIn("uninstall:regulated_data_not_preserved", self.source)
        self.assertIn("RandomNumberGenerator]::GetBytes(32)", self.source)
        self.assertIn("retainedDataSentinelSha256", self.source)
        self.assertIn("uninstall:retention_sentinel", self.source)
        self.assertIn("uninstall:retention_sentinel_changed", self.source)
        self.assertNotIn("retentionSentinelPath = $retentionSentinelPath", self.source)


if __name__ == "__main__":
    unittest.main()
