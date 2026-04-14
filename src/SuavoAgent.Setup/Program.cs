using System.Text.Json;
using SuavoAgent.Setup;

const string InstallDir = @"C:\Program Files\Suavo\Agent";
const string DataDir = @"C:\ProgramData\SuavoAgent";

try
{
    // ── Load Configuration ──
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

    // Validate release tag format
    if (!System.Text.RegularExpressions.Regex.IsMatch(config.ReleaseTag, @"^v\d+\.\d+\.\d+(-[a-zA-Z0-9]+)?$"))
    {
        ConsoleUI.WriteFail($"Invalid release tag format: {config.ReleaseTag}");
        ConsoleUI.WaitForExit();
        return 1;
    }

    ConsoleUI.Banner(config.PharmacyId, config.ReleaseTag);

    // ════════════════════════════════════════════
    // PHASE 1: Find PioneerRx
    // ════════════════════════════════════════════
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

    // ════════════════════════════════════════════
    // PHASE 2: Discover SQL Credentials
    // ════════════════════════════════════════════
    ConsoleUI.WriteStep("Phase 2: Discovering SQL Server credentials");

    var sqlCreds = SqlCredentialDiscovery.Discover(pioneer.PioneerConfig);
    if (sqlCreds == null)
    {
        ConsoleUI.FatalError(
            "Could not discover SQL Server credentials.\n" +
            "  Contact Suavo support for manual configuration.");
        return 1;
    }

    // Show discovered credentials summary
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine();
    Console.WriteLine($"  Server:   {sqlCreds.Server}");
    Console.WriteLine($"  Database: {sqlCreds.Database}");
    Console.WriteLine($"  Auth:     {(sqlCreds.IsWindowsAuth ? "Windows" : $"SQL ({sqlCreds.User})")}");
    Console.ResetColor();

    // ════════════════════════════════════════════
    // PHASE 3: Download Agent Binaries
    // ════════════════════════════════════════════
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

    // ════════════════════════════════════════════
    // PHASE 4: Write Configuration
    // ════════════════════════════════════════════
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

    // Remove null SQL auth entries for Windows auth
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

    // C-4: Create dir -> lockdown ACL -> THEN write config (credentials never on disk unprotected)
    var configPath = Path.Combine(InstallDir, "appsettings.json");
    Directory.CreateDirectory(InstallDir);
    ServiceInstaller.LockdownDirectoryAcl(InstallDir);
    File.WriteAllText(configPath, configJson);
    ConsoleUI.WriteOk($"appsettings.json written to {InstallDir} (ACL applied first)");

    // ════════════════════════════════════════════
    // PHASE 5: Install Windows Services
    // ════════════════════════════════════════════
    ConsoleUI.WriteStep("Phase 5: Installing Windows services");

    var serviceSuccess = ServiceInstaller.InstallAndStart(InstallDir, DataDir);
    if (!serviceSuccess)
    {
        ConsoleUI.WriteWarn("Services registered but may need a manual start.");
        ConsoleUI.WriteInfo("Run: sc.exe start SuavoAgent.Core");
    }

    // ════════════════════════════════════════════
    // PHASE 6: Done
    // ════════════════════════════════════════════
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

    // List installed files
    var exeFiles = Directory.GetFiles(InstallDir, "*.exe");
    foreach (var file in exeFiles)
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

/// <summary>
/// Gets a stable machine fingerprint via registry MachineGuid (M-4: replaces deprecated wmic).
/// Falls back to machine name if registry read fails.
/// </summary>
static string GetMachineFingerprint()
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
        // Registry unavailable — fall back
    }
    return Environment.MachineName;
}
