namespace SuavoAgent.Core.Cloud;

public record UpdateManifest(
    string CoreUrl, string CoreSha256,
    string BrokerUrl, string BrokerSha256,
    string HelperUrl, string HelperSha256,
    string Version, string Runtime, string Arch,
    // Watchdog is mandatory for OTA activation; native maintenance is optional.
    string? WatchdogUrl = null, string? WatchdogSha256 = null,
    string? MaintenanceUrl = null, string? MaintenanceSha256 = null)
{
    private const int WatchdogFieldCount = 11;
    private const int FullCohortFieldCount = 13;

    /// <summary>True when both Watchdog fields are populated (the manifest carries a Watchdog binary).</summary>
    public bool HasWatchdog =>
        !string.IsNullOrWhiteSpace(WatchdogUrl) && !string.IsNullOrWhiteSpace(WatchdogSha256);

    /// <summary>True when the manifest carries the signed native maintenance host.</summary>
    public bool HasMaintenance =>
        HasWatchdog &&
        !string.IsNullOrWhiteSpace(MaintenanceUrl) && !string.IsNullOrWhiteSpace(MaintenanceSha256);

    public static UpdateManifest? Parse(string manifest)
    {
        var parts = manifest.Split('|');
        if (parts.Length != WatchdogFieldCount &&
            parts.Length != FullCohortFieldCount) return null;
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;
        var hasWatchdog = parts.Length >= WatchdogFieldCount;
        var hasMaintenance = parts.Length == FullCohortFieldCount;
        return new UpdateManifest(
            parts[0], parts[1], parts[2], parts[3],
            parts[4], parts[5], parts[6], parts[7], parts[8],
            hasWatchdog ? parts[9] : null,
            hasWatchdog ? parts[10] : null,
            hasMaintenance ? parts[11] : null,
            hasMaintenance ? parts[12] : null);
    }

    public string ToCanonical()
    {
        var core = $"{CoreUrl}|{CoreSha256}|{BrokerUrl}|{BrokerSha256}|{HelperUrl}|{HelperSha256}|{Version}|{Runtime}|{Arch}";
        // Keep canonicalization total for legacy diagnostic/signature tests.
        // Parse and the privileged activation contract both reject this
        // 9-field shape, so it can never become an executable authority.
        if (!HasWatchdog) return core;
        var withWatchdog = $"{core}|{WatchdogUrl}|{WatchdogSha256}";
        return HasMaintenance
            ? $"{withWatchdog}|{MaintenanceUrl}|{MaintenanceSha256}"
            : withWatchdog;
    }

    public bool MatchesRuntime(string expectedRuntime, string expectedArch) =>
        string.Equals(Runtime, expectedRuntime, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Arch, expectedArch, StringComparison.OrdinalIgnoreCase);
}
