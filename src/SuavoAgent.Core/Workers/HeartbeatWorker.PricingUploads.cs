namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    internal static object BuildAgentCapabilities() => new
    {
        structuredPricingCommandsVersion = 1,
    };
}
