using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("already_at_target")]
    [InlineData("post_verify_mismatch")]
    [InlineData("status_conflict")]
    [InlineData("retry_exhausted")]
    [InlineData("manual_review")]
    public void DeliveryReceipt_AcceptsEveryClosedResultCodeWithBoundStatus(
        string wireResult)
    {
        var expected = wireResult switch
        {
            "success" => DeliveryWritebackResultCode.Success,
            "already_at_target" => DeliveryWritebackResultCode.AlreadyAtTarget,
            "post_verify_mismatch" => DeliveryWritebackResultCode.PostVerifyMismatch,
            "status_conflict" => DeliveryWritebackResultCode.StatusConflict,
            "retry_exhausted" => DeliveryWritebackResultCode.RetryExhausted,
            "manual_review" => DeliveryWritebackResultCode.ManualReview,
            _ => throw new InvalidOperationException(),
        };
        var command = DeliveryCommand();
        var response = JsonSerializer.Deserialize<JsonElement>(
            DeliveryReceiptJson(command, expected, idempotent: true));

        Assert.True(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            response, command, expected, out var receipt));
        Assert.Equal(expected, receipt!.ResultCode);
        Assert.Equal(
            expected is DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget
                ? "succeeded"
                : "needs_attention",
            receipt.Status);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("writebackId")]
    [InlineData("commandId")]
    [InlineData("pharmacyId")]
    [InlineData("orderId")]
    [InlineData("candidateId")]
    [InlineData("pmsReferenceId")]
    [InlineData("proofRecordId")]
    [InlineData("proofDigest")]
    [InlineData("transition")]
    [InlineData("status")]
    [InlineData("resultCode")]
    [InlineData("completedAt")]
    [InlineData("idempotent")]
    public void DeliveryReceipt_EveryFieldIsMandatory(string field)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]!.AsObject().Remove(field);

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("writebackId")]
    [InlineData("commandId")]
    [InlineData("pharmacyId")]
    [InlineData("orderId")]
    [InlineData("candidateId")]
    [InlineData("pmsReferenceId")]
    [InlineData("proofRecordId")]
    [InlineData("proofDigest")]
    [InlineData("transition")]
    [InlineData("status")]
    [InlineData("resultCode")]
    [InlineData("completedAt")]
    public void DeliveryReceipt_StringFieldsRejectNonStringValues(string field)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]![field] = 1;

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("writebackId")]
    [InlineData("commandId")]
    [InlineData("pharmacyId")]
    [InlineData("orderId")]
    [InlineData("candidateId")]
    public void DeliveryReceipt_StableIdsRejectUppercaseAndMalformedUuid(string field)
    {
        var command = DeliveryCommand();
        foreach (var value in new[]
                 {
                     "NOT-A-UUID",
                     "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
                     "00000000-0000-4000-8000-00000000001",
                 })
        {
            var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
            root["data"]![field] = value;
            AssertRejected(root, command, DeliveryWritebackResultCode.Success);
        }
    }

    [Fact]
    public void DeliveryReceipt_RootAndNestedSchemasAreExact()
    {
        var command = DeliveryCommand();
        foreach (var invalid in new[] { "null", "[]", "true", "\"text\"" })
        {
            var element = JsonSerializer.Deserialize<JsonElement>(invalid);
            Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
                element, command, DeliveryWritebackResultCode.Success, out _));
        }

        var rootExtra = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        rootExtra["extra"] = true;
        AssertRejected(rootExtra, command, DeliveryWritebackResultCode.Success);

        var nestedExtra = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        nestedExtra["data"]!["extra"] = true;
        AssertRejected(nestedExtra, command, DeliveryWritebackResultCode.Success);

        var falseSuccess = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        falseSuccess["success"] = false;
        AssertRejected(falseSuccess, command, DeliveryWritebackResultCode.Success);

        var arrayData = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        arrayData["data"] = new JsonArray();
        AssertRejected(arrayData, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("\"2\"")]
    [InlineData("2147483648")]
    public void DeliveryReceipt_SchemaVersionMustBeInt32Two(string rawValue)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]!["schemaVersion"] = JsonNode.Parse(rawValue);

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("SUCCESS")]
    [InlineData("")]
    public void DeliveryReceipt_ResultCodeIsClosedLowercaseEnum(string resultCode)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]!["resultCode"] = resultCode;

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("2026-07-10 12:16:00Z")]
    [InlineData("2026-07-10T12:16:00")]
    [InlineData("not-a-time")]
    [InlineData("2026-99-99T12:16:00.000Z")]
    public void DeliveryReceipt_CompletionTimeRequiresParseableOffsetTimestamp(string completedAt)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]!["completedAt"] = completedAt;

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Theory]
    [InlineData("writebackId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("commandId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("pharmacyId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("orderId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("candidateId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("pmsReferenceId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("proofRecordId", "00000000-0000-4000-8000-000000000099")]
    [InlineData("proofDigest", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("transition", "pickup")]
    [InlineData("status", "needs_attention")]
    public void DeliveryReceipt_EveryIdentityAndProofMustMatchCommand(
        string field,
        string mismatchedValue)
    {
        var command = DeliveryCommand();
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);
        root["data"]![field] = mismatchedValue;

        AssertRejected(root, command, DeliveryWritebackResultCode.Success);
    }

    [Fact]
    public void PickupReceipt_AllowsExplicitNullProofBindingsOnlyWhenCommandDoes()
    {
        var command = DeliveryCommand() with
        {
            Transition = "pickup",
            ProofRecordId = null,
            ProofDigest = null,
        };
        var root = ReceiptNode(command, DeliveryWritebackResultCode.Success);

        var element = JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
        Assert.True(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            element, command, DeliveryWritebackResultCode.Success, out var receipt));
        Assert.Null(receipt!.ProofRecordId);
        Assert.Null(receipt.ProofDigest);
    }

    [Fact]
    public void DeliveryReceipt_RejectsDuplicateRootAndDataProperties()
    {
        var command = DeliveryCommand();
        var valid = DeliveryReceiptJson(command, DeliveryWritebackResultCode.Success, false);
        var duplicateRoot = JsonSerializer.Deserialize<JsonElement>(
            "{\"success\":true," + valid[1..]);
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            duplicateRoot, command, DeliveryWritebackResultCode.Success, out _));

        var duplicateData = valid.Replace(
            "\"schemaVersion\":2",
            "\"schemaVersion\":2,\"schemaVersion\":2",
            StringComparison.Ordinal);
        var duplicateDataElement = JsonSerializer.Deserialize<JsonElement>(duplicateData);
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            duplicateDataElement, command, DeliveryWritebackResultCode.Success, out _));
    }

    private static JsonObject ReceiptNode(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode result) =>
        JsonNode.Parse(DeliveryReceiptJson(command, result, idempotent: false))!.AsObject();

    private static void AssertRejected(
        JsonObject root,
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode expected)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
        Assert.False(SuavoCloudClient.TryParseDeliveryWritebackReceipt(
            element, command, expected, out _));
    }
}
