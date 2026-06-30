namespace SuavoAgent.Setup.Connectors;

internal sealed class NullConnector : ISystemConnector
{
    public string Key => "none";
    public ConnectorCapabilities Capabilities => new(HasPms: false, Label: "your system", RedactionProfileId: "none");
    public ConnectorProbe Probe() => new(false, null, null, "Observe-only (no system connector)");
    public SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe) => null;
}
