using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class ApprovedPatientRetrievalTests : IDisposable
{
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string AgentId = "agent-install-1";
    private const string Fingerprint = "machine-fingerprint-1";
    private const string CandidateId = "00000000-0000-4000-8000-000000000002";
    private const string CommandId = "00000000-0000-4000-8000-000000000003";
    private const string RawRx = "RX-123456";
    private const string HmacKey = "test-hmac-key";
    private const string LearnedDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-approved-fetch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDb _db;

    public ApprovedPatientRetrievalTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new AgentStateDb(Path.Combine(_directory, "state.db"));
    }

    [Fact]
    public void CommandContract_AcceptsOnlyExactSourceBoundHashOnlyShape()
    {
        var command = Command();
        var valid = JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "candidateId":"{{command.CandidateId}}",
              "rxHash":"{{command.RxHash}}",
              "evidenceId":"{{command.EvidenceId}}",
              "pharmacyId":"{{command.PharmacyId}}",
              "commandId":"{{command.CommandId}}",
              "sourceKind":"pioneerrx_builtin",
              "sourceBinding":null
            }
            """);

        Assert.True(FetchPatientCommandContract.TryParse(valid, out var parsed, out _));
        Assert.Equal(command, parsed);

        var withRawRx = JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "candidateId":"{{command.CandidateId}}",
              "rxHash":"{{command.RxHash}}",
              "evidenceId":"{{command.EvidenceId}}",
              "pharmacyId":"{{command.PharmacyId}}",
              "commandId":"{{command.CommandId}}",
              "sourceKind":"pioneerrx_builtin",
              "sourceBinding":null,
              "rxNumber":"{{RawRx}}"
            }
            """);
        Assert.False(FetchPatientCommandContract.TryParse(withRawRx, out _, out var reason));
        Assert.Equal("fetch_data_shape_mismatch", reason);
    }

    [Fact]
    public async Task PatientQuery_HappensOnlyAfterSignedCommandRegistration_ThenCallbackBeforeAck()
    {
        var store = CreateStore();
        store.UpsertObservation(Observation());
        var source = new FakeSource();
        var cloud = new FakeCloud { CallbackReceipt = Receipt() };
        var coordinator = CreateCoordinator(store, source, cloud);

        await coordinator.RetryPendingAsync(default);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, cloud.CallbackCount);
        Assert.Equal(0, cloud.AckCount);

        Assert.True(coordinator.Register(Command()).Accepted);
        await coordinator.RetryPendingAsync(default);

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cloud.CallbackCount);
        Assert.Equal(1, cloud.AckCount);
        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
    }

    [Fact]
    public async Task ClosedEgressGate_DoesNotReadPatientPhi()
    {
        var store = CreateStore();
        store.UpsertObservation(Observation());
        var source = new FakeSource();
        var cloud = new FakeCloud { CallbackReceipt = Receipt() };
        var coordinator = new ApprovedPatientRetrievalCoordinator(
            new AgentOptions
            {
                PharmacyId = PharmacyId,
                AgentId = AgentId,
                MachineFingerprint = Fingerprint,
                HmacSalt = HmacKey,
                EnableAuditedPatientDetailsEgress = false,
            },
            store,
            _db,
            source,
            cloud,
            NullLogger.Instance,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z")));
        Assert.True(coordinator.Register(Command()).Accepted);

        await coordinator.RetryPendingAsync(default);

        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, cloud.CallbackCount);
        Assert.Equal(0, cloud.AckCount);
    }

    [Fact]
    public async Task UnsignedOrRejectedCallback_NeverAcknowledgesCommand()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(time);
        store.UpsertObservation(Observation());
        var source = new FakeSource();
        var cloud = new FakeCloud { CallbackReceipt = null };
        var coordinator = CreateCoordinator(store, source, cloud);
        Assert.True(coordinator.Register(Command()).Accepted);

        await coordinator.RetryPendingAsync(default);

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cloud.CallbackCount);
        Assert.Equal(0, cloud.AckCount);
        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(
            RxCorrelationCommandState.AwaitingCallback,
            Assert.Single(store.GetPending(PharmacyId, AgentId, Fingerprint, 8)).State);
    }

    [Fact]
    public async Task AckFailure_RetriesAckWithoutRequeryingOrResendingPhi()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(time);
        store.UpsertObservation(Observation());
        var source = new FakeSource();
        var cloud = new FakeCloud
        {
            CallbackReceipt = Receipt(),
            AckResults = new Queue<bool>(new[] { false, true }),
        };
        var coordinator = CreateCoordinator(store, source, cloud);
        Assert.True(coordinator.Register(Command()).Accepted);

        await coordinator.RetryPendingAsync(default);
        time.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RetryPendingAsync(default);

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cloud.CallbackCount);
        Assert.Equal(2, cloud.AckCount);
        Assert.Empty(store.GetPending(PharmacyId, AgentId, Fingerprint, 8));
    }

    [Fact]
    public async Task CorrelationHashMismatch_FailsBeforePatientQueryCallbackOrAck()
    {
        var store = CreateStore();
        var wrongHash = new string('a', 64);
        var wrongEvidence = $"rxh-{wrongHash[..16]}-1770000000";
        store.UpsertObservation(new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, wrongHash, wrongEvidence),
            Fingerprint,
            RawRx));
        var source = new FakeSource();
        var cloud = new FakeCloud { CallbackReceipt = Receipt() };
        var coordinator = CreateCoordinator(store, source, cloud);
        var wrongCommand = Command() with { RxHash = wrongHash, EvidenceId = wrongEvidence };
        Assert.True(coordinator.Register(wrongCommand).Accepted);

        await coordinator.RetryPendingAsync(default);

        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, cloud.CallbackCount);
        Assert.Equal(0, cloud.AckCount);
        Assert.Equal(1, cloud.FailureAckCount);
    }

    [Fact]
    public async Task PioneerRxResultForDifferentRx_FailsBeforeCallbackOrAck()
    {
        var store = CreateStore();
        store.UpsertObservation(Observation());
        var source = new FakeSource { ReturnedRxNumber = "RX-DIFFERENT" };
        var cloud = new FakeCloud { CallbackReceipt = Receipt() };
        var coordinator = CreateCoordinator(store, source, cloud);
        Assert.True(coordinator.Register(Command()).Accepted);

        await coordinator.RetryPendingAsync(default);

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(0, cloud.CallbackCount);
        Assert.Equal(0, cloud.AckCount);
        Assert.Equal(1, cloud.FailureAckCount);
    }

    [Fact]
    public async Task ExpiredAuthorization_AcksTerminalFailureOnceAndCannotPoisonRedelivery()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(time);
        store.UpsertObservation(Observation());
        var cloud = new FakeCloud();
        var coordinator = CreateCoordinator(store, new FakeSource(), cloud, time);
        Assert.True(coordinator.Register(Command()).Accepted);

        time.Advance(RxCorrelationStore.PatientFetchAuthorizationTtl + TimeSpan.FromSeconds(1));
        await coordinator.RetryPendingAsync(default);
        await coordinator.RetryPendingAsync(default);

        Assert.Equal(1, cloud.FailureAckCount);
        Assert.Equal("authorization_expired", cloud.LastFailureError);
        Assert.Equal(
            RxCorrelationRegistrationCode.CorrelationAlreadyClaimed,
            coordinator.Register(Command()).Code);
    }

    [Fact]
    public async Task FailedTerminalAck_RemainsDurableAndRetriesWithoutPatientRead()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var store = CreateStore(time);
        var wrongHash = new string('a', 64);
        var wrongEvidence = $"rxh-{wrongHash[..16]}-1770000000";
        store.UpsertObservation(new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, wrongHash, wrongEvidence),
            Fingerprint,
            RawRx));
        var source = new FakeSource();
        var cloud = new FakeCloud
        {
            FailureAckResults = new Queue<bool>(new[] { false, true }),
        };
        var coordinator = CreateCoordinator(store, source, cloud, time);
        Assert.True(coordinator.Register(Command() with
        {
            RxHash = wrongHash,
            EvidenceId = wrongEvidence,
        }).Accepted);

        await coordinator.RetryPendingAsync(default);
        await coordinator.RetryPendingAsync(default);

        Assert.Equal(0, source.ReadCount);
        Assert.Equal(2, cloud.FailureAckCount);
    }

    [Fact]
    public async Task LearnedCorrelation_PreservesExactApprovedSourceThroughPatientRead()
    {
        var store = CreateStore();
        store.UpsertObservation(Observation() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        });
        var source = new FakeSource();
        var coordinator = CreateCoordinator(
            store,
            source,
            new FakeCloud { CallbackReceipt = Receipt() });
        Assert.True(coordinator.Register(Command() with
        {
            SourceKind = RxCorrelationSourceKinds.LearnedApproved,
            SourceBinding = LearnedDigest,
        }).Accepted);

        await coordinator.RetryPendingAsync(default);

        Assert.Equal(RxCorrelationSourceKinds.LearnedApproved, source.LastPending?.SourceKind);
        Assert.Equal(LearnedDigest, source.LastPending?.SourceBinding);
    }

    [Fact]
    public async Task LearnedSource_RequiresExactActiveDigestAndApprovedPatientContract()
    {
        using var adapter = new LearnedPmsAdapter(
            "learned-test",
            "",
            "SELECT 1",
            new Dictionary<string, string>(),
            "RxNumber",
            "Status",
            Array.Empty<string>(),
            "SELECT 1",
            new Dictionary<string, string>(),
            NullLogger.Instance);
        var registry = new FakeRegistry(adapter, LearnedDigest);
        using var services = new ServiceCollection()
            .AddSingleton<IActivePmsAdapterRegistry>(registry)
            .BuildServiceProvider();
        var source = new PioneerRxApprovedPatientSource(services);
        var pending = Pending(RxCorrelationSourceKinds.LearnedApproved, LearnedDigest);

        var result = await source.ReadAsync(pending, RawRx, default);

        Assert.False(result.SourceAvailable);
        Assert.Equal(1, registry.AcquireCount);
        Assert.Equal(1, registry.UnhealthyCount);

        var wrongBinding = await source.ReadAsync(
            pending with { SourceBinding = "b" + LearnedDigest[1..] },
            RawRx,
            default);
        Assert.False(wrongBinding.SourceAvailable);
        Assert.Equal(2, registry.AcquireCount);
        Assert.Equal(1, registry.UnhealthyCount);
    }

    [Fact]
    public async Task BuiltInSource_NeverAcquiresLearnedAdapter()
    {
        using var adapter = new LearnedPmsAdapter(
            "learned-test",
            "",
            "SELECT 1",
            new Dictionary<string, string>(),
            "RxNumber",
            "Status",
            Array.Empty<string>(),
            "SELECT 1",
            new Dictionary<string, string>(),
            NullLogger.Instance);
        var registry = new FakeRegistry(adapter, LearnedDigest);
        using var services = new ServiceCollection()
            .AddSingleton<IActivePmsAdapterRegistry>(registry)
            .BuildServiceProvider();

        var result = await new PioneerRxApprovedPatientSource(services).ReadAsync(
            Pending(RxCorrelationSourceKinds.PioneerRxBuiltIn, null),
            RawRx,
            default);

        Assert.False(result.SourceAvailable);
        Assert.Equal(0, registry.AcquireCount);
    }

    [Fact]
    public void FixedTimeHashComparison_RequiresExactPerAgentHmac()
    {
        var hash = PhiScrubber.HmacHash(RawRx, HmacKey);
        Assert.True(ApprovedPatientRetrievalCoordinator.FixedTimeRxHashMatches(RawRx, HmacKey, hash));
        Assert.False(ApprovedPatientRetrievalCoordinator.FixedTimeRxHashMatches(RawRx, "wrong-key", hash));
        Assert.False(ApprovedPatientRetrievalCoordinator.FixedTimeRxHashMatches("different-rx", HmacKey, hash));
    }

    private ApprovedPatientRetrievalCoordinator CreateCoordinator(
        IRxCorrelationStore store,
        IApprovedPatientSource source,
        IApprovedPatientCloudTransport cloud,
        TimeProvider? timeProvider = null) =>
        new(
            new AgentOptions
            {
                PharmacyId = PharmacyId,
                AgentId = AgentId,
                MachineFingerprint = Fingerprint,
                HmacSalt = HmacKey,
                EnableAuditedPatientDetailsEgress = true,
            },
            store,
            _db,
            source,
            cloud,
            NullLogger.Instance,
            timeProvider ?? new FixedTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z")));

    private RxCorrelationStore CreateStore(TimeProvider? timeProvider = null) => new(
        Path.Combine(_directory, "correlations-" + Guid.NewGuid().ToString("N") + ".json"),
        new TestProtector(),
        timeProvider ?? new FixedTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z")),
        maxEntries: 32);

    private static ApprovedPatientFetchCommand Command()
    {
        var hash = PhiScrubber.HmacHash(RawRx, HmacKey);
        return new ApprovedPatientFetchCommand(
            CandidateId,
            hash,
            $"rxh-{hash[..16]}-1770000000",
            PharmacyId,
            CommandId);
    }

    private static RxCorrelationObservation Observation()
    {
        var command = Command();
        return new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, command.RxHash, command.EvidenceId),
            Fingerprint,
            RawRx);
    }

    private static PatientDetailsCallbackReceipt Receipt() => new(
        CommandId,
        CandidateId,
        PharmacyId,
        "00000000-0000-4000-8000-000000000004",
        "00000000-0000-4000-8000-000000000005",
        "patient_details_received",
        "ready_for_review",
        DateTimeOffset.Parse("2026-07-10T12:30:00Z"),
        false);

    private static PendingApprovedPatientFetch Pending(string sourceKind, string? sourceBinding)
    {
        var command = Command();
        return new PendingApprovedPatientFetch(
            new RxCorrelationKey(PharmacyId, AgentId, command.RxHash, command.EvidenceId),
            Fingerprint,
            CandidateId,
            CommandId,
            RxCorrelationCommandState.AwaitingCallback,
            sourceKind,
            sourceBinding);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class FakeSource : IApprovedPatientSource
    {
        public string ReturnedRxNumber { get; init; } = RawRx;
        public int ReadCount { get; private set; }
        public PendingApprovedPatientFetch? LastPending { get; private set; }
        public Task<PatientLookupResult> ReadAsync(
            PendingApprovedPatientFetch pending,
            string rawRxNumber,
            CancellationToken ct)
        {
            ReadCount++;
            LastPending = pending;
            Assert.Equal(RawRx, rawRxNumber);
            return Task.FromResult(PatientLookupResult.Found(new RxPatientDetails(
                ReturnedRxNumber,
                "Jane",
                "R",
                "5551234567",
                "123 Main St",
                null,
                "San Diego",
                "CA",
                "92101")));
        }
    }

    private sealed class FakeCloud : IApprovedPatientCloudTransport
    {
        public PatientDetailsCallbackReceipt? CallbackReceipt { get; init; }
        public Queue<bool>? AckResults { get; init; }
        public Queue<bool>? FailureAckResults { get; init; }
        public int CallbackCount { get; private set; }
        public int AckCount { get; private set; }
        public int FailureAckCount { get; private set; }
        public string? LastFailureError { get; private set; }

        public Task<PatientDetailsCallbackReceipt?> SendCallbackAsync(
            ApprovedPatientFetchCommand command,
            PatientDetailsPayload details,
            CancellationToken ct)
        {
            CallbackCount++;
            Assert.Equal(CommandId, command.CommandId);
            Assert.Equal("Jane", details.FirstName);
            return Task.FromResult(CallbackReceipt);
        }

        public Task<bool> AckAsync(string commandId, object result, CancellationToken ct)
        {
            AckCount++;
            Assert.Equal(CommandId, commandId);
            return Task.FromResult(AckResults is { Count: > 0 } ? AckResults.Dequeue() : true);
        }

        public Task<bool> AckFailureAsync(
            string commandId,
            object result,
            string error,
            CancellationToken ct)
        {
            FailureAckCount++;
            LastFailureError = error;
            Assert.Equal(CommandId, commandId);
            return Task.FromResult(
                FailureAckResults is { Count: > 0 } ? FailureAckResults.Dequeue() : true);
        }
    }

    private sealed class FakeRegistry : IActivePmsAdapterRegistry
    {
        private readonly LearnedPmsAdapter _adapter;
        private readonly ActivePmsAdapterBinding _binding;

        internal FakeRegistry(LearnedPmsAdapter adapter, string digest)
        {
            _adapter = adapter;
            _binding = new ActivePmsAdapterBinding(
                PharmacyId,
                "session-1",
                digest,
                new string('c', 64),
                "pharmacist-1",
                DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        }

        internal int AcquireCount { get; private set; }
        internal int UnhealthyCount { get; private set; }

        public AdapterActivationResult ActivateApproved(string sessionId) =>
            new(AdapterActivationOutcome.AlreadyActive, "test", _binding);

        public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now)
        {
            AcquireCount++;
            return new ActivePmsAdapterLease(_adapter, _binding, () => { });
        }

        public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now) { }

        public void ReportUnhealthy(
            ActivePmsAdapterBinding binding,
            DateTimeOffset now,
            string errorCategory) => UnhealthyCount++;

        public ActivePmsAdapterStatus Snapshot(DateTimeOffset now) =>
            new(true, _binding.SessionId, _binding.TemplateDigest[..12], 0, null, null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class TestProtector : IRxCorrelationProtector
    {
        private static readonly byte[] Key = SHA256.HashData("test-only-approved-fetch-key"u8.ToArray());

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag, entropy);
            return [.. nonce, .. tag, .. cipher];
        }

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            var plain = new byte[protectedBytes.Length - 28];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(protectedBytes[..12], protectedBytes[28..], protectedBytes[12..28], plain, entropy);
            return plain;
        }
    }
}
