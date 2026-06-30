namespace SuavoAgent.Setup.Connectors;

internal sealed record ConnectorProbe(bool Detected, string? InstallDir, string? ConfigPath, string Message);
internal sealed record ConnectorCapabilities(bool HasPms, string Label, string RedactionProfileId);

internal interface ISystemConnector
{
    string Key { get; }
    ConnectorCapabilities Capabilities { get; }
    ConnectorProbe Probe();
    SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe);
}

internal sealed class UnknownConnectorException(string key)
    : Exception($"Unknown systemConnector '{key}'");
