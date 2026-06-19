using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PioneerRxSim;

/// <summary>
/// A WPF Menu whose automation peer reports ControlType.MenuBar.
///
/// WHY: stock WPF Menu exposes UIA ControlType.Menu, but PricingWorkflow.OpenRxItemDialog and
/// PioneerRxUiaEngine.CheckHealth both resolve the menu bar via
/// FindFirstDescendant(ControlType.MenuBar) — the surface the workflow asserts from the Apr 4
/// field screenshots. The default sim presents that asserted surface so the full chain can
/// rehearse; the `wpf-menu` variant swaps in a stock Menu to demonstrate what happens if the
/// real PioneerRx menu is WPF-native (workflow dies at step 1).
///
/// Which control type the REAL PioneerRx menu reports is a VM-unanswerable unknown (screenshots
/// carry no control types) — it is called out in the runbook as a first-minutes-on-Nadim's-box
/// verification (Accessibility Insights / FlaUInspect).
/// </summary>
public sealed class PrxMenuBar : Menu
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PrxMenuBarPeer(this);

    private sealed class PrxMenuBarPeer : MenuAutomationPeer
    {
        public PrxMenuBarPeer(Menu owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.MenuBar;

        protected override string GetClassNameCore() => "PrxMenuBar";
    }
}
