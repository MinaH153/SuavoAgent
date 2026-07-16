using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Contracts.Maintenance;

public sealed record PioneerRxApprovalBootstrapRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("approvedBySid")] string ApprovedBySid,
    [property: JsonPropertyName("consentReceiptSha256")] string ConsentReceiptSha256,
    [property: JsonPropertyName("requestedAtUtc")] string RequestedAtUtc);

public sealed record PioneerRxApprovalBootstrapState(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposal")] PioneerRxProcessApprovalReceipt Proposal,
    [property: JsonPropertyName("consentReceiptSha256")] string ConsentReceiptSha256,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("nextPollAtUtc")] string NextPollAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc);

public sealed record PioneerRxApprovalBootstrapStatus(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("receiptId")] string? ReceiptId,
    [property: JsonPropertyName("approvalCounter")] long? ApprovalCounter,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc);

public static class PioneerRxApprovalBootstrapContract
{
    public const int SchemaVersion = 1;
    public const string BootstrapSwitch = "--bootstrap-pioneerrx-approval";
    public const string RequestPathSwitch = "--bootstrap-request";
    public const string RequestFileName = "pioneerrx-approval-bootstrap.request.json";
    public const string StateFileName = "pioneerrx-approval-bootstrap.state.json";
    public const string StatusFileName = "pioneerrx-approval-status.json";

    public static string DefaultRequestPath() => Path.Combine(
        PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory(),
        RequestFileName);

    public static string DefaultStatePath() => Path.Combine(
        PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory(),
        StateFileName);

    public static string DefaultStatusPath() => Path.Combine(
        PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory(),
        StatusFileName);

    public static bool IsExactRequestPath(string? candidate, string? expected = null)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) ||
            !string.Equals(
                Path.GetFileName(candidate),
                RequestFileName,
                StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(expected ?? DefaultRequestPath()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
