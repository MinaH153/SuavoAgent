namespace SuavoAgent.Contracts.Writeback;

public record WritebackResult(
    bool Success,
    string Outcome,
    Guid? TransactionId,
    string? Details,
    bool IsReplay = false)
{
    public string? CorrelationKey { get; init; }
    public string? UiEventTimestamp { get; init; }
    public string? SqlExecutionTimestamp { get; init; }

    public static WritebackResult Succeeded(Guid txId, string transition)
        => new(true, "success", txId, transition);

    public static WritebackResult AlreadyAtTarget(Guid txId)
        => new(true, "already_at_target", txId, "idempotent", IsReplay: true);

    public static WritebackResult VerifiedWithDrift(Guid txId, string expected, string actual)
        => new(false, "post_verify_mismatch", null, "completed_date_mismatch");

    public static WritebackResult StatusConflict(string? observed)
        => new(false, "status_conflict", null, observed);

    public static WritebackResult ConnectionReset()
        => new(false, "connection_reset", null, null);

    public static WritebackResult PostVerifyMismatch(string? observed)
        => new(false, "post_verify_mismatch", null, observed);

    public static WritebackResult SqlError()
        => new(false, "sql_error", null, "sql_operation_failed");

    public static WritebackResult TriggerBlocked(string triggerName)
        => new(false, "trigger_blocked", null, triggerName switch
        {
            "instead_of_trigger" => "instead_of_trigger",
            "after_trigger_requires_signed_approval" => "after_trigger_requires_signed_approval",
            "writeback_disabled" => "writeback_disabled",
            "status_map_incomplete" => "status_map_incomplete",
            _ => "trigger_policy_blocked",
        });
}
