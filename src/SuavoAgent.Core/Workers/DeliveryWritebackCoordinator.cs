using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal sealed record DeliveryWritebackExecutionOutcome(
    DeliveryWritebackResultCode? ResultCode,
    bool Transient,
    string ErrorCode)
{
    internal static DeliveryWritebackExecutionOutcome Completed(DeliveryWritebackResultCode result) =>
        new(result, false, "none");

    internal static DeliveryWritebackExecutionOutcome Retry(string errorCode) =>
        new(null, true, errorCode);
}

internal interface IDeliveryWritebackExecutor
{
    Task<DeliveryWritebackExecutionOutcome> ExecuteAsync(
        ReadOnlyMemory<char> rawRxNumber,
        int fillNumber,
        string transition,
        DateTimeOffset transitionAt,
        CancellationToken ct);
}

internal interface IDeliveryWritebackCloudTransport
{
    Task<DeliveryWritebackCallbackReceipt?> SendCallbackAsync(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        CancellationToken ct);

    Task<bool> AckAsync(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        CancellationToken ct);
}

internal sealed class PioneerRxDeliveryWritebackExecutor : IDeliveryWritebackExecutor
{
    private readonly IServiceProvider _services;

    internal PioneerRxDeliveryWritebackExecutor(IServiceProvider services) => _services = services;

    public async Task<DeliveryWritebackExecutionOutcome> ExecuteAsync(
        ReadOnlyMemory<char> rawRxNumber,
        int fillNumber,
        string transition,
        DateTimeOffset transitionAt,
        CancellationToken ct)
    {
        var engine = _services.GetService<RxDetectionWorker>()?.WritebackEngine;
        if (engine is null || !engine.WritebackEnabled)
            return DeliveryWritebackExecutionOutcome.Retry("writeback_engine_unavailable");
        if (!int.TryParse(rawRxNumber.Span, NumberStyles.None, CultureInfo.InvariantCulture, out var rxNumber) ||
            rxNumber <= 0)
            return DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.ManualReview);
        if (!engine.TryAcquireRxLock(rxNumber))
            return DeliveryWritebackExecutionOutcome.Retry("rx_write_lock_busy");

        try
        {
            var result = await engine.ExecuteDeliveryTransitionAsync(
                rxNumber, fillNumber, transition, transitionAt, ct).ConfigureAwait(false);
            return Map(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DeliveryWritebackExecutionOutcome.Retry("writeback_executor_exception");
        }
        finally
        {
            engine.ReleaseRxLock(rxNumber);
        }
    }

    private static DeliveryWritebackExecutionOutcome Map(WritebackResult result) => result.Outcome switch
    {
        "success" => DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.Success),
        "already_at_target" => DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.AlreadyAtTarget),
        "post_verify_mismatch" or "verified_with_drift" =>
            DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.PostVerifyMismatch),
        "status_conflict" =>
            DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.StatusConflict),
        "trigger_blocked" =>
            DeliveryWritebackExecutionOutcome.Completed(DeliveryWritebackResultCode.ManualReview),
        "connection_reset" or "sql_error" =>
            DeliveryWritebackExecutionOutcome.Retry("writeback_sql_unavailable"),
        _ => DeliveryWritebackExecutionOutcome.Retry("writeback_result_unknown"),
    };
}

internal sealed class SuavoDeliveryWritebackCloudTransport : IDeliveryWritebackCloudTransport
{
    private readonly SuavoCloudClient _client;

    internal SuavoDeliveryWritebackCloudTransport(SuavoCloudClient client) => _client = client;

    public Task<DeliveryWritebackCallbackReceipt?> SendCallbackAsync(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        CancellationToken ct) =>
        _client.SendDeliveryWritebackAsync(command, resultCode, ct);

    public Task<bool> AckAsync(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        CancellationToken ct) =>
        _client.TryAckCommandAsync(
            command.CommandId,
            resultCode.IsCloudSuccess(),
            new
            {
                status = "delivery_writeback_received",
                writebackId = command.WritebackId,
                pmsReferenceId = command.PmsReferenceId,
                proofRecordId = command.ProofRecordId,
                proofDigest = command.ProofDigest,
                resultCode = resultCode.ToWireValue(),
            },
            resultCode.IsCloudSuccess() ? null : resultCode.ToWireValue(),
            ct);
}

internal sealed record DeliveryWritebackCommandRegistration(
    bool Accepted,
    string Reason,
    string? CommandId = null);

