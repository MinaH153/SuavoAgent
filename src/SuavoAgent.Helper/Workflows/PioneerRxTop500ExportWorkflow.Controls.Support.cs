using System.Collections.Frozen;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.SystemObservers;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PioneerRxTop500ExportWorkflow
{
    private enum SaveAsMonitorOutcome
    {
        AutoSaved,
        TrustedDialogHandled,
        ForeignProcessRejected,
        InvalidTrustedDialog,
    }

    private static FrozenSet<string> CaptureSaveAsDialogBaseline(
        UIA2Automation automation)
    {
        var cf = automation.ConditionFactory;
        return FindVisibleSaveAsDialogs(automation.GetDesktop(), cf)
            .Select(TryGetStableElementKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    private async Task<SaveAsMonitorOutcome> MonitorOptionalSaveAsAsync(
        Task<XlsxExportWatchResult> watchTask,
        UIA2Automation automation,
        IReadOnlySet<string> baselineDialogKeys,
        string fixedOutputPath,
        CancellationToken ct)
    {
        var desktop = automation.GetDesktop();
        var cf = automation.ConditionFactory;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var dialog in FindVisibleSaveAsDialogs(desktop, cf))
            {
                var key = TryGetStableElementKey(dialog);
                if (key is not null && baselineDialogKeys.Contains(key))
                    continue;

                int dialogProcessId;
                try { dialogProcessId = dialog.Properties.ProcessId.ValueOrDefault; }
                catch { return SaveAsMonitorOutcome.ForeignProcessRejected; }
                if (!IsTrustedSaveAsDialogProcess(dialogProcessId, _engine.ProcessId))
                    return SaveAsMonitorOutcome.ForeignProcessRejected;

                if (!SetText(
                        dialog,
                        cf,
                        PioneerRxTop500ReportSurface.SaveAsFileNameId,
                        PioneerRxTop500ReportSurface.SaveAsFileNameHelp,
                        fixedOutputPath) ||
                    !VerifyText(
                        dialog,
                        cf,
                        PioneerRxTop500ReportSurface.SaveAsFileNameId,
                        PioneerRxTop500ReportSurface.SaveAsFileNameHelp,
                        fixedOutputPath) ||
                    !ClickFixedButton(
                        dialog,
                        automation,
                        PioneerRxTop500ReportSurface.SaveAsButtonId,
                        PioneerRxTop500ReportSurface.SaveAsButtonName))
                    return SaveAsMonitorOutcome.InvalidTrustedDialog;

                return SaveAsMonitorOutcome.TrustedDialogHandled;
            }

            // Scan once even when the workbook watcher just completed. This
            // closes the race where a newly created foreign Save As dialog and
            // the auto-saved workbook become visible in the same poll cycle.
            if (watchTask.IsCompleted)
                return SaveAsMonitorOutcome.AutoSaved;

            await Task.WhenAny(
                    watchTask,
                    Task.Delay(TimeSpan.FromMilliseconds(200), ct))
                .ConfigureAwait(false);
        }
    }

    internal static bool IsTrustedSaveAsDialogProcess(
        int dialogProcessId,
        int trustedPioneerRxProcessId) =>
        dialogProcessId > 0 &&
        trustedPioneerRxProcessId > 0 &&
        dialogProcessId == trustedPioneerRxProcessId;

    private static AutomationElement[] FindVisibleSaveAsDialogs(
        AutomationElement desktop,
        ConditionFactory cf) =>
        desktop.FindAllDescendants(new AndCondition(
                cf.ByControlType(ControlType.Window),
                cf.ByName(PioneerRxTop500ReportSurface.SaveAsTitle)))
            .Where(dialog =>
            {
                try { return !dialog.Properties.IsOffscreen.ValueOrDefault; }
                catch { return false; }
            })
            .ToArray();

    private static string? TryGetStableElementKey(AutomationElement element)
    {
        int processId;
        try { processId = element.Properties.ProcessId.ValueOrDefault; }
        catch { return null; }
        if (processId <= 0) return null;

        try
        {
            var runtimeId = element.Properties.RuntimeId.ValueOrDefault;
            if (runtimeId is { Length: > 0 })
                return $"pid:{processId}:runtime:" + string.Join('.', runtimeId);
        }
        catch { }

        try
        {
            var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
            return handle == 0 ? null : $"pid:{processId}:handle:{handle}";
        }
        catch
        {
            return null;
        }
    }

    private void BringPmsToForeground(Window window)
    {
        var processId = _engine.ProcessId;
        var rawHandle = window.Properties.NativeWindowHandle.ValueOrDefault;
        var foregroundEstablished = false;
        ExecuteLiveMutation(() =>
        {
            if (OperatingSystem.IsWindows() && rawHandle != 0)
                _ = WindowFocusManager.ForceForeground(new IntPtr(rawHandle), _logger);
            window.Focus();
            foregroundEstablished = ForegroundGuard.IsPidForeground(processId);
        });
        if (!foregroundEstablished)
            throw new Top500ActuationBlockedException(ActuationRejectionCodes.ForegroundNotTarget);
    }

    private void ActivateElement(AutomationElement element)
    {
        try
        {
            ExecuteLiveMutation(() =>
            {
                if (element.Patterns.Invoke.IsSupported)
                    element.Patterns.Invoke.Pattern.Invoke();
                else
                    element.Click();
            });
        }
        catch (Top500ActuationBlockedException) { throw; }
        catch
        {
            ExecuteLiveMutation(() => element.Click());
        }
    }

    private static AutomationElement? FindInput(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        string helpText,
        ControlType controlType) =>
        root.FindFirstDescendant(new AndCondition(
            cf.ByControlType(controlType),
            cf.ByAutomationId(automationId)))
        ?? root.FindFirstDescendant(new AndCondition(
            cf.ByControlType(controlType),
            cf.ByHelpText(helpText)))
        ?? FindExactDescendant(root, cf, controlType, helpText);

    private static AutomationElement? FindByIdOrExactName(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        ControlType controlType,
        string exactName) =>
        root.FindFirstDescendant(new AndCondition(
            cf.ByControlType(controlType),
            cf.ByAutomationId(automationId)))
        ?? FindExactDescendant(root, cf, controlType, exactName);

    private static AutomationElement? FindExactDescendant(
        AutomationElement root,
        ConditionFactory cf,
        ControlType controlType,
        string exactName) => root.FindFirstDescendant(new AndCondition(
            cf.ByControlType(controlType),
            cf.ByName(exactName)));

    private static AutomationElement? FindExactNavigationElement(
        AutomationElement root,
        ConditionFactory cf,
        string exactName,
        IReadOnlyList<ControlType> controlTypes)
    {
        foreach (var controlType in controlTypes)
        {
            var match = FindExactDescendant(root, cf, controlType, exactName);
            if (match is not null) return match;
        }

        return null;
    }

    private AutomationElement? WaitForExactVisibleProcessSurface(
        AutomationElement root,
        ConditionFactory cf,
        string exactName,
        IReadOnlyList<ControlType> controlTypes,
        int processId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ElementTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            var surface = FindExactVisibleProcessSurface(
                root, cf, exactName, controlTypes, processId);
            if (surface is not null) return surface;
            Thread.Sleep(200);
        }

        return null;
    }

    private static AutomationElement? FindExactVisibleProcessSurface(
        AutomationElement root,
        ConditionFactory cf,
        string exactName,
        IReadOnlyList<ControlType> controlTypes,
        int processId)
    {
        foreach (var controlType in controlTypes)
        {
            var matches = root.FindAllDescendants(new AndCondition(
                cf.ByControlType(controlType),
                cf.ByName(exactName)));
            foreach (var match in matches)
            {
                try
                {
                    if (match.Properties.ProcessId.ValueOrDefault == processId &&
                        !match.Properties.IsOffscreen.ValueOrDefault)
                        return match;
                }
                catch
                {
                    // Window creation/destruction races are normal while a
                    // PioneerRx modal or report viewer is being replaced.
                }
            }
        }

        return null;
    }

    private static AutomationElement? FindExactProcessWindow(
        AutomationElement root,
        ConditionFactory cf,
        string exactName,
        int processId)
    {
        var matches = root.FindAllDescendants(new AndCondition(
            cf.ByControlType(ControlType.Window),
            cf.ByName(exactName)));
        foreach (var match in matches)
        {
            try
            {
                if (match.Properties.ProcessId.ValueOrDefault == processId)
                    return match;
            }
            catch
            {
                // A minimized or closing viewer can race UIA enumeration.
            }
        }
        return null;
    }

    private AutomationElement? FindExactVisibleProcessNavigationElement(
        AutomationElement root,
        ConditionFactory cf,
        string exactName,
        IReadOnlyList<ControlType> controlTypes,
        int processId)
    {
        foreach (var controlType in controlTypes)
        {
            var matches = root.FindAllDescendants(new AndCondition(
                cf.ByControlType(controlType),
                cf.ByName(exactName)));
            foreach (var match in matches)
            {
                try
                {
                    if (match.Properties.ProcessId.ValueOrDefault != processId ||
                        !match.IsEnabled)
                        continue;
                    if (match.Properties.IsOffscreen.ValueOrDefault)
                    {
                        if (!match.Patterns.ScrollItem.IsSupported) continue;
                        ExecuteLiveMutation(
                            () => match.Patterns.ScrollItem.Pattern.ScrollIntoView());
                        Thread.Sleep(100);
                        if (match.Properties.IsOffscreen.ValueOrDefault) continue;
                    }
                    return match;
                }
                catch
                {
                    // A popup can disappear while UIA is enumerating it. Keep
                    // looking for the exact live entry from this process.
                }
            }
        }

        return null;
    }
}
