using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    public RxCorrelationRegistrationResult RegisterApprovedFetch(
        ApprovedPatientFetchCommand command,
        string currentAgentId,
        string currentMachineFingerprint)
    {
        ValidateCommand(command);
        ValidateIdentity(currentAgentId, nameof(currentAgentId));
        ValidateIdentity(currentMachineFingerprint, nameof(currentMachineFingerprint));

        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var key = new RxCorrelationKey(command.PharmacyId, currentAgentId, command.RxHash, command.EvidenceId);

            var replay = document.Entries.SingleOrDefault(e =>
                string.Equals(e.CommandId, command.CommandId, StringComparison.Ordinal));
            if (replay is not null)
            {
                if (replay.State == StoredCorrelationState.Quarantined)
                    return new RxCorrelationRegistrationResult(
                        RxCorrelationRegistrationCode.CorrelationAlreadyClaimed,
                        null);
                if (!string.Equals(replay.SourceKind, command.SourceKind, StringComparison.Ordinal) ||
                    !string.Equals(replay.SourceBinding, command.SourceBinding, StringComparison.Ordinal))
                    return new RxCorrelationRegistrationResult(
                        RxCorrelationRegistrationCode.CommandReplayConflict,
                        null);
                if (!Matches(replay, key) ||
                    !string.Equals(replay.CandidateId, command.CandidateId, StringComparison.Ordinal) ||
                    !string.Equals(replay.MachineFingerprint, currentMachineFingerprint, StringComparison.Ordinal))
                {
                    return new RxCorrelationRegistrationResult(
                        RxCorrelationRegistrationCode.CommandReplayConflict,
                        null);
                }

                return new RxCorrelationRegistrationResult(
                    RxCorrelationRegistrationCode.Idempotent,
                    ToPending(replay));
            }

            var entry = document.Entries.SingleOrDefault(e =>
                Matches(e, key) &&
                string.Equals(e.SourceKind, command.SourceKind, StringComparison.Ordinal) &&
                string.Equals(e.SourceBinding, command.SourceBinding, StringComparison.Ordinal));
            if (entry is null)
                return new RxCorrelationRegistrationResult(RxCorrelationRegistrationCode.CorrelationNotFound, null);

            if (!string.Equals(entry.AgentId, currentAgentId, StringComparison.Ordinal) ||
                !string.Equals(entry.MachineFingerprint, currentMachineFingerprint, StringComparison.Ordinal))
            {
                return new RxCorrelationRegistrationResult(RxCorrelationRegistrationCode.IdentityMismatch, null);
            }

            if (!string.Equals(entry.SourceKind, command.SourceKind, StringComparison.Ordinal) ||
                !string.Equals(entry.SourceBinding, command.SourceBinding, StringComparison.Ordinal))
                return new RxCorrelationRegistrationResult(
                    RxCorrelationRegistrationCode.CorrelationNotFound,
                    null);

            if (entry.State != StoredCorrelationState.Observed || entry.CommandId is not null)
                return new RxCorrelationRegistrationResult(RxCorrelationRegistrationCode.CorrelationAlreadyClaimed, null);

            entry.CommandId = command.CommandId;
            entry.CandidateId = command.CandidateId;
            entry.State = StoredCorrelationState.AwaitingCallback;
            entry.LastUpdatedAtUtc = now;
            entry.AttemptCount = 0;
            entry.NextAttemptAtUtc = now;
            entry.AuthorizationExpiresAtUtc = now + PatientFetchAuthorizationTtl;
            entry.LastFailureCategory = null;
            entry.ExpiresAtUtc = now + PatientFetchAuthorizationTtl;
            Write(document);
            return new RxCorrelationRegistrationResult(
                RxCorrelationRegistrationCode.Registered,
                ToPending(entry));
        }
    }

    public IReadOnlyList<PendingApprovedPatientFetch> GetPending(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        int maxCount)
    {
        ValidateIdentity(pharmacyId, nameof(pharmacyId));
        ValidateIdentity(agentId, nameof(agentId));
        ValidateIdentity(machineFingerprint, nameof(machineFingerprint));
        if (maxCount <= 0 || maxCount > 32) throw new ArgumentOutOfRangeException(nameof(maxCount));

        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now, out var pruned);
            if (pruned) Write(document);

            return document.Entries
                .Where(e =>
                    (e.State is StoredCorrelationState.AwaitingCallback or StoredCorrelationState.CallbackAccepted) &&
                    string.Equals(e.PharmacyId, pharmacyId, StringComparison.Ordinal) &&
                    string.Equals(e.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(e.MachineFingerprint, machineFingerprint, StringComparison.Ordinal) &&
                    (e.State == StoredCorrelationState.CallbackAccepted ||
                     e.AuthorizationExpiresAtUtc is { } authorizationExpiry && authorizationExpiry > now) &&
                    (e.NextAttemptAtUtc is null || e.NextAttemptAtUtc <= now))
                .OrderBy(e => e.NextAttemptAtUtc ?? e.LastUpdatedAtUtc ?? e.CreatedAtUtc)
                .ThenBy(e => e.LastUpdatedAtUtc ?? e.CreatedAtUtc)
                .Take(maxCount)
                .Select(ToPending)
                .ToArray();
        }
    }

    public bool TryRevealRawRx(PendingApprovedPatientFetch pending, out string rawRxNumber)
    {
        ArgumentNullException.ThrowIfNull(pending);
        rawRxNumber = string.Empty;
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now, out var pruned);
            if (pruned) Write(document);
            var entry = FindExact(document, pending);
            if (entry is null || entry.State != StoredCorrelationState.AwaitingCallback ||
                string.IsNullOrEmpty(entry.ProtectedRx))
            {
                return false;
            }

            rawRxNumber = Reveal(entry);
            return true;
        }
    }

    public void MarkCallbackAccepted(
        PendingApprovedPatientFetch pending,
        string stagingId,
        string transitionId,
        DateTimeOffset callbackExpiresAtUtc)
    {
        ValidateUuid(stagingId, nameof(stagingId));
        ValidateUuid(transitionId, nameof(transitionId));
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = FindExact(document, pending)
                        ?? throw new InvalidOperationException("Pending Rx correlation no longer exists.");

            if (entry.State == StoredCorrelationState.Completed)
                return;
            if (entry.State == StoredCorrelationState.CallbackAccepted)
            {
                if (!string.Equals(entry.StagingId, stagingId, StringComparison.Ordinal) ||
                    !string.Equals(entry.TransitionId, transitionId, StringComparison.Ordinal))
                    throw new InvalidDataException("Authenticated callback receipt conflicts with local state.");
                return;
            }
            if (entry.State != StoredCorrelationState.AwaitingCallback)
                throw new InvalidOperationException("Rx correlation is not awaiting a callback.");

            entry.State = StoredCorrelationState.CallbackAccepted;
            entry.StagingId = stagingId;
            entry.TransitionId = transitionId;
            entry.CallbackExpiresAtUtc = callbackExpiresAtUtc;
            entry.LastUpdatedAtUtc = now;
            entry.AttemptCount = 0;
            entry.NextAttemptAtUtc = now;
            entry.LastFailureCategory = null;
            // The staged PHI expires on the cloud receipt timestamp, but this PHI-free local state
            // must outlive a transient ACK outage so the command can converge to terminal status.
            entry.ExpiresAtUtc = now + _ttl;
            Write(document);
        }
    }

    public void MarkCompleted(PendingApprovedPatientFetch pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = FindExact(document, pending)
                        ?? throw new InvalidOperationException("Pending Rx correlation no longer exists.");
            if (entry.State == StoredCorrelationState.Completed) return;
            if (entry.State != StoredCorrelationState.CallbackAccepted)
                throw new InvalidOperationException("Cannot complete an Rx correlation before callback acceptance.");

            entry.State = StoredCorrelationState.Completed;
            entry.LastUpdatedAtUtc = now;
            entry.ExpiresAtUtc = now + _ttl; // Replay tombstone; contains no PHI.
            Write(document);
        }
    }

    public void DeferPatientFetch(
        PendingApprovedPatientFetch pending,
        string failureCategory,
        bool quarantine)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (string.IsNullOrWhiteSpace(failureCategory) || failureCategory.Length > 64 ||
            failureCategory.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Patient fetch failure category is invalid.", nameof(failureCategory));

        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = FindExact(document, pending);
            if (entry is null ||
                entry.State is not (StoredCorrelationState.AwaitingCallback or StoredCorrelationState.CallbackAccepted))
                return;

            entry.AttemptCount = Math.Min(entry.AttemptCount + 1, MaxPatientFetchAttempts);
            entry.LastFailureCategory = failureCategory;
            entry.LastUpdatedAtUtc = now;
            if (entry.State == StoredCorrelationState.CallbackAccepted && quarantine)
            {
                Quarantine(entry, now);
                Write(document);
                return;
            }
            if (entry.State == StoredCorrelationState.CallbackAccepted)
            {
                var ackExponent = Math.Min(entry.AttemptCount - 1, 6);
                var ackSeconds = Math.Min(30 * Math.Pow(2, ackExponent), 1_800);
                var ackJitter = Math.Abs(StringComparer.Ordinal.GetHashCode(entry.CommandId ?? "")) % 11;
                entry.NextAttemptAtUtc = now + TimeSpan.FromSeconds(ackSeconds + ackJitter);
                Write(document);
                return;
            }

            var expired = entry.AuthorizationExpiresAtUtc is null ||
                          entry.AuthorizationExpiresAtUtc <= now;
            if (quarantine || expired || entry.AttemptCount >= MaxPatientFetchAttempts)
            {
                Quarantine(entry, now);
            }
            else
            {
                var exponent = Math.Min(entry.AttemptCount - 1, 6);
                var seconds = Math.Min(30 * Math.Pow(2, exponent), 1_800);
                var jitter = Math.Abs(StringComparer.Ordinal.GetHashCode(entry.CommandId ?? "")) % 11;
                entry.NextAttemptAtUtc = now + TimeSpan.FromSeconds(seconds + jitter);
                entry.ExpiresAtUtc = entry.AuthorizationExpiresAtUtc!.Value;
            }
            Write(document);
        }
    }

    public void PruneExpired()
    {
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            _ = ReadAndPrune(_timeProvider.GetUtcNow());
        }
    }

    public IReadOnlyList<PendingApprovedPatientFailure> GetUnacknowledgedFailures(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        int maxCount)
    {
        ValidateIdentity(pharmacyId, nameof(pharmacyId));
        ValidateIdentity(agentId, nameof(agentId));
        ValidateIdentity(machineFingerprint, nameof(machineFingerprint));
        if (maxCount <= 0 || maxCount > 32) throw new ArgumentOutOfRangeException(nameof(maxCount));

        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            var document = ReadAndPrune(_timeProvider.GetUtcNow(), out var pruned);
            if (pruned) Write(document);
            return document.Entries
                .Where(entry =>
                    entry.State == StoredCorrelationState.Quarantined &&
                    entry.FailureAckedAtUtc is null &&
                    entry.CommandId is not null &&
                    entry.CandidateId is not null &&
                    entry.LastFailureCategory is not null &&
                    string.Equals(entry.PharmacyId, pharmacyId, StringComparison.Ordinal) &&
                    string.Equals(entry.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(entry.MachineFingerprint, machineFingerprint, StringComparison.Ordinal))
                .OrderBy(entry => entry.LastUpdatedAtUtc ?? entry.CreatedAtUtc)
                .Take(maxCount)
                .Select(entry => new PendingApprovedPatientFailure(
                    entry.CommandId!,
                    entry.CandidateId!,
                    entry.LastFailureCategory!))
                .ToArray();
        }
    }

    public void MarkFailureAcknowledged(
        PendingApprovedPatientFailure failure,
        string pharmacyId,
        string agentId,
        string machineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ValidateUuid(failure.CommandId, nameof(failure.CommandId));
        ValidateUuid(failure.CandidateId, nameof(failure.CandidateId));
        ValidateIdentity(pharmacyId, nameof(pharmacyId));
        ValidateIdentity(agentId, nameof(agentId));
        ValidateIdentity(machineFingerprint, nameof(machineFingerprint));
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = document.Entries.SingleOrDefault(candidate =>
                candidate.State == StoredCorrelationState.Quarantined &&
                string.Equals(candidate.CommandId, failure.CommandId, StringComparison.Ordinal) &&
                string.Equals(candidate.CandidateId, failure.CandidateId, StringComparison.Ordinal) &&
                string.Equals(candidate.LastFailureCategory, failure.FailureCategory, StringComparison.Ordinal) &&
                string.Equals(candidate.PharmacyId, pharmacyId, StringComparison.Ordinal) &&
                string.Equals(candidate.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(candidate.MachineFingerprint, machineFingerprint, StringComparison.Ordinal));
            if (entry is null)
                throw new InvalidOperationException("Quarantined Rx correlation no longer exists.");
            if (entry.FailureAckedAtUtc is not null) return;
            entry.FailureAckedAtUtc = now;
            Write(document);
        }
    }

}