/// <summary>
/// Durable PHI-minimal command loop. A terminal SQL result is persisted before
/// callback, so retries never issue another write. Raw Rx is revealed only from
/// the exact DPAPI-bound local candidate and is purged only after a signed
/// terminal callback receipt for the complete transition.
/// </summary>
internal sealed class DeliveryWritebackCoordinator
{
    private const int MaxPerHeartbeat = 8;
    private const int MaxExecutionAttempts = 5;
    private readonly AgentOptions _options;
    private readonly IRxCorrelationStore _correlations;
    private readonly IDeliveryWritebackLedger _ledger;
    private readonly AgentStateDb _stateDb;
    private readonly IDeliveryWritebackExecutor _executor;
    private readonly IDeliveryWritebackCloudTransport _cloud;
    private readonly ILogger _logger;

    internal DeliveryWritebackCoordinator(
        AgentOptions options,
        IRxCorrelationStore correlations,
        IDeliveryWritebackLedger ledger,
        AgentStateDb stateDb,
        IDeliveryWritebackExecutor executor,
        IDeliveryWritebackCloudTransport cloud,
        ILogger logger)
    {
        _options = options;
        _correlations = correlations;
        _ledger = ledger;
        _stateDb = stateDb;
        _executor = executor;
        _cloud = cloud;
        _logger = logger;
    }

    internal DeliveryWritebackCommandRegistration Register(AgentDeliveryWritebackCommand command)
    {
        if (!IdentityConfigured() ||
            !string.Equals(command.PharmacyId, _options.PharmacyId, StringComparison.Ordinal))
            return new(false, "identity_mismatch", command.CommandId);

        var ledgerResult = _ledger.Register(command);
        if (!ledgerResult.Accepted)
            return new(false, ledgerResult.Code.ToString().ToLowerInvariant(), command.CommandId);

        var correlation = _correlations.RegisterDeliveryWriteback(
            command,
            _options.AgentId!,
            _options.MachineFingerprint!);
        if (correlation.Accepted)
            _ledger.MarkCorrelationBound(command.CommandId);

        var current = _ledger.Get(command.CommandId)!;
        if (current.ResultCode is null &&
            (_options.ReceiptOnlyMode || !correlation.Accepted))
        {
            _ledger.RecordResult(command.CommandId, DeliveryWritebackResultCode.ManualReview);
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: command.WritebackId,
            EventType: "delivery_writeback_registered",
            FromState: "signed_command",
            ToState: correlation.Accepted ? "correlation_bound" : "manual_review_pending",
            Trigger: "delivery_writeback",
            CommandId: command.CommandId,
            RequesterId: command.CandidateId,
            Actor: "system",
            SourceComponent: "delivery_writeback_coordinator",
            CaptureReason: _options.ReceiptOnlyMode
                ? "receipt_only_mode"
                : correlation.Code.ToString().ToLowerInvariant()));

