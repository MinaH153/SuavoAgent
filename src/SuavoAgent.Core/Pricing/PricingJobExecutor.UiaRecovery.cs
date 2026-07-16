using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Pricing;

public sealed partial class UiaFirstPricingJobExecutor
{
    public PricingJobSpec? GetRecoverableSpec(
        PricingJobSpec proposed,
        string? commandId) => _db.GetRecoverablePricingJob(
            proposed.CostBasis == PricingApprovalContract.PackageCostBasis
                ? "uia"
                : _options.PricingExecutor == PricingExecutorMode.VisionFirst
                    ? "vision"
                    : "uia",
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            proposed.ExcelPath,
            _trustedApprovalKeys,
            proposed.CostBasis);

    public PricingJobSpec? GetRecoverableSpecForCommand(string commandId)
    {
        var primaryModality = _options.PricingExecutor == PricingExecutorMode.VisionFirst
            ? "vision"
            : "uia";
        var recovered = _db.GetRecoverablePricingJob(
            primaryModality,
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            trustedApprovalKeys: _trustedApprovalKeys);
        return recovered ?? (primaryModality == "vision"
            ? _db.GetRecoverablePricingJob(
                "uia",
                _options.PharmacyId ?? "",
                _options.AgentId ?? "",
                _options.MachineFingerprint ?? "",
                DateTimeOffset.UtcNow,
                commandId,
                trustedApprovalKeys: _trustedApprovalKeys,
                expectedCostBasis: PricingApprovalContract.PackageCostBasis)
            : null);
    }

    private async Task<PricingScreenObservationContext?> CaptureScreenContextAsync(
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        IpcResponse? response;
        try
        {
            response = await _commandClient.SendAsync(
                    new IpcRequest(
                        id,
                        IpcCommands.PricingObservationContext,
                        1,
                        Data: null),
                    TimeSpan.FromSeconds(10),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogSafeWarning(exception);
            return null;
        }

        if (response is null || response.Id != id ||
            response.Command != IpcCommands.PricingObservationContext ||
            response.Status != IpcStatus.Ok || response.Error is not null ||
            response.Data is null)
            return null;
        try
        {
            var parsed = response.Data.Value.Deserialize<PricingScreenObservationContext>();
            return parsed is { ProcessId: > 0, ScreenSignatureV1.Length: 64 } &&
                   parsed.ScreenSignatureV1.All(character =>
                       character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
