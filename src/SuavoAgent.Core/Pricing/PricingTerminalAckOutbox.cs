using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Stages a finite failure ACK before transport and retries only those durable
/// bytes. It never owns an executor, workbook, discovery client, or result
/// uploader, so recovery cannot re-run a pricing command.
/// </summary>
internal sealed class PricingTerminalAckOutbox
{
    private readonly AgentStateDb _db;
    private readonly Func<string, bool, object?, string?, CancellationToken, Task<bool>> _ack;
    private readonly ILogger<PricingTerminalAckOutbox> _logger;
    private readonly IReadOnlyDictionary<string, string> _trustedCommandKeys;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    internal string OwnerId { get; } = Guid.NewGuid().ToString("N");

    internal PricingTerminalAckOutbox(
        AgentStateDb db,
        SuavoCloudClient cloudClient,
        ILogger<PricingTerminalAckOutbox> logger)
        : this(
            db,
            (commandId, succeeded, result, error, ct) =>
                cloudClient.TryAckCommandAsync(
                    commandId, succeeded, result, error, ct),
            logger,
            RemoteCommandTrust.CreateProductionKeyRegistry())
    {
    }

    internal PricingTerminalAckOutbox(
        AgentStateDb db,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack,
        ILogger<PricingTerminalAckOutbox> logger)
        : this(
            db,
            ack,
            logger,
            RemoteCommandTrust.CreateProductionKeyRegistry())
    {
    }

    internal PricingTerminalAckOutbox(
        AgentStateDb db,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack,
        ILogger<PricingTerminalAckOutbox> logger,
        IReadOnlyDictionary<string, string> trustedCommandKeys)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ack = ack ?? throw new ArgumentNullException(nameof(ack));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _trustedCommandKeys = trustedCommandKeys ??
            throw new ArgumentNullException(nameof(trustedCommandKeys));
    }

    internal async Task StageAndTryDeliverAsync(
        string commandId,
        PricingTerminalAck ack,
        CancellationToken ct)
    {
        var staged = _db.StagePricingTerminalAck(commandId, ack);
        _db.MarkPricingCommandIntentTerminal(commandId);
        if (staged.State == "delivered") return;
        await TryDeliverAsync(staged, ct).ConfigureAwait(false);
    }

    internal bool TryRegisterVerifiedCommand(
        string nonce,
        string commandId,
        string commandKind,
        string? approvalId = null,
        string? grantDigest = null) =>
        _db.TryRecordNonceAndRegisterPricingIntent(
            nonce,
            commandId,
            commandKind,
            OwnerId,
            verifiedCommand: null,
            approvalId,
            grantDigest);

    internal bool TryRegisterVerifiedCommand(
        SignedCommand verifiedCommand,
        string commandId,
        string commandKind,
        string? approvalId = null,
        string? grantDigest = null) =>
        _db.TryRecordNonceAndRegisterPricingIntent(
            verifiedCommand.Nonce,
            commandId,
            commandKind,
            OwnerId,
            verifiedCommand,
            approvalId,
            grantDigest);

    internal void MarkResultPending(string commandId) =>
        _db.MarkPricingCommandIntentResultPending(commandId);

    internal void MarkCompleted(string commandId) =>
        _db.MarkPricingCommandIntentCompleted(commandId);

    internal async Task RecoverAbandonedAsync(CancellationToken ct)
    {
        foreach (var intent in _db.GetRecoverablePricingCommandIntents(OwnerId, 20))
        {
            ct.ThrowIfCancellationRequested();
            var evidence = _db.GetPricingCommandRecoveryEvidence(intent.CommandId);
            switch (evidence.Kind)
            {
                case AgentStateDb.PricingCommandRecoveryKind.TerminalAck:
                    _db.MarkPricingCommandIntentTerminal(intent.CommandId);
                    break;
                case AgentStateDb.PricingCommandRecoveryKind.ResultAccepted:
                    _db.MarkPricingCommandIntentCompleted(intent.CommandId);
                    break;
                case AgentStateDb.PricingCommandRecoveryKind.ResultPending:
                    _db.MarkPricingCommandIntentResultPending(intent.CommandId);
                    break;
                case AgentStateDb.PricingCommandRecoveryKind.ResultTerminal:
                    await StageAndTryDeliverAsync(
                        intent.CommandId,
                        evidence.TerminalAck ?? PricingTerminalAck.Early(
                            "pricing_execution_exception"),
                        ct).ConfigureAwait(false);
                    break;
                case AgentStateDb.PricingCommandRecoveryKind.None:
                    if (_db.IsPricingCommandResumeReady(
                            intent.CommandId,
                            _trustedCommandKeys))
                    {
                        _logger.LogInformation(
                            "core.pricing.recovery_checkpoint_retained");
                        break;
                    }
                    await StageAndTryDeliverAsync(
                        intent.CommandId,
                        PricingTerminalAck.Early("pricing_execution_exception"),
                        ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException(
                        "Pricing command recovery evidence is invalid.");
            }
        }
    }

    internal async Task RetryPendingAsync(
        CancellationToken ct,
        bool includeDeferred = false)
    {
        if (!await _deliveryGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return;
        try
        {
            await RecoverAbandonedAsync(ct).ConfigureAwait(false);
            foreach (var entry in _db.GetPendingPricingTerminalAcks(
                         20, includeDeferred))
            {
                ct.ThrowIfCancellationRequested();
                await TryDeliverAsync(entry, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task TryDeliverAsync(
        AgentStateDb.PricingTerminalAckOutboxEntry entry,
        CancellationToken ct)
    {
        bool delivered;
        try
        {
            delivered = await _ack(
                entry.CommandId,
                false,
                entry.Ack.BuildResult(),
                entry.Ack.ErrorCode,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            delivered = false;
        }

        if (delivered)
        {
            _db.MarkPricingTerminalAckDelivered(
                entry.CommandId,
                entry.PayloadSha256);
            return;
        }

        _db.DelayPricingTerminalAck(
            entry.CommandId,
            entry.PayloadSha256,
            entry.AttemptCount);
    }
}
