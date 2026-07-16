using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private async Task RecoverSignedAdmittedPricingCommandsAsync(
        CancellationToken ct)
    {
        if (_pricingJobExecutor is null || _pricingTerminalAckOutbox is null)
            return;
        var coordinator = new PricingCommandRecoveryCoordinator(
            _stateDb,
            _pricingJobExecutor,
            _pricingJobCloudUploader,
            _pricingTerminalAckOutbox,
            () => BuildTrustedPricingAutonomyScope()?.ScopeDigest,
            _serviceProvider.GetService<ILogger<PricingCommandRecoveryCoordinator>>() ??
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<PricingCommandRecoveryCoordinator>.Instance,
            pricedWorkbookPublisher: _pricedWorkbookPublisher);
        await coordinator.RecoverAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Durable result evidence outranks a cancellation observed while its exact
    /// bytes are being uploaded. Preserve that result channel and let receipt
    /// recovery converge; a weaker failed ACK must never land first.
    /// </summary>
    private bool PreserveDurablePricingOutcome(string commandId)
    {
        var evidence = _stateDb.GetPricingCommandRecoveryEvidence(commandId);
        switch (evidence.Kind)
        {
            case AgentStateDb.PricingCommandRecoveryKind.TerminalAck:
                _stateDb.MarkPricingCommandIntentTerminal(commandId);
                return true;
            case AgentStateDb.PricingCommandRecoveryKind.ResultAccepted:
                _stateDb.MarkPricingCommandIntentCompleted(commandId);
                return true;
            case AgentStateDb.PricingCommandRecoveryKind.ResultPending:
            case AgentStateDb.PricingCommandRecoveryKind.ResultTerminal:
                _stateDb.MarkPricingCommandIntentResultPending(commandId);
                return true;
            case AgentStateDb.PricingCommandRecoveryKind.None:
                return false;
            default:
                throw new InvalidDataException(
                    "Pricing command recovery evidence is invalid.");
        }
    }
}
