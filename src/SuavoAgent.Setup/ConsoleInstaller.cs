using System.Text.Json;

namespace SuavoAgent.Setup;

/// <summary>
/// Console (headless) installer path. Runs the 6-phase flow non-interactively for
/// fleet-deploy scripted installs (PowerShell bootstrap → setup.json + --console flag).
///
/// The GUI path (see <see cref="Gui.App"/>) wraps the same underlying phases but
/// surfaces them through Avalonia screens with operator consent capture.
/// </summary>
internal static class ConsoleInstaller
{
    private const string InstallDir = @"C:\Program Files\Suavo\Agent";
    private const string DataDir = @"C:\ProgramData\SuavoAgent";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var config = SetupConfig.Load(args);
            if (config == null)
            {
                ConsoleUI.WriteFail("No configuration found.");
                Console.WriteLine();
                Console.WriteLine("  SuavoSetup needs a setup.json file in the same folder,");
                Console.WriteLine("  or command-line arguments:");
                Console.WriteLine();
                Console.WriteLine("  SuavoSetup.exe --pharmacy-id PH123 --api-key sk_xxx");
                Console.WriteLine();
                Console.WriteLine("  Download the installer from your pharmacy dashboard");
                Console.WriteLine("  at https://suavollc.com — it includes the setup.json.");
                ConsoleUI.WaitForExit();
                return 1;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(config.ReleaseTag, @"^v\d+\.\d+\.\d+(-[a-zA-Z0-9]+)?$"))
            {
                ConsoleUI.WriteFail($"Invalid release tag format: {config.ReleaseTag}");
                ConsoleUI.WaitForExit();
                return 1;
            }

            ConsoleUI.Banner(config.PharmacyId, config.ReleaseTag);

            // PioneerRx is an OPTIONAL connector, never an install precondition. Many
            // pharmacies/businesses don't run it — SuavoAgent installs and runs as a general
            // desktop agent regardless, and self-heals the PMS connection if PioneerRx appears.
            // (Parity with the GUI installer, which already treats this as deferred.)
            ConsoleUI.WriteStep("Phase 1: Finding PioneerRx installation");
            var pioneer = PioneerRxDiscovery.Discover();
            if (pioneer == null)
                ConsoleUI.WriteWarn(
                    "PioneerRx not detected — installing in no-PMS mode; SQL auto-config " +
                    "deferred (the agent self-heals when PioneerRx appears).");
            else
                ConsoleUI.WriteOk($"PioneerRx at: {pioneer.PioneerDir}");

