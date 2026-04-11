namespace SuavoAgent.Core.Cloud;

public record UpdateManifest(
    string CoreUrl, string CoreSha256,
    string BrokerUrl, string BrokerSha256,
    string HelperUrl, string HelperSha256,
    string Version, string Runtime, string Arch)
{
    private const int FieldCount = 9;

    public static UpdateManifest? Parse(string manifest)
    {
        var parts = manifest.Split('|');
        if (parts.Length != FieldCount) return null;
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;
        return new UpdateManifest(
            parts[0], parts[1], parts[2], parts[3],
            parts[4], parts[5], parts[6], parts[7], parts[8]);
    }

    public string ToCanonical() =>
        $"{CoreUrl}|{CoreSha256}|{BrokerUrl}|{BrokerSha256}|{HelperUrl}|{HelperSha256}|{Version}|{Runtime}|{Arch}";

    public bool MatchesRuntime(string expectedRuntime, string expectedArch) =>
        string.Equals(Runtime, expectedRuntime, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Arch, expectedArch, StringComparison.OrdinalIgnoreCase);
}
