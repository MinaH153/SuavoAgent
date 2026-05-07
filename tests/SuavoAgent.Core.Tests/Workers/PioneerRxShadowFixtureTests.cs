using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PioneerRxShadowFixtureTests
{
    [Fact]
    public void NonPhiShadowFixture_ProducesDeterministicCanonicalCandidate()
    {
        var harness = PioneerRxShadowReplayHarness.Load(FixturePath("pioneerrx-ready-batch.v1.json"));
        var replay = harness.Replay(includeLegacyDeliveryQueue: true);

        using var doc = JsonDocument.Parse(replay.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("rx_delivery_queue", root.GetProperty("snapshotType").GetString());
        Assert.Equal(replay.SerializedAtUtc.ToString("o"), root.GetProperty("data").GetProperty("syncedAt").GetString());
        Assert.Equal(1, replay.CandidateCount);
        Assert.Equal(1, replay.LegacyQueueCount);
        Assert.Single(replay.EvidenceIds);
        Assert.Matches("^rxh-[a-f0-9]{16}-[0-9]{10}$", replay.EvidenceIds[0]);

        var candidate = root.GetProperty("data").GetProperty("rxOrderCandidates")[0];

        Assert.Equal($"rxscan-{replay.SerializedAtUtc.ToUnixTimeMilliseconds()}", candidate.GetProperty("provenance").GetProperty("scanWindowId").GetString());
        Assert.Equal(replay.PharmacyId, candidate.GetProperty("provenance").GetProperty("pharmacyId").GetString());
        Assert.Equal(replay.AgentInstallId, candidate.GetProperty("provenance").GetProperty("agentInstallId").GetString());
        Assert.Equal(replay.HashKeyVersion, candidate.GetProperty("provenance").GetProperty("hashKeyVersion").GetString());
        Assert.Equal(replay.PmsVersion, candidate.GetProperty("provenance").GetProperty("pmsVersion").GetString());
        Assert.Equal(1.0d, candidate.GetProperty("confidence").GetDouble(), precision: 3);
        Assert.Empty(candidate.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void NonPhiShadowFixture_CandidateOnlyPayload_DoesNotEmitLegacyQueue()
    {
        var harness = PioneerRxShadowReplayHarness.Load(FixturePath("pioneerrx-ready-batch.v1.json"));
        var replay = harness.Replay(includeLegacyDeliveryQueue: false);

        using var doc = JsonDocument.Parse(replay.PayloadJson);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var candidate = data.GetProperty("rxOrderCandidates")[0];

        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        Assert.Equal(replay.SerializedAtUtc.ToString("o"), data.GetProperty("syncedAt").GetString());
        Assert.Equal($"rxscan-{replay.SerializedAtUtc.ToUnixTimeMilliseconds()}", candidate.GetProperty("provenance").GetProperty("scanWindowId").GetString());
        Assert.Equal(replay.PharmacyId, candidate.GetProperty("provenance").GetProperty("pharmacyId").GetString());
        Assert.Equal(replay.AgentInstallId, candidate.GetProperty("provenance").GetProperty("agentInstallId").GetString());
        Assert.Equal(replay.HashKeyVersion, candidate.GetProperty("provenance").GetProperty("hashKeyVersion").GetString());
        Assert.Equal(replay.PmsVersion, candidate.GetProperty("provenance").GetProperty("pmsVersion").GetString());
        Assert.Equal(1, replay.CandidateCount);
        Assert.Equal(0, replay.LegacyQueueCount);
    }

    [Fact]
    public void NonPhiShadowFixture_FailsClosedWhenForbiddenTokenWouldLeak()
    {
        var sourcePath = FixturePath("pioneerrx-ready-batch.v1.json");
        var fixture = JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
        var forbiddenInCandidate = fixture["forbiddenInCandidate"]!.AsArray();
        forbiddenInCandidate.Add("pioneerrx.sql.metadata.v1");

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, fixture.ToJsonString());
        try
        {
            var harness = PioneerRxShadowReplayHarness.Load(tempPath);
            var ex = Assert.Throws<InvalidOperationException>(() => harness.Replay(includeLegacyDeliveryQueue: true));
            Assert.Contains("forbiddenInCandidate", ex.Message);
            Assert.DoesNotContain("pioneerrx.sql.metadata.v1", ex.Message);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PioneerRxShadow", name);
}
