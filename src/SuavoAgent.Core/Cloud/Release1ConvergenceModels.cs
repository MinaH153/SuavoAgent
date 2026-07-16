using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.Cloud;

internal static class Release1ConvergenceCommand
{
    internal const string Name = "release1_convergence_challenge";
}

internal sealed record Release1ConvergenceChallenge(
    string CommandId,
    string InventorySha256,
    string BridgeReleaseTag,
    string BridgeSourceSha,
    string ExpiresAtUtc,
    SignedCommand Envelope);

internal sealed record Release1PreliminaryRequest(
    Release1PreliminaryConvergenceProof Proof,
    string ProofSignatureBase64Url);

internal sealed record Release1FinalRequest(
    Release1DeviceConvergenceAttestation Attestation,
    string AttestationSignatureBase64Url,
    string InstallReceiptSignatureBase64Url);

internal sealed record Release1ChallengeAckRequest(
    string Status,
    Release1ChallengeAckResult Result);

internal sealed record Release1ChallengeAckResult(
    string CommandId,
    string InventorySha256);
