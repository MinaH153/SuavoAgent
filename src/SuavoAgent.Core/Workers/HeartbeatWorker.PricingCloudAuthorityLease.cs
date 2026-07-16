using System.Globalization;
using System.Net;
using System.Text.Json;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private const string InactiveAgentError = "Agent binding inactive";

    private void RenewPricingCloudAuthorityLease(
        JsonElement? response,
        DateTimeOffset observedAt)
    {
        if (!TryReadAuthenticatedServerTime(response, out var serverTime))
            throw new InvalidOperationException(
                "pricing_cloud_authority_heartbeat_response_invalid");
        if (!_stateDb.RecordPricingCloudAuthorityHeartbeat(
                serverTime,
                observedAt,
                out var code))
            throw new InvalidOperationException(code);
    }

    internal static bool TryReadAuthenticatedServerTime(
        JsonElement? response,
        out DateTimeOffset serverTime)
    {
        serverTime = default;
        if (response is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("serverTime", out var serverTimeElement) ||
            serverTimeElement.ValueKind != JsonValueKind.String)
            return false;

        var value = serverTimeElement.GetString();
        if (value is null ||
            value.Length is < 20 or > 40 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !(value.EndsWith('Z') ||
              value.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out serverTime) ||
            serverTime.Offset != TimeSpan.Zero)
        {
            serverTime = default;
            return false;
        }

        serverTime = serverTime.ToUniversalTime();
        return true;
    }

    internal static bool IsTerminalInactiveAgentResponse(
        HttpRequestException exception)
    {
        if (exception.StatusCode != HttpStatusCode.Unauthorized)
            return false;
        var body = CloudErrorResponse.ReadBody(exception);
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                string.Equals(
                    error.GetString(),
                    InactiveAgentError,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static AutopilotRunCancellationReceipt
        RevokePricingAuthorityAndCancelRuns(
            AgentStateDb stateDb,
            AutopilotRunCoordinator autopilotRuns,
            DateTimeOffset observedAt)
    {
        Exception? persistenceFailure = null;
        try
        {
            stateDb.LatchPricingCloudAuthorityRevocation(observedAt);
        }
        catch (Exception ex)
        {
            persistenceFailure = ex;
        }

        // Cancellation is unconditional: a broken SQLite write must never
        // leave a detached pricing lease running on its prior grace period.
        var cancellation = autopilotRuns.CancelRuns(AutopilotRunKind.Pricing);
        if (persistenceFailure is not null)
        {
            throw new InvalidOperationException(
                "pricing_cloud_authority_revocation_persist_failed",
                persistenceFailure);
        }
        return cancellation;
    }
}
