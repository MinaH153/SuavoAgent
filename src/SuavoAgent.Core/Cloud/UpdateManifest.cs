namespace SuavoAgent.Core.Cloud;

public record UpdateManifest(
    string CoreUrl, string CoreSha256,
    string BrokerUrl, string BrokerSha256,
    string HelperUrl, string HelperSha256,
    string Version, string Runtime, string Arch,
    // Self-heal Chunk B — OPTIONAL Watchdog binary. Back-compat: when absent the manifest is the
    // original 9-field form (and ToCanonical is byte-identical, so existing signatures still verify).
    // When present the canonical gains two trailing fields (11 total). Lets the cloud deliver the
    // Watchdog binary to a box that's missing it, which the Broker then registers as a service.
    string? WatchdogUrl = null, string? WatchdogSha256 = null)
{
    private const int BaseFieldCount = 9;
    private const int WatchdogFieldCount = 11;

    /// <summary>True when both Watchdog fields are populated (the manifest carries a Watchdog binary).</summary>
    public bool HasWatchdog =>
        !string.IsNullOrWhiteSpace(WatchdogUrl) && !string.IsNullOrWhiteSpace(WatchdogSha256);

    public static UpdateManifest? Parse(string manifest)
    {
        var parts = manifest.Split('|');
        if (parts.Length != BaseFieldCount && parts.Length != WatchdogFieldCount) return null;
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;
        var hasWatchdog = parts.Length == WatchdogFieldCount;
        return new UpdateManifest(
            parts[0], parts[1], parts[2], parts[3],
            parts[4], parts[5], parts[6], parts[7], parts[8],
            hasWatchdog ? parts[9] : null,
            hasWatchdog ? parts[10] : null);
    }

    public string ToCanonical()
    {
        var core = $"{CoreUrl}|{CoreSha256}|{BrokerUrl}|{BrokerSha256}|{HelperUrl}|{HelperSha256}|{Version}|{Runtime}|{Arch}";
        // Watchdog fields are APPENDED (original 9 positions unchanged) so a watchdog-less manifest's
        // canonical — and therefore its signature — is identical to before this change.
        return HasWatchdog ? $"{core}|{WatchdogUrl}|{WatchdogSha256}" : core;
    }

    public bool MatchesRuntime(string expectedRuntime, string expectedArch) =>
        string.Equals(Runtime, expectedRuntime, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Arch, expectedArch, StringComparison.OrdinalIgnoreCase);
}
