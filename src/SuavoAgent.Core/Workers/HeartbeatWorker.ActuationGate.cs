using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    /// <summary>
    /// Reads Helper gate truth over the authenticated Core-to-Helper IPC channel.
    /// Callers must pass only a fixed structural operation label. Unavailable,
    /// malformed, or timed-out gate state is represented as null and every safety
    /// gate treats null as a denial.
    /// </summary>
    private async Task<ActuationGateState?> ReadHelperActuationGateAsync(
        string operation,
        CancellationToken ct)
    {
        if (_actuationGateway is null)
        {
            _logger.LogWarning(
                "Helper actuation gate unavailable for {Operation}: gateway not configured",
                operation);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await _actuationGateway.GetStateAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Helper actuation gate unavailable for {Operation} ({ErrorType})",
                operation,
                ex.GetType().Name);
            return null;
        }
    }
}