        return new(true, correlation.Code.ToString().ToLowerInvariant(), command.CommandId);
    }

    internal async Task RetryPendingAsync(CancellationToken ct)
    {
        if (!IdentityConfigured()) return;
        IReadOnlyList<DeliveryWritebackLedgerItem> due;
        try
        {
            due = _ledger.GetDue(
                _options.PharmacyId!, MaxPerHeartbeat, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError(
                "Delivery writeback ledger unavailable ({ErrorType}); failing closed",
                ex.GetType().Name);
            return;
        }

        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessOneAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Delivery writeback deferred for command {CommandId} ({ErrorType})",
                    item.Command.CommandId,
                    ex.GetType().Name);
                TryDefer(item, "writeback_pipeline_exception", callbackAttempt: item.ResultCode is not null);
            }
        }
    }

    private async Task ProcessOneAsync(DeliveryWritebackLedgerItem item, CancellationToken ct)
    {
        if (item.State is DeliveryWritebackLedgerState.Registered or DeliveryWritebackLedgerState.Executing)
        {
            if (_options.ReceiptOnlyMode)
            {
                item = _ledger.RecordResult(
                    item.Command.CommandId,
                    DeliveryWritebackResultCode.ManualReview);
            }
            else
            {
                item = await ExecuteAsync(item, ct).ConfigureAwait(false);
            }
        }

        if (item.State == DeliveryWritebackLedgerState.ResultPendingCallback)
            item = await SendCallbackAsync(item, ct).ConfigureAwait(false);

        if (item.State != DeliveryWritebackLedgerState.ReceiptVerified) return;

        if (item.CorrelationBound)
        {
            _correlations.MarkDeliveryWritebackReceiptVerified(
                item.Command,
                _options.AgentId!,
                _options.MachineFingerprint!,
                item.ResultCode!.Value);
        }

        var acked = await _cloud.AckAsync(
            item.Command,
            item.ResultCode!.Value,
            ct).ConfigureAwait(false);
        if (!acked)
        {
            TryDefer(item, "command_ack_unavailable", callbackAttempt: true);
            return;
        }

        _ledger.MarkAcked(item.Command.CommandId);
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: item.Command.WritebackId,
            EventType: "delivery_writeback_completed",
            FromState: "receipt_verified",
            ToState: "command_acked",
            Trigger: "delivery_writeback",
            CommandId: item.Command.CommandId,
            RequesterId: item.Command.CandidateId,
            Actor: "agent",
            SourceComponent: "delivery_writeback_coordinator",
            CaptureReason: item.ResultCode.Value.ToWireValue()));
    }

    private async Task<DeliveryWritebackLedgerItem> ExecuteAsync(
        DeliveryWritebackLedgerItem item,
        CancellationToken ct)
    {
        if (!item.CorrelationBound ||
            !_correlations.TryRevealDeliveryWriteback(
                item.Command,
                _options.AgentId!,
                _options.MachineFingerprint!,
                out var rawRx,
                out var fillNumber))
        {
            return _ledger.RecordResult(
                item.Command.CommandId,
                DeliveryWritebackResultCode.ManualReview);
        }

        using (rawRx)
        {
            if (!FixedTimeRxHashMatches(rawRx!.Memory.Span, _options.HmacSalt!, item.Command.RxHash))
                return _ledger.RecordResult(
                    item.Command.CommandId,
                    DeliveryWritebackResultCode.ManualReview);

            item = _ledger.MarkExecuting(item.Command.CommandId);
            var transitionAt = DateTimeOffset.Parse(
                item.Command.TransitionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            var outcome = await _executor.ExecuteAsync(
                rawRx.Memory,
                fillNumber,
                item.Command.Transition,
                transitionAt,
                ct).ConfigureAwait(false);
            if (!outcome.Transient && outcome.ResultCode is { } result)
                return _ledger.RecordResult(item.Command.CommandId, result);

            if (item.ExecutionAttempts >= MaxExecutionAttempts)
                return _ledger.RecordResult(
                    item.Command.CommandId,
                    DeliveryWritebackResultCode.RetryExhausted);

            return _ledger.Defer(
                item.Command.CommandId,
                outcome.ErrorCode,
                DateTimeOffset.UtcNow + ExecutionBackoff(item.ExecutionAttempts),
                callbackAttempt: false);
        }
    }

    private async Task<DeliveryWritebackLedgerItem> SendCallbackAsync(
        DeliveryWritebackLedgerItem item,
        CancellationToken ct)
    {
        DeliveryWritebackCallbackReceipt? receipt;
        try
        {
            receipt = await _cloud.SendCallbackAsync(
                item.Command,
                item.ResultCode!.Value,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Delivery writeback callback unavailable for command {CommandId} ({ErrorType})",
                item.Command.CommandId,
                ex.GetType().Name);
            return _ledger.Defer(
                item.Command.CommandId,
                "callback_unavailable",
                DateTimeOffset.UtcNow + CallbackBackoff(item.CallbackAttempts + 1),
                callbackAttempt: true);
        }

        if (receipt is null)
        {
            return _ledger.Defer(
                item.Command.CommandId,
                "callback_receipt_unverified",
                DateTimeOffset.UtcNow + CallbackBackoff(item.CallbackAttempts + 1),
                callbackAttempt: true);
        }

        return _ledger.MarkReceiptVerified(item.Command.CommandId, receipt);
    }

    private void TryDefer(
        DeliveryWritebackLedgerItem item,
        string errorCode,
        bool callbackAttempt)
    {
        try
        {
            _ledger.Defer(
                item.Command.CommandId,
                errorCode,
                DateTimeOffset.UtcNow + CallbackBackoff(item.CallbackAttempts + 1),
                callbackAttempt);
        }
        catch
        {
            // Original PHI-free failure is already logged. Never mask it with
            // a second exception containing local storage detail.
        }
    }

    private bool IdentityConfigured() =>
        !string.IsNullOrWhiteSpace(_options.PharmacyId) &&
        !string.IsNullOrWhiteSpace(_options.AgentId) &&
        !string.IsNullOrWhiteSpace(_options.MachineFingerprint) &&
        !string.IsNullOrWhiteSpace(_options.HmacSalt);

    internal static bool FixedTimeRxHashMatches(
        ReadOnlySpan<char> rawRxNumber,
        string hmacSalt,
        string expectedHash)
    {
        if (string.IsNullOrEmpty(hmacSalt) || expectedHash.Length != 64) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { return false; }
        var key = Encoding.UTF8.GetBytes(hmacSalt);
        var raw = new byte[Encoding.UTF8.GetByteCount(rawRxNumber)];
        Encoding.UTF8.GetBytes(rawRxNumber, raw);
        try
        {
            using var hmac = new HMACSHA256(key);
            var actual = hmac.ComputeHash(raw);
            try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static TimeSpan ExecutionBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(0, attempt - 1)), 300));

    private static TimeSpan CallbackBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, Math.Max(0, attempt - 1)), 300));

}
