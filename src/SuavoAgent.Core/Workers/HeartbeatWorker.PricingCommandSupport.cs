using System.Text.Json;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private static bool IsExcelPathSafe(string path, out string canonical, out string reason)
    {
        canonical = "";
        reason = "";
        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            reason = "pricing_candidate_extension_invalid";
            return false;
        }
        if (path.StartsWith(@"\\") || !Path.IsPathRooted(path))
        {
            reason = "pricing_candidate_path_invalid";
            return false;
        }
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "pricing_candidate_path_invalid";
            return false;
        }
        if (!string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase))
        {
            reason = "pricing_candidate_path_invalid";
            return false;
        }
        return true;
    }

    private static JsonElement CommandDataObject(JsonElement signedCommand)
    {
        if (signedCommand.ValueKind != JsonValueKind.Object)
            return default;
        return signedCommand.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object
            ? data
            : signedCommand;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return null;
        return property.GetString();
    }

    private static bool TryReadPricingCommandId(
        JsonElement signedCommand,
        out string commandId)
    {
        var data = CommandDataObject(signedCommand);
        return IsCanonicalUuidV4(
            ReadStringProperty(data, "commandId"),
            out commandId);
    }

    private static bool TryReadPricingAuthorityBinding(
        JsonElement signedCommand,
        out string approvalId,
        out string grantDigest)
    {
        approvalId = string.Empty;
        grantDigest = string.Empty;
        var data = CommandDataObject(signedCommand);
        if (!IsCanonicalUuidV4(
                ReadStringProperty(data, "approvalId"),
                out approvalId))
            return false;
        var digest = ReadStringProperty(data, "grantDigest");
        if (digest is not { Length: 64 } ||
            digest.Any(value => value is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            approvalId = string.Empty;
            return false;
        }
        grantDigest = digest;
        return true;
    }

    private static string PricingTerminalFailureCode(string? value) => value switch
    {
        "pricing_workbook_validation_failed" => value,
        "pricing_result_payload_too_large" => value,
        "helper_unreachable" => value,
        "actuation_gate_closed" => value,
        "pioneerrx_not_attached" => value,
        "pricing_brain_operator_required" => value,
        "pricing_package_cost_surface_unavailable" => value,
        _ => "pricing_job_failed",
    };

    private async Task AckPricingFailureAsync(
        string commandId,
        PricingTerminalAck ack,
        CancellationToken ct)
    {
        ack.Validated();
        if (_pricingTerminalAckOutbox is null)
        {
            // Durable state remains authoritative even in a deliberately
            // cloud-less test/runtime configuration. A later correctly wired
            // process can deliver it; execution is never reopened here.
            _stateDb.StagePricingTerminalAck(commandId, ack);
            _stateDb.MarkPricingCommandIntentTerminal(commandId);
            _logger.LogWarning("core.command.pricing_terminal_ack_transport_unavailable");
            return;
        }
        await _pricingTerminalAckOutbox.StageAndTryDeliverAsync(
            commandId,
            ack,
            ct).ConfigureAwait(false);
    }

    private static string PricingEarlyFailureCode(string? value) =>
        value is not null && PricingTerminalAck.EarlyFailureCodes.Contains(value)
            ? value
            : "helper_preflight_failed";

    private static string PricingDiscoveryFailureCode(string? value) =>
        value is not null && PricingTerminalAck.DiscoveryFailureCodes.Contains(value)
            ? value
            : "unknown";

    private static string PricingAutopilotRejectionCode(string? value) => value switch
    {
        "autopilot_paused" => value,
        "autopilot_stopped" => value,
        _ => throw new InvalidDataException(
            "Pricing autopilot rejection code is invalid."),
    };
}
