using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// Wave 1B Track 1.4 — verifies HeartbeatWorker emits agent.health_composite
/// each tick AND that composite emission failure does NOT block the
/// heartbeat critical path.
///
/// We exercise the private <c>EmitHealthComposite()</c> helper directly via
/// reflection rather than driving <c>ExecuteAsync</c>, because the full tick
/// pulls in cloud HTTP, IPC, pricing, intelligence, etc. — which would make
/// this an integration test of a dozen subsystems instead of the composite
/// emission path. This pattern matches <c>HeartbeatWorkerTests</c> which
/// reflects on the private <c>ProcessSignedCommandAsync</c> for the same
/// reason.
/// </summary>
public class HeartbeatWorkerCompositeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private const string TestAgentId = "agent-composite-test";
    private const string TestFingerprint = "fp-composite-test";
    private const string TestPharmacyId = "pharm-composite-test";

    public HeartbeatWorkerCompositeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hbc_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Helpers ──

    private HeartbeatWorker BuildWorker(IHealthSignals? signals,
        HealthCompositeCalculator? calculator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        if (signals != null)
            services.AddSingleton(signals);
        if (calculator != null)
            services.AddSingleton(calculator);

        var sp = services.BuildServiceProvider();

        var options = Options.Create(new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            HeartbeatIntervalSeconds = 30,
        });

        return new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance, options, sp, _db);
    }

    private static HealthCompositePayload? InvokeEmit(HeartbeatWorker worker)
    {
        var method = typeof(HeartbeatWorker)
            .GetMethod("EmitHealthComposite",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (HealthCompositePayload?)method.Invoke(worker, Array.Empty<object>());
    }

    private int CountAuditEntries(string eventType)
    {
        // The AgentStateDb keeps its connection in WAL mode; opening a separate
        // read-only connection sees committed writes immediately. No locking
        // hazard since AppendChainedAuditEntry has already committed.
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_entries WHERE event_type = @t";
        cmd.Parameters.AddWithValue("@t", eventType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static (string LastEventType, string LastToState) ReadLastAuditRow(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_type, to_state
            FROM audit_entries
            ORDER BY id DESC LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return ("", "");
        return (reader.GetString(0), reader.GetString(1));
    }

    private sealed class FakeBusinessHoursProvider : IBusinessHoursProvider
    {
        private readonly bool _inside;
        public FakeBusinessHoursProvider(bool inside) => _inside = inside;
        public bool IsInsideBusinessHours(DateTimeOffset at) => _inside;
    }

    private sealed class FakeHealthSignals : IHealthSignals
    {
        private readonly HealthSignalsSnapshot _snapshot;
        public FakeHealthSignals(HealthSignalsSnapshot snapshot) => _snapshot = snapshot;
        public HealthSignalsSnapshot Snapshot() => _snapshot;
    }

    private sealed class ThrowingHealthSignals : IHealthSignals
    {
        public HealthSignalsSnapshot Snapshot() =>
            throw new InvalidOperationException("simulated health probe failure");
    }

    // ── Tests ──

    [Fact]
    public void EmitHealthComposite_AllSignalsHealthy_ReturnsHealthyPayload()
    {
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new FakeHealthSignals(snapshot), calc);

        var result = InvokeEmit(worker);

        Assert.NotNull(result);
        Assert.Equal("healthy", result!.Status);
        Assert.True(result.Components.HelperAttached);
        Assert.True(result.Components.IpcConnected);
        Assert.True(result.Components.SchemaCanaryGreen);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void EmitHealthComposite_DegradedSignals_ReturnsDegradedPayload()
    {
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false, // helper down
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new FakeHealthSignals(snapshot), calc);

        var result = InvokeEmit(worker);

        Assert.NotNull(result);
        Assert.Equal("heartbeating-but-unhealthy", result!.Status);
        Assert.False(result.Components.HelperAttached);
    }

    [Fact]
    public void EmitHealthComposite_AppendsAuditEntry_WithCorrectEventType()
    {
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: DateTimeOffset.UtcNow);
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new FakeHealthSignals(snapshot), calc);

        var countBefore = CountAuditEntries("agent.health_composite");

        InvokeEmit(worker);

        var countAfter = CountAuditEntries("agent.health_composite");
        Assert.Equal(countBefore + 1, countAfter);
    }

    [Fact]
    public void EmitHealthComposite_AuditEntry_RecordsCurrentStatusInToState()
    {
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false,
            IpcConnected: false,
            SchemaCanaryGreen: false,
            LastExtractionAt: null);
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new FakeHealthSignals(snapshot), calc);

        InvokeEmit(worker);

        var (eventType, toState) = ReadLastAuditRow(_dbPath);
        Assert.Equal("agent.health_composite", eventType);
        Assert.Equal("heartbeating-but-unhealthy", toState);
    }

    [Fact]
    public void EmitHealthComposite_EachInvocation_AppendsExactlyOneRow()
    {
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: DateTimeOffset.UtcNow);
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new FakeHealthSignals(snapshot), calc);

        InvokeEmit(worker);
        InvokeEmit(worker);
        InvokeEmit(worker);

        Assert.Equal(3, CountAuditEntries("agent.health_composite"));
    }

    // ── Failure non-blocking ──

    [Fact]
    public void EmitHealthComposite_SignalsThrows_ReturnsNull_DoesNotThrow()
    {
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new ThrowingHealthSignals(), calc);

        // Must not throw — this is the heartbeat critical-path guarantee.
        var result = InvokeEmit(worker);

        Assert.Null(result);
    }

    [Fact]
    public void EmitHealthComposite_SignalsThrows_DoesNotAppendAuditEntry()
    {
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(new ThrowingHealthSignals(), calc);

        var countBefore = CountAuditEntries("agent.health_composite");

        InvokeEmit(worker);

        // Snapshot threw before composite computation, so no audit entry is
        // appended. The agent retries on the next tick.
        Assert.Equal(countBefore, CountAuditEntries("agent.health_composite"));
    }

    [Fact]
    public void EmitHealthComposite_HealthSignalsNotWired_ReturnsNull_NoOp()
    {
        // Production-equivalent of "Program.cs hasn't registered IHealthSignals
        // yet." Worker must still construct and emit must be a safe no-op.
        var worker = BuildWorker(signals: null, calculator: null);

        var countBefore = CountAuditEntries("agent.health_composite");

        var result = InvokeEmit(worker);

        Assert.Null(result);
        Assert.Equal(countBefore, CountAuditEntries("agent.health_composite"));
    }

    [Fact]
    public void EmitHealthComposite_OnlyCalculatorWired_ReturnsNull_NoOp()
    {
        // Half-wired DI — must still be a safe no-op.
        var calc = new HealthCompositeCalculator(new FakeBusinessHoursProvider(inside: true));
        var worker = BuildWorker(signals: null, calculator: calc);

        var result = InvokeEmit(worker);

        Assert.Null(result);
    }
}
