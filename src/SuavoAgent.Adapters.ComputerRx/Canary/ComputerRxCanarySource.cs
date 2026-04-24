using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Adapters.ComputerRx.Canary;

/// <summary>
/// Computer-Rx schema-canary source scaffolding.
///
/// Every method intentionally throws <see cref="NotImplementedException"/>:
/// the canary contract for Computer-Rx cannot be written until recon
/// answers the questions tracked in
/// <c>docs/self-healing/second-pms-integration-spec.md</c> §"Data layer" and
/// §"Process + UI fingerprint".
///
/// This class exists so the adapter project compiles, the solution layout
/// matches the spec, and DI wiring can reference the adapter type when the
/// second-PMS kickoff begins.
///
/// DO NOT implement until Tier 1 5b is unblocked per Mission memo
/// (<c>MEMORY.md</c> → Mission: Square-level Suavo ecosystem, revenue-first).
/// </summary>
public sealed class ComputerRxCanarySource : ICanaryDetectionSource
{
    public string AdapterType => "computerrx";

    public ContractBaseline GetContractBaseline() =>
        throw new NotImplementedException(
            "Computer-Rx canary pending Tier 1 5b kickoff per Mission Loop deferral.");

    public Task<ContractVerification> VerifyPreflightAsync(
        ContractBaseline approved, CancellationToken ct) =>
        throw new NotImplementedException(
            "Computer-Rx canary pending Tier 1 5b kickoff per Mission Loop deferral.");

    public Task<DetectionResult> DetectWithCanaryAsync(
        ContractBaseline approved, CancellationToken ct) =>
        throw new NotImplementedException(
            "Computer-Rx canary pending Tier 1 5b kickoff per Mission Loop deferral.");
}
