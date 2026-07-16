using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class PomApprovalCommandLedgerTests : IDisposable
{
    private const string PharmacyId = "11111111-1111-4111-8111-111111111111";
    private const string SessionId = "learn-22222222-2222-4222-8222-222222222222-20260710120000";
    private const string PomId = "33333333-3333-4333-8333-333333333333";
    private const string CommandId = "44444444-4444-4444-8444-444444444444";
    private const string ApprovedBy = "55555555-5555-4555-8555-555555555555";
    private static readonly string TemplateDigest = new('a', 64);

    private readonly AgentStateDb _db = new(":memory:");
    private readonly string _pomJson;
    private readonly string _modelDigest;

    public PomApprovalCommandLedgerTests()
    {
        _db.CreateLearningSession(SessionId, PharmacyId);
        _db.UpdateLearningPhase(SessionId, "pattern");
        _db.UpdateLearningPhase(SessionId, "model");
        _pomJson = JsonSerializer.Serialize(new
        {
            sessionId = SessionId,
            pharmacyId = PharmacyId,
            phase = "model",
            learnedAdapterTemplate = new
            {
                sessionId = SessionId,
                templateDigest = TemplateDigest,
                sourceIdentityDigest = new string('b', 64),
                schemaContractDigest = new string('c', 64),
            },
        });
        _db.StorePomSnapshot(SessionId, _pomJson);
        _modelDigest = PomExporter.ComputeDigest(PharmacyId, SessionId, _pomJson);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Apply_CommitsApprovalAndPayloadLedgerAtomically()
    {
        var command = BuildCommand();

        var applied = _db.ApplyPomApproval(command, PharmacyId);

        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Apply, applied.Kind);
        Assert.Equal("applying", applied.OutcomeCode);
        var session = _db.GetLearningSession(SessionId)!.Value;
        Assert.Equal("approved", session.Phase);
        Assert.Equal("supervised", session.Mode);
        Assert.Equal(_modelDigest, session.ApprovedModelDigest);
        var approval = _db.GetLearningApproval(SessionId)!;
        Assert.Equal(ApprovedBy, approval.ApprovedBy);
        Assert.NotNull(approval.ApprovedAt);

        var ledger = _db.GetPomApprovalLedger(CommandId)!;
        Assert.Equal(command.PayloadDigest, ledger.PayloadDigest);
        Assert.Equal(PomId, ledger.PomId);
        Assert.Equal(SessionId, ledger.SessionId);
        Assert.Equal(_modelDigest, ledger.ModelDigest);
        Assert.Equal(TemplateDigest, ledger.TemplateDigest);
        Assert.Equal("applying", ledger.ResultCode);
        Assert.Null(ledger.CompletedAt);
    }

    [Fact]
    public void CompletedSuccess_ExactRetryReturnsOriginalSuccess()
    {
        var command = BuildCommand();
        Assert.Equal(
            AgentStateDb.PomApprovalLedgerKind.Apply,
            _db.ApplyPomApproval(command, PharmacyId).Kind);
        var completed = _db.CompletePomApproval(
            command,
            succeeded: true,
            "pom_approval_activated");

        var retry = _db.ApplyPomApproval(command, PharmacyId);

        Assert.True(completed.Succeeded);
        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Terminal, retry.Kind);
        Assert.True(retry.Succeeded);
        Assert.Equal("pom_approval_activated", retry.OutcomeCode);
        Assert.NotNull(_db.GetPomApprovalLedger(CommandId)!.CompletedAt);
    }

    [Fact]
    public void SameCommandIdDifferentPayload_IsAConflictAndCannotOverwriteSuccess()
    {
        var command = BuildCommand();
        _db.ApplyPomApproval(command, PharmacyId);
        _db.CompletePomApproval(command, true, "pom_approval_activated");
        var changed = BuildCommand(templateDigest: new string('d', 64));

        var conflict = _db.ApplyPomApproval(changed, PharmacyId);

        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Conflict, conflict.Kind);
        Assert.False(conflict.Succeeded);
        Assert.Equal("command_payload_conflict", conflict.OutcomeCode);
        Assert.Equal("pom_approval_activated", _db.GetPomApprovalLedger(CommandId)!.ResultCode);
        Assert.Equal(TemplateDigest, _db.GetPomApprovalLedger(CommandId)!.TemplateDigest);
    }

    [Fact]
    public void InvalidModelDigest_FailureIsDurableAndCorrectedPayloadConflicts()
    {
        var bad = BuildCommand(modelDigest: new string('0', 64));

        var rejected = _db.ApplyPomApproval(bad, PharmacyId);
        var replay = _db.ApplyPomApproval(bad, PharmacyId);
        var corrected = _db.ApplyPomApproval(BuildCommand(), PharmacyId);

        Assert.Equal("pom_approval_model_digest_mismatch", rejected.OutcomeCode);
        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Terminal, replay.Kind);
        Assert.Equal("pom_approval_model_digest_mismatch", replay.OutcomeCode);
        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Conflict, corrected.Kind);
        Assert.Equal("model", _db.GetLearningSession(SessionId)!.Value.Phase);
        Assert.Null(_db.GetLearningSession(SessionId)!.Value.ApprovedModelDigest);
    }

    [Fact]
    public void InvalidTemplateDigest_FailsBeforeApprovalMutation()
    {
        var rejected = _db.ApplyPomApproval(
            BuildCommand(templateDigest: new string('d', 64)),
            PharmacyId);

        Assert.Equal("pom_approval_template_digest_mismatch", rejected.OutcomeCode);
        Assert.Equal("model", _db.GetLearningSession(SessionId)!.Value.Phase);
        Assert.Equal(
            "pom_approval_template_digest_mismatch",
            _db.GetPomApprovalLedger(CommandId)!.ResultCode);
    }

    [Fact]
    public void FailedActivation_AllowsNewCommandIdToReapplyExactFrozenBinding()
    {
        var first = BuildCommand();
        _db.ApplyPomApproval(first, PharmacyId);
        _db.CompletePomApproval(first, false, "pom_approval_activation_failed");
        var retryCommand = BuildCommand(commandId: "66666666-6666-4666-8666-666666666666");

        var reapplied = _db.ApplyPomApproval(retryCommand, PharmacyId);
        var completed = _db.CompletePomApproval(
            retryCommand,
            true,
            "pom_approval_already_active");

        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Apply, reapplied.Kind);
        Assert.True(completed.Succeeded);
        Assert.Equal("pom_approval_already_active", completed.OutcomeCode);
        Assert.Equal("pom_approval_activation_failed", _db.GetPomApprovalLedger(CommandId)!.ResultCode);
        Assert.Equal("pom_approval_already_active", _db.GetPomApprovalLedger(retryCommand.CommandId)!.ResultCode);
    }

    [Fact]
    public void MalformedCommand_HasDurableExactReplayAndConflictBehavior()
    {
        var firstDigest = new string('1', 64);
        var recorded = _db.RecordMalformedPomApproval(
            CommandId,
            firstDigest,
            "pom_approval_schema_invalid");
        var replay = _db.RecordMalformedPomApproval(
            CommandId,
            firstDigest,
            "pom_approval_schema_invalid");
        var conflict = _db.RecordMalformedPomApproval(
            CommandId,
            new string('2', 64),
            "pom_approval_schema_invalid");

        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Terminal, recorded.Kind);
        Assert.Equal("pom_approval_schema_invalid", replay.OutcomeCode);
        Assert.Equal(AgentStateDb.PomApprovalLedgerKind.Conflict, conflict.Kind);
        Assert.Equal("command_payload_conflict", conflict.OutcomeCode);
    }

    private PomApprovalCommand BuildCommand(
        string commandId = CommandId,
        string? modelDigest = null,
        string? templateDigest = null)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            pomId = PomId,
            sessionId = SessionId,
            approvedModelDigest = modelDigest ?? _modelDigest,
            approvedTemplateDigest = templateDigest ?? TemplateDigest,
            approvedBy = ApprovedBy,
            commandId,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        });
        Assert.True(PomApprovalCommandContract.TryParse(data, out var command, out _));
        return command!;
    }
}
