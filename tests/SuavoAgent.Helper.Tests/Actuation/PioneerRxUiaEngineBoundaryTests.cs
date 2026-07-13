using FlaUI.Core.Definitions;
using Serilog;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class PioneerRxUiaEngineBoundaryTests
{
    [Fact]
    public void MissingLocalApprovalRejectsAttachAndEveryReadSurface()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var trust = new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved"));
        using var engine = new PioneerRxUiaEngine(logger, trust);

        Assert.False(engine.TryAttach());
        Assert.Null(engine.MainWindow);
        Assert.Equal(-1, engine.ProcessId);
        Assert.Equal("pioneerrx_not_attached", engine.VerifyAttachedProcessIdentity().Code);
        var health = engine.CheckHealth();
        Assert.False(health.WindowFound);
        Assert.False(health.MenuBarFound);
        Assert.Empty(health.MenuItems);
        Assert.Null(engine.FindElement(ControlType.Button, "Save"));
        Assert.Null(engine.ReadElementValue("PatientName"));
    }

    [Fact]
    public void LivePricingGateWithNoAttachedPmsRejectsUnprovenProcessIdentity()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var engine = new PioneerRxUiaEngine(logger);
        var config = new ActuationConfig
        {
            Enabled = true,
            DryRun = false,
        };
        var workflow = new PricingWorkflow(
            engine,
            new ActuationGate(config, logger),
            logger);

        var result = workflow.Lookup(new NdcPricingRequest(
            "job",
            1,
            "00093505698"));

        Assert.False(result.Found);
        Assert.Equal(
            PricingSafetyErrors.ActuationGateClosed(
                SuavoAgent.Contracts.Ipc.ActuationRejectionCodes.ProcessIdentityUntrusted),
            result.ErrorMessage);
        Assert.NotNull(result.Observations);
        Assert.Empty(result.Observations!);
    }
}
