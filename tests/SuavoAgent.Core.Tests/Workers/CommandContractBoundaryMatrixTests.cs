using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class FetchPatientCommandContractBoundaryTests
{
    private const string CandidateId = "00000000-0000-4000-8000-000000000002";
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string CommandId = "00000000-0000-4000-8000-000000000003";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void BuiltInAndLearnedApprovedSources_RequireExactBindingSemantics()
    {
        AssertAccepted(Valid());

        var learned = Valid();
        learned["sourceKind"] = "learned_approved";
        learned["sourceBinding"] = new string('b', 64);
        AssertAccepted(learned);

        var builtInBound = Valid();
        builtInBound["sourceBinding"] = new string('b', 64);
        AssertRejected(builtInBound, "fetch_source_invalid");

        var learnedUnbound = learned.DeepClone().AsObject();
        learnedUnbound["sourceBinding"] = null;
        AssertRejected(learnedUnbound, "fetch_source_invalid");
    }

    [Theory]
    [InlineData("candidateId")]
    [InlineData("rxHash")]
    [InlineData("evidenceId")]
    [InlineData("pharmacyId")]
    [InlineData("commandId")]
    [InlineData("sourceKind")]
    [InlineData("sourceBinding")]
    public void EveryRequiredField_IsMandatory(string field)
    {
        var candidate = Valid();
        candidate.Remove(field);

        AssertRejected(candidate, "fetch_data_shape_mismatch");
    }

    [Theory]
    [InlineData("candidateId")]
    [InlineData("rxHash")]
    [InlineData("evidenceId")]
    [InlineData("pharmacyId")]
    [InlineData("commandId")]
    [InlineData("sourceKind")]
    public void RequiredStringFields_RejectNonStringValues(string field)
    {
        var candidate = Valid();
        candidate[field] = 1;

        AssertRejected(candidate, "fetch_data_shape_mismatch");
    }

    [Theory]
    [InlineData("candidateId")]
    [InlineData("pharmacyId")]
    [InlineData("commandId")]
    public void StableIds_RejectUppercaseShortAndNonUuidValues(string field)
    {
        const string alphabeticUuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        foreach (var value in new[]
                 {
                     "NOT-A-UUID",
                     alphabeticUuid.ToUpperInvariant(),
                     CandidateId[..35],
                 })
        {
            var candidate = Valid();
            candidate[field] = value;
            AssertRejected(candidate, "fetch_identifier_invalid");
        }
    }

    [Theory]
    [InlineData(63, 'a')]
    [InlineData(65, 'a')]
    [InlineData(64, 'A')]
    [InlineData(64, 'g')]
    public void RxHash_IsExactlyLowerHex(int length, char character)
    {
        var candidate = Valid();
        candidate["rxHash"] = new string(character, length);

        AssertRejected(candidate, "fetch_hash_invalid");
    }

    [Theory]
    [InlineData("rxh-aaaaaaaaaaaaaaaa-123456789")]
    [InlineData("rxh-aaaaaaaaaaaaaaaa-12345678901234")]
    [InlineData("rxh-aaaaaaaaaaaaaaaa-123456789x")]
    [InlineData("rxh-bbbbbbbbbbbbbbbb-1234567890")]
    [InlineData("wrong-aaaaaaaaaaaaaaaa-1234567890")]
    public void EvidenceId_IsHashBoundAndHasTenToThirteenDigitEpoch(string evidenceId)
    {
        var candidate = Valid();
        candidate["evidenceId"] = evidenceId;

        AssertRejected(candidate, "fetch_evidence_invalid");
    }

    [Fact]
    public void DuplicateAndUnknownFields_AreRejected()
    {
        var duplicate = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"candidateId":"{{CandidateId}}","candidateId":"{{CandidateId}}","rxHash":"{{Hash}}","evidenceId":"rxh-aaaaaaaaaaaaaaaa-1770000000","pharmacyId":"{{PharmacyId}}","commandId":"{{CommandId}}","sourceKind":"pioneerrx_builtin","sourceBinding":null}""");
        Assert.False(FetchPatientCommandContract.TryParse(duplicate, out _, out var duplicateCode));
        Assert.Equal("fetch_data_shape_mismatch", duplicateCode);

        var unknown = Valid();
        unknown["rxNumber"] = "123456";
        AssertRejected(unknown, "fetch_data_shape_mismatch");
    }

    private static JsonObject Valid() => new()
    {
        ["candidateId"] = CandidateId,
        ["rxHash"] = Hash,
        ["evidenceId"] = "rxh-aaaaaaaaaaaaaaaa-1770000000",
        ["pharmacyId"] = PharmacyId,
        ["commandId"] = CommandId,
        ["sourceKind"] = "pioneerrx_builtin",
        ["sourceBinding"] = null,
    };

    private static void AssertAccepted(JsonObject value)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        Assert.True(FetchPatientCommandContract.TryParse(element, out var command, out var code), code);
        Assert.NotNull(command);
    }

    private static void AssertRejected(JsonObject value, string expectedCode)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        Assert.False(FetchPatientCommandContract.TryParse(element, out _, out var code));
        Assert.Equal(expectedCode, code);
    }
}

public sealed class AutoRuleCommandBoundaryMatrixTests
{
    private const string ApprovalId = "11111111-1111-4111-8111-111111111111";
    private const string ApprovedBy = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "aaaaaaaa-3333-4333-8333-333333333333";
    private const string RunId = "44444444-4444-4444-8444-444444444444";
    private const string TemplateId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Yaml = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RuleId = "auto.learned.aaaaaaaaaaaa";

    [Theory]
    [InlineData(AgentStateDb.AutoRuleStatus.Pending, AgentStateDb.AutoRuleStatus.Shadow, "shadow_started", false)]
    [InlineData(AgentStateDb.AutoRuleStatus.Pending, AgentStateDb.AutoRuleStatus.Rejected, "operator_rejected", false)]
    [InlineData(AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Approved, "human_approved", true)]
    [InlineData(AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Rejected, "operator_rejected", false)]
    [InlineData(AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Pending, "operator_reset", false)]
    [InlineData(AgentStateDb.AutoRuleStatus.Approved, AgentStateDb.AutoRuleStatus.Rejected, "operator_rejected", false)]
    [InlineData(AgentStateDb.AutoRuleStatus.Rejected, AgentStateDb.AutoRuleStatus.Pending, "operator_reset", false)]
    public void EveryLegalTransition_HasExactReasonAndApprovalMetadata(
        AgentStateDb.AutoRuleStatus from,
        AgentStateDb.AutoRuleStatus to,
        string reason,
        bool approved)
    {
        var data = Transition(from, to, reason, approved);

        Assert.True(AutoRuleCommandContracts.TryParseTransition(
            ToElement(data), out var command, out var code), code);
        Assert.NotNull(command);
        Assert.Equal(from, command!.FromStatus);
        Assert.Equal(to, command.ToStatus);
    }

    [Fact]
    public void EveryIllegalTransition_IsRejected()
    {
        foreach (var from in Enum.GetValues<AgentStateDb.AutoRuleStatus>())
        foreach (var to in Enum.GetValues<AgentStateDb.AutoRuleStatus>())
        {
            if (AutoRuleCommandContracts.IsLegalTransition(from, to))
                continue;

            Assert.False(AutoRuleCommandContracts.TryParseTransition(
                ToElement(Transition(from, to, ReasonFor(to), to == AgentStateDb.AutoRuleStatus.Approved)),
                out _,
                out _));
        }
    }

    [Theory]
    [InlineData("schemaVersion", "string")]
    [InlineData("approvalId", "uppercase")]
    [InlineData("ruleId", "control")]
    [InlineData("templateId", "uppercase-hash")]
    [InlineData("yamlSha256", "short-hash")]
    [InlineData("runId", "uppercase")]
    [InlineData("deadlineSeconds", "string")]
    [InlineData("commandId", "uppercase")]
    public void RunContract_RejectsEveryMalformedBoundary(string field, string mutation)
    {
        var run = Run();
        run[field] = mutation switch
        {
            "string" => "1",
            "uppercase" => CommandId.ToUpperInvariant(),
            "control" => "auto.bad.aaaaaaaaaaaa\n",
            "uppercase-hash" => TemplateId.ToUpperInvariant(),
            "short-hash" => Yaml[..63],
            _ => throw new InvalidOperationException(),
        };

        Assert.False(AutoRuleCommandContracts.TryParseRun(ToElement(run), out _, out _));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(900)]
    public void RunDeadline_AcceptsBothInclusiveBounds(int deadline)
    {
        var run = Run();
        run["deadlineSeconds"] = deadline;

        Assert.True(AutoRuleCommandContracts.TryParseRun(
            ToElement(run), out var command, out var code), code);
        Assert.Equal(deadline, command!.DeadlineSeconds);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(901)]
    [InlineData(2147483648L)]
    public void RunDeadline_RejectsOutOfRangeOrNonInt32(long deadline)
    {
        var run = Run();
        run["deadlineSeconds"] = deadline;

        Assert.False(AutoRuleCommandContracts.TryParseRun(ToElement(run), out _, out _));
    }

    [Fact]
    public void Digest_IsLengthDelimitedAndNullDistinct()
    {
        Assert.NotEqual(
            AutoRuleCommandContracts.Digest("ab", "c"),
            AutoRuleCommandContracts.Digest("a", "bc"));
        Assert.NotEqual(
            AutoRuleCommandContracts.Digest(null, ""),
            AutoRuleCommandContracts.Digest("", null));
    }

    private static JsonObject Transition(
        AgentStateDb.AutoRuleStatus from,
        AgentStateDb.AutoRuleStatus to,
        string reason,
        bool approved) => new()
    {
        ["schemaVersion"] = 1,
        ["approvalId"] = ApprovalId,
        ["ruleId"] = RuleId,
        ["templateId"] = TemplateId,
        ["yamlSha256"] = Yaml,
        ["fromStatus"] = from.ToString(),
        ["toStatus"] = to.ToString(),
        ["approvedBy"] = approved ? ApprovedBy : null,
        ["approvedAt"] = approved ? "2026-07-10T12:15:00.000Z" : null,
        ["reasonCode"] = reason,
        ["commandId"] = CommandId,
    };

    private static JsonObject Run() => new()
    {
        ["schemaVersion"] = 1,
        ["approvalId"] = ApprovalId,
        ["ruleId"] = RuleId,
        ["templateId"] = TemplateId,
        ["yamlSha256"] = Yaml,
        ["runId"] = RunId,
        ["deadlineSeconds"] = 300,
        ["commandId"] = CommandId,
    };

    private static string ReasonFor(AgentStateDb.AutoRuleStatus status) => status switch
    {
        AgentStateDb.AutoRuleStatus.Approved => "human_approved",
        AgentStateDb.AutoRuleStatus.Rejected => "operator_rejected",
        AgentStateDb.AutoRuleStatus.Shadow => "shadow_started",
        _ => "operator_reset",
    };

    private static JsonElement ToElement(JsonObject value) =>
        JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
}

public sealed class DeliveryWritebackExecutionMappingTests
{
    [Theory]
    [InlineData("success", "success", false, "none")]
    [InlineData("already_at_target", "already_at_target", false, "none")]
    [InlineData("post_verify_mismatch", "post_verify_mismatch", false, "none")]
    [InlineData("verified_with_drift", "post_verify_mismatch", false, "none")]
    [InlineData("status_conflict", "status_conflict", false, "none")]
    [InlineData("trigger_blocked", "manual_review", false, "none")]
    [InlineData("connection_reset", null, true, "writeback_sql_unavailable")]
    [InlineData("sql_error", null, true, "writeback_sql_unavailable")]
    [InlineData("unknown", null, true, "writeback_result_unknown")]
    public void PioneerRxOutcome_IsMappedToClosedDeliveryResult(
        string outcome,
        string? expectedResult,
        bool transient,
        string errorCode)
    {
        var method = typeof(PioneerRxDeliveryWritebackExecutor).GetMethod(
            "Map",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = new WritebackResult(false, outcome, null, null);

        var mapped = Assert.IsType<DeliveryWritebackExecutionOutcome>(method!.Invoke(null, [result]));

        Assert.Equal(expectedResult, mapped.ResultCode?.ToWireValue());
        Assert.Equal(transient, mapped.Transient);
        Assert.Equal(errorCode, mapped.ErrorCode);
    }
}
