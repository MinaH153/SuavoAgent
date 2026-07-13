using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class RxDetectionWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _stateDb;

    public RxDetectionWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_rxworker_test_{Guid.NewGuid():N}.db");
        _stateDb = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void InitialState_NotConnected()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var options = Options.Create(new AgentOptions());
        var worker = new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            options, _stateDb, sp);

        Assert.False(worker.IsSqlConnected);
        Assert.Equal(0, worker.LastDetectedCount);
        Assert.Null(worker.LastDetectionTime);
    }

    private RxDetectionWorker CreateWorker()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            Options.Create(new AgentOptions()), _stateDb, sp);
    }

    // ── B2: sustained SQL-outage visibility ──────────────────────────────────────────────
    // A down PMS makes the detection cycle skip GRACEFULLY (no throw), so the worker supervisor
    // never escalates. The heartbeat must instead surface an explicit `degraded` signal once SQL
    // has been dark past the threshold, so a pharmacy can't go quietly dark on delivery detection.

    [Fact]
    public void Degraded_InitiallyFalse_NoOutage()
    {
        var worker = CreateWorker();
        var now = DateTimeOffset.UtcNow;
        Assert.False(worker.IsDetectionDegraded(now));
        Assert.Equal(0, worker.ConsecutiveSqlFailures);
        Assert.Equal(0, worker.SqlDarkSeconds(now));
        Assert.Null(worker.SqlDownSince);
    }

    [Fact]
    public void Degraded_TrueOnlyAfterThreshold()
    {
        var worker = CreateWorker();
        var t0 = DateTimeOffset.UnixEpoch; // deterministic clock
        worker.MarkSqlConnectFailed(t0);

        Assert.Equal(1, worker.ConsecutiveSqlFailures);
        Assert.Equal(t0, worker.SqlDownSince);
        Assert.False(worker.IsDetectionDegraded(t0));                             // 0s dark
        Assert.False(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(179))); // just under
        Assert.True(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(180)));  // at threshold
        Assert.True(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(600)));  // well past
        Assert.Equal(600, worker.SqlDarkSeconds(t0 + TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void Degraded_DarkSince_PinnedToFirstFailure()
    {
        var worker = CreateWorker();
        var t0 = DateTimeOffset.UnixEpoch;
        worker.MarkSqlConnectFailed(t0);
        worker.MarkSqlConnectFailed(t0 + TimeSpan.FromSeconds(60));
        worker.MarkSqlConnectFailed(t0 + TimeSpan.FromSeconds(120));

        Assert.Equal(3, worker.ConsecutiveSqlFailures);
        Assert.Equal(t0, worker.SqlDownSince); // later failures must NOT reset the outage clock
        Assert.True(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(180)));
    }

    [Fact]
    public void Degraded_ClearsOnReconnect()
    {
        var worker = CreateWorker();
        var t0 = DateTimeOffset.UnixEpoch;
        worker.MarkSqlConnectFailed(t0);
        Assert.True(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(300)));

        worker.MarkSqlConnected();

        Assert.True(worker.IsSqlConnected);
        Assert.Equal(0, worker.ConsecutiveSqlFailures);
        Assert.Null(worker.SqlDownSince);
        Assert.False(worker.IsDetectionDegraded(t0 + TimeSpan.FromSeconds(300)));
        Assert.Equal(0, worker.SqlDarkSeconds(t0 + TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void Degraded_NewOutageAfterRecovery_StampsFreshDarkSince()
    {
        var worker = CreateWorker();
        var t0 = DateTimeOffset.UnixEpoch;
        worker.MarkSqlConnectFailed(t0);
        worker.MarkSqlConnected();

        var t1 = t0 + TimeSpan.FromSeconds(1000);
        worker.MarkSqlConnectFailed(t1);

        Assert.Equal(1, worker.ConsecutiveSqlFailures);
        Assert.Equal(t1, worker.SqlDownSince); // fresh outage, not the stale t0
        Assert.False(worker.IsDetectionDegraded(t1 + TimeSpan.FromSeconds(179)));
        Assert.True(worker.IsDetectionDegraded(t1 + TimeSpan.FromSeconds(180)));
    }

    [Fact]
    public void SerializeRxBatch_LegacyFlag_CannotEmitLegacyQueueOrRawRxDrugMetadata()
    {
        // Track 3 invariant (Codex CRITICAL #15, closed 2026-05-12):
        // the legacy rxDeliveryQueue used to ship patientFirstName /
        // patientLastInitial / patientPhone / deliveryAddress1-2 /
        // deliveryCity / deliveryState / deliveryZip cleartext (null or
        // populated). Cloud's sanitizeSnapshotData stripped them before
        // insert, but minimum-necessary HIPAA forbids putting them on
        // the wire at all. Pin the absence so a future regression
        // re-introducing those keys is a visible diff + failing test.
        var batch = new List<RxMetadata>
        {
            new("12345", "Amoxicillin 500mg", "00093-3109-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var json = RxDetectionWorker.SerializeRxBatch(batch, includeLegacyDeliveryQueue: true);
        var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        var payload = doc.RootElement.GetRawText();
        Assert.DoesNotContain("12345", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Amoxicillin", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeRxBatch_LegacyQueue_ExcludesPhiEvenWhenPatientDetailsProvided()
    {
        // Even when a caller pre-populates the patientDetails map (used by
        // the canonical rxOrderCandidates path for HMAC-hashed delivery),
        // the legacy rxDeliveryQueue still drops PHI on the floor.
        // patientDetails is intentionally NOT read by the legacy branch.
        var batch = new List<RxMetadata>
        {
            new("12345", "Amoxicillin 500mg", "00093-3109-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["12345"] = new("12345", "John", "D", "6195551234",
                "123 Main St", null, "El Cajon", "CA", "92020")
        };

        var json = RxDetectionWorker.SerializeRxBatch(batch, "", patientMap, includeLegacyDeliveryQueue: true);
        var doc = JsonDocument.Parse(json);
        var candidate = doc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0];
        Assert.False(candidate.TryGetProperty("patientDelivery", out _));
        Assert.DoesNotContain(
            candidate.GetProperty("fieldConfidence").EnumerateObject(),
            property => property.Name.StartsWith("patientDelivery", StringComparison.Ordinal));
        Assert.DoesNotContain(
            candidate.GetProperty("fieldProvenance").EnumerateObject(),
            property => property.Name.StartsWith("patientDelivery", StringComparison.Ordinal));

        var payload = doc.RootElement.GetRawText();
        Assert.DoesNotContain("John", payload);
        Assert.DoesNotContain("6195551234", payload);
        Assert.DoesNotContain("123 Main St", payload);
        Assert.DoesNotContain("El Cajon", payload);
        Assert.DoesNotContain("92020", payload);
    }

    [Fact]
    public void SerializeRxBatch_ContainsOperationalFields()
    {
        var batch = new List<RxMetadata>
        {
            new("12345", "Amoxicillin 500mg", "00093-3109-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var json = RxDetectionWorker.SerializeRxBatch(batch, includeLegacyDeliveryQueue: true);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("rx_delivery_queue", root.GetProperty("snapshotType").GetString());
        var data = root.GetProperty("data");
        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        var rx = data.GetProperty("rxOrderCandidates")[0];
        var expectedHash = PhiScrubber.HmacHash("12345", "[no-hmac-salt]");
        Assert.Equal(expectedHash, rx.GetProperty("rxHash").GetString());
        Assert.Equal("00093-3109-01", rx.GetProperty("medication").GetProperty("ndc").GetString());
        Assert.Equal(30m, rx.GetProperty("medication").GetProperty("quantity").GetDecimal());
        Assert.DoesNotContain("Amoxicillin", rx.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeRxBatch_EmptyList_ProducesValidJson()
    {
        var json = RxDetectionWorker.SerializeRxBatch(Array.Empty<RxMetadata>());
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("data").GetProperty("totalDetected").GetInt32());
    }

    [Fact]
    public void SerializeRxBatch_HashesRxNumber()
    {
        var rxs = new List<RxMetadata>
        {
            new("12345", "Lisinopril", "12345-678-90",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var json = RxDetectionWorker.SerializeRxBatch(rxs, "test-salt", includeLegacyDeliveryQueue: true);
        var doc = JsonDocument.Parse(json);
        var rx = doc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0];

        var expectedHash = PhiScrubber.HmacHash("12345", "test-salt");
        Assert.Equal(expectedHash, rx.GetProperty("rxHash").GetString());
        Assert.NotEqual("12345", rx.GetProperty("rxHash").GetString());
    }

    [Fact]
    public void SerializeRxBatch_EmitsCanonicalRxOrderCandidatesWithoutPlainRxNumbers()
    {
        var detectedAt = DateTimeOffset.Parse("2026-04-29T12:00:00+00:00");
        var rxs = new List<RxMetadata>
        {
            new(
                RxNumber: "12345",
                DrugName: "Lisinopril 10mg",
                Ndc: "00093-7180-01",
                DateFilled: DateTime.UtcNow,
                Quantity: 30m,
                StatusGuid: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                DetectedAt: detectedAt,
                FillNumber: 1,
                DaysSupply: 30,
                DrugSchedule: 2)
        };

        var json = RxDetectionWorker.SerializeRxBatch(rxs, "pharmacy-salt");
        var doc = JsonDocument.Parse(json);
        var candidate = doc.RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];

        var serializedCandidate = candidate.GetRawText();
        var expectedHash = PhiScrubber.HmacHash("12345", "pharmacy-salt");

        Assert.Equal(expectedHash, candidate.GetProperty("rxHash").GetString());
        Assert.False(candidate.TryGetProperty("rxNumber", out _));
        Assert.False(candidate.TryGetProperty("sourceExternalKeyHash", out _));
        Assert.DoesNotContain("12345", serializedCandidate);
        Assert.DoesNotContain("Lisinopril", serializedCandidate);
        Assert.Equal(1, candidate.GetProperty("schemaVersion").GetInt32());

        var medication = candidate.GetProperty("medication");
        Assert.Equal(PhiScrubber.HmacHash("lisinopril 10mg", "pharmacy-salt"), medication.GetProperty("nameHash").GetString());
        Assert.Equal("00093-7180-01", medication.GetProperty("ndc").GetString());
        Assert.True(medication.GetProperty("isControlled").GetBoolean());
        Assert.Equal(2, medication.GetProperty("drugSchedule").GetInt32());
        Assert.True(medication.GetProperty("patientIdRequired").GetBoolean());
        Assert.Equal(30, medication.GetProperty("daysSupply").GetInt32());

        var provenance = candidate.GetProperty("provenance");
        Assert.Equal("sql", provenance.GetProperty("extractionMethod").GetString());
        Assert.Equal("PioneerRx", provenance.GetProperty("pms").GetString());
        Assert.Equal("pioneerrx.sql.metadata.v1", provenance.GetProperty("schemaSignature").GetString());
        var localEvidenceId = provenance.GetProperty("evidenceId").GetString();
        Assert.Matches("^rxh-[a-f0-9]{16}-[0-9]{10}$", localEvidenceId);
        Assert.DoesNotContain("12345", localEvidenceId);
        Assert.Empty(candidate.GetProperty("warnings").EnumerateArray());
        Assert.Equal(1.0d, candidate.GetProperty("confidence").GetDouble(), precision: 3);
        Assert.True(candidate.GetProperty("fieldProvenance").TryGetProperty("rxHash", out var rxProv));
        Assert.Equal("sql", rxProv.GetProperty("source").GetString());
        Assert.Equal("phi-direct-hmac", rxProv.GetProperty("classification").GetString());
    }

    [Fact]
    public void SerializeRxBatch_RepeatedPollsReuseDailyFillEvidenceAndRotateNextDay()
    {
        var first = new RxMetadata(
            "RX-STABLE-1",
            "Lisinopril",
            "00093-7180-01",
            new DateTime(2026, 7, 9, 14, 30, 0),
            30m,
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse("2026-07-10T12:00:01Z"),
            FillNumber: 2);
        var laterPoll = first with { DetectedAt = DateTimeOffset.Parse("2026-07-10T19:45:59Z") };

        using var firstDoc = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch([first], "stable-salt"));
        using var laterDoc = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch([laterPoll], "stable-salt"));
        var firstProvenance = firstDoc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0]
            .GetProperty("provenance");
        var laterProvenance = laterDoc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0]
            .GetProperty("provenance");

        Assert.Equal(
            firstProvenance.GetProperty("evidenceId").GetString(),
            laterProvenance.GetProperty("evidenceId").GetString());
        Assert.NotEqual(
            firstProvenance.GetProperty("capturedAtUtc").GetString(),
            laterProvenance.GetProperty("capturedAtUtc").GetString());

        var nextFill = laterPoll with { FillNumber = 3 };
        using var nextFillDoc = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch([nextFill], "stable-salt"));
        Assert.NotEqual(
            firstProvenance.GetProperty("evidenceId").GetString(),
            nextFillDoc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0]
                .GetProperty("provenance").GetProperty("evidenceId").GetString());

        var nextDay = laterPoll with { DetectedAt = DateTimeOffset.Parse("2026-07-11T00:00:01Z") };
        using var nextDayDoc = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch([nextDay], "stable-salt"));
        Assert.NotEqual(
            firstProvenance.GetProperty("evidenceId").GetString(),
            nextDayDoc.RootElement.GetProperty("data").GetProperty("rxOrderCandidates")[0]
                .GetProperty("provenance").GetProperty("evidenceId").GetString());
    }

    [Fact]
    public void SerializeRxBatch_CandidateOnlyMode_OmitsLegacyQueueAndPlainPhi()
    {
        var rxs = new List<RxMetadata>
        {
            new(
                RxNumber: "RX-PLAIN-1001",
                DrugName: "Cephalexin 500mg",
                Ndc: "00093-3147-01",
                DateFilled: DateTime.UtcNow,
                Quantity: 28m,
                StatusGuid: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                DetectedAt: DateTimeOffset.Parse("2026-05-06T15:20:00+00:00"),
                FillNumber: 0,
                DaysSupply: 7)
        };
        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["RX-PLAIN-1001"] = new("RX-PLAIN-1001", "Nora", "P", "6195552222",
                "1234 Privacy Ln", "Unit 8", "San Diego", "CA", "92101")
        };

        var json = RxDetectionWorker.SerializeRxBatch(
            rxs,
            "candidate-only-salt",
            patientMap);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.GetProperty("data");

        Assert.Equal("rx_delivery_queue", root.GetProperty("snapshotType").GetString());
        Assert.True(data.TryGetProperty("rxOrderCandidates", out var candidates));
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        Assert.Equal(1, data.GetProperty("totalDetected").GetInt32());

        var payload = root.GetRawText();
        Assert.DoesNotContain("RX-PLAIN-1001", payload);
        Assert.DoesNotContain("Cephalexin", payload);
        Assert.DoesNotContain("Nora", payload);
        Assert.DoesNotContain("6195552222", payload);
        Assert.DoesNotContain("1234 Privacy Ln", payload);
        Assert.DoesNotContain("Unit 8", payload);
    }

    [Fact]
    public void SerializeRxBatch_PreApprovalCandidateExcludesAllPatientAndLocationDetailsEvenWhenProvided()
    {
        var rxs = new List<RxMetadata>
        {
            new(
                RxNumber: "99001",
                DrugName: "Metformin 500mg",
                Ndc: "00093-7214-01",
                DateFilled: DateTime.UtcNow,
                Quantity: 60m,
                StatusGuid: Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
                DetectedAt: DateTimeOffset.Parse("2026-04-29T12:00:00+00:00"),
                DaysSupply: 30)
        };
        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["99001"] = new("99001", "Sarah", "M", "7605551234",
                "456 Oak Ave", "Apt 3B", "Victorville", "CA", "92392")
        };

        var json = RxDetectionWorker.SerializeRxBatch(rxs, "test-salt", patientMap);
        var doc = JsonDocument.Parse(json);
        var candidate = doc.RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];
        var serializedCandidate = candidate.GetRawText();

        Assert.Empty(candidate.GetProperty("warnings").EnumerateArray());
        Assert.Equal(1.0d, candidate.GetProperty("confidence").GetDouble(), precision: 3);
        Assert.DoesNotContain("Sarah", serializedCandidate);
        Assert.DoesNotContain("456 Oak Ave", serializedCandidate);
        Assert.DoesNotContain("7605551234", serializedCandidate);
        Assert.DoesNotContain("Metformin", serializedCandidate);

        Assert.False(candidate.TryGetProperty("patientDelivery", out _));
        Assert.DoesNotContain("Victorville", serializedCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\":\"CA\"", serializedCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("92392", serializedCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("patientDelivery", serializedCandidate, StringComparison.Ordinal);
        Assert.DoesNotContain("missingAddress", serializedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RxOrderCandidateContract_HasNoPatientDeliveryProperty()
    {
        Assert.DoesNotContain(
            typeof(RxOrderCandidate).GetProperties(),
            property => string.Equals(property.Name, "PatientDelivery", StringComparison.Ordinal));
    }

    [Fact]
    public void SerializeRxBatch_CandidateCarriesWarningGradeSchemaDrift()
    {
        var rxs = new List<RxMetadata>
        {
            new(
                RxNumber: "99001",
                DrugName: "Metformin 500mg",
                Ndc: "00093-7214-01",
                DateFilled: DateTime.UtcNow,
                Quantity: 60m,
                StatusGuid: Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
                DetectedAt: DateTimeOffset.Parse("2026-04-29T12:00:00+00:00"))
        };
        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["99001"] = new("99001", "Sarah", "M", "7605551234",
                "456 Oak Ave", "Apt 3B", "Victorville", "CA", "92392")
        };
        var warningVerification = new ContractVerification(
            IsValid: false,
            Severity: CanarySeverity.Warning,
            DriftedComponents: new[] { "object" },
            BaselineHash: "baseline",
            ObservedHash: "observed",
            Details: "optional column widened");

        var json = RxDetectionWorker.SerializeRxBatch(
            rxs,
            "test-salt",
            patientMap,
            warningVerification);
        var doc = JsonDocument.Parse(json);
        var candidate = doc.RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];
        var warnings = candidate.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetString())
            .ToArray();

        Assert.Contains("schemaCanaryDrift", warnings);
        Assert.Contains("schemaCanaryObject", warnings);
        Assert.True(candidate.GetProperty("confidence").GetDouble() < 1.0d);
    }

    [Fact]
    public void SerializeRxBatch_ConfidenceUsesEvidenceNotWarningCount()
    {
        var rxs = new List<RxMetadata>
        {
            new(
                RxNumber: "99001",
                DrugName: "Metformin 500mg",
                Ndc: "00093-7214-01",
                DateFilled: DateTime.UtcNow,
                Quantity: 60m,
                StatusGuid: Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
                DetectedAt: DateTimeOffset.Parse("2026-04-29T12:00:00+00:00"),
                DaysSupply: 30)
        };
        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["99001"] = new("99001", "Sarah", "M", "7605551234",
                "456 Oak Ave", "Apt 3B", "Victorville", "CA", "92392")
        };
        var objectOnly = new ContractVerification(
            IsValid: false,
            Severity: CanarySeverity.Warning,
            DriftedComponents: new[] { "object" },
            BaselineHash: "baseline",
            ObservedHash: "observed",
            Details: "optional object changed");
        var manyComponents = new ContractVerification(
            IsValid: false,
            Severity: CanarySeverity.Warning,
            DriftedComponents: new[] { "object", "column", "index", "type" },
            BaselineHash: "baseline",
            ObservedHash: "observed",
            Details: "field-shaped canary warning");

        var objectOnlyCandidate = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch(
                rxs,
                "test-salt",
                patientMap,
                objectOnly))
            .RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];
        var manyComponentsCandidate = JsonDocument.Parse(RxDetectionWorker.SerializeRxBatch(
                rxs,
                "test-salt",
                patientMap,
                manyComponents))
            .RootElement
            .GetProperty("data")
            .GetProperty("rxOrderCandidates")[0];

        Assert.NotEqual(
            objectOnlyCandidate.GetProperty("warnings").GetArrayLength(),
            manyComponentsCandidate.GetProperty("warnings").GetArrayLength());
        Assert.Equal(
            objectOnlyCandidate.GetProperty("confidence").GetDouble(),
            manyComponentsCandidate.GetProperty("confidence").GetDouble(),
            precision: 6);
    }

    [Fact]
    public void CloudSyncFails_EnrichedBatchPersistedToSqlite()
    {
        // Simulate the exact path: serialize enriched batch → persist to SQLite → retrieve intact
        var batch = new List<RxMetadata>
        {
            new("99001", "Metformin 500mg", "00093-7214-01",
                DateTime.UtcNow, 60m, Guid.NewGuid(), DateTimeOffset.UtcNow),
            new("99002", "Atorvastatin 20mg", "00378-3952-77",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["99001"] = new("99001", "Sarah", "M", "7605551234",
                "456 Oak Ave", "Apt 3B", "Victorville", "CA", "92392"),
            ["99002"] = new("99002", "Ahmed", "K", "6195559876",
                "789 Pine St", null, "El Cajon", "CA", "92020")
        };

        // This is the exact PHI-bearing call RunLegacyDetectionAsync makes before InsertUnsyncedBatch.
        var json = RxDetectionWorker.SerializeRxBatch(batch, "test-salt", patientMap, includeLegacyDeliveryQueue: true);

        // Simulate cloud sync failure → persist to SQLite (same as line 138 in RxDetectionWorker)
        _stateDb.InsertUnsyncedBatch(json);

        // Verify: batch persisted and retrievable
        var pending = _stateDb.GetPendingBatches();
        Assert.Single(pending);

        // Verify: the inert legacy flag cannot resurrect rxDeliveryQueue in persisted retries.
        var doc = JsonDocument.Parse(pending[0].Payload);
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        var candidates = data.GetProperty("rxOrderCandidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.Equal(PhiScrubber.HmacHash("99001", "test-salt"), candidates[0].GetProperty("rxHash").GetString());
        Assert.Equal(PhiScrubber.HmacHash("99002", "test-salt"), candidates[1].GetProperty("rxHash").GetString());
        Assert.False(candidates[0].TryGetProperty("patientDelivery", out _));
        Assert.False(candidates[1].TryGetProperty("patientDelivery", out _));

        // Per-name PHI absence at the persisted-payload level — catches any
        // future caller that smuggles PHI into the legacy shape via another
        // code path before SQLite persistence.
        var raw = pending[0].Payload;
        Assert.DoesNotContain("Sarah", raw);
        Assert.DoesNotContain("Ahmed", raw);
        Assert.DoesNotContain("7605551234", raw);
        Assert.DoesNotContain("456 Oak Ave", raw);
        Assert.DoesNotContain("789 Pine St", raw);
        Assert.DoesNotContain("Metformin", raw);
        Assert.DoesNotContain("Atorvastatin", raw);
    }

    [Fact]
    public void RetryPendingBatches_SendsPreviouslyFailedBatch()
    {
        // Insert a batch simulating a previous cloud sync failure
        var batch = new List<RxMetadata>
        {
            new("88001", "Lisinopril 10mg", "00378-0127-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        var patientMap = new Dictionary<string, RxPatientDetails>
        {
            ["88001"] = new("88001", "Maria", "G", "8585551111",
                "100 First St", null, "San Diego", "CA", "92101")
        };
        var json = RxDetectionWorker.SerializeRxBatch(batch, "", patientMap, includeLegacyDeliveryQueue: true);
        _stateDb.InsertUnsyncedBatch(json);

        // Verify batch is pending
        var pending = _stateDb.GetPendingBatches();
        Assert.Single(pending);
        var batchId = pending[0].Id;

        // Simulate successful cloud sync on retry (RetryPendingBatchesAsync calls DeleteBatch on success)
        _stateDb.DeleteBatch(batchId);

        // Verify batch is cleared — exactly what RetryPendingBatchesAsync does after TrySyncPayloadToCloudAsync returns true
        var afterRetry = _stateDb.GetPendingBatches();
        Assert.Empty(afterRetry);
    }

    [Fact]
    public void RetryPendingBatches_IncrementRetryOnFailure()
    {
        var json = RxDetectionWorker.SerializeRxBatch(
            new List<RxMetadata> { new("77001", "Test Drug", "12345-678-90",
                DateTime.UtcNow, 10m, Guid.NewGuid(), DateTimeOffset.UtcNow) });
        _stateDb.InsertUnsyncedBatch(json);

        var pending = _stateDb.GetPendingBatches();
        Assert.Equal(0, pending[0].RetryCount);

        // Simulate failed retry (RetryPendingBatchesAsync calls IncrementBatchRetry on failure)
        _stateDb.IncrementBatchRetry(pending[0].Id);

        pending = _stateDb.GetPendingBatches();
        Assert.Equal(1, pending[0].RetryCount);
        Assert.Equal("pending", pending[0].Status);
    }

    [Fact]
    public void RetryPendingBatches_DeadLettersAfterMaxRetries()
    {
        var json = RxDetectionWorker.SerializeRxBatch(
            new List<RxMetadata> { new("66001", "Test Drug", "12345-678-90",
                DateTime.UtcNow, 10m, Guid.NewGuid(), DateTimeOffset.UtcNow) });
        _stateDb.InsertUnsyncedBatch(json);

        var batchId = _stateDb.GetPendingBatches()[0].Id;

        // Exhaust 10 retries (IncrementBatchRetry dead-letters at retry_count >= 10)
        for (int i = 0; i < 10; i++)
            _stateDb.IncrementBatchRetry(batchId);

        // Batch should be dead-lettered and no longer appear in pending
        var pending = _stateDb.GetPendingBatches();
        Assert.Empty(pending);
        Assert.Equal(1, _stateDb.GetDeadLetterCount());
    }

    [Fact]
    public void CanaryDetection_PersistsProtectedCorrelationWithoutPreApprovalPatientQuery()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.Contains("PersistRxCorrelations(result.Rxs, hmacSalt)", source);
        Assert.Contains("PersistRxCorrelations(detection.Rxs, hmacSalt)", source);
        Assert.DoesNotContain("EnrichPatientDetailsAsync", source);
        Assert.DoesNotContain("PullPatientForRxAsync", source);
    }

    [Fact]
    public void CanaryDetection_EstablishesBaselineFromLiveObservedSchema()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.Contains("EstablishBaselineAsync(ct)", source);
        Assert.DoesNotContain("templateBaseline", source);
    }

    [Fact]
    public void LiveDetection_LegacyOptionCannotCreateLegacyWireShape()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.DoesNotContain("data[\"rxDeliveryQueue\"]", source);
        Assert.Contains("includeLegacyDeliveryQueue is intentionally inert", source);
    }

    [Fact]
    public void RxDetectionWorker_Source_NeverNamesPhiFieldsOnSyncWire()
    {
        // Track 3 invariant (Codex CRITICAL #15, closed 2026-05-12): the
        // worker that owns the agent→cloud sync wire MUST NOT contain
        // any PHI-shaped field-name literals. Those names belonged to the
        // pre-2026-05-12 legacy rxDeliveryQueue. Re-introducing one would
        // re-open the wire-tap exposure that cloud-side
        // sanitizeSnapshotData silently papered over. PHI delivery
        // details flow exclusively through SendApprovedPatientDetailsAsync
        // (typed PatientDetailsPayload, signed-command-driven path).
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.DoesNotContain("patientFirstName", source);
        Assert.DoesNotContain("patientLastInitial", source);
        Assert.DoesNotContain("patientPhone", source);
        Assert.DoesNotContain("deliveryAddress1", source);
        Assert.DoesNotContain("deliveryAddress2", source);
        Assert.DoesNotContain("deliveryCity", source);
        Assert.DoesNotContain("deliveryState", source);
        Assert.DoesNotContain("deliveryZip", source);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}");
    }

    public void Dispose()
    {
        _stateDb.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

}
