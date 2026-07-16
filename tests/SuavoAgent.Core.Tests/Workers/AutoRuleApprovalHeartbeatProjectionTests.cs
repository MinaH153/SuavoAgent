using System.Text.Json;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class AutoRuleApprovalHeartbeatProjectionTests
{
    [Fact]
    public void Project_IncludesPersistedHasWritebackInExactCloudField()
    {
        var row = new AgentStateDb.AutoRuleApprovalRow(
            "auto.learned.abc",
            "template-abc",
            "yaml-sha",
            true,
            AgentStateDb.AutoRuleStatus.Pending,
            0,
            0,
            0,
            null,
            null,
            null);

        var payload = Assert.Single(AutoRuleApprovalHeartbeatProjection.Project([row]));
        var json = JsonSerializer.SerializeToElement(payload);

        Assert.True(json.GetProperty("hasWriteback").GetBoolean());
        Assert.False(json.TryGetProperty("HasWriteback", out _));
        Assert.Equal("auto.learned.abc", json.GetProperty("ruleId").GetString());
    }
}
