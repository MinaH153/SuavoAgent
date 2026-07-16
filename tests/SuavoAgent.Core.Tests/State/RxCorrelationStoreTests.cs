using System.Security.Cryptography;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class RxCorrelationStoreTests : IDisposable
{
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string AgentId = "agent-install-1";
    private const string Fingerprint = "machine-fingerprint-1";
    private const string CandidateId = "00000000-0000-4000-8000-000000000002";
    private const string CommandId = "00000000-0000-4000-8000-000000000003";
    private const string WritebackId = "00000000-0000-4000-8000-000000000006";
    private const string WritebackCommandId = "00000000-0000-4000-8000-000000000007";
    private const string OrderId = "00000000-0000-4000-8000-000000000008";
    private const string InboxItemId = "00000000-0000-4000-8000-000000000009";
    private const string RawRx = "RX-123456";
    private const string LearnedDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string RxHash = PhiScrubber.HmacHash(RawRx, "test-hmac-key");
    private static readonly string EvidenceId = $"rxh-{RxHash[..16]}-1770000000";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-rx-correlation-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Observation_PersistsOnlyAuthenticatedCiphertext_ThenResolvesExactCommand()
    {
        var path = Path.Combine(_directory, "rx-correlations.json");
        var store = CreateStore(path);
        store.UpsertObservation(Observation());

        var disk = File.ReadAllText(path);
        Assert.DoesNotContain(RawRx, disk, StringComparison.Ordinal);
        Assert.DoesNotContain("rawRx", disk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RxHash, disk, StringComparison.Ordinal);
        Assert.Contains(EvidenceId, disk, StringComparison.Ordinal);

        var registration = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint);
        Assert.Equal(RxCorrelationRegistrationCode.Registered, registration.Code);
        var pending = Assert.Single(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        Assert.True(store.TryRevealRawRx(pending, out var revealed));
        Assert.Equal(RawRx, revealed);
    }

    [Fact]
    public void Registration_IsIdempotent_ButCommandCannotMoveToDifferentEvidence()
    {
        var store = CreateStore(Path.Combine(_directory, "store.json"));
        store.UpsertObservation(Observation());

        Assert.Equal(
            RxCorrelationRegistrationCode.Registered,
            store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Code);
        Assert.Equal(
            RxCorrelationRegistrationCode.Idempotent,
            store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Code);

        var otherHash = new string('b', 64);
        var otherEvidence = $"rxh-{otherHash[..16]}-1770000001";
        store.UpsertObservation(new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, otherHash, otherEvidence),
            Fingerprint,
            "RX-OTHER"));
        var moved = Command() with { RxHash = otherHash, EvidenceId = otherEvidence };
        Assert.Equal(
            RxCorrelationRegistrationCode.CommandReplayConflict,
            store.RegisterApprovedFetch(moved, AgentId, Fingerprint).Code);
    }

    [Fact]
    public void RepeatedPollOfSameEvidence_StoresOneCorrelationWithoutExtendingTtl()
    {
        var path = Path.Combine(_directory, "store.json");
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(path, time, TimeSpan.FromHours(1));
        store.UpsertObservation(Observation());
        var firstJson = File.ReadAllText(path);
        time.Advance(TimeSpan.FromMinutes(30));
        store.UpsertObservation(Observation());
        var secondJson = File.ReadAllText(path);

        Assert.Equal(firstJson, secondJson);
        using var document = System.Text.Json.JsonDocument.Parse(secondJson);
        Assert.Equal(1, document.RootElement.GetProperty("entries").GetArrayLength());

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(
            RxCorrelationRegistrationCode.CorrelationNotFound,
            store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Code);
    }

    [Fact]
    public void PatientCallback_HidesRawFromPatientPath_ButRetainsItForExactWriteback()
    {
        var path = Path.Combine(_directory, "store.json");
        var store = CreateStore(path);
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;

        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z"));

        var accepted = Assert.Single(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        Assert.Equal(RxCorrelationCommandState.CallbackAccepted, accepted.State);
        Assert.False(store.TryRevealRawRx(accepted, out _));
        Assert.DoesNotContain(RawRx, File.ReadAllText(path), StringComparison.Ordinal);

        store.MarkCompleted(accepted);
        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        Assert.Equal(
            RxCorrelationRegistrationCode.Idempotent,
            store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Code);

        // Polling the same row again cannot resurrect lookup material while the replay tombstone lives.
        store.UpsertObservation(Observation());
        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));

        var writeback = WritebackCommand();
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(writeback, AgentId, Fingerprint).Code);
        Assert.True(store.TryRevealDeliveryWriteback(
            writeback, AgentId, Fingerprint, out var rawRx, out var fillNumber));
        using (rawRx)
        {
            Assert.Equal(RawRx, new string(rawRx!.Memory.Span));
        }
        Assert.Equal(2, fillNumber);
    }

    [Fact]
    public void CompletedPatientFetch_PurgesRawAtBoundedRetentionDeadline()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(
            Path.Combine(_directory, "retained.json"),
            time,
            TimeSpan.FromMinutes(10));
        CompletePatientFetch(store);

        time.Advance(TimeSpan.FromMinutes(11));
        var command = WritebackCommand();
        Assert.Equal(
            WritebackCorrelationRegistrationCode.RawLookupUnavailable,
            store.RegisterDeliveryWriteback(command, AgentId, Fingerprint).Code);
        Assert.False(store.TryRevealDeliveryWriteback(
            command, AgentId, Fingerprint, out _, out _));
        Assert.Contains(
            "\"lookupMaterialPurged\":true",
            File.ReadAllText(Path.Combine(_directory, "retained.json")));
    }

    [Fact]
    public void ExpiredPendingWriteback_AtomicallyTerminatesClaimAndPurgesRaw()
    {
        var path = Path.Combine(_directory, "expired-writeback.json");
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(path, time, TimeSpan.FromMinutes(10));
        CompletePatientFetch(store);
        var command = WritebackCommand();
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(command, AgentId, Fingerprint).Code);

        time.Advance(TimeSpan.FromMinutes(11));
        store.PruneExpired();
        store.PruneExpired();

        Assert.False(store.TryRevealDeliveryWriteback(
            command, AgentId, Fingerprint, out _, out _));
        Assert.Equal(
            WritebackCorrelationRegistrationCode.RawLookupUnavailable,
            store.RegisterDeliveryWriteback(command, AgentId, Fingerprint).Code);
        var persisted = File.ReadAllText(path);
        Assert.Contains("\"state\":\"Expired\"", persisted);
        Assert.Contains("writeback_authorization_expired", persisted);
        Assert.Contains("\"lookupMaterialPurged\":true", persisted);
    }

    [Fact]
    public void PickupReceipt_RetainsRawForLaterCompleteTransition()
    {
        var store = CreateStore(Path.Combine(_directory, "pickup.json"));
        CompletePatientFetch(store);
        var pickup = WritebackCommand() with
        {
            Transition = "pickup",
            ProofRecordId = null,
            ProofDigest = null,
        };

        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(pickup, AgentId, Fingerprint).Code);
        store.MarkDeliveryWritebackReceiptVerified(
            pickup, AgentId, Fingerprint, DeliveryWritebackResultCode.AlreadyAtTarget);

        var complete = WritebackCommand() with
        {
            WritebackId = "00000000-0000-4000-8000-000000000010",
            CommandId = "00000000-0000-4000-8000-000000000011",
        };
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(complete, AgentId, Fingerprint).Code);
        Assert.True(store.TryRevealDeliveryWriteback(
            complete, AgentId, Fingerprint, out var rawRx, out var fillNumber));
        using (rawRx)
        {
            Assert.Equal(RawRx, new string(rawRx!.Memory.Span));
        }
        Assert.Equal(2, fillNumber);
    }

    [Fact]
    public void NeedsAttentionCompletion_RetainsRawForOneSignedSuccessor()
    {
        var store = CreateStore(Path.Combine(_directory, "complete-retry.json"));
        CompletePatientFetch(store);
        var first = WritebackCommand();

        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(first, AgentId, Fingerprint).Code);
        store.MarkDeliveryWritebackReceiptVerified(
            first, AgentId, Fingerprint, DeliveryWritebackResultCode.RetryExhausted);

        var successor = first with
        {
            WritebackId = "00000000-0000-4000-8000-000000000013",
            CommandId = "00000000-0000-4000-8000-000000000014",
        };
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(successor, AgentId, Fingerprint).Code);
        Assert.True(store.TryRevealDeliveryWriteback(
            successor, AgentId, Fingerprint, out var rawRx, out _));
        rawRx?.Dispose();

        store.MarkDeliveryWritebackReceiptVerified(
            successor, AgentId, Fingerprint, DeliveryWritebackResultCode.Success);
        var afterSuccess = successor with
        {
            WritebackId = "00000000-0000-4000-8000-000000000015",
            CommandId = "00000000-0000-4000-8000-000000000016",
        };
        Assert.Equal(
            WritebackCorrelationRegistrationCode.RawLookupUnavailable,
            store.RegisterDeliveryWriteback(afterSuccess, AgentId, Fingerprint).Code);
    }

    [Fact]
    public void SensitiveRxBuffer_DisposeZeroesOwnedStorage()
    {
        var storage = RawRx.ToCharArray();
        var buffer = new SensitiveRxBuffer(storage);

        buffer.Dispose();

        Assert.True(buffer.IsDisposed);
        Assert.All(storage, value => Assert.Equal('\0', value));
        Assert.Throws<ObjectDisposedException>(() => _ = buffer.Memory);
    }

    [Fact]
    public void Writeback_RequiresCompletedPatientRetrievalAndExactCandidateBinding()
    {
        var store = CreateStore(Path.Combine(_directory, "binding.json"));
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;

        Assert.Equal(
            WritebackCorrelationRegistrationCode.PatientRetrievalIncomplete,
            store.RegisterDeliveryWriteback(WritebackCommand(), AgentId, Fingerprint).Code);

        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z"));
        store.MarkCompleted(pending with { State = RxCorrelationCommandState.CallbackAccepted });

        var wrongCandidate = WritebackCommand() with
        {
            CandidateId = "00000000-0000-4000-8000-000000000012",
        };
        Assert.Equal(
            WritebackCorrelationRegistrationCode.CandidateMismatch,
            store.RegisterDeliveryWriteback(wrongCandidate, AgentId, Fingerprint).Code);

        var exact = WritebackCommand();
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Registered,
            store.RegisterDeliveryWriteback(exact, AgentId, Fingerprint).Code);
        Assert.Equal(
            WritebackCorrelationRegistrationCode.Idempotent,
            store.RegisterDeliveryWriteback(exact, AgentId, Fingerprint).Code);
        Assert.Equal(
            WritebackCorrelationRegistrationCode.CommandReplayConflict,
            store.RegisterDeliveryWriteback(
                exact with { OrderId = "00000000-0000-4000-8000-000000000013" },
                AgentId,
                Fingerprint).Code);
    }

    [Fact]
    public void ExpiredObservation_IsPruned_AndCannotBeApproved()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(
            Path.Combine(_directory, "store.json"),
            time,
            TimeSpan.FromMinutes(10));
        store.UpsertObservation(Observation());
        time.Advance(TimeSpan.FromMinutes(11));

        var result = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint);
        Assert.Equal(RxCorrelationRegistrationCode.CorrelationNotFound, result.Code);
    }

    [Fact]
    public void TamperedCiphertext_FailsClosed()
    {
        var path = Path.Combine(_directory, "store.json");
        var store = CreateStore(path);
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;

        var json = File.ReadAllText(path);
        var marker = "\"protectedRx\":\"";
        var index = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.True(index >= marker.Length);
        var replacement = json[index] == 'A' ? 'B' : 'A';
        File.WriteAllText(path, json[..index] + replacement + json[(index + 1)..]);

        Assert.Throws<InvalidDataException>(() => store.TryRevealRawRx(pending, out _));
    }

    [Fact]
    public void LearnedObservation_RoundTripsExactApprovedTemplateBinding()
    {
        var store = CreateStore(Path.Combine(_directory, "learned.json"));
        store.UpsertObservation(Observation() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        });

        var pending = store.RegisterApprovedFetch(LearnedCommand(), AgentId, Fingerprint).Pending!;

        Assert.Equal(RxCorrelationSourceKinds.LearnedApproved, pending.SourceKind);
        Assert.Equal(LearnedDigest, pending.SourceBinding);
        Assert.True(store.TryRevealRawRx(pending, out var rawRx));
        Assert.Equal(RawRx, rawRx);
    }

    [Fact]
    public void LearnedObservation_TamperedTemplateBindingInvalidatesCiphertext()
    {
        var path = Path.Combine(_directory, "learned-tampered.json");
        var store = CreateStore(path);
        store.UpsertObservation(Observation() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        });
        var pending = store.RegisterApprovedFetch(LearnedCommand(), AgentId, Fingerprint).Pending!;
        var json = File.ReadAllText(path).Replace(LearnedDigest, "b" + LearnedDigest[1..], StringComparison.Ordinal);
        File.WriteAllText(path, json);

        Assert.Throws<InvalidDataException>(() => store.TryRevealRawRx(
            pending with { SourceBinding = "b" + LearnedDigest[1..] },
            out _));
    }

    [Fact]
    public void SameEvidenceFromBuiltInAndLearnedSources_CoexistsWithoutFallthrough()
    {
        var store = CreateStore(Path.Combine(_directory, "source-conflict.json"));
        store.UpsertObservation(Observation());

        store.UpsertObservation(Observation() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        });

        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_directory, "source-conflict.json")));
        Assert.Equal(2, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void LearnedCorrelation_NeverFallsThroughToBuiltInPioneerWriteback()
    {
        var store = CreateStore(Path.Combine(_directory, "learned-writeback.json"));
        store.UpsertObservation(Observation() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        });
        var pending = store.RegisterApprovedFetch(
            LearnedCommand(), AgentId, Fingerprint).Pending!;
        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z"));
        store.MarkCompleted(pending with { State = RxCorrelationCommandState.CallbackAccepted });

        Assert.Equal(
            WritebackCorrelationRegistrationCode.SourceUnsupported,
            store.RegisterDeliveryWriteback(
                WritebackCommand(), AgentId, Fingerprint).Code);
    }

    [Fact]
    public void DeferredPatientFetch_UsesDurableBackoffWithoutBlockingNewerWork()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(Path.Combine(_directory, "backoff.json"), time);
        store.UpsertObservation(Observation());
        var first = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;
        store.DeferPatientFetch(first, "callback_unavailable", quarantine: false);

        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));

        var otherHash = new string('b', 64);
        var otherEvidence = $"rxh-{otherHash[..16]}-1770000001";
        store.UpsertObservation(new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, otherHash, otherEvidence),
            Fingerprint,
            "RX-OTHER"));
        var other = Command() with
        {
            CandidateId = "00000000-0000-4000-8000-000000000014",
            CommandId = "00000000-0000-4000-8000-000000000015",
            RxHash = otherHash,
            EvidenceId = otherEvidence,
        };
        Assert.True(store.RegisterApprovedFetch(other, AgentId, Fingerprint).Accepted);
        Assert.Equal(other.CommandId, Assert.Single(
            store.GetPending(PharmacyId, AgentId, Fingerprint, 8)).CommandId);

        time.Advance(TimeSpan.FromMinutes(1));
        var eligible = store.GetPending(PharmacyId, AgentId, Fingerprint, 8);
        Assert.Equal(2, eligible.Count);
        Assert.Contains(eligible, item => item.CommandId == first.CommandId && item.AttemptCount == 1);
    }

    [Fact]
    public void ExpiredPatientAuthorization_QuarantinesAndPurgesLookupMaterial()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(Path.Combine(_directory, "authorization-expired.json"), time);
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;

        time.Advance(RxCorrelationStore.PatientFetchAuthorizationTtl + TimeSpan.FromSeconds(1));

        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        Assert.False(store.TryRevealRawRx(pending, out _));
        Assert.Equal(
            RxCorrelationRegistrationCode.CorrelationAlreadyClaimed,
            store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Code);
        Assert.DoesNotContain(RawRx, File.ReadAllText(Path.Combine(_directory, "authorization-expired.json")));
    }

    [Fact]
    public void ExpiredCallbackReceipt_IsQuarantinedBeforeCommandAck()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(Path.Combine(_directory, "callback-expired.json"), time);
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;
        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z"));

        time.Advance(TimeSpan.FromMinutes(31));
        store.PruneExpired();

        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        Assert.False(store.TryRevealRawRx(
            pending with { State = RxCorrelationCommandState.CallbackAccepted }, out _));
        Assert.Contains(
            "callback_receipt_expired",
            File.ReadAllText(Path.Combine(_directory, "callback-expired.json")));
    }

    private RxCorrelationStore CreateStore(
        string path,
        TimeProvider? time = null,
        TimeSpan? ttl = null) =>
        new(
            path,
            new TestProtector(),
            time ?? new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z")),
            ttl,
            maxEntries: 32);

    private static RxCorrelationObservation Observation() => new(
        new RxCorrelationKey(PharmacyId, AgentId, RxHash, EvidenceId),
        Fingerprint,
        RawRx,
        FillNumber: 2);

    private static ApprovedPatientFetchCommand Command() => new(
        CandidateId,
        RxHash,
        EvidenceId,
        PharmacyId,
        CommandId);

    private static ApprovedPatientFetchCommand LearnedCommand() => Command() with
    {
        SourceKind = RxCorrelationSourceKinds.LearnedApproved,
        SourceBinding = LearnedDigest,
    };

    private static AgentDeliveryWritebackCommand WritebackCommand() => new(
        2,
        WritebackId,
        CandidateId,
        RxHash,
        EvidenceId,
        PharmacyId,
        OrderId,
        InboxItemId,
        "00000000-0000-4000-8000-000000000024",
        "00000000-0000-4000-8000-000000000025",
        new string('b', 64),
        "complete",
        "2026-07-10T12:15:00.000Z",
        WritebackCommandId);

    private static void CompletePatientFetch(RxCorrelationStore store)
    {
        store.UpsertObservation(Observation());
        var pending = store.RegisterApprovedFetch(Command(), AgentId, Fingerprint).Pending!;
        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.Parse("2026-07-10T12:30:00Z"));
        store.MarkCompleted(pending with { State = RxCorrelationCommandState.CallbackAccepted });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class TestProtector : IRxCorrelationProtector
    {
        private static readonly byte[] Key = SHA256.HashData("test-only-rx-correlation-key"u8.ToArray());

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, tagSizeInBytes: 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
            return [.. nonce, .. tag, .. ciphertext];
        }

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            if (protectedBytes.Length < 29) throw new CryptographicException("invalid test ciphertext");
            var nonce = protectedBytes[..12];
            var tag = protectedBytes[12..28];
            var ciphertext = protectedBytes[28..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(Key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, entropy);
            return plaintext;
        }
    }
}
