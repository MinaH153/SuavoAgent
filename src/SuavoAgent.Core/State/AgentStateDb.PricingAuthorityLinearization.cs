namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private readonly SemaphoreSlim _pricingAuthorityLinearizationGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        _pricingApprovalRevocationsInProcess = new(StringComparer.Ordinal);

    internal sealed record PricingAuthorityOperationResult<T>(
        bool Admitted,
        string Code,
        T? Value);

    internal sealed record PricingAuthorityOperationContext(
        bool IsReconciliation,
        int RecoveryAttempt);

    /// <summary>
    /// Linearizes an outbound authority-bearing operation with both exact PIC
    /// revocation and terminal workstation revocation. The authority time is
    /// sampled only after this operation wins the shared gate.
    /// </summary>
    internal async Task<PricingAuthorityOperationResult<T>>
        ExecuteUnderPricingAuthorityAsync<T>(
            string jobId,
            string payloadSha256,
            string approvalId,
            string grantDigest,
            TimeProvider clock,
            IReadOnlyDictionary<string, string>? trustedPublicKeys,
            Func<PricingAuthorityOperationContext, CancellationToken, Task<T>> operation,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(operation);
        await _pricingAuthorityLinearizationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var admitted = TryAdmitPricingJobAuthority(
                    jobId,
                    approvalId,
                    grantDigest,
                    clock.GetUtcNow(),
                    trustedPublicKeys,
                    out var code);
            var reconciliation = !admitted &&
                IsPricingAuthorityReconciliationCode(code) &&
                HasExactPricingAuthoritySendAttempt(
                    jobId,
                    payloadSha256,
                    approvalId,
                    grantDigest);
            if (!admitted && !reconciliation)
                return new(false, code, default);
            if (!reconciliation &&
                IsPricingApprovalRevocationPending(approvalId))
                return new(
                    false,
                    "pricing_cost_basis_approval_revoked",
                    default);

            var recoveryAttempt = 0;
            if (admitted)
                RecordPricingAuthoritySendAttempt(
                    jobId,
                    payloadSha256,
                    approvalId,
                    grantDigest,
                    clock.GetUtcNow());
            else if (!TryRecordPricingAuthorityRecoveryAttempt(
                         jobId,
                         payloadSha256,
                         approvalId,
                         grantDigest,
                         clock.GetUtcNow(),
                         out recoveryAttempt))
                return new(
                    false,
                    "pricing_result_manual_reconciliation_required",
                    default);

            var value = await operation(
                    new PricingAuthorityOperationContext(
                        reconciliation,
                        recoveryAttempt),
                    ct)
                .ConfigureAwait(false);
            return new(
                true,
                reconciliation
                    ? "pricing_result_authority_reconciliation"
                    : "pricing_job_authority_active",
                value);
        }
        finally
        {
            _pricingAuthorityLinearizationGate.Release();
        }
    }

    private IDisposable EnterPricingAuthorityMutation()
    {
        _pricingAuthorityLinearizationGate.Wait();
        return new PricingAuthorityMutationLease(
            _pricingAuthorityLinearizationGate);
    }

    private bool IsPricingApprovalRevocationPending(string approvalId) =>
        _pricingApprovalRevocationsInProcess.ContainsKey(approvalId);

    internal int GetPendingPricingApprovalRevocationCount(string approvalId) =>
        _pricingApprovalRevocationsInProcess.TryGetValue(
            approvalId,
            out var count)
                ? count
                : 0;

    private void BeginPricingApprovalRevocation(string approvalId) =>
        _pricingApprovalRevocationsInProcess.AddOrUpdate(
            approvalId,
            1,
            static (_, count) => checked(count + 1));

    private void EndPricingApprovalRevocation(string approvalId)
    {
        while (_pricingApprovalRevocationsInProcess.TryGetValue(
                   approvalId,
                   out var count))
        {
            if (count == 1)
            {
                if (_pricingApprovalRevocationsInProcess.TryRemove(
                        new KeyValuePair<string, int>(approvalId, count)))
                    return;
            }
            else if (_pricingApprovalRevocationsInProcess.TryUpdate(
                         approvalId,
                         count - 1,
                         count))
                return;
        }
    }

    private static bool IsPricingAuthorityReconciliationCode(string code) =>
        code is
            "pricing_cost_basis_approval_revoked" or
            "pricing_cost_basis_approval_expired" or
            "pricing_cloud_authority_revoked" or
            "pricing_cloud_authority_lease_expired" or
            "pricing_cloud_authority_clock_rollback";

    private sealed class PricingAuthorityMutationLease(
        SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
        }
    }
}
