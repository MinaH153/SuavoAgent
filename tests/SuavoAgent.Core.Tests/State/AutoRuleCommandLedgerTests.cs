using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class AutoRuleCommandLedgerTests : IDisposable
{
    private const string ApprovalId = "11111111-1111-4111-8111-111111111111";
    private const string ApprovedBy = "22222222-2222-4222-8222-222222222222";
    private const string TransitionId = "33333333-3333-4333-8333-333333333333";
    private const string RunCommandId = "44444444-4444-4444-8444-444444444444";
    private const string RunId = "55555555-5555-4555-8555-555555555555";
    private const string TemplateId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Yaml = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RuleId = "auto.learned.aaaaaaaaaaaa";

    private readonly AgentStateDb _db = new(":memory:");

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Transition_CommitsApprovalRegistryAndLedgerAtomically()
    {
        SeedShadowApproval();
        var command = ApprovedTransition();

        var applied = _db.ApplyAutoRuleTransition(command, exactRuleValidated: true);

        Assert.True(applied.Succeeded);
        Assert.False(applied.Replay);
        Assert.Equal("applied", applied.ResultCode);
        var row = _db.GetAutoRuleApproval(RuleId)!;
        Assert.Equal(AgentStateDb.AutoRuleStatus.Approved, row.Status);
        Assert.Equal(ApprovalId, row.ApprovalId);
        Assert.Equal(ApprovedBy, row.ApprovedBy);
        var binding = Assert.Single(_db.GetActiveAutoRuleBindings());
        Assert.Equal(ApprovalId, binding.ApprovalId);
        Assert.Equal(TransitionId, binding.ActivatedByCommandId);

        var replay = _db.ApplyAutoRuleTransition(command, exactRuleValidated: true);
        Assert.True(replay.Succeeded);
        Assert.True(replay.Replay);
        Assert.Equal("applied", replay.ResultCode);
        Assert.Single(_db.GetActiveAutoRuleBindings());
    }

    [Fact]
    public void Transition_SameIdDifferentPayloadRejectsWithoutMutation()
    {
        SeedShadowApproval();
        var command = ApprovedTransition();
        Assert.True(_db.ApplyAutoRuleTransition(command, true).Succeeded);
        var changed = command with { ApprovedAt = "2026-07-10T12:16:00.000Z" };

        var conflict = _db.ApplyAutoRuleTransition(changed, true);

        Assert.False(conflict.Succeeded);
        Assert.True(conflict.Replay);
        Assert.Equal("command_payload_conflict", conflict.ResultCode);
        Assert.Equal("2026-07-10T12:15:00.000Z", _db.GetAutoRuleApproval(RuleId)!.ApprovedAt);
    }

    [Fact]
    public void Transition_NewCommandReappliesWhenLocalStateAlreadyAtTarget()
    {
        SeedShadowApproval();
        Assert.True(_db.ApplyAutoRuleTransition(ApprovedTransition(), true).Succeeded);
        var reapply = ApprovedTransition() with
        {
            CommandId = "66666666-6666-4666-8666-666666666666",
        };

        var result = _db.ApplyAutoRuleTransition(reapply, true);

        Assert.True(result.Succeeded);
        Assert.True(result.AlreadyAtTarget);
        Assert.Equal("already_at_target", result.ResultCode);
        Assert.Equal(reapply.CommandId,
            Assert.Single(_db.GetActiveAutoRuleBindings()).ActivatedByCommandId);
    }

    [Fact]
    public void Transition_ExactBindingAndValidatedYamlAreRequiredAndFailureIsDurable()
    {
        SeedShadowApproval();
        var command = ApprovedTransition();

        var rejected = _db.ApplyAutoRuleTransition(command, exactRuleValidated: false);
        var replay = _db.ApplyAutoRuleTransition(command, exactRuleValidated: true);

        Assert.False(rejected.Succeeded);
        Assert.Equal("rule_validation_failed", rejected.ResultCode);
        Assert.True(replay.Replay);
        Assert.False(replay.Succeeded);
        Assert.Equal("rule_validation_failed", replay.ResultCode);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Shadow, _db.GetAutoRuleApproval(RuleId)!.Status);
        Assert.Empty(_db.GetActiveAutoRuleBindings());
    }

    [Fact]
    public void RejectionAndYamlChangeRemoveDurableAdmission()
    {
        SeedShadowApproval();
        Assert.True(_db.ApplyAutoRuleTransition(ApprovedTransition(), true).Succeeded);
        var reject = new AutoRuleTransitionCommand(
            1, ApprovalId, RuleId, TemplateId, Yaml,
            AgentStateDb.AutoRuleStatus.Approved,
            AgentStateDb.AutoRuleStatus.Rejected,
            null, null, "operator_rejected",
            "77777777-7777-4777-8777-777777777777");
        Assert.True(_db.ApplyAutoRuleTransition(reject, false).Succeeded);
        Assert.Empty(_db.GetActiveAutoRuleBindings());

        _db.UpsertAutoRuleApproval(RuleId, TemplateId, Yaml);
        _db.SetAutoRuleApprovalStatus(RuleId, AgentStateDb.AutoRuleStatus.Shadow);
        var approveAgain = ApprovedTransition() with
        {
            FromStatus = AgentStateDb.AutoRuleStatus.Shadow,
            CommandId = "88888888-8888-4888-8888-888888888888",
        };
        Assert.True(_db.ApplyAutoRuleTransition(approveAgain, true).Succeeded);
        _db.UpsertAutoRuleApproval(RuleId, TemplateId, new string('c', 64));

        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, _db.GetAutoRuleApproval(RuleId)!.Status);
        Assert.Empty(_db.GetActiveAutoRuleBindings());
    }

    [Fact]
    public void RunLedger_RedeliveryNeverStartsActuationTwice()
    {
        Approve();
        var run = RunCommand();

        var first = _db.BeginAutoRuleRun(run, "process-a", runtimeRegistryExact: true);
        var concurrent = _db.BeginAutoRuleRun(run, "process-a", runtimeRegistryExact: true);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Start, first.Kind);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.InProgress, concurrent.Kind);

        Assert.True(_db.CompleteAutoRuleRun(
            run.CommandId, "process-a", true, "Completed", 3, null));
        var terminal = _db.BeginAutoRuleRun(run, "process-a", runtimeRegistryExact: true);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Terminal, terminal.Kind);
        Assert.True(terminal.Succeeded);
        Assert.Equal("Completed", terminal.OutcomeCode);
        Assert.Equal(3, terminal.StepsCompleted);
    }

    [Fact]
    public void RunLedger_RestartAfterRunningMarksInterruptedWithoutReplay()
    {
        Approve();
        var run = RunCommand();
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Start,
            _db.BeginAutoRuleRun(run, "old-process", true).Kind);

        var restarted = _db.BeginAutoRuleRun(run, "new-process", true);
        var replay = _db.BeginAutoRuleRun(run, "new-process", true);

        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Terminal, restarted.Kind);
        Assert.False(restarted.Succeeded);
        Assert.Equal("interrupted_no_replay", restarted.OutcomeCode);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Terminal, replay.Kind);
        Assert.Equal("interrupted_no_replay", replay.OutcomeCode);
    }

    [Fact]
    public void RunLedger_RequiresExactRuntimeAndDbBindingAndDetectsPayloadConflict()
    {
        Approve();
        var run = RunCommand();
        var rejected = _db.BeginAutoRuleRun(run, "process-a", runtimeRegistryExact: false);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Terminal, rejected.Kind);
        Assert.Equal("runtime_registry_mismatch", rejected.OutcomeCode);

        var changed = run with { DeadlineSeconds = 301 };
        var conflict = _db.BeginAutoRuleRun(changed, "process-a", runtimeRegistryExact: true);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Conflict, conflict.Kind);
        Assert.Equal("command_payload_conflict", conflict.OutcomeCode);

        var reusedRunId = run with
        {
            CommandId = "99999999-9999-4999-8999-999999999999",
        };
        var runIdConflict = _db.BeginAutoRuleRun(
            reusedRunId, "process-a", runtimeRegistryExact: false);
        Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Conflict, runIdConflict.Kind);
        Assert.Equal("run_id_conflict", runIdConflict.OutcomeCode);
    }

    [Fact]
    public void TransitionRegistryAndRunningLedger_SurviveSqliteRestart()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "suavo-auto-control-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var transition = ApprovedTransition();
            var run = RunCommand();
            using (var first = new AgentStateDb(path))
            {
                SeedShadowApproval(first);
                Assert.True(first.ApplyAutoRuleTransition(transition, true).Succeeded);
                Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Start,
                    first.BeginAutoRuleRun(run, "old-process", true).Kind);
            }

            using (var restarted = new AgentStateDb(path))
            {
                Assert.Single(restarted.GetActiveAutoRuleBindings());
                var transitionReplay = restarted.ApplyAutoRuleTransition(transition, true);
                Assert.True(transitionReplay.Replay);
                Assert.True(transitionReplay.Succeeded);

                var interrupted = restarted.BeginAutoRuleRun(run, "new-process", true);
                Assert.Equal(AgentStateDb.AutoRuleRunBeginKind.Terminal, interrupted.Kind);
                Assert.Equal("interrupted_no_replay", interrupted.OutcomeCode);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch { }
        }
    }

    private void Approve()
    {
        SeedShadowApproval();
        Assert.True(_db.ApplyAutoRuleTransition(ApprovedTransition(), true).Succeeded);
    }

    private void SeedShadowApproval(AgentStateDb? target = null)
    {
        var db = target ?? _db;
        db.UpsertWorkflowTemplate(
            TemplateId, "1.0.0", "learned", "PioneerPharmacy*", "[]",
            "screen", "steps", null, "[]", 0.95, 12, false,
            "2026-07-10T12:00:00Z", "test");
        db.UpsertAutoRuleApproval(RuleId, TemplateId, Yaml);
        db.SetAutoRuleApprovalStatus(RuleId, AgentStateDb.AutoRuleStatus.Shadow);
    }

    private static AutoRuleTransitionCommand ApprovedTransition() => new(
        1, ApprovalId, RuleId, TemplateId, Yaml,
        AgentStateDb.AutoRuleStatus.Shadow,
        AgentStateDb.AutoRuleStatus.Approved,
        ApprovedBy,
        "2026-07-10T12:15:00.000Z",
        "human_approved",
        TransitionId);

    private static AutoRuleRunCommand RunCommand() => new(
        1, ApprovalId, RuleId, TemplateId, Yaml, RunId, 300, RunCommandId);
}
