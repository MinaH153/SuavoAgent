using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// QA learning-hardening: gates auto-GENERATED rules (those emitted by <c>TemplateRuleGenerator</c>,
/// each tracked by an <c>auto_rule_approvals</c> row) so only an OPERATOR-APPROVED auto-rule is ever
/// loaded into the <see cref="RuleEngine"/>.
///
/// Before this gate, the loader read every YAML under the rules/ tree — including the auto/ subtree —
/// with NO approval check, so a Pending/Shadow/Rejected auto-rule loaded (and, via the hardcoded
/// <c>AutonomousOk=false</c>, surfaced as an operator prompt) identically to an Approved one: the cloud
/// approve/reject UI was cosmetic for the load path. This would become a correctness/safety hole the
/// moment auto-approval-on-fingerprint-match is ever honored.
///
/// A rule whose id has NO approval row is a hand-authored or embedded override and is admitted
/// unconditionally — this gate governs only auto-generated rules.
///
/// <para><b>Why Shadow is also blocked:</b> the engine's shadow MODE is a per-evaluation flag, not a
/// per-rule status, and no on-device path measures Shadow-status rules through this engine (there is no
/// on-device <c>shadow_runs</c> writer). A Shadow rule loaded here would therefore behave like an
/// Approved one (surface as a prompt) — not shadow semantics. On-device shadow measurement, if ever
/// built, must run as a separate evaluation pass, not by admitting Shadow rules to the live engine.</para>
/// </summary>
public static class AutoRuleApprovalGate
{
    /// <summary>
    /// Returns the subset of <paramref name="directoryRules"/> permitted to load: every rule whose id
    /// has no approval row (not auto-generated), plus auto-generated rules that are
    /// <see cref="AgentStateDb.AutoRuleStatus.Approved"/>. <paramref name="approvalStatusOf"/> returns a
    /// rule's auto-approval status, or <c>null</c> if the rule is not auto-generated.
    /// </summary>
    public static List<Rule> AdmitApproved(
        IEnumerable<Rule> directoryRules,
        Func<string, AgentStateDb.AutoRuleStatus?> approvalStatusOf,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(directoryRules);
        ArgumentNullException.ThrowIfNull(approvalStatusOf);
        ArgumentNullException.ThrowIfNull(logger);

        var admitted = new List<Rule>();
        foreach (var rule in directoryRules)
        {
            var status = approvalStatusOf(rule.Id);
            if (status is null)
            {
                admitted.Add(rule); // hand-authored / embedded override — not governed by approvals
                continue;
            }

            if (status == AgentStateDb.AutoRuleStatus.Approved)
            {
                admitted.Add(rule);
                continue;
            }

            logger.LogWarning(
                "RuleEngine: auto-rule {RuleId} NOT loaded — approval status {Status} (operator approval required before it can match or surface)",
                rule.Id, status);
        }

        return admitted;
    }
}
