using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    private bool ClickPricingTab(
        Window editWindow,
        ConditionFactory cf,
        SelectorResolver resolver)
    {
        try
        {
            var deadline = DateTime.UtcNow + ElementTimeout;
            while (DateTime.UtcNow < deadline)
            {
                EnsureLiveActuation();
                var (pricingTab, resolution) = resolver.FindFirst(
                    editWindow,
                    cf,
                    SelectorStepId.PricingTab,
                    cf.ByName(PricingTabName));
                if (pricingTab != null)
                {
                    LogIfLearned(SelectorStepId.PricingTab, resolution);
                    ExecuteLiveMutation(() => pricingTab.AsTabItem()?.Select());
                    Thread.Sleep(500);
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: ClickPricingTab failed locally");
            return false;
        }
    }

    private void TryCloseEditWindow(Window editWindow, ConditionFactory cf)
    {
        try
        {
            ExecuteLiveMutation(editWindow.Focus);
            ExecuteLiveMutation(() => Keyboard.Press(
                FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE));
            Thread.Sleep(300);
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: could not close Edit Rx Item window");
        }
    }

    private static SupplierPriceResult Fail(
        NdcPricingRequest request,
        string error) => new(
            request.JobId,
            request.RowIndex,
            request.Ndc,
            false,
            null,
            null,
            error);

    private sealed class PricingActuationGateClosedException(
        string rejectionCode) : Exception
    {
        public string RejectionCode { get; } = rejectionCode;
    }
}
