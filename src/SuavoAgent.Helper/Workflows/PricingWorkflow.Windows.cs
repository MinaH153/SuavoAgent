using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    internal static bool IsEditRxItemWindowTitle(string? title) =>
        string.Equals(title, EditRxItemWindowTitle, StringComparison.Ordinal) ||
        title is not null &&
        title.StartsWith(EditRxItemWindowTitle + " - ", StringComparison.Ordinal) &&
        title.Length > EditRxItemWindowTitle.Length + 3;

    private Window? WaitForWindow(UIA2Automation automation)
    {
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            EnsureLiveActuation();
            try
            {
                var desktop = automation.GetDesktop();
                var cf = automation.ConditionFactory;
                var window = desktop
                    .FindAllDescendants(cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(element => IsEditRxItemWindowTitle(element.Name))
                    ?.AsWindow();
                if (window is not null)
                    return window;
            }
            catch
            {
                // A transient UIA read failure must not terminate the bounded wait.
            }

            Thread.Sleep(200);
        }

        return null;
    }
}
