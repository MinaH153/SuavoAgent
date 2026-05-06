using System.Text.Json;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PioneerRxShadowFixtureTests
{
    [Fact]
    public void NonPhiShadowFixture_ProducesDeterministicCanonicalCandidate()
    {
        var fixture = LoadFixture("pioneerrx-ready-batch.v1.json");
        var serializedAt = fixture.RootElement.GetProperty("serializedAtUtc").GetDateTimeOffset();
        var hmacSalt = fixture.RootElement.GetProperty("hmacSalt").GetString()!;
        var hashKeyVersion = fixture.RootElement.GetProperty("hashKeyVersion").GetString()!;
        var pharmacyId = fixture.RootElement.GetProperty("pharmacyId").GetString()!;
        var agentInstallId = fixture.RootElement.GetProperty("agentInstallId").GetString()!;
        var pmsVersion = fixture.RootElement.GetProperty("pmsVersion").GetString()!;

        var rxs = fixture.RootElement.GetProperty("rxs")
            .EnumerateArray()
            .Select(ReadRx)
            .ToArray();
        var patients = fixture.RootElement.GetProperty("patients")
            .EnumerateArray()
            .Select(ReadPatient)
            .ToDictionary(p => p.RxNumber, p => p);

        var json = RxDetectionWorker.SerializeRxBatch(
            rxs,
            hmacSalt,
            patients,
            pharmacyId: pharmacyId,
            agentInstallId: agentInstallId,
            pmsVersion: pmsVersion,
            hashKeyVersion: hashKeyVersion,
            serializedAtUtc: serializedAt);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("rx_delivery_queue", root.GetProperty("snapshotType").GetString());
        Assert.Equal(serializedAt.ToString("o"), root.GetProperty("data").GetProperty("syncedAt").GetString());

        var candidate = root.GetProperty("data").GetProperty("rxOrderCandidates")[0];
        var candidateJson = candidate.GetRawText();
        var queueJson = root.GetProperty("data").GetProperty("rxDeliveryQueue").GetRawText();

        Assert.Equal($"rxscan-{serializedAt.ToUnixTimeMilliseconds()}", candidate.GetProperty("provenance").GetProperty("scanWindowId").GetString());
        Assert.Equal(pharmacyId, candidate.GetProperty("provenance").GetProperty("pharmacyId").GetString());
        Assert.Equal(agentInstallId, candidate.GetProperty("provenance").GetProperty("agentInstallId").GetString());
        Assert.Equal(hashKeyVersion, candidate.GetProperty("provenance").GetProperty("hashKeyVersion").GetString());
        Assert.Equal(pmsVersion, candidate.GetProperty("provenance").GetProperty("pmsVersion").GetString());
        Assert.Equal(1.0d, candidate.GetProperty("confidence").GetDouble(), precision: 3);
        Assert.Empty(candidate.GetProperty("warnings").EnumerateArray());

        foreach (var value in fixture.RootElement.GetProperty("forbiddenInCandidate").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.DoesNotContain(token, candidateJson);
        }

        foreach (var value in fixture.RootElement.GetProperty("forbiddenEverywhere").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.DoesNotContain(token, root.GetRawText());
        }

        foreach (var value in fixture.RootElement.GetProperty("minimumNecessaryInQueue").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.Contains(token, queueJson);
        }
    }

    [Fact]
    public void NonPhiShadowFixture_CandidateOnlyPayload_DoesNotEmitLegacyQueue()
    {
        var fixture = LoadFixture("pioneerrx-ready-batch.v1.json");
        var serializedAt = fixture.RootElement.GetProperty("serializedAtUtc").GetDateTimeOffset();
        var hmacSalt = fixture.RootElement.GetProperty("hmacSalt").GetString()!;
        var hashKeyVersion = fixture.RootElement.GetProperty("hashKeyVersion").GetString()!;
        var pharmacyId = fixture.RootElement.GetProperty("pharmacyId").GetString()!;
        var agentInstallId = fixture.RootElement.GetProperty("agentInstallId").GetString()!;
        var pmsVersion = fixture.RootElement.GetProperty("pmsVersion").GetString()!;

        var rxs = fixture.RootElement.GetProperty("rxs")
            .EnumerateArray()
            .Select(ReadRx)
            .ToArray();
        var patients = fixture.RootElement.GetProperty("patients")
            .EnumerateArray()
            .Select(ReadPatient)
            .ToDictionary(p => p.RxNumber, p => p);

        var json = RxDetectionWorker.SerializeRxBatch(
            rxs,
            hmacSalt,
            patients,
            pharmacyId: pharmacyId,
            agentInstallId: agentInstallId,
            pmsVersion: pmsVersion,
            hashKeyVersion: hashKeyVersion,
            serializedAtUtc: serializedAt,
            includeLegacyDeliveryQueue: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var candidate = data.GetProperty("rxOrderCandidates")[0];

        Assert.False(data.TryGetProperty("rxDeliveryQueue", out _));
        Assert.Equal(serializedAt.ToString("o"), data.GetProperty("syncedAt").GetString());
        Assert.Equal($"rxscan-{serializedAt.ToUnixTimeMilliseconds()}", candidate.GetProperty("provenance").GetProperty("scanWindowId").GetString());
        Assert.Equal(pharmacyId, candidate.GetProperty("provenance").GetProperty("pharmacyId").GetString());
        Assert.Equal(agentInstallId, candidate.GetProperty("provenance").GetProperty("agentInstallId").GetString());
        Assert.Equal(hashKeyVersion, candidate.GetProperty("provenance").GetProperty("hashKeyVersion").GetString());
        Assert.Equal(pmsVersion, candidate.GetProperty("provenance").GetProperty("pmsVersion").GetString());

        var payload = root.GetRawText();
        foreach (var value in fixture.RootElement.GetProperty("forbiddenInCandidate").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.DoesNotContain(token, payload);
        }

        foreach (var value in fixture.RootElement.GetProperty("forbiddenEverywhere").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.DoesNotContain(token, payload);
        }

        foreach (var value in fixture.RootElement.GetProperty("minimumNecessaryInQueue").EnumerateArray())
        {
            var token = value.GetString()!;
            Assert.DoesNotContain(token, payload);
        }
    }

    private static JsonDocument LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PioneerRxShadow", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static RxMetadata ReadRx(JsonElement element) => new(
        RxNumber: element.GetProperty("rxNumber").GetString()!,
        DrugName: element.GetProperty("drugName").GetString(),
        Ndc: element.GetProperty("ndc").GetString(),
        DateFilled: element.GetProperty("dateFilled").GetDateTime(),
        Quantity: element.GetProperty("quantity").GetDecimal(),
        StatusGuid: element.GetProperty("statusGuid").GetGuid(),
        DetectedAt: element.GetProperty("detectedAt").GetDateTimeOffset(),
        FillNumber: element.GetProperty("fillNumber").GetInt32(),
        DaysSupply: element.GetProperty("daysSupply").GetInt32(),
        DrugSchedule: element.GetProperty("drugSchedule").GetInt32(),
        Priority: element.GetProperty("priority").GetString()!,
        TemperatureRequirement: element.GetProperty("temperatureRequirement").GetString()!);

    private static RxPatientDetails ReadPatient(JsonElement element) => new(
        RxNumber: element.GetProperty("rxNumber").GetString()!,
        FirstName: element.GetProperty("firstName").GetString(),
        LastInitial: element.GetProperty("lastInitial").GetString(),
        Phone: element.GetProperty("phone").GetString(),
        Address1: element.GetProperty("address1").GetString(),
        Address2: element.GetProperty("address2").GetString(),
        City: element.GetProperty("city").GetString(),
        State: element.GetProperty("state").GetString(),
        Zip: element.GetProperty("zip").GetString());
}
