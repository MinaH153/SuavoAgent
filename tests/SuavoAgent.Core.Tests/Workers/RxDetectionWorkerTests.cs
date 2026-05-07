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

public class RxDetectionWorkerTests : IDisposable
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

    [Fact]
    public void SerializeRxBatch_WithoutPatientDetails_NullsPhiValues()
    {
        var batch = new List<RxMetadata>
        {
            new("12345", "Amoxicillin 500mg", "00093-3109-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var json = RxDetectionWorker.SerializeRxBatch(batch, includeLegacyDeliveryQueue: true);
        var doc = JsonDocument.Parse(json);
        var rx = doc.RootElement.GetProperty("data").GetProperty("rxDeliveryQueue")[0];

        // PHI keys present but null when no patient details provided
        Assert.Equal(JsonValueKind.Null, rx.GetProperty("patientFirstName").ValueKind);
        Assert.Equal(JsonValueKind.Null, rx.GetProperty("deliveryAddress1").ValueKind);
        Assert.Equal(JsonValueKind.Null, rx.GetProperty("deliveryCity").ValueKind);
    }

    [Fact]
    public void SerializeRxBatch_WithPatientDetails_IncludesDeliveryData()
    {
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
        var rx = doc.RootElement.GetProperty("data").GetProperty("rxDeliveryQueue")[0];

        Assert.Equal("John", rx.GetProperty("patientFirstName").GetString());
        Assert.Equal("D", rx.GetProperty("patientLastInitial").GetString());
        Assert.Equal("123 Main St", rx.GetProperty("deliveryAddress1").GetString());
        Assert.Equal("El Cajon", rx.GetProperty("deliveryCity").GetString());
        Assert.Equal("CA", rx.GetProperty("deliveryState").GetString());
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
        var queue = root.GetProperty("data").GetProperty("rxDeliveryQueue");
        Assert.Equal(1, queue.GetArrayLength());

        var rx = queue[0];
        var expectedHash = PhiScrubber.HmacHash("12345", "[no-hmac-salt]");
        Assert.Equal(expectedHash, rx.GetProperty("rxNumber").GetString());
        Assert.Equal("Amoxicillin 500mg", rx.GetProperty("drugName").GetString());
        Assert.Equal("00093-3109-01", rx.GetProperty("ndc").GetString());
        Assert.Equal(30m, rx.GetProperty("quantity").GetDecimal());
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
        var rx = doc.RootElement.GetProperty("data").GetProperty("rxDeliveryQueue")[0];

        var expectedHash = PhiScrubber.HmacHash("12345", "test-salt");
        Assert.Equal(expectedHash, rx.GetProperty("rxNumber").GetString());
        Assert.NotEqual("12345", rx.GetProperty("rxNumber").GetString());
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
        Assert.Equal("missingPatientIdentity", candidate.GetProperty("warnings")[0].GetString());
        Assert.Equal("missingDeliveryAddress", candidate.GetProperty("warnings")[1].GetString());
        Assert.Equal("missingZip5", candidate.GetProperty("warnings")[2].GetString());
        Assert.True(candidate.GetProperty("confidence").GetDouble() < 1.0);
        Assert.True(candidate.GetProperty("fieldProvenance").TryGetProperty("rxHash", out var rxProv));
        Assert.Equal("sql", rxProv.GetProperty("source").GetString());
        Assert.Equal("phi-direct-hmac", rxProv.GetProperty("classification").GetString());
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
    public void SerializeRxBatch_CandidateIncludesPatientProvenanceAndFullConfidenceWhenDeliveryFieldsExist()
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

        var patientDelivery = candidate.GetProperty("patientDelivery");
        Assert.Equal(PhiScrubber.HmacHash("sarah m", "test-salt"), patientDelivery.GetProperty("nameHash").GetString());
        Assert.Equal(PhiScrubber.HmacHash("456 oak ave", "test-salt"), patientDelivery.GetProperty("addressLine1Hash").GetString());
        Assert.Equal(PhiScrubber.HmacHash("7605551234", "test-salt"), patientDelivery.GetProperty("phoneHash").GetString());
        Assert.Equal("Victorville", patientDelivery.GetProperty("city").GetString());
        Assert.Equal("CA", patientDelivery.GetProperty("state").GetString());
        Assert.Equal("92392", patientDelivery.GetProperty("zip5").GetString());
        Assert.False(patientDelivery.GetProperty("zip4Present").GetBoolean());
        Assert.Empty(patientDelivery.GetProperty("missingAddressFlags").EnumerateArray());

        Assert.True(candidate.GetProperty("fieldProvenance").TryGetProperty("patientDelivery.nameHash", out var patientProv));
        Assert.Equal("phi-direct", patientProv.GetProperty("classification").GetString());
        Assert.Equal("sql_patient_detail", patientProv.GetProperty("sourceDetail").GetString());
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
        Assert.Equal(1, pending.Count);

        // Verify: round-tripped JSON preserves ALL enriched patient fields
        var doc = JsonDocument.Parse(pending[0].Payload);
        var queue = doc.RootElement.GetProperty("data").GetProperty("rxDeliveryQueue");
        Assert.Equal(2, queue.GetArrayLength());

        var rx1 = queue[0];
        Assert.Equal(PhiScrubber.HmacHash("99001", "test-salt"), rx1.GetProperty("rxNumber").GetString());
        Assert.Equal("Metformin 500mg", rx1.GetProperty("drugName").GetString());
        Assert.Equal("Sarah", rx1.GetProperty("patientFirstName").GetString());
        Assert.Equal("M", rx1.GetProperty("patientLastInitial").GetString());
        Assert.Equal("7605551234", rx1.GetProperty("patientPhone").GetString());
        Assert.Equal("456 Oak Ave", rx1.GetProperty("deliveryAddress1").GetString());
        Assert.Equal("Apt 3B", rx1.GetProperty("deliveryAddress2").GetString());
        Assert.Equal("Victorville", rx1.GetProperty("deliveryCity").GetString());
        Assert.Equal("CA", rx1.GetProperty("deliveryState").GetString());
        Assert.Equal("92392", rx1.GetProperty("deliveryZip").GetString());

        var rx2 = queue[1];
        Assert.Equal(PhiScrubber.HmacHash("99002", "test-salt"), rx2.GetProperty("rxNumber").GetString());
        Assert.Equal("Ahmed", rx2.GetProperty("patientFirstName").GetString());
        Assert.Equal("789 Pine St", rx2.GetProperty("deliveryAddress1").GetString());
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
        Assert.Equal(1, pending.Count);
        var batchId = pending[0].Id;

        // Simulate successful cloud sync on retry (RetryPendingBatchesAsync calls DeleteBatch on success)
        _stateDb.DeleteBatch(batchId);

        // Verify batch is cleared — exactly what RetryPendingBatchesAsync does after TrySyncPayloadToCloudAsync returns true
        var afterRetry = _stateDb.GetPendingBatches();
        Assert.Equal(0, afterRetry.Count);
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
        Assert.Equal(0, pending.Count);
        Assert.Equal(1, _stateDb.GetDeadLetterCount());
    }

    [Fact]
    public void CanaryDetection_UsesAuditedPatientEnrichmentAfterSchemaPass()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.Contains("EnrichPatientDetailsAsync(result.Rxs, hmacSalt, ct)", source);
        Assert.Contains("EnrichPatientDetailsAsync(detection.Rxs, hmacSalt, ct)", source);
        Assert.Contains("Trigger: \"rx_detection_worker.enrich_for_delivery_sync\"", source);
    }

    [Fact]
    public void CanaryDetection_EstablishesBaselineFromLiveObservedSchema()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.Contains("EstablishBaselineAsync(ct)", source);
        Assert.DoesNotContain("templateBaseline", source);
    }

    [Fact]
    public void LiveDetection_GatesLegacyPhiQueueBehindExplicitOption()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Workers/RxDetectionWorker.cs");

        Assert.DoesNotContain("includeLegacyDeliveryQueue: true);", source);
        Assert.Contains("includeLegacyDeliveryQueue: _options.EnableLegacyPhiDeliveryQueueSync", source);
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
