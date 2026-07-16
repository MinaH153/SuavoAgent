using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PioneerRxShadowBoundaryMatrixTests
{
    [Theory]
    [InlineData("{\"commandId\":\"cmd-1\",\"requesterId\":\"operator:1\",\"maxRows\":0,\"includeSyntheticPatientDetails\":false}", false)]
    [InlineData("{\"commandId\":\"cmd-1\",\"requesterId\":\"operator:1\",\"maxRows\":25,\"includeSyntheticPatientDetails\":true}", false)]
    [InlineData("[]", true)]
    [InlineData("null", true)]
    [InlineData("{\"commandId\":1}", true)]
    [InlineData("{\"commandId\":\"\"}", true)]
    [InlineData("{\"requesterId\":\"contains space\"}", true)]
    [InlineData("{\"maxRows\":-1}", true)]
    [InlineData("{\"maxRows\":26}", true)]
    [InlineData("{\"maxRows\":1.5}", true)]
    [InlineData("{\"maxRows\":\"3\"}", true)]
    [InlineData("{\"includeSyntheticPatientDetails\":1}", true)]
    [InlineData("{\"unknown\":true}", true)]
    public void EnvelopeSchema_IsClosedAndBounded(string json, bool rejected)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(rejected, PioneerRxShadowFixtureCommand.ContainsUnsafeField(element));
    }

    [Theory]
    [MemberData(nameof(BlockedFieldPayloads))]
    public void EnvelopeAndNestedEvidence_RejectEveryPhiOrActuationField(string json)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(element));
    }

    [Fact]
    public void DashboardEvidence_AllowsFalseWithoutReceiptAndRequiresDigestWhenClaimingProof()
    {
        var absent = JsonSerializer.Deserialize<JsonElement>("{}");
        Assert.Equal(PioneerRxShadowDashboardEvidence.Empty,
            PioneerRxShadowFixtureCommand.ReadDashboardEvidence(absent));

        var falseClaims = RootWith("dashboardEvidence", new JsonObject
        {
            ["candidateRowsVisible"] = false,
            ["correctionPathExercised"] = false,
        });
        Assert.False(PioneerRxShadowFixtureCommand.ContainsUnsafeField(falseClaims));

        foreach (var field in new[] { "candidateRowsVisible", "correctionPathExercised" })
        {
            var missingReceipt = RootWith("dashboardEvidence", new JsonObject { [field] = true });
            Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(missingReceipt));

            var wrongBoolean = RootWith("dashboardEvidence", new JsonObject { [field] = "true" });
            Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(wrongBoolean));
        }
    }

    [Theory]
    [InlineData(15, 'a')]
    [InlineData(65, 'a')]
    [InlineData(16, 'A')]
    [InlineData(16, 'g')]
    public void DashboardReceiptDigest_IsSixteenToSixtyFourLowerHex(int length, char character)
    {
        foreach (var field in new[]
                 {
                     "candidateRowsReceiptSha256", "correctionPathReceiptSha256",
                 })
        {
            var root = RootWith("dashboardEvidence", new JsonObject
            {
                [field] = new string(character, length),
            });
            Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(root));
        }
    }

    [Fact]
    public void DashboardEvidence_RejectsUnknownNonObjectAndInvalidRead()
    {
        foreach (var nested in new JsonNode?[]
                 {
                     JsonValue.Create("text"),
                     new JsonArray(),
                     new JsonObject { ["unknown"] = true },
                     new JsonObject { ["candidateRowsReceiptSha256"] = 1 },
                 })
        {
            var root = RootWith("dashboardEvidence", nested);
            Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(root));
            Assert.Throws<InvalidOperationException>(() =>
                PioneerRxShadowFixtureCommand.ReadDashboardEvidence(root));
        }
    }

    [Theory]
    [InlineData("releaseTag")]
    [InlineData("sourceCommit")]
    [InlineData("artifactSha256")]
    [InlineData("checksumSignatureSha256")]
    [InlineData("installReceiptSha256")]
    [InlineData("rollbackArtifactSha256")]
    public void ReleaseEvidence_EverySignedFieldIsMandatory(string field)
    {
        var evidence = ValidReleaseEvidence();
        evidence.Remove(field);
        var root = RootWith("releaseEvidence", evidence);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(root));
        Assert.Throws<InvalidOperationException>(() =>
            PioneerRxShadowFixtureCommand.ReadReleaseEvidence(root));
    }

    [Theory]
    [InlineData("releaseTag", "release-1")]
    [InlineData("releaseTag", "vfield")]
    [InlineData("releaseTag", "v1/unsafe")]
    [InlineData("sourceCommit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sourceCommit", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("artifactSha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("checksumSignatureSha256", "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("installReceiptSha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rollbackArtifactSha256", "")]
    public void ReleaseEvidence_RejectsUnsafeTagOrNonCanonicalDigest(string field, string value)
    {
        var evidence = ValidReleaseEvidence();
        evidence[field] = value;
        var root = RootWith("releaseEvidence", evidence);

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(root));
    }

    [Fact]
    public void ReleaseEvidence_RejectsNonStringUnknownAndNonObjectShapes()
    {
        var nonString = ValidReleaseEvidence();
        nonString["sourceCommit"] = 1;
        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(
            RootWith("releaseEvidence", nonString)));

        var unknown = ValidReleaseEvidence();
        unknown["unknown"] = "value";
        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(
            RootWith("releaseEvidence", unknown)));

        Assert.True(PioneerRxShadowFixtureCommand.ContainsUnsafeField(
            RootWith("releaseEvidence", new JsonArray())));
    }

    [Theory]
    [InlineData("v1", true)]
    [InlineData("v3.14.6-rc_1", true)]
    [InlineData("v", false)]
    [InlineData("vfield", false)]
    [InlineData("V1", false)]
    [InlineData("v1/unsafe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReleaseTag_HasClosedSafeShape(string? value, bool accepted)
    {
        Assert.Equal(accepted, InvokeBool("IsSafeReleaseTag", value));
    }

    [Fact]
    public void ReleaseTag_RejectsOverSixtyFourCharacters()
    {
        Assert.False(InvokeBool("IsSafeReleaseTag", "v" + new string('1', 64)));
    }

    [Theory]
    [InlineData("cmd:1-2_3.4", true)]
    [InlineData("contains space", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EnvelopeToken_HasClosedSafeShape(string? value, bool accepted)
    {
        Assert.Equal(accepted, InvokeBool("IsSafeEnvelopeToken", value));
    }

    [Fact]
    public void EnvelopeToken_RejectsOverOneHundredTwentyEightCharacters()
    {
        Assert.False(InvokeBool("IsSafeEnvelopeToken", new string('a', 129)));
    }

    [Theory]
    [InlineData("Command ID", "commandid")]
    [InlineData("Patient_Name", "patientname")]
    [InlineData("N-D-C", "ndc")]
    public void FieldNormalization_RemovesSeparatorsAndCase(string value, string expected)
    {
        Assert.Equal(expected, InvokeString("NormalizeFieldName", value));
    }

    [Theory]
    [InlineData("command:with unsafe/path", "commandwithunsafepath")]
    [InlineData("---", "---")]
    [InlineData("!@#$", "command")]
    public void FileToken_StripsUnsafeCharactersAndNeverReturnsBlank(string value, string expected)
    {
        Assert.Equal(expected, InvokeString("SafeFileToken", value));
    }

    [Fact]
    public void FileToken_IsCappedAtSixtyFourCharacters()
    {
        Assert.Equal(64, InvokeString("SafeFileToken", new string('a', 100)).Length);
    }

    public static IEnumerable<object[]> BlockedFieldPayloads()
    {
        foreach (var field in new[]
                 {
                     "text", "label", "windowTitle", "rx", "rxNumber", "rxId",
                     "prescription", "prescriptionId", "patient", "patientId", "patientName",
                     "patientFirstName", "patientLastName", "medication", "ndc", "address",
                     "phone", "screenshot", "image", "ocr", "click", "type", "key", "mouse",
                     "coordinates",
                 })
        {
            yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = "value" })];
            yield return [RootWith("dashboardEvidence", new JsonObject { [field] = "value" }).GetRawText()];
            yield return [RootWith("releaseEvidence", new JsonObject { [field] = "value" }).GetRawText()];
        }
    }

    private static JsonElement RootWith(string name, JsonNode? value)
    {
        var root = new JsonObject { [name] = value?.DeepClone() };
        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    private static JsonObject ValidReleaseEvidence() => new()
    {
        ["releaseTag"] = "v3.14.6",
        ["sourceCommit"] = new string('a', 40),
        ["artifactSha256"] = new string('b', 64),
        ["checksumSignatureSha256"] = new string('c', 64),
        ["installReceiptSha256"] = new string('d', 64),
        ["rollbackArtifactSha256"] = new string('e', 64),
    };

    private static bool InvokeBool(string methodName, object? argument)
    {
        var method = typeof(PioneerRxShadowFixtureCommand).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [argument]));
    }

    private static string InvokeString(string methodName, string argument)
    {
        var method = typeof(PioneerRxShadowFixtureCommand).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [argument]));
    }
}
