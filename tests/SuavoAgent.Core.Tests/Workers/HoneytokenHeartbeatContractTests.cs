using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HoneytokenHeartbeatContractTests
{
    [Theory]
    [InlineData("agent_process")]
    [InlineData("system_process")]
    [InlineData("sensitive_shell")]
    [InlineData("unexpected_process")]
    [InlineData("unknown_process")]
    public void FixedReasonCategory_SurvivesHeartbeatBoundary(string value)
    {
        Assert.Equal(
            value,
            HeartbeatWorker.NormalizeHoneytokenReasonLabel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("powershell")]
    [InlineData("Jane_Doe_01-15-1990")]
    [InlineData("unexpected_process.Jane_Doe")]
    public void DynamicOrLegacyReason_NeverCrossesHeartbeatBoundary(string? value)
    {
        Assert.Equal(
            HoneytokenReasonLabels.UnknownProcess,
            HeartbeatWorker.NormalizeHoneytokenReasonLabel(value));
    }
}
