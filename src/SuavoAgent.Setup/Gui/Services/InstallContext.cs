namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Shared, mutable state that threads through the six installer steps.
/// Created once when the GUI launches, populated as the operator moves
/// forward through Welcome → SystemCheck → Consent → InstallDestination →
/// Progress → Success, and then consumed when writing the on-disk
/// consent-receipt / appsettings during the progress phase.
/// </summary>
internal sealed class InstallContext
{
    public SetupConfig Config { get; }

    public string InstallDir { get; set; } = @"C:\Program Files\Suavo\Agent";
    public string DataDir { get; } = @"C:\ProgramData\SuavoAgent";

    public PioneerRxDiscovery.DiscoveryResult? Pioneer { get; set; }
    public SqlCredentialDiscovery.SqlCredentials? SqlCredentials { get; set; }
    public ConsentReceiptData? Consent { get; set; }

    public string? AgentId { get; set; }
    public string? MachineFingerprint { get; set; }

    public InstallContext(SetupConfig config)
    {
        Config = config;
    }

    public string InstallerVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0";
}
