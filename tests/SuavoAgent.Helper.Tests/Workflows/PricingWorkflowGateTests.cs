using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Workflows;

public sealed class PricingWorkflowGateTests
{
    [Theory]
    [InlineData(false, false, ActuationRejectionCodes.GateDisabled)]
    [InlineData(true, true, ActuationRejectionCodes.GateDryRun)]
    public void Lookup_ClosedLiveGate_FailsBeforeAnyUiaNavigation(
        bool enabled,
        bool dryRun,
        string expectedCode)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger); // deliberately unattached
        var gate = new ActuationGate(new ActuationConfig
        {
            Enabled = enabled,
            DryRun = dryRun,
        }, logger);
        var workflow = new PricingWorkflow(engine, gate, logger);

        var result = workflow.Lookup(new NdcPricingRequest("job", 2, "00093505698"));

        Assert.False(result.Found);
        Assert.Equal(PricingSafetyErrors.ActuationGateClosed(expectedCode), result.ErrorMessage);
    }

    [Fact]
    public void Lookup_UserPause_FailsBeforeAnyUiaNavigation()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var gate = new ActuationGate(new ActuationConfig { Enabled = true, DryRun = false }, logger);
        gate.NotifyUserInputDetected("test");
        var workflow = new PricingWorkflow(engine, gate, logger);

        var result = workflow.Lookup(new NdcPricingRequest("job", 2, "00093505698"));

        Assert.Equal(
            PricingSafetyErrors.ActuationGateClosed(ActuationRejectionCodes.GatePaused),
            result.ErrorMessage);
    }
}
