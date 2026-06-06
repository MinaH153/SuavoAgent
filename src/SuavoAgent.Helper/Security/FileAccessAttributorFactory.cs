using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// Selects the honeytoken file-access attributor. <c>Null = 0</c> is the safe default — byte-for-byte today's
/// <see cref="NullFileAccessAttributor"/> for any un-flipped pharmacy. <c>EtwBroker</c> reads the Broker's
/// ETW-derived signed handoff (<see cref="EtwBrokerFileAccessAttributor"/>) and requires the Broker oracle
/// armed + a clean per-pharmacy shadow day before it is enabled.
/// </summary>
public enum HoneytokenAttributorMode
{
    Null = 0,
    EtwBroker = 1,
}

public static class FileAccessAttributorFactory
{
    /// <summary>
    /// Build the attributor for <paramref name="mode"/> and LOG the EFFECTIVE choice (never-blind: an operator
    /// can read from the Helper log which attributor is actually live). <paramref name="rawNonce"/> is the raw
    /// pipe nonce (the Broker stamps the same raw nonce into the handoff doc).
    /// </summary>
    public static IFileAccessAttributor Create(HoneytokenAttributorMode mode, string rawNonce, ILogger logger)
    {
        switch (mode)
        {
            case HoneytokenAttributorMode.EtwBroker:
                logger.Information(
                    "Honeytoken attributor: EtwBroker (handoff={Path})",
                    HoneytokenAttributionStore.GetDefaultPath());
                return new EtwBrokerFileAccessAttributor(
                    HoneytokenAttributionStore.GetDefaultPath(), rawNonce);
            default:
                logger.Information("Honeytoken attributor: Null (no attribution — every touch is unknown→Degrade)");
                return new NullFileAccessAttributor();
        }
    }
}
