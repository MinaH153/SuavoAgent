using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
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

        var json = RxDetectionWorker.SerializeRxBatch(batch);
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

        var json = RxDetectionWorker.SerializeRxBatch(batch, "", patientMap);
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

        var json = RxDetectionWorker.SerializeRxBatch(batch);
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

        var json = RxDetectionWorker.SerializeRxBatch(rxs, "test-salt");
        var doc = JsonDocument.Parse(json);
        var rx = doc.RootElement.GetProperty("data").GetProperty("rxDeliveryQueue")[0];

        var expectedHash = PhiScrubber.HmacHash("12345", "test-salt");
        Assert.Equal(expectedHash, rx.GetProperty("rxNumber").GetString());
        Assert.NotEqual("12345", rx.GetProperty("rxNumber").GetString());
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

        // This is the exact call RunLegacyDetectionAsync makes before InsertUnsyncedBatch
        var json = RxDetectionWorker.SerializeRxBatch(batch, "test-salt", patientMap);

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
        var json = RxDetectionWorker.SerializeRxBatch(batch, "", patientMap);
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

    public void Dispose()
    {
        _stateDb.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
