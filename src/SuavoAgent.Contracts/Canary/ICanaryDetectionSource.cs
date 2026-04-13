using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Contracts.Canary;

public interface ICanaryDetectionSource
{
    string AdapterType { get; }
    ContractBaseline GetContractBaseline();
    Task<ContractVerification> VerifyPreflightAsync(
        ContractBaseline approved, CancellationToken ct);
    Task<DetectionResult> DetectWithCanaryAsync(
        ContractBaseline approved, CancellationToken ct);
}

public record DetectionResult(
    IReadOnlyList<RxMetadata> Rxs,
    ContractVerification PostflightVerification);
