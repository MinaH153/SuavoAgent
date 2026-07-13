using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Pricing;

internal static class PricingTerminalAckPolicy
{
    /// <summary>
    /// Converts a cryptographically verified server-terminal rejection or an
    /// exact append-only local authority tombstone into a finite command
    /// failure. Transient upload outcomes stay exclusively in the pricing
    /// result outbox and return no ACK projection.
    /// </summary>
    internal static PricingTerminalAck? FromResultSync(
        PricingJobCloudUploadReceipt? receipt,
        string jobId,
        PricingJobExecutionResult execution)
    {
        if (receipt is null || receipt.Accepted || !receipt.VerifiedTerminal)
            return null;
        if (PricingTerminalAck.EarlyFailureCodes.Contains(receipt.Code))
            return PricingTerminalAck.Early(receipt.Code);
        var progress = execution.Progress;
        return PricingTerminalAck.PricingFailed(
            jobId,
            execution.Mode,
            progress.TotalItems,
            progress.CompletedItems,
            progress.FailedItems,
            "pricing_job_failed");
    }
}
