using FlaUI.Core.AutomationElements;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    private void BringPmsToForeground(Window mainWindow)
    {
        var processId = _engine.ProcessId;
        var rawHandle = mainWindow.Properties.NativeWindowHandle.ValueOrDefault;
        var foregroundEstablished = false;

        ExecuteLiveMutation(() =>
        {
            if (OperatingSystem.IsWindows() && rawHandle != 0)
            {
                _ = WindowFocusManager.ForceForeground(
                    new IntPtr(rawHandle),
                    _logger);
            }

            mainWindow.Focus();
            foregroundEstablished = SystemObservers.ForegroundGuard
                .IsPidForeground(processId);
        });

        if (!foregroundEstablished)
        {
            throw new PricingActuationGateClosedException(
                ActuationRejectionCodes.ForegroundNotTarget);
        }
    }

    private void PrepareVisibleAction(
        AutomationElement target,
        string actionKind,
        string safeLabel)
    {
        var driver = _pointerDriver;
        if (driver is null) return;

        var rect = target.BoundingRectangle;
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
        var x = (int)(rect.Left + rect.Width / 2);
        var y = (int)(rect.Top + rect.Height / 2);
        driver.NarratePresence(actionKind, safeLabel);
        var result = driver.MovePointerTo(
            x,
            y,
            _engine.ProcessId,
            "PioneerRx",
            SendInputDriver.TargetTrustKind.PioneerRx);
        if (!result.Ok)
            throw new PricingActuationGateClosedException(
                result.RejectionCode ?? ActuationRejectionCodes.ExecutionException);
    }

    private void NarrateVisibleRead(string safeLabel) =>
        _pointerDriver?.NarratePresence("Reading", safeLabel);
}
