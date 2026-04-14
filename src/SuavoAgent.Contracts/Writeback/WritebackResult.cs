namespace SuavoAgent.Contracts.Writeback;

public record WritebackResult(
    bool Success,
    string Outcome,
    Guid? TransactionId,
    string? Details,
    bool IsReplay = false)
{
    public static WritebackResult Succeeded(Guid txId, string transition)
        => new(true, "success", txId, transition);

    public static WritebackResult AlreadyAtTarget(Guid txId)
        => new(true, "already_at_target", txId, "idempotent", IsReplay: true);

    public static WritebackResult VerifiedWithDrift(Guid txId, string expected, string actual)
        => new(true, "verified_with_drift", txId, $"expected={expected},actual={actual}");

    public static WritebackResult StatusConflict(string? observed)
        => new(false, "status_conflict", null, observed);

    public static WritebackResult ConnectionReset()
        => new(false, "connection_reset", null, null);

    public static WritebackResult PostVerifyMismatch(string? observed)
        => new(false, "post_verify_mismatch", null, observed);

    public static WritebackResult SqlError(Exception ex)
        => new(false, "sql_error", null, ex.Message);

    public static WritebackResult TriggerBlocked(string triggerName)
        => new(false, "trigger_blocked", null, triggerName);
}
