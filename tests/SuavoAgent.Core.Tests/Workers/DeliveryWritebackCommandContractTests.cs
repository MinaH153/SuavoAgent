using System.Text.Json;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class DeliveryWritebackCommandContractTests
{
    private const string CommandId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string RxHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ExactFourteenFieldData_BindsCompletionProof()
    {
        using var document = JsonDocument.Parse(ValidJson());

        var accepted = DeliveryWritebackCommandContract.TryParse(
            document.RootElement,
            out var command,
            out var rejection);

        Assert.True(accepted);
        Assert.Equal("", rejection);
        Assert.NotNull(command);
        Assert.Equal(CommandId, command!.CommandId);
        Assert.Equal(RxHash, command.RxHash);
        Assert.Equal("complete", command.Transition);
        Assert.Equal("00000000-0000-4000-8000-000000000007", command.PmsReferenceId);
        Assert.Equal("00000000-0000-4000-8000-000000000008", command.ProofRecordId);
        Assert.Equal(new string('b', 64), command.ProofDigest);
    }

    [Theory]
    [InlineData("\"rxNumber\":\"RX-123\",")]
    [InlineData("\"receipt\":{},")]
    [InlineData("\"freeform\":\"anything\",")]
    public void ExtraLegacyOrPhiField_IsRejected(string extraProperty)
    {
        using var document = JsonDocument.Parse("{" + extraProperty + ValidJson()[1..]);

        Assert.False(DeliveryWritebackCommandContract.TryParse(
            document.RootElement,
            out _,
            out var rejection));
        Assert.Equal("delivery_writeback_schema_invalid", rejection);
    }

    [Theory]
    [InlineData("rxHash", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("evidenceId", "rxh-bbbbbbbbbbbbbbbb-1770000000")]
    [InlineData("transition", "delivered")]
    [InlineData("transitionAt", "2026-07-10 12:15:00")]
    [InlineData("writebackId", "NOT-A-UUID")]
    public void InvalidIdentityOrShape_IsRejected(string property, string replacement)
    {
        using var source = JsonDocument.Parse(ValidJson());
        var values = source.RootElement.EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
        var json = "{" + string.Join(",", values.Select(pair =>
            pair.Key == property
                ? $"\"{pair.Key}\":{JsonSerializer.Serialize(replacement)}"
                : $"\"{pair.Key}\":{pair.Value.GetRawText()}")) + "}";
        using var document = JsonDocument.Parse(json);

        Assert.False(DeliveryWritebackCommandContract.TryParse(
            document.RootElement,
            out _,
            out _));
    }

    [Fact]
    public void DuplicatePropertyAndNonCanonicalStableCommandId_AreRejected()
    {
        using var duplicate = JsonDocument.Parse(
            "{\"schemaVersion\":2," + ValidJson()[1..]);
        Assert.False(DeliveryWritebackCommandContract.TryParse(
            duplicate.RootElement,
            out _,
            out _));

        using var nonCanonical = JsonDocument.Parse(
            ValidJson().Replace(CommandId, CommandId.ToUpperInvariant(), StringComparison.Ordinal));
        Assert.False(DeliveryWritebackCommandContract.TryParse(
            nonCanonical.RootElement,
            out _,
            out _));
    }

    private static string ValidJson() => $$"""
        {
          "schemaVersion": 2,
          "writebackId": "00000000-0000-4000-8000-000000000002",
          "candidateId": "00000000-0000-4000-8000-000000000003",
          "rxHash": "{{RxHash}}",
          "evidenceId": "rxh-aaaaaaaaaaaaaaaa-1770000000",
          "pharmacyId": "00000000-0000-4000-8000-000000000004",
          "orderId": "00000000-0000-4000-8000-000000000005",
          "inboxItemId": "00000000-0000-4000-8000-000000000006",
          "pmsReferenceId": "00000000-0000-4000-8000-000000000007",
          "proofRecordId": "00000000-0000-4000-8000-000000000008",
          "proofDigest": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "transition": "complete",
          "transitionAt": "2026-07-10T12:15:00.000Z",
          "commandId": "{{CommandId}}"
        }
        """;
}
