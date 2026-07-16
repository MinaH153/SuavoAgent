namespace SuavoAgent.Core.Ipc;

internal readonly record struct IpcPeerVerificationResult(
    bool Accepted,
    string? RejectionReason,
    bool AcceptedByBrokerAttestation);

public readonly record struct IpcBrokerAttestationEvidence(
    uint ProcessId,
    uint SessionId,
    DateTimeOffset ProcessStartedAtUtc,
    string CurrentHelperSha256);

internal static class IpcPeerVerifier
{
    public static IpcPeerVerificationResult Verify(
        string processName,
        uint processId,
        string? executablePath,
        string coreBaseDirectory,
        IpcBrokerAttestationEvidence? brokerEvidence,
        Func<IpcBrokerAttestationEvidence, bool>? isBrokerAttestedHelper)
    {
        if (processName != "SuavoAgent.Helper" && processName != "SuavoAgent.Broker")
        {
            return Reject($"unauthorized_process:{SanitizeReasonLabel(processName)}");
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            if (processName == "SuavoAgent.Helper" && brokerEvidence is { } evidence &&
                (isBrokerAttestedHelper?.Invoke(evidence) ?? false))
            {
                return new IpcPeerVerificationResult(true, null, true);
            }

            return Reject("image_path_unreadable");
        }

        var installDir = ComputeInstallDir(coreBaseDirectory);
        if (!executablePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("path_outside_install_dir");
        }

        return new IpcPeerVerificationResult(true, null, false);
    }

    private static IpcPeerVerificationResult Reject(string reason) =>
        new(false, reason, false);

    private static string ComputeInstallDir(string coreBaseDirectory)
    {
        var trimmed = coreBaseDirectory.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return coreBaseDirectory;

        var separator = trimmed.Contains('\\') ? '\\' : Path.DirectorySeparatorChar;
        return trimmed + separator;
    }

    private static string SanitizeReasonLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "_empty";
        if (raw.Length > 32) return "_too_long";
        foreach (var c in raw)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return "_other";
        }

        return raw;
    }
}
