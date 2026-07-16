using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class LearnedAdapterFallbackTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"suavo_learned_fallback_{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public LearnedAdapterFallbackTests() => _db = new AgentStateDb(_dbPath);

    [Fact]
    public async Task ApprovedHealthyFallback_DeduplicatesAndUsesNormalHashOnlyEgress()
    {
        var adapter = new FakeAdapter(healthy: true, Rows(
            Rx("12345"),
            Rx("12345")));
        var registry = new FakeRegistry(adapter);
        var correlations = new FakeCorrelationStore();
        var worker = CreateWorker(registry, correlations);

        var used = await worker.TryRunLearnedFallbackAsync(
            "builtin_connection_unavailable", CancellationToken.None);

        Assert.True(used);
        Assert.True(worker.IsLearnedFallbackHealthy);
        Assert.Equal("learned-approved", worker.ActiveDetectionSource);
        Assert.Equal(1, worker.LastDetectedCount);
        Assert.Single(correlations.Observations);
        Assert.Equal(
            RxCorrelationSourceKinds.LearnedApproved,
            correlations.Observations[0].SourceKind);
        Assert.Equal(new string('a', 64), correlations.Observations[0].SourceBinding);
        Assert.Equal(1, registry.HealthyReports);
        Assert.Equal(0, registry.UnhealthyReports);
        Assert.Equal(1, adapter.PullCount);
    }

    [Fact]
    public async Task UnhealthyFallback_FailsClosedWithoutQueryingOrChangingSource()
    {
        var adapter = new FakeAdapter(healthy: false, Rows(Rx("12345")));
        var registry = new FakeRegistry(adapter);
        var worker = CreateWorker(registry, new FakeCorrelationStore());

        var used = await worker.TryRunLearnedFallbackAsync(
            "builtin_contract_unavailable", CancellationToken.None);

        Assert.False(used);
        Assert.False(worker.IsLearnedFallbackHealthy);
        Assert.Equal("none", worker.ActiveDetectionSource);
        Assert.Equal(0, adapter.PullCount);
        Assert.Equal(1, registry.UnhealthyReports);
    }

    [Fact]
    public async Task ApprovedFallback_PaginatesPastFiftyWithoutStarvingLaterRows()
    {
        var rows = Enumerable.Range(1, 75)
            .Select(number => Rx(number.ToString("D5")))
            .ToArray();
        var adapter = new FakeAdapter(healthy: true, rows);
        var correlations = new FakeCorrelationStore();
        var worker = CreateWorker(new FakeRegistry(adapter), correlations);

        var used = await worker.TryRunLearnedFallbackAsync(
            "builtin_connection_unavailable",
            CancellationToken.None);

        Assert.True(used);
        Assert.Equal(2, adapter.PullCount);
        Assert.Equal(75, worker.LastDetectedCount);
        Assert.Equal(75, correlations.Observations.Count);
        Assert.Contains(correlations.Observations, observation =>
            observation.RawRxNumber == "00075");
    }

    [Fact]
    public void LearnedCandidateTelemetry_ContainsOnlySafeBindingIdentity()
    {
        var metadata = new[]
        {
            new RxMetadata(
                "12345", "Secret Drug", "00000-0000", null, 30m, Guid.Empty,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
        };

        var json = RxDetectionWorker.SerializeRxBatch(
            metadata,
            hmacSalt: "local-secret",
            pharmacyId: "pharmacy-1",
            agentInstallId: "agent-1",
            sourcePms: "learned-approved",
            schemaSignature: $"learned.template.{new string('a', 64)}",
            evidenceSourceKind: RxCorrelationSourceKinds.LearnedApproved,
            evidenceSourceBinding: new string('a', 64));
        using var document = JsonDocument.Parse(json);
        var candidate = document.RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];
        var provenance = candidate.GetProperty("provenance");

        Assert.Equal("learned-approved", provenance.GetProperty("pms").GetString());
        Assert.Equal(
            $"learned.template.{new string('a', 64)}",
            provenance.GetProperty("schemaSignature").GetString());
        Assert.DoesNotContain("12345", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Drug", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceIdentity_IsSourceAndApprovedTemplateSpecific()
    {
        var row = new RxMetadata(
            "12345", "", "", null, 0, Guid.Empty,
            DateTimeOffset.Parse("2026-07-10T12:00:00Z"), FillNumber: 2);
        var hash = new string('f', 64);
        var builtIn = RxDetectionWorker.BuildLocalEvidenceId(hash, row);
        var learnedA = RxDetectionWorker.BuildLocalEvidenceId(
            hash,
            row,
            RxCorrelationSourceKinds.LearnedApproved,
            new string('0', 64));
        var learnedB = RxDetectionWorker.BuildLocalEvidenceId(
            hash,
            row,
            RxCorrelationSourceKinds.LearnedApproved,
            "1" + new string('0', 63));

        Assert.NotEqual(builtIn, learnedA);
        Assert.NotEqual(learnedA, learnedB);
    }

    private RxDetectionWorker CreateWorker(
        IActivePmsAdapterRegistry registry,
        IRxCorrelationStore correlationStore)
    {
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(correlationStore)
            .BuildServiceProvider();
        var options = Options.Create(new AgentOptions
        {
            PharmacyId = "pharmacy-1",
            AgentId = "agent-1",
            MachineFingerprint = "machine-1",
            HmacSalt = "local-secret",
        });
        return new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            options,
            _db,
            services);
    }

    private static RxReadyForDelivery Rx(string number) =>
        new(
            RxNumber: number,
            FillNumber: 0,
            DrugName: "",
            Ndc: "",
            Quantity: 0,
            DaysSupply: 0,
            StatusText: "guid-ready",
            IsControlled: false,
            DrugSchedule: null,
            PatientIdRequired: false,
            CounselingRequired: false,
            DetectedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Source: DetectionSource.Sql);

    private static IReadOnlyList<RxReadyForDelivery> Rows(params RxReadyForDelivery[] rows) => rows;

    public void Dispose()
    {
        _db.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class FakeRegistry : IActivePmsAdapterRegistry
    {
        private readonly ILocalPmsAdapter _adapter;
        private readonly ActivePmsAdapterBinding _binding = new(
            "pharmacy-1",
            "session-1",
            new string('a', 64),
            new string('b', 64),
            "operator-1",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        internal FakeRegistry(ILocalPmsAdapter adapter) => _adapter = adapter;
        internal int HealthyReports { get; private set; }
        internal int UnhealthyReports { get; private set; }

        public AdapterActivationResult ActivateApproved(string sessionId) =>
            new(AdapterActivationOutcome.AlreadyActive, "test", _binding);

        public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now) =>
            new(_adapter, _binding, () => { });

        public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now) =>
            HealthyReports++;

        public void ReportUnhealthy(
            ActivePmsAdapterBinding binding,
            DateTimeOffset now,
            string errorCategory) => UnhealthyReports++;

        public ActivePmsAdapterStatus Snapshot(DateTimeOffset now) =>
            new(true, _binding.SessionId, _binding.TemplateDigest[..12], 0, null, now);
    }

    private sealed class FakeAdapter : ILocalPmsAdapter
    {
        private readonly bool _healthy;
        private readonly IReadOnlyList<RxReadyForDelivery> _rows;

        internal FakeAdapter(bool healthy, IReadOnlyList<RxReadyForDelivery> rows)
        {
            _healthy = healthy;
            _rows = rows;
        }

        internal int PullCount { get; private set; }
        public string PmsName => "test-learned";

        public Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new CapabilityManifest(
                true, false, false, false, false, null, null, null, Array.Empty<string>()));

        public Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct)
        {
            PullCount++;
            var start = 0;
            if (cursor is not null)
            {
                var index = _rows.ToList().FindIndex(row =>
                    string.Equals(row.RxNumber, cursor, StringComparison.Ordinal));
                if (index < 0)
                    throw new InvalidDataException("test cursor missing");
                start = index + 1;
            }
            return Task.FromResult<IReadOnlyList<RxReadyForDelivery>>(
                _rows.Skip(start).Take(LearnedPmsAdapter.DetectionPageSize).ToArray());
        }

        public Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct) =>
            Task.FromResult(new WritebackReceipt(false, null, "not_supported", WritebackMethod.Manual, false, DateTimeOffset.UtcNow));

        public Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new AdapterHealthReport(
                "learned-approved",
                _healthy,
                _healthy ? "connected" : "unavailable",
                null,
                null,
                DateTimeOffset.UtcNow,
                null));
    }

    private sealed class FakeCorrelationStore : IRxCorrelationStore
    {
        internal List<RxCorrelationObservation> Observations { get; } = new();
        public void UpsertObservation(RxCorrelationObservation observation) => Observations.Add(observation);
        public RxCorrelationRegistrationResult RegisterApprovedFetch(
            ApprovedPatientFetchCommand command,
            string currentAgentId,
            string currentMachineFingerprint) =>
            new(RxCorrelationRegistrationCode.CorrelationNotFound, null);
        public IReadOnlyList<PendingApprovedPatientFetch> GetPending(
            string pharmacyId,
            string agentId,
            string machineFingerprint,
            int maxCount) => Array.Empty<PendingApprovedPatientFetch>();
        public bool TryRevealRawRx(PendingApprovedPatientFetch pending, out string rawRxNumber)
        {
            rawRxNumber = "";
            return false;
        }
        public void MarkCallbackAccepted(
            PendingApprovedPatientFetch pending,
            string stagingId,
            string transitionId,
            DateTimeOffset callbackExpiresAtUtc) { }
        public void MarkCompleted(PendingApprovedPatientFetch pending) { }
        public void DeferPatientFetch(
            PendingApprovedPatientFetch pending,
            string failureCategory,
            bool quarantine) { }
        public void PruneExpired() { }
        public IReadOnlyList<PendingApprovedPatientFailure> GetUnacknowledgedFailures(
            string pharmacyId,
            string agentId,
            string machineFingerprint,
            int maxCount) => Array.Empty<PendingApprovedPatientFailure>();
        public void MarkFailureAcknowledged(
            PendingApprovedPatientFailure failure,
            string pharmacyId,
            string agentId,
            string machineFingerprint) { }
        public WritebackCorrelationRegistrationResult RegisterDeliveryWriteback(
            AgentDeliveryWritebackCommand command,
            string currentAgentId,
            string currentMachineFingerprint) =>
            new(WritebackCorrelationRegistrationCode.CorrelationNotFound);
        public bool TryRevealDeliveryWriteback(
            AgentDeliveryWritebackCommand command,
            string currentAgentId,
            string currentMachineFingerprint,
            out SensitiveRxBuffer? rawRxNumber,
            out int fillNumber)
        {
            rawRxNumber = null;
            fillNumber = 0;
            return false;
        }
        public void MarkDeliveryWritebackReceiptVerified(
            AgentDeliveryWritebackCommand command,
            string currentAgentId,
            string currentMachineFingerprint,
            DeliveryWritebackResultCode resultCode) { }
    }
}
