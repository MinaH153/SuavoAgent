namespace SuavoAgent.Setup.Connectors;

internal sealed class PioneerRxConnector : ISystemConnector
{
    public string Key => "pioneerrx";
    public ConnectorCapabilities Capabilities => new(HasPms: true, Label: "PioneerRx", RedactionProfileId: "phi-v1");

    public ConnectorProbe Probe()
    {
        var d = PioneerRxDiscovery.Discover();
        return d is null
            ? new ConnectorProbe(false, null, null, "PioneerRx not found (no-PMS mode)")
            : new ConnectorProbe(true, d.PioneerDir, d.PioneerConfig, "PioneerRx detected");
    }

    public SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe) =>
        probe.ConfigPath is null ? null : SqlCredentialDiscovery.TryAutoDiscover(probe.ConfigPath);
}
