using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    public void UpsertObservation(RxCorrelationObservation observation)
    {
        ValidateObservation(observation);
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = document.Entries.SingleOrDefault(e =>
                Matches(e, observation.Key) &&
                string.Equals(e.SourceKind, observation.SourceKind, StringComparison.Ordinal) &&
                string.Equals(e.SourceBinding, observation.SourceBinding, StringComparison.Ordinal));

            if (entry is not null)
            {
                if (!string.Equals(entry.MachineFingerprint, observation.MachineFingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("Rx correlation identity changed for an existing evidence key.");
                if (entry.FillNumber != observation.FillNumber)
                    throw new InvalidDataException("Rx correlation fill number changed for an existing evidence key.");
                // Terminal/pending entries are immutable. Re-observing the same PioneerRx row must not
                // resurrect raw PHI or replace a command already in flight.
                if (entry.State != StoredCorrelationState.Observed)
                    return;

                var existing = Reveal(entry);
                try
                {
                    if (!FixedTimeUtf8Equals(existing, observation.RawRxNumber))
                        throw new InvalidDataException("Rx correlation lookup material changed for an existing evidence key.");
                }
                finally
                {
                    ZeroStringBytes(existing);
                }

                return; // Do not extend TTL indefinitely on repeated polling.
            }

            MakeRoom(document);
            var entropy = BuildEntropy(
                observation.Key,
                observation.MachineFingerprint,
                observation.FillNumber,
                protectionVersion: 3,
                sourceKind: observation.SourceKind,
                sourceBinding: observation.SourceBinding);
            var clearBytes = Encoding.UTF8.GetBytes(observation.RawRxNumber);
            byte[] protectedBytes;
            try
            {
                protectedBytes = _protector.Protect(clearBytes, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
                CryptographicOperations.ZeroMemory(entropy);
            }

            if (protectedBytes.Length is <= 0 or > MaxProtectedRxBytes)
                throw new InvalidDataException("Protected Rx correlation size is invalid.");

            document.Entries.Add(new StoredCorrelation
            {
                PharmacyId = observation.Key.PharmacyId,
                AgentId = observation.Key.AgentId,
                MachineFingerprint = observation.MachineFingerprint,
                RxHash = observation.Key.RxHash,
                EvidenceId = observation.Key.EvidenceId,
                FillNumber = observation.FillNumber,
                ProtectionVersion = 3,
                SourceKind = observation.SourceKind,
                SourceBinding = observation.SourceBinding,
                ProtectedRx = Convert.ToBase64String(protectedBytes),
                CreatedAtUtc = now,
                ExpiresAtUtc = now + _ttl,
                State = StoredCorrelationState.Observed,
            });
            CryptographicOperations.ZeroMemory(protectedBytes);
            Write(document);
        }
    }

}