            SqlCredentialDiscovery.SqlCredentials? sqlCreds = null;
            if (pioneer != null)
            {
                ConsoleUI.WriteStep("Phase 2: Discovering SQL Server credentials");
                // TryAutoDiscover, NOT Discover(): Discover() falls through to PromptManual's
                // Console.ReadLine, which hangs a headless/fleet (--console/--silent) install.
                sqlCreds = SqlCredentialDiscovery.TryAutoDiscover(pioneer.PioneerConfig);
                if (sqlCreds == null)
                {
                    // PioneerRx present but its SQL is undiscoverable IS a genuine misconfig.
                    ConsoleUI.FatalError(
                        "Could not discover SQL Server credentials.\n" +
                        "  Contact Suavo support for manual configuration.");
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine();
                Console.WriteLine($"  Server:   {sqlCreds.Server}");
                Console.WriteLine($"  Database: {sqlCreds.Database}");
                Console.WriteLine($"  Auth:     {(sqlCreds.IsWindowsAuth ? "Windows" : $"SQL ({sqlCreds.User})")}");
                Console.ResetColor();
            }
            else
            {
                ConsoleUI.WriteInfo("Phase 2 skipped (no PMS) — SQL credentials deferred.");
            }

            ConsoleUI.WriteStep("Phase 3: Downloading SuavoAgent binaries");
            ConsoleUI.WriteInfo("Stopping any running SuavoAgent services before download...");
            ServiceInstaller.StopServices();
            var downloadSuccess = await BinaryDownloader.DownloadAndVerifyAsync(
                config.ReleaseTag, InstallDir);
            if (!downloadSuccess)
            {
                ConsoleUI.FatalError(
                    "Binary download or verification failed.\n" +
                    "  Check your internet connection and try again.");
                return 1;
            }

            ConsoleUI.WriteStep("Phase 4: Writing configuration");
            var agentId = config.AgentId;
            var fingerprint = GetMachineFingerprint();
            var agentConfig = BuildAgentConfig(config, agentId, fingerprint, pioneer != null, sqlCreds);

            var configJson = JsonSerializer.Serialize(agentConfig, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            // C-4: Create dir → lockdown ACL → THEN write config (credentials never on disk unprotected)
            var configPath = Path.Combine(InstallDir, "appsettings.json");
            Directory.CreateDirectory(InstallDir);
            ServiceInstaller.LockdownDirectoryAcl(InstallDir);
            File.WriteAllText(configPath, configJson);
            ConsoleUI.WriteOk($"appsettings.json written to {InstallDir} (ACL applied first)");

            // Regenerate the Broker's integrity manifest from the just-placed binaries, BEFORE the
            // services start. Without this, an install/reinstall over an existing agent leaves a stale
            // binaries.manifest -> the Broker rejects the (new) Helper as tampered and exits -> the
            // agent comes up "online" but BLIND (no Helper, no vision/UIA), with no crash. The OTA path
            // already does this; the GUI installer did not. (Live brick on Mina's box 2026-06-05.)
            BinaryDownloader.WriteBinariesManifest(InstallDir);

            ConsoleUI.WriteStep("Phase 5: Installing Windows services");
            var serviceSuccess = ServiceInstaller.InstallAndStart(InstallDir, DataDir);
            if (!serviceSuccess)
            {
                ConsoleUI.WriteWarn("Services registered but may need a manual start.");
                ConsoleUI.WriteInfo("Run: sc.exe start SuavoAgent.Core");
            }

            ConsoleUI.WriteStep("Phase 6: Verification complete");
            ConsoleUI.CompletionSummary(
                InstallDir, DataDir, agentId,
                sqlCreds?.Server ?? "(deferred)", sqlCreds?.Database ?? "(deferred)", sqlCreds?.User);

            // L-1: Delete setup.json after successful install (contains ApiKey)
            var setupJsonPath = Path.Combine(AppContext.BaseDirectory, "setup.json");
            if (File.Exists(setupJsonPath))
            {
                try
                {
                    File.Delete(setupJsonPath);
                    ConsoleUI.WriteOk("setup.json deleted (credentials no longer on disk)");
                }
                catch (Exception ex)
                {
                    ConsoleUI.WriteWarn($"Could not delete setup.json: {ex.Message}");
                }
            }

            foreach (var file in Directory.GetFiles(InstallDir, "*.exe"))
            {
                var sizeMb = new FileInfo(file).Length / (1024.0 * 1024.0);
                ConsoleUI.WriteInfo($"{Path.GetFileName(file)} - {sizeMb:F1} MB");
            }

            ConsoleUI.WaitForExit();
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleUI.FatalError($"Unexpected error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Builds the appsettings.json "Agent" section. SQL is an OPTIONAL connector: its keys are
    /// written ONLY when SQL credentials were discovered (PioneerRx present). A no-PMS install
    /// omits them entirely so the runtime starts in no-PMS mode and self-heals if PioneerRx later
    /// appears. <c>PmsDetected</c> records the deliberate no-PMS choice so the cloud can tell it
    /// apart from a broken install. Pure + side-effect-free so it is unit-testable; mirrors the
    /// GUI <c>InstallOrchestrator.BuildAppSettings</c>.
    /// </summary>
    internal static Dictionary<string, object> BuildAgentConfig(
        SetupConfig config, string agentId, string fingerprint,
        bool pmsDetected, SqlCredentialDiscovery.SqlCredentials? sqlCreds)
    {
        var agent = new Dictionary<string, object?>
        {
            ["CloudUrl"] = config.CloudUrl,
            ["ApiKey"] = config.ApiKey,
            ["AgentId"] = agentId,
            ["PharmacyId"] = config.PharmacyId,
            ["MachineFingerprint"] = fingerprint,
            ["Version"] = config.ReleaseTag.TrimStart('v'),
            ["LearningMode"] = config.LearningMode,
            ["PmsDetected"] = pmsDetected,
        };

        if (sqlCreds != null)
        {
            agent["SqlServer"] = sqlCreds.Server;
            agent["SqlDatabase"] = sqlCreds.Database;
            // Windows auth → no user/password keys (IsWindowsAuth == User is null).
            if (!sqlCreds.IsWindowsAuth)
            {
                agent["SqlUser"] = sqlCreds.User;
                agent["SqlPassword"] = sqlCreds.Password;
            }
        }

        return new Dictionary<string, object> { ["Agent"] = agent };
    }

    // M-4: stable machine fingerprint via registry MachineGuid (replaces deprecated wmic).
    private static string GetMachineFingerprint()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(guid))
                return guid;
        }
        catch
        {
            // Registry unavailable — fall back to machine name.
        }
        return Environment.MachineName;
    }
}
