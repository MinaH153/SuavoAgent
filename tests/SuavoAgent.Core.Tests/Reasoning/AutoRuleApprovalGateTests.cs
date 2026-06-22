using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// QA learning-hardening: the load-time gate that stops a non-Approved auto-GENERATED rule from being
/// loaded into the RuleEngine (the cloud approve/reject was cosmetic for the load path before this).
/// </summary>
public class AutoRuleApprovalGateTests
{
    private static Rule R(string id) => new()
    {
        Id = id,
        SkillId = "s",
        When = new RulePredicate(),
        Then = Array.Empty<RuleActionSpec>(),
    };

    private static List<Rule> Admit(IEnumerable<Rule> rules, Func<string, AgentStateDb.AutoRuleStatus?> lookup)
        => AutoRuleApprovalGate.AdmitApproved(rules, lookup, NullLogger.Instance);

    [Fact]
    public void Rule_with_no_approval_row_is_admitted_as_manual_or_embedded_override()
    {
        var admitted = Admit(new[] { R("manual") }, _ => null);
        Assert.Single(admitted);
        Assert.Equal("manual", admitted[0].Id);
    }

    [Fact]
    public void Approved_auto_rule_is_admitted()
        => Assert.Single(Admit(new[] { R("a") }, _ => AgentStateDb.AutoRuleStatus.Approved));

    [Theory]
    [InlineData(AgentStateDb.AutoRuleStatus.Pending)]
    [InlineData(AgentStateDb.AutoRuleStatus.Shadow)]
    [InlineData(AgentStateDb.AutoRuleStatus.Rejected)]
    public void Non_approved_auto_rule_is_blocked(AgentStateDb.AutoRuleStatus status)
        => Assert.Empty(Admit(new[] { R("x") }, _ => status));

    [Fact]
    public void Mixed_set_admits_only_approved_and_unrowed()
    {
        var rules = new[] { R("manual"), R("approved"), R("pending"), R("rejected") };
        var statuses = new Dictionary<string, AgentStateDb.AutoRuleStatus?>
        {
            ["manual"] = null,
            ["approved"] = AgentStateDb.AutoRuleStatus.Approved,
            ["pending"] = AgentStateDb.AutoRuleStatus.Pending,
            ["rejected"] = AgentStateDb.AutoRuleStatus.Rejected,
        };
        var admitted = Admit(rules, id => statuses[id]).Select(r => r.Id).ToList();
        Assert.Equal(new[] { "manual", "approved" }, admitted);
    }
}
