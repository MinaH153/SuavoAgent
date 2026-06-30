namespace SuavoAgent.Setup.Connectors;

internal static class SystemConnectorFactory
{
    public static ISystemConnector Select(string systemConnector) => systemConnector switch
    {
        "pioneerrx" => new PioneerRxConnector(),
        "none"      => new NullConnector(),
        _           => throw new UnknownConnectorException(systemConnector),
    };
}
