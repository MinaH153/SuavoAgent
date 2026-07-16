using System.Text.Json;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class AutoRuleCommandContractsTests
{
    private const string ApprovalId = "11111111-1111-4111-8111-111111111111";
    private const string ApprovedBy = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "aaaaaaaa-3333-4333-8333-333333333333";
    private const string RunId = "44444444-4444-4444-8444-444444444444";
    private const string TemplateId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Yaml = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RuleId = "auto.learned.aaaaaaaaaaaa";

    [Fact]
    public void ExactTransitionSchema_ParsesStableCommandIdAndDigest()
    {
        using var document = JsonDocument.Parse(TransitionJson());

        Assert.True(AutoRuleCommandContracts.TryParseTransition(
            document.RootElement, out var command, out var rejection));
        Assert.Equal("", rejection);
        Assert.Equal(CommandId, command!.CommandId);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Shadow, command.FromStatus);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Approved, command.ToStatus);
        Assert.Equal(64, command.PayloadDigest.Length);
        Assert.Equal(command.PayloadDigest, command.PayloadDigest);
    }

    [Theory]
    [InlineData("\"freeform\":\"unsafe\",")]
    [InlineData("\"reason\":\"pharmacist_workflow_verified\",")]
    [InlineData("\"nonce\":\"55555555-5555-4555-8555-555555555555\",")]
    public void Transition_ExtraFieldRejected(string extra)
    {
        using var document = JsonDocument.Parse("{" + extra + TransitionJson()[1..]);
        Assert.False(AutoRuleCommandContracts.TryParseTransition(
            document.RootElement, out _, out _));
    }

    [Theory]
    [InlineData("Pending", "Approved", "human_approved")]
    [InlineData("Approved", "Shadow", "shadow_started")]
    [InlineData("Approved", "Rejected", "human_approved")]
    public void Transition_IllegalGraphOrWrongReasonRejected(
        string from, string to, string reason)
    {
        var json = TransitionJson()
            .Replace("\"fromStatus\": \"Shadow\"", $"\"fromStatus\": \"{from}\"")
            .Replace("\"toStatus\": \"Approved\"", $"\"toStatus\": \"{to}\"")
            .Replace("\"reasonCode\": \"human_approved\"", $"\"reasonCode\": \"{reason}\"");
        if (to != "Approved")
        {
            json = json
                .Replace($"\"approvedBy\": \"{ApprovedBy}\"", "\"approvedBy\": null")
                .Replace("\"approvedAt\": \"2026-07-10T12:15:00.000Z\"", "\"approvedAt\": null");
        }
        using var document = JsonDocument.Parse(json);
        Assert.False(AutoRuleCommandContracts.TryParseTransition(
            document.RootElement, out _, out _));
    }

    [Fact]
    public void ExactRunSchema_ParsesDeadlineAndRejectsUnknownOrOutOfRange()
    {
        using var valid = JsonDocument.Parse(RunJson());
        Assert.True(AutoRuleCommandContracts.TryParseRun(
            valid.RootElement, out var command, out _));
        Assert.Equal(300, command!.DeadlineSeconds);
        Assert.Equal(CommandId, command.CommandId);

        using var extra = JsonDocument.Parse("{\"prompt\":\"click it\"," + RunJson()[1..]);
        Assert.False(AutoRuleCommandContracts.TryParseRun(extra.RootElement, out _, out _));

        using var shortDeadline = JsonDocument.Parse(
            RunJson().Replace("\"deadlineSeconds\": 300", "\"deadlineSeconds\": 29"));
        Assert.False(AutoRuleCommandContracts.TryParseRun(
            shortDeadline.RootElement, out _, out _));
    }

    [Fact]
    public void NonCanonicalIdsAndDuplicateFieldsAreRejected()
    {
        using var upper = JsonDocument.Parse(
            RunJson().Replace(CommandId, CommandId.ToUpperInvariant(), StringComparison.Ordinal));
        Assert.False(AutoRuleCommandContracts.TryParseRun(upper.RootElement, out _, out _));

        using var duplicate = JsonDocument.Parse("{\"schemaVersion\":1," + RunJson()[1..]);
        Assert.False(AutoRuleCommandContracts.TryParseRun(duplicate.RootElement, out _, out _));
    }

    private static string TransitionJson() => $$"""
        {
          "schemaVersion": 1,
          "approvalId": "{{ApprovalId}}",
          "ruleId": "{{RuleId}}",
          "templateId": "{{TemplateId}}",
          "yamlSha256": "{{Yaml}}",
          "fromStatus": "Shadow",
          "toStatus": "Approved",
          "approvedBy": "{{ApprovedBy}}",
          "approvedAt": "2026-07-10T12:15:00.000Z",
          "reasonCode": "human_approved",
          "commandId": "{{CommandId}}"
        }
        """;

    private static string RunJson() => $$"""
        {
          "schemaVersion": 1,
          "approvalId": "{{ApprovalId}}",
          "ruleId": "{{RuleId}}",
          "templateId": "{{TemplateId}}",
          "yamlSha256": "{{Yaml}}",
          "runId": "{{RunId}}",
          "deadlineSeconds": 300,
          "commandId": "{{CommandId}}"
        }
        """;
}
