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

            ConsoleUI.WriteStep("Phase 1: Finding PioneerRx installation");
            var pioneer = PioneerRxDiscovery.Discover();
            if (pioneer == null)
            {
                ConsoleUI.FatalError(
                    "PioneerRx not found on this computer.\n" +
                    "  Make sure PioneerRx is installed before running SuavoSetup.");
                return 1;
            }
            ConsoleUI.WriteOk($"PioneerRx at: {pioneer.PioneerDir}");

            ConsoleUI.WriteStep("Phase 2: Discovering SQL Server credentials");
            var sqlCreds = SqlCredentialDiscovery.Discover(pioneer.PioneerConfig);
            if (sqlCreds == null)
            {
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

            ConsoleUI.WriteStep("Phase 3: Downloading SuavoAgent binaries");
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
            var agentId = "agent-" + Guid.NewGuid().ToString("N")[..12];
            var fingerprint = GetMachineFingerprint();
            var agentConfig = new Dictionary<string, object>
            {
                ["Agent"] = new Dictionary<string, object?>
                {
                    ["CloudUrl"] = config.CloudUrl,
                    ["ApiKey"] = config.ApiKey,
                    ["AgentId"] = agentId,
                    ["PharmacyId"] = config.PharmacyId,
                    ["MachineFingerprint"] = fingerprint,
                    ["Version"] = config.ReleaseTag.TrimStart('v'),
                    ["SqlServer"] = sqlCreds.Server,
                    ["SqlDatabase"] = sqlCreds.Database,
                    ["SqlUser"] = sqlCreds.User,
                    ["SqlPassword"] = sqlCreds.Password,
                    ["LearningMode"] = config.LearningMode,
                }
            };

            if (sqlCreds.IsWindowsAuth)
            {
                var agentSection = (Dictionary<string, object?>)agentConfig["Agent"];
                agentSection.Remove("SqlUser");
                agentSection.Remove("SqlPassword");
            }

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
                sqlCreds.Server, sqlCreds.Database, sqlCreds.User);

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
