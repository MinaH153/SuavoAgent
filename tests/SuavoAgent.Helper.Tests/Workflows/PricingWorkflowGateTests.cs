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
    [InlineData("Edit Rx Item", true)]
    [InlineData("Edit Rx Item - Fluticasone Prop 50 Mcg Spray", true)]
    [InlineData("Edit Rx Item - ", false)]
    [InlineData("Edit Rx Item History", false)]
    [InlineData("Other - Edit Rx Item", false)]
    [InlineData("", false)]
    public void EditWindowTitle_RequiresExactTitleOrStrictSuffix(
        string title,
        bool expected) => Assert.Equal(
            expected,
            PricingWorkflow.IsEditRxItemWindowTitle(title));

    [Fact]
    public void QuickSearch_requires_exact_highlighted_ndc_without_do_not_use()
    {
        Assert.True(PricingWorkflow.QuickSearchSelectionMatches(
            "60505082901",
            ["Fluticasone Prop 50 Mcg Spray", "50 mcg", "60505-0829-01", "16"]));
        Assert.False(PricingWorkflow.QuickSearchSelectionMatches(
            "60505082901",
            ["Fluticasone Prop 50 Mcg Spray", "50 mcg", "00093-5056-98", "16"]));
        Assert.False(PricingWorkflow.QuickSearchSelectionMatches(
            "60505082901",
            ["Fluticasone Prop (Do Not Use)", "50 mcg", "60505-0829-01", "16"]));
    }

    [Fact]
    public void Loaded_item_verification_cannot_match_ndc_while_chooser_is_open()
    {
        const string ndc = "60505082901";
        var chooserTexts = new[]
        {
            "Fluticasone Prop 50 Mcg Spray",
            "60505-0829-01",
        };

        Assert.False(PricingWorkflow.LoadedItemIdentityMatches(
            ndc,
            chooserTexts,
            chooserVisible: true));
        Assert.True(PricingWorkflow.LoadedItemIdentityMatches(
            ndc,
            chooserTexts,
            chooserVisible: false));
    }

    [Theory]
    [InlineData("No", "Rx", true)]
    [InlineData("Unknown", "Rx", false)]
    [InlineData("No", "Unknown", false)]
    [InlineData("Yes", "Rx", false)]
    [InlineData("No", "rx", false)]
    public void Package_filters_require_exact_fixed_readback(
        string includeDiscontinued,
        string inventoryGroup,
        bool expected) => Assert.Equal(
            expected,
            PricingWorkflow.PackageFilterContractMatches(
                includeDiscontinued,
                inventoryGroup));

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
