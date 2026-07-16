using Microsoft.Data.SqlClient;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class ActivePmsAdapterRegistryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"suavo_active_adapter_{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;
    private readonly AgentOptions _options = new()
    {
        PharmacyId = "pharmacy-1",
        SqlServer = "server-a",
        SqlDatabase = "TestDB",
    };

    public ActivePmsAdapterRegistryTests() => _db = new AgentStateDb(_dbPath);

    [Fact]
    public void ExactHumanApprovedBinding_ActivatesAndPersistsReceipt()
    {
        const string sessionId = "session-approved";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var fake = new FakeAdapter();
        using var registry = CreateRegistry(_ => fake);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Activated, result.Outcome);
        Assert.True(result.IsActive);
        Assert.Equal("active", _db.GetLearningSession(sessionId)!.Value.Phase);
        var receipt = _db.GetLearnedAdapterActivation(_options.PharmacyId!);
        Assert.NotNull(receipt);
        Assert.Equal(sessionId, receipt.SessionId);
        Assert.Equal(result.Binding!.TemplateDigest, receipt.TemplateDigest);
        Assert.Equal(result.Binding.ModelDigest, receipt.ModelDigest);
        Assert.Equal("operator-42", receipt.ApprovedBy);
        Assert.Equal("active", receipt.Status);

        using var lease = registry.TryAcquire(DateTimeOffset.UtcNow);
        Assert.NotNull(lease);
        Assert.Same(fake, lease.Adapter);
        Assert.Equal(result.Binding, lease.Binding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("system")]
    [InlineData("agent")]
    public void NonHumanApproval_CannotActivate(string approvedBy)
    {
        const string sessionId = "session-non-human";
        PrepareApprovedSession(sessionId, approvedBy);
        using var registry = CreateRegistry(_ => new FakeAdapter());

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Rejected, result.Outcome);
        Assert.Equal("local_human_approval_missing", result.Reason);
        Assert.Equal("approved", _db.GetLearningSession(sessionId)!.Value.Phase);
        Assert.Null(_db.GetLearnedAdapterActivation(_options.PharmacyId!));
    }

    [Fact]
    public void PostApprovalTemplateMutation_IsRejected()
    {
        const string sessionId = "session-drift";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        _db.InsertDiscoveredStatus(
            sessionId,
            "Prescription.RxTransaction",
            "StatusTypeID",
            "guid-new-ready",
            "ready_pickup",
            9,
            1,
            0.9);
        _db.CompleteLearnedTemplateEvidence(sessionId);
        using var registry = CreateRegistry(_ => new FakeAdapter());

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Rejected, result.Outcome);
        Assert.Equal("adapter_template_digest_mismatch", result.Reason);
        Assert.Equal("approved", _db.GetLearningSession(sessionId)!.Value.Phase);
    }

    [Fact]
    public void ActiveSession_RehydratesExactAdapterAfterRestart()
    {
        const string sessionId = "session-rehydrate";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var firstAdapter = new FakeAdapter();
        using (var firstRegistry = CreateRegistry(_ => firstAdapter))
        {
            Assert.True(firstRegistry.ActivateApproved(sessionId).IsActive);
        }
        Assert.True(firstAdapter.Disposed);

        var restoredAdapter = new FakeAdapter();
        using var restartedRegistry = CreateRegistry(_ => restoredAdapter);
        var restored = restartedRegistry.ActivateApproved(sessionId);

        Assert.True(restored.IsActive);
        Assert.Equal("active", _db.GetLearningSession(sessionId)!.Value.Phase);
        using var lease = restartedRegistry.TryAcquire(DateTimeOffset.UtcNow);
        Assert.Same(restoredAdapter, lease?.Adapter);
    }

    [Fact]
    public void Disposal_WaitsForOutstandingLease()
    {
        const string sessionId = "session-lease";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var fake = new FakeAdapter();
        var registry = CreateRegistry(_ => fake);
        Assert.True(registry.ActivateApproved(sessionId).IsActive);
        var lease = registry.TryAcquire(DateTimeOffset.UtcNow);
        Assert.NotNull(lease);

        registry.Dispose();
        Assert.False(fake.Disposed);

        lease.Dispose();
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void HealthFailure_AppliesBoundedRetryAndSuccessClearsIt()
    {
        const string sessionId = "session-health";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        using var registry = CreateRegistry(_ => new FakeAdapter());
        var activation = registry.ActivateApproved(sessionId);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        registry.ReportUnhealthy(activation.Binding!, now, "SqlException");
        Assert.Null(registry.TryAcquire(now + TimeSpan.FromSeconds(29)));

        using var lease = registry.TryAcquire(now + TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        registry.ReportHealthy(activation.Binding!, now + TimeSpan.FromSeconds(30));
        var status = registry.Snapshot(now + TimeSpan.FromSeconds(30));
        Assert.Equal(0, status.ConsecutiveHealthFailures);
        Assert.Null(status.RetryAfter);
        Assert.Equal(now + TimeSpan.FromSeconds(30), status.LastHealthyAt);
    }

    [Fact]
    public void LiveSqlSourceMismatch_RejectsApprovedTemplateBeforeConstruction()
    {
        const string sessionId = "session-source-drift";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var constructed = false;
        using var registry = new ActivePmsAdapterRegistry(
            _db,
            _options,
            NullLogger<ActivePmsAdapterRegistry>.Instance,
            _ =>
            {
                constructed = true;
                return new FakeAdapter();
            },
            _ => "live_source_identity_mismatch");

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Rejected, result.Outcome);
        Assert.Equal("live_source_identity_mismatch", result.Reason);
        Assert.False(constructed);
    }

    [Fact]
    public void ExactBindingSecondActivationIsIdempotent()
    {
        const string sessionId = "session-idempotent";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var constructed = 0;
        using var registry = CreateRegistry(_ =>
        {
            constructed++;
            return new FakeAdapter();
        });

        var first = registry.ActivateApproved(sessionId);
        var second = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Activated, first.Outcome);
        Assert.Equal(AdapterActivationOutcome.AlreadyActive, second.Outcome);
        Assert.Equal("exact_binding_already_active", second.Reason);
        Assert.Equal(1, constructed);
    }

    [Fact]
    public void AdapterConstructionFailureIsContainedWithoutActivationReceipt()
    {
        const string sessionId = "session-construction-failure";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        using var registry = CreateRegistry(
            _ => throw new InvalidOperationException("adapter construction failed"));

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Failed, result.Outcome);
        Assert.Equal("adapter_construction_failed", result.Reason);
        Assert.Null(_db.GetLearnedAdapterActivation(_options.PharmacyId!));
    }

    [Fact]
    public void UnhealthyLiveContractRejectsAndDisposesCandidate()
    {
        const string sessionId = "session-unhealthy-contract";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var adapter = new FakeAdapter { Healthy = false };
        using var registry = CreateRegistry(_ => adapter);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Rejected, result.Outcome);
        Assert.Equal("live_schema_contract_invalid", result.Reason);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public void LiveContractExceptionFailsAndDisposesCandidate()
    {
        const string sessionId = "session-contract-exception";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var adapter = new FakeAdapter
        {
            HealthException = new IOException("SQL contract unavailable"),
        };
        using var registry = CreateRegistry(_ => adapter);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Failed, result.Outcome);
        Assert.Equal("live_schema_contract_unavailable", result.Reason);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public void DisposedRegistryRejectsActivationAndExposesNoBinding()
    {
        const string sessionId = "session-disposed";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var registry = CreateRegistry(_ => new FakeAdapter());
        registry.Dispose();

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Failed, result.Outcome);
        Assert.Equal("registry_disposed", result.Reason);
        Assert.Null(registry.CurrentBinding());
        Assert.Null(registry.TryAcquire(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EmptyRegistrySnapshotIsExplicitlyUnavailable()
    {
        using var registry = CreateRegistry(_ => new FakeAdapter());

        var status = registry.Snapshot(DateTimeOffset.UtcNow);

        Assert.False(status.HasActiveAdapter);
        Assert.Null(status.SessionId);
        Assert.Null(status.TemplateDigestPrefix);
        Assert.Null(registry.CurrentBinding());
    }

    [Fact]
    public void ProductionConstructorLiveSourceFailureIsContainedBeforeAdapterConstruction()
    {
        const string sessionId = "session-production-constructor";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        _options.SqlServer = "\0";
        using var registry = new ActivePmsAdapterRegistry(
            _db,
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLoggerFactory.Instance,
            NullLogger<ActivePmsAdapterRegistry>.Instance);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Rejected, result.Outcome);
        Assert.Equal("live_source_identity_unavailable", result.Reason);
    }

    [Fact]
    public void PersistenceFailureDisposesCandidateAndReturnsStructuralFailure()
    {
        const string sessionId = "session-persistence-failure";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        var adapter = new FakeAdapter { HealthCallback = _db.Dispose };
        using var registry = CreateRegistry(_ => adapter);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Failed, result.Outcome);
        Assert.Equal("activation_persistence_failed", result.Reason);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public void RegistryDisposedDuringHealthProbeFailsSwapAndDisposesCandidate()
    {
        const string sessionId = "session-swap-failure";
        PrepareApprovedSession(sessionId, approvedBy: "operator-42");
        ActivePmsAdapterRegistry? registry = null;
        var adapter = new FakeAdapter { HealthCallback = () => registry!.Dispose() };
        registry = CreateRegistry(_ => adapter);

        var result = registry.ActivateApproved(sessionId);

        Assert.Equal(AdapterActivationOutcome.Failed, result.Outcome);
        Assert.Equal("registry_swap_failed", result.Reason);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public void StatusAvailabilityAndDefaultRegistryBindingFailClosed()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        Assert.False(new ActivePmsAdapterStatus(false, null, null, 0, null, null)
            .IsAvailable(now));
        Assert.True(new ActivePmsAdapterStatus(true, "session", "digest", 0, null, now)
            .IsAvailable(now));
        Assert.False(new ActivePmsAdapterStatus(
            true, "session", "digest", 1, now.AddSeconds(1), null).IsAvailable(now));
        Assert.True(new ActivePmsAdapterStatus(
            true, "session", "digest", 1, now, null).IsAvailable(now));

        IActivePmsAdapterRegistry registry = new EmptyRegistry();
        Assert.Null(registry.CurrentBinding());
    }

    [Theory]
    [InlineData("SqlException:timeout!", "SqlExceptiontimeout")]
    [InlineData("***", "unknown")]
    public void HealthCategorySanitizerKeepsOnlyBoundedStructuralCharacters(
        string category,
        string expected)
    {
        var method = typeof(ActivePmsAdapterRegistry).GetMethod(
            "SanitizeCategory", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SanitizeCategory missing.");

        Assert.Equal(expected, method.Invoke(null, [category]));
    }

    [Fact]
    public void LearnedConnection_DisablesTransparentDriverReconnect()
    {
        var connectionString = ActivePmsAdapterRegistry.BuildConnectionString(_options);
        var parsed = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(0, parsed.ConnectRetryCount);
    }

    [Fact]
    public void LearnedConnection_UsesExplicitSqlCredentialsWhenConfigured()
    {
        var options = new AgentOptions
        {
            SqlServer = "server-b",
            SqlDatabase = "DatabaseB",
            SqlUser = "readonly-user",
            SqlPassword = "runtime-secret",
        };

        var parsed = new SqlConnectionStringBuilder(
            ActivePmsAdapterRegistry.BuildConnectionString(options));

        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("readonly-user", parsed.UserID);
        Assert.Equal("runtime-secret", parsed.Password);
    }

    [Fact]
    public void LearningConnection_DisablesTransparentDriverReconnect()
    {
        var connectionString = LearningWorker.BuildConnectionString(_options);
        var parsed = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(0, parsed.ConnectRetryCount);
    }

    private ActivePmsAdapterRegistry CreateRegistry(
        Func<LearnedPmsAdapterTemplate, ILocalPmsAdapter> factory) =>
        new(
            _db,
            _options,
            NullLogger<ActivePmsAdapterRegistry>.Instance,
            factory,
            _ => null);

    private void PrepareApprovedSession(string sessionId, string approvedBy)
    {
        _db.CreateLearningSession(sessionId, _options.PharmacyId!);
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");
        var sourceDigest = new string('a', 64);
        _db.BeginDiscoveredSchemaSnapshot(sessionId, sourceDigest, "TestDB");
        foreach (var (column, type) in new[]
                 {
                     ("RxNumber", "int"),
                     ("StatusTypeID", "uniqueidentifier"),
                     ("DateFilled", "datetime"),
                     ("PatientID", "uniqueidentifier"),
                 })
        {
            _db.InsertDiscoveredSchema(
                sessionId, sourceDigest, "TestDB", "Prescription", "RxTransaction",
                column, type, null, false, false, false, null, null, "unknown");
        }
        _db.InsertDiscoveredUniqueColumn(
            sessionId, "Prescription", "RxTransaction", "RxNumber");
        _db.CompleteDiscoveredSchemaSnapshot(sessionId);
        _db.InsertRxQueueCandidate(
            sessionId,
            "Prescription.RxTransaction",
            "RxNumber",
            "StatusTypeID",
            "DateFilled",
            "PatientID",
            0.9,
            "[]");
        _db.InsertDiscoveredStatus(
            sessionId,
            "Prescription.RxTransaction",
            "StatusTypeID",
            "guid-ready",
            "ready_pickup",
            8,
            10,
            0.9);
        _db.CompleteLearnedTemplateEvidence(sessionId);

        var frozenPom = PomExporter.Export(_db, sessionId);
        _db.StorePomSnapshot(sessionId, frozenPom);
        var digest = PomExporter.ComputeDigest(_options.PharmacyId!, sessionId, frozenPom);
        _db.SetApprovalDigest(sessionId, digest, approvedBy);
        _db.UpdateLearningPhase(sessionId, "approved");
    }

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

    private sealed class FakeAdapter : ILocalPmsAdapter, IDisposable
    {
        internal bool Disposed { get; private set; }
        internal bool Healthy { get; init; } = true;
        internal Exception? HealthException { get; init; }
        internal Action? HealthCallback { get; init; }
        public string PmsName => "test-learned";

        public Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new CapabilityManifest(
                true, false, false, false, false, null, null, null, Array.Empty<string>()));

        public Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RxReadyForDelivery>>(Array.Empty<RxReadyForDelivery>());

        public Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct) =>
            Task.FromResult(new WritebackReceipt(false, null, "not_supported", WritebackMethod.Manual, false, DateTimeOffset.UtcNow));

        public Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct)
        {
            HealthCallback?.Invoke();
            return HealthException is not null
                ? Task.FromException<AdapterHealthReport>(HealthException)
                : Task.FromResult(new AdapterHealthReport(
                    PmsName, Healthy, Healthy ? "connected" : "schema_mismatch",
                    null, null, DateTimeOffset.UtcNow, null));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class EmptyRegistry : IActivePmsAdapterRegistry
    {
        public AdapterActivationResult ActivateApproved(string sessionId) =>
            new(AdapterActivationOutcome.Rejected, "empty");
        public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now) => null;
        public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now) { }
        public void ReportUnhealthy(
            ActivePmsAdapterBinding binding, DateTimeOffset now, string errorCategory) { }
        public ActivePmsAdapterStatus Snapshot(DateTimeOffset now) =>
            new(false, null, null, 0, null, null);
    }
}
