using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
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
    public void SerializeRxBatch_ContainsZeroPhi()
    {
        var batch = new List<RxMetadata>
        {
            new("12345", "Amoxicillin 500mg", "00093-3109-01",
                DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow),
            new("67890", "Lisinopril 10mg", "00591-0270-01",
                DateTime.UtcNow, 90m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        };

        var json = RxDetectionWorker.SerializeRxBatch(batch);

        // Must NOT contain any patient-identifiable fields
        Assert.DoesNotContain("patientFirstName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patientLastInitial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patientPhone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryAddress", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryCity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryState", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryZip", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firstName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("12345", rx.GetProperty("rxNumber").GetString());
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

    public void Dispose()
    {
        _stateDb.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
