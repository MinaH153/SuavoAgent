using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

internal enum UpdateClaimState
{
    None,
    AwaitingHeartbeat,
    ResumeRequired,
    Completed,
    Invalid,
}

internal sealed record UpdateClaimMonitorResult(
    UpdateClaimState State,
    string Code,
    UpdateActivationClaimPointer? Pointer = null,
    UpdateActivationCompletion? Completion = null);

/// <summary>
/// Observes the SYSTEM/Admin-only durable update claim after Maintenance removes the untrusted
/// source request. Process.Start is not treated as liveness: an absent completion plus an expired
/// claim heartbeat requires a trusted resume launch. Maintenance owns all activation idempotency.
/// </summary>
internal sealed class UpdateClaimMonitor
{
    internal static readonly TimeSpan ClaimHeartbeatLease = TimeSpan.FromMinutes(2);

    public UpdateClaimMonitorResult Inspect(
        string maintenanceRoot,
        string activeClaimPath,
        string completionPath,
        DateTimeOffset now)
    {
        try
        {
            if (!IsExactPath(
                    activeClaimPath,
                    Path.Combine(maintenanceRoot, UpdateActivationContract.ActiveClaimFileName)) ||
                !IsExactPath(
                    completionPath,
                    Path.Combine(maintenanceRoot, UpdateActivationContract.CompletionFileName)))
                return Invalid("claim_monitor_path_invalid");

            if (!File.Exists(activeClaimPath))
            {
                if (!File.Exists(completionPath))
                    return new UpdateClaimMonitorResult(UpdateClaimState.None, "no_active_claim");
                if (!TryReadCompletion(completionPath, out var terminal, out var terminalCode))
                    return Invalid(terminalCode, completion: terminal);
                if (!UpdateActivationContract.ValidateCompletionStandalone(
                        terminal!,
                        now,
                        out var validationCode))
                    return Invalid(validationCode, completion: terminal);
                return new UpdateClaimMonitorResult(
                    UpdateClaimState.Completed,
                    "terminal_completion_present",
                    Completion: terminal);
            }
            if (!IsBoundedRegularFile(
                    activeClaimPath,
                    UpdateActivationContract.MaxClaimPointerBytes))
                return Invalid("claim_pointer_file_invalid");

            var pointerJson = new UTF8Encoding(false, true).GetString(ReadBounded(
                activeClaimPath,
                UpdateActivationContract.MaxClaimPointerBytes));
            if (!UpdateActivationContract.TryDeserializeClaimPointer(
                    pointerJson,
                    out var pointer,
                    out var deserializeCode))
                return Invalid(deserializeCode);
            if (!UpdateActivationContract.ValidateClaimPointer(
                    pointer!,
                    maintenanceRoot,
                    now,
                    out var pointerCode))
                return Invalid(pointerCode, pointer);
            if (!IsBoundedRegularFile(
                    pointer!.RequestPath,
                    UpdateActivationContract.MaxRequestBytes) ||
                !Directory.Exists(pointer.PayloadDirectory) ||
                HasReparsePoint(pointer.PayloadDirectory))
                return Invalid("durable_claim_payload_missing", pointer);

            if (File.Exists(completionPath))
            {
                if (!TryReadCompletion(
                        completionPath,
                        out var completion,
                        out var completionDeserializeCode))
                    return Invalid(completionDeserializeCode, pointer);
                if (!UpdateActivationContract.ValidateCompletion(
                        completion!,
                        pointer,
                        now,
                        out var completionCode))
                    return Invalid(completionCode, pointer, completion);
                return new UpdateClaimMonitorResult(
                    UpdateClaimState.Completed,
                    "terminal_completion_present",
                    pointer,
                    completion);
            }

            if (!TryParseTimestamp(pointer.LastHeartbeatAtUtc, out var heartbeatAt))
                return Invalid("claim_heartbeat_invalid", pointer);
            return heartbeatAt > now - ClaimHeartbeatLease
                ? new UpdateClaimMonitorResult(
                    UpdateClaimState.AwaitingHeartbeat,
                    "claim_heartbeat_live",
                    pointer)
                : new UpdateClaimMonitorResult(
                    UpdateClaimState.ResumeRequired,
                    "claim_heartbeat_expired",
                    pointer);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            DecoderFallbackException)
        {
            return Invalid("claim_monitor_unreadable:" + ex.GetType().Name);
        }
    }

    private static UpdateClaimMonitorResult Invalid(
        string code,
        UpdateActivationClaimPointer? pointer = null,
        UpdateActivationCompletion? completion = null) =>
        new(UpdateClaimState.Invalid, code, pointer, completion);

    private static bool IsExactPath(string candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.IsNullOrWhiteSpace(expected) ||
            !Path.IsPathFullyQualified(candidate) ||
            !Path.IsPathFullyQualified(expected))
            return false;
        return string.Equals(
            Path.GetFullPath(candidate),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoundedRegularFile(string path, int maximumBytes)
    {
        if (!File.Exists(path) || HasReparsePoint(path)) return false;
        var length = new FileInfo(path).Length;
        return length > 0 && length <= maximumBytes;
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Update claim metadata has an invalid size");
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total <= 0 || total > maximumBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Update claim metadata has an invalid size");
        return bytes.AsSpan(0, total).ToArray();
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out timestamp);

    private static bool TryReadCompletion(
        string path,
        out UpdateActivationCompletion? completion,
        out string code)
    {
        completion = null;
        code = "completion_file_invalid";
        if (!IsBoundedRegularFile(path, UpdateActivationContract.MaxCompletionBytes))
            return false;
        var json = new UTF8Encoding(false, true).GetString(ReadBounded(
            path,
            UpdateActivationContract.MaxCompletionBytes));
        return UpdateActivationContract.TryDeserializeCompletion(
            json,
            out completion,
            out code);
    }
}
