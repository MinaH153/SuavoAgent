using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PioneerRxShadowFixtureTests
{
    [Fact]
    public void ShadowFixtureCommand_RequiresDashboardEvidenceReleaseMinimum()
    {
        Assert.Equal(
            "b8f8888279c8960280823c2308a0339fff4a2c74",
            PioneerRxShadowFixtureCommand.RequiredTrack2SourceCommitMinimum);
    }

    [Fact]
    public void ShadowFixtureCommand_AllowsOnlyDigestBackedDashboardEvidence()
    {
        using var doc = JsonDocument.Parse("""
        {
          "commandId": "cmd-shadow-proof",
          "requesterId": "operator-1",
          "maxRows": 3,
          "includeSyntheticPatientDetails": false,
          "dashboardEvidence": {
            "candidateRowsVisible": true,
            "correctionPathExercised": true,
            "candidateRowsReceiptSha256": "0123456789abcdef0123456789abcdef",
            "correctionPathReceiptSha256": "fedcba9876543210fedcba9876543210"
          }
        }
        """);

        Assert.False(PioneerRxShadowFixtureCommand.ContainsUnsafeField(doc.RootElement));

        var evidence = PioneerRxShadowFixtureCommand.ReadDashboardEvidence(doc.RootElement);
        Assert.True(evidence.CandidateRowsVisible);
        Assert.True(evidence.CorrectionPathExercised);
        Assert.Equal("0123456789abcdef0123456789abcdef", evidence.CandidateRowsReceiptSha256);
        Assert.Equal("fedcba9876543210fedcba9876543210", evidence.CorrectionPathReceiptSha256);
    }

    [Fact]
    public void ShadowFixtureCommand_AllowsOnlyDigestBackedReleaseEvidence()
    {
        using var doc = JsonDocument.Parse("""
        {
          "commandId": "cmd-shadow-proof",
          "requesterId": "operator-1",
          "maxRows": 3,
          "includeSyntheticPatientDetails": false,
          "releaseEvidence": {
            "releaseTag": "v3.14.6",
            "sourceCommit": "b8f8888279c8960280823c2308a0339fff4a2c74",
            "artifactSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "checksumSignatureSha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "installReceiptSha256": "2222222222222222222222222222222222222222222222222222222222222222",
            "rollbackArtifactSha256": "3333333333333333333333333333333333333333333333333333333333333333"
          }
        }
        """);

        Assert.False(PioneerRxShadowFixtureCommand.ContainsUnsafeField(doc.RootElement));

        var evidence = PioneerRxShadowFixtureCommand.ReadReleaseEvidence(doc.RootElement);
        Assert.True(evidence.Recorded);
        Assert.Equal("v3.14.6", evidence.ReleaseTag);
        Assert.Equal("b8f8888279c8960280823c2308a0339fff4a2c74", evidence.SourceCommit);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", evidence.ArtifactSha256);
        Assert.Equal("1111111111111111111111111111111111111111111111111111111111111111", evidence.ChecksumSignatureSha256);
        Assert.Equal("2222222222222222222222222222222222222222222222222222222222222222", evidence.InstallReceiptSha256);
        Assert.Equal("3333333333333333333333333333333333333333333333333333333333333333", evidence.RollbackArtifactSha256);
    }

    [Theory]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "dashboardEvidence": {
            "candidateRowsVisible": true,
            "note": "Jane Doe row was visible"
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "dashboardEvidence": {
            "candidateRowsVisible": true,
            "candidateRowsReceiptSha256": "Jane Doe"
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "dashboardEvidence": {
            "candidateRowsVisible": true
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "dashboardEvidence": {
            "candidateRowsVisible": true,
            "candidateRowsReceiptSha256": "0123456789abcdef",
            "patientName": "Jane Doe"
          }
        }
        """)]
    public void ShadowFixtureCommand_RejectsUnsafeDashboardEvidence(string payload)
    {
        using var doc = JsonDocument.Parse(payload);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(doc.RootElement));
    }

    [Theory]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "releaseEvidence": {
            "releaseTag": "field release for Jane Doe",
            "sourceCommit": "b8f8888279c8960280823c2308a0339fff4a2c74",
            "artifactSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "checksumSignatureSha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "installReceiptSha256": "2222222222222222222222222222222222222222222222222222222222222222",
            "rollbackArtifactSha256": "3333333333333333333333333333333333333333333333333333333333333333"
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "releaseEvidence": {
            "releaseTag": "vfield",
            "sourceCommit": "b8f8888279c8960280823c2308a0339fff4a2c74",
            "artifactSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "checksumSignatureSha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "installReceiptSha256": "2222222222222222222222222222222222222222222222222222222222222222",
            "rollbackArtifactSha256": "3333333333333333333333333333333333333333333333333333333333333333"
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "releaseEvidence": {
            "releaseTag": "v3.14.6",
            "sourceCommit": "b8f8888279c8960280823c2308a0339fff4a2c74",
            "artifactSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg",
            "checksumSignatureSha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "installReceiptSha256": "2222222222222222222222222222222222222222222222222222222222222222",
            "rollbackArtifactSha256": "3333333333333333333333333333333333333333333333333333333333333333"
          }
        }
        """)]
    [InlineData("""
        {
          "commandId": "cmd-shadow-proof",
          "releaseEvidence": {
            "releaseTag": "v3.14.6",
            "sourceCommit": "b8f8888279c8960280823c2308a0339fff4a2c74",
            "artifactSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "checksumSignatureSha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "installReceiptSha256": "2222222222222222222222222222222222222222222222222222222222222222",
            "rollbackArtifactSha256": "3333333333333333333333333333333333333333333333333333333333333333",
            "patientName": "Jane Doe"
          }
        }
        """)]
    public void ShadowFixtureCommand_RejectsUnsafeReleaseEvidence(string payload)
    {
        using var doc = JsonDocument.Parse(payload);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(doc.RootElement));
    }

    [Fact]
    public void ShadowFixtureCommand_RejectsFreeTextEnvelopeIds()
    {
        using var doc = JsonDocument.Parse("""
        {
          "commandId": "cmd-shadow-proof",
          "requesterId": "Jane Doe",
          "maxRows": 3
        }
        """);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(doc.RootElement));

        using var unicodeDoc = JsonDocument.Parse("""
        {
          "commandId": "cmd-shadow-proof",
          "requesterId": "Jos\u00e9",
          "maxRows": 3
        }
        """);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(unicodeDoc.RootElement));
    }

    [Fact]
    public void Track2ExitGate_RequiresLocalReplaySchemaAndDashboardCorrectionProof()
    {
        var replay = new PioneerRxShadowReplayResult(
            PayloadJson: "{}",
            CandidateCount: 1,
            LegacyQueueCount: 0,
            EvidenceIds: new[] { "rxh-0123456789abcdef-1770000000" },
            RxHashes: new[] { "rxh-0123456789abcdef" },
            StableCandidateHashes: true,
            PayloadSha256Prefix: "0123456789abcdef",
            ForbiddenTokenHitCount: 0,
            SerializedAtUtc: new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero),
            PharmacyId: "pharm-1",
            AgentInstallId: "agent-1",
            PmsVersion: "PioneerRx Shadow Export",
            HashKeyVersion: "fixture-v1");
        var schemaCanary = new SchemaCanaryExportGate(
            Status: "passing",
            Severity: "info",
            Recorded: true,
            Passing: true,
            BaselineHash: "baseline",
            ObservedHash: "observed",
            DriftedComponents: Array.Empty<string>(),
            Details: null,
            RecordedAtUtc: "2026-05-07T12:00:00.0000000+00:00");
        var dashboardEvidence = new PioneerRxShadowDashboardEvidence(
            CandidateRowsVisible: true,
            CorrectionPathExercised: true,
            CandidateRowsReceiptSha256: "0123456789abcdef0123456789abcdef",
            CorrectionPathReceiptSha256: "fedcba9876543210fedcba9876543210");
        var releaseEvidence = new PioneerRxShadowReleaseEvidence(
            ReleaseTag: "v3.14.6",
            SourceCommit: "b8f8888279c8960280823c2308a0339fff4a2c74",
            ArtifactSha256: new string('0', 64),
            ChecksumSignatureSha256: new string('1', 64),
            InstallReceiptSha256: new string('2', 64),
            RollbackArtifactSha256: new string('3', 64));

        var gate = PioneerRxShadowFixtureCommand.BuildTrack2ExitGate(
            replay,
            schemaCanary,
            dashboardEvidence,
            releaseEvidence);

        Assert.True(gate.ZeroForbiddenTokens);
        Assert.True(gate.StableCandidateHashes);
        Assert.True(gate.SchemaCanaryRecorded);
        Assert.True(gate.SchemaCanaryPassing);
        Assert.True(gate.CandidateRowsDashboardVisible);
        Assert.True(gate.CorrectionPathExercised);
        Assert.True(gate.DashboardEvidenceRecorded);
        Assert.True(gate.ReleaseEvidenceRecorded);
        Assert.Equal("v3.14.6", gate.ReleaseTag);
        Assert.True(gate.ReadyForTrack2Exit);
    }

    [Fact]
    public void Track2ExitGate_StaysBlockedWithoutDashboardEvidence()
    {
        var replay = new PioneerRxShadowReplayResult(
            PayloadJson: "{}",
            CandidateCount: 1,
            LegacyQueueCount: 0,
            EvidenceIds: new[] { "rxh-0123456789abcdef-1770000000" },
            RxHashes: new[] { "rxh-0123456789abcdef" },
            StableCandidateHashes: true,
            PayloadSha256Prefix: "0123456789abcdef",
            ForbiddenTokenHitCount: 0,
            SerializedAtUtc: new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero),
            PharmacyId: "pharm-1",
            AgentInstallId: "agent-1",
            PmsVersion: "PioneerRx Shadow Export",
            HashKeyVersion: "fixture-v1");
        var schemaCanary = new SchemaCanaryExportGate(
            Status: "passing",
            Severity: "info",
            Recorded: true,
            Passing: true,
            BaselineHash: "baseline",
            ObservedHash: "observed",
            DriftedComponents: Array.Empty<string>(),
            Details: null,
            RecordedAtUtc: "2026-05-07T12:00:00.0000000+00:00");

        var gate = PioneerRxShadowFixtureCommand.BuildTrack2ExitGate(
            replay,
            schemaCanary,
            PioneerRxShadowDashboardEvidence.Empty,
            PioneerRxShadowReleaseEvidence.Empty);

        Assert.False(gate.DashboardEvidenceRecorded);
        Assert.False(gate.CandidateRowsDashboardVisible);
        Assert.False(gate.CorrectionPathExercised);
        Assert.False(gate.ReleaseEvidenceRecorded);
        Assert.False(gate.ReadyForTrack2Exit);
    }

    [Fact]
    public void Track2ExitGate_StaysBlockedWithoutSignedReleaseEvidence()
    {
        var replay = new PioneerRxShadowReplayResult(
            PayloadJson: "{}",
            CandidateCount: 1,
            LegacyQueueCount: 0,
            EvidenceIds: new[] { "rxh-0123456789abcdef-1770000000" },
            RxHashes: new[] { "rxh-0123456789abcdef" },
            StableCandidateHashes: true,
            PayloadSha256Prefix: "0123456789abcdef",
            ForbiddenTokenHitCount: 0,
            SerializedAtUtc: new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero),
            PharmacyId: "pharm-1",
            AgentInstallId: "agent-1",
            PmsVersion: "PioneerRx Shadow Export",
            HashKeyVersion: "fixture-v1");
        var schemaCanary = new SchemaCanaryExportGate(
            Status: "passing",
            Severity: "info",
            Recorded: true,
            Passing: true,
            BaselineHash: "baseline",
            ObservedHash: "observed",
            DriftedComponents: Array.Empty<string>(),
            Details: null,
            RecordedAtUtc: "2026-05-07T12:00:00.0000000+00:00");
        var dashboardEvidence = new PioneerRxShadowDashboardEvidence(
            CandidateRowsVisible: true,
            CorrectionPathExercised: true,
            CandidateRowsReceiptSha256: "0123456789abcdef0123456789abcdef",
            CorrectionPathReceiptSha256: "fedcba9876543210fedcba9876543210");

        var gate = PioneerRxShadowFixtureCommand.BuildTrack2ExitGate(
            replay,
            schemaCanary,
            dashboardEvidence,
            PioneerRxShadowReleaseEvidence.Empty);

        Assert.True(gate.DashboardEvidenceRecorded);
        Assert.False(gate.ReleaseEvidenceRecorded);
        Assert.False(gate.ReadyForTrack2Exit);
    }

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
        Assert.Single(replay.RxHashes);
        Assert.True(replay.StableCandidateHashes);
        Assert.Equal(0, replay.ForbiddenTokenHitCount);
        Assert.Matches("^[a-f0-9]{16}$", replay.PayloadSha256Prefix);
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
        Assert.True(replay.StableCandidateHashes);
        Assert.Equal(0, replay.ForbiddenTokenHitCount);
        Assert.Single(replay.RxHashes);
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

    [Fact]
    public void ShadowFixtureExporter_ScrubsLiveIdentifiersAndReplaysCandidateOnly()
    {
        var source = new[]
        {
            new RxMetadata(
                RxNumber: "987654321",
                DrugName: "ActualMedication 20mg",
                Ndc: "12345-6789-01",
                DateFilled: new DateTime(2026, 5, 6, 9, 15, 0, DateTimeKind.Utc),
                Quantity: 42,
                StatusGuid: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                DetectedAt: new DateTimeOffset(2026, 5, 6, 9, 17, 0, TimeSpan.Zero),
                FillNumber: 2,
                DaysSupply: 14,
                DrugSchedule: 3)
        };

        var json = PioneerRxShadowFixtureExporter.Export(
            source,
            new PioneerRxShadowFixtureExportOptions(
                SerializedAtUtc: new DateTimeOffset(2026, 5, 6, 15, 21, 0, TimeSpan.Zero),
                PharmacyId: "pharm-shadow",
                AgentInstallId: "agent-shadow",
                PmsVersion: "PioneerRx Shadow Export Test",
                IncludeSyntheticPatientDetails: false));

        Assert.DoesNotContain("987654321", json);
        Assert.DoesNotContain("ActualMedication", json);
        Assert.DoesNotContain("12345-6789-01", json);
        Assert.DoesNotContain("2026-05-06T09:15:00", json);
        Assert.Contains("SHADOW-RX-0001", json);
        Assert.Contains("ShadowMed 001", json);

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, json);
        try
        {
            var replay = PioneerRxShadowReplayHarness
                .Load(tempPath)
                .Replay(includeLegacyDeliveryQueue: false);

            Assert.Equal(1, replay.CandidateCount);
            Assert.Equal(0, replay.LegacyQueueCount);
            Assert.True(replay.StableCandidateHashes);
            Assert.Matches("^[a-f0-9]{16}$", replay.PayloadSha256Prefix);
            Assert.DoesNotContain("ShadowMed 001", replay.PayloadJson);
            Assert.DoesNotContain("SHADOW-RX-0001", replay.PayloadJson);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PioneerRxShadow", name);
}
