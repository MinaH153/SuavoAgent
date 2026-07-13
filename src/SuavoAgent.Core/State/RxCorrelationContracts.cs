using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SuavoAgent.Core.State;

internal static class RxCorrelationSourceKinds
{
    internal const string PioneerRxBuiltIn = "pioneerrx_builtin";
    internal const string LearnedApproved = "learned_approved";
}

internal sealed record RxCorrelationKey(
    string PharmacyId,
    string AgentId,
    string RxHash,
    string EvidenceId);

internal sealed record RxCorrelationObservation(
    RxCorrelationKey Key,
    string MachineFingerprint,
    string RawRxNumber,
    int FillNumber = 0,
    string SourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
    string? SourceBinding = null);

internal sealed record ApprovedPatientFetchCommand(
    string CandidateId,
    string RxHash,
    string EvidenceId,
    string PharmacyId,
    string CommandId,
    string SourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
    string? SourceBinding = null);

internal enum RxCorrelationCommandState
{
    AwaitingCallback,
    CallbackAccepted,
    Completed,
}

internal sealed record PendingApprovedPatientFetch(
    RxCorrelationKey Key,
    string MachineFingerprint,
    string CandidateId,
    string CommandId,
    RxCorrelationCommandState State,
    string SourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
    string? SourceBinding = null,
    int AttemptCount = 0,
    DateTimeOffset? NextAttemptAtUtc = null,
    DateTimeOffset? AuthorizationExpiresAtUtc = null,
    DateTimeOffset? CallbackExpiresAtUtc = null);

internal sealed record PendingApprovedPatientFailure(
    string CommandId,
    string CandidateId,
    string FailureCategory);

internal enum RxCorrelationRegistrationCode
{
    Registered,
    Idempotent,
    CorrelationNotFound,
    IdentityMismatch,
    CommandReplayConflict,
    CorrelationAlreadyClaimed,
}

internal sealed record RxCorrelationRegistrationResult(
    RxCorrelationRegistrationCode Code,
    PendingApprovedPatientFetch? Pending)
{
    internal bool Accepted =>
        Code is RxCorrelationRegistrationCode.Registered or RxCorrelationRegistrationCode.Idempotent;
}

internal interface IRxCorrelationProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] protectedBytes, byte[] entropy);
}

internal interface IRxCorrelationStore
{
    void UpsertObservation(RxCorrelationObservation observation);

    RxCorrelationRegistrationResult RegisterApprovedFetch(
        ApprovedPatientFetchCommand command,
        string currentAgentId,
        string currentMachineFingerprint);

    IReadOnlyList<PendingApprovedPatientFetch> GetPending(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        int maxCount);

    bool TryRevealRawRx(PendingApprovedPatientFetch pending, out string rawRxNumber);

    void MarkCallbackAccepted(
        PendingApprovedPatientFetch pending,
        string stagingId,
        string transitionId,
        DateTimeOffset callbackExpiresAtUtc);

    void MarkCompleted(PendingApprovedPatientFetch pending);

    void DeferPatientFetch(
        PendingApprovedPatientFetch pending,
        string failureCategory,
        bool quarantine);

    void PruneExpired();

    IReadOnlyList<PendingApprovedPatientFailure> GetUnacknowledgedFailures(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        int maxCount);

    void MarkFailureAcknowledged(
        PendingApprovedPatientFailure failure,
        string pharmacyId,
        string agentId,
        string machineFingerprint);

    WritebackCorrelationRegistrationResult RegisterDeliveryWriteback(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint);

    bool TryRevealDeliveryWriteback(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint,
        out SensitiveRxBuffer? rawRxNumber,
        out int fillNumber);

    void MarkDeliveryWritebackReceiptVerified(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint,
        DeliveryWritebackResultCode resultCode);
}

internal sealed class SensitiveRxBuffer : IDisposable
{
    private char[]? _value;

    internal SensitiveRxBuffer(char[] value) =>
        _value = value ?? throw new ArgumentNullException(nameof(value));

    internal ReadOnlyMemory<char> Memory =>
        _value is { } value
            ? value
            : throw new ObjectDisposedException(nameof(SensitiveRxBuffer));

    internal bool IsDisposed => _value is null;

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null) Array.Clear(value);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DpapiRxCorrelationProtector : IRxCorrelationProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy) =>
        ProtectedData.Protect(plaintext, entropy, DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] protectedBytes, byte[] entropy) =>
        ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.LocalMachine);
}
