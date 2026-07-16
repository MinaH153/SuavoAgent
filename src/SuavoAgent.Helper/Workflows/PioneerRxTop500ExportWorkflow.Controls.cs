using System.Collections.Frozen;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.SystemObservers;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PioneerRxTop500ExportWorkflow
{
    private sealed record ReportOpenResult(
        bool NavigationSucceeded,
        AutomationElement? Surface);

    private ReportOpenResult OpenReportWindow(
        Window mainWindow,
        UIA2Automation automation,
        CancellationToken ct)
    {
        var cf = automation.ConditionFactory;
        var existing = FindEmbeddedReportSurface(mainWindow, cf);
        if (existing is not null) return new(true, existing);

        var toolbar = mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.ToolBar));
        foreach (var exactName in DirectReportOpenNames)
        {
            var directOpen = FindExactNavigationElement(
                toolbar ?? mainWindow,
                cf,
                exactName,
                SearchControlTypes)
                ?? (toolbar is null
                    ? null
                    : FindExactNavigationElement(
                        mainWindow,
                        cf,
                        exactName,
                        SearchControlTypes));
            if (directOpen is null) continue;
            ActivateElement(directOpen);
            var directlyOpened = WaitForEmbeddedReportSurface(mainWindow, cf, ct);
            if (directlyOpened is not null) return new(true, directlyOpened);
        }

        // PioneerRx exposes Actions / Tools / Search / Reports as a toolbar in
        // the field build. Depending on the UIA provider, Search can surface as
        // a MenuItem, Button, or SplitButton. Never require a MenuBar ancestor.
        var globalSearch = FindExactNavigationElement(
            toolbar ?? mainWindow,
            cf,
            PioneerRxTop500ReportSurface.GlobalSearchMenu,
            SearchControlTypes)
            ?? (toolbar is null
                ? null
                : FindExactNavigationElement(
                    mainWindow,
                    cf,
                    PioneerRxTop500ReportSurface.GlobalSearchMenu,
                    SearchControlTypes));
        if (globalSearch is null) return new(false, null);

        var desktop = automation.GetDesktop();
        AutomationElement? reportEntry = null;
        foreach (var opener in MenuOpeners)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            try { ExecuteLiveMutation(() => opener(globalSearch)); }
            catch (Top500ActuationBlockedException) { throw; }
            catch { }
            Thread.Sleep(250);
            reportEntry = FindExactVisibleProcessNavigationElement(
                desktop,
                cf,
                PioneerRxTop500ReportSurface.OpenReportMenu,
                PopupEntryControlTypes,
                _engine.ProcessId);
            if (reportEntry is not null) break;
        }
        if (reportEntry is null) return new(false, null);

        ActivateElement(reportEntry);
        return new(true, WaitForEmbeddedReportSurface(mainWindow, cf, ct));
    }

    private AutomationElement? WaitForEmbeddedReportSurface(
        Window mainWindow,
        ConditionFactory cf,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ElementTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            try
            {
                var currentMain = _engine.MainWindow ?? mainWindow;
                var surface = FindEmbeddedReportSurface(currentMain, cf);
                if (surface is not null) return surface;
            }
            catch { }
            Thread.Sleep(200);
        }

        return null;
    }

    private ReportOpenResult OpenReportParameters(
        Window mainWindow,
        UIA2Automation automation,
        CancellationToken ct)
    {
        var cf = automation.ConditionFactory;
        var toolbar = mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.ToolBar));
        var reports = FindExactNavigationElement(
            toolbar ?? mainWindow,
            cf,
            PioneerRxTop500ReportSurface.ReportsMenu,
            SearchControlTypes)
            ?? (toolbar is null
                ? null
                : FindExactNavigationElement(
                    mainWindow,
                    cf,
                    PioneerRxTop500ReportSurface.ReportsMenu,
                    SearchControlTypes));
        if (reports is null) return new(false, null);

        var desktop = automation.GetDesktop();
        AutomationElement? reportEntry = null;
        foreach (var opener in MenuOpeners)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            try { ExecuteLiveMutation(() => opener(reports)); }
            catch (Top500ActuationBlockedException) { throw; }
            catch { }
            Thread.Sleep(250);
            reportEntry = FindExactVisibleProcessNavigationElement(
                desktop,
                cf,
                PioneerRxTop500ReportSurface.ReportEntry,
                PopupEntryControlTypes,
                _engine.ProcessId);
            if (reportEntry is not null) break;
        }
        if (reportEntry is not null)
        {
            ActivateElement(reportEntry);
        }
        else
        {
            // Some PioneerRx builds virtualize the very long Reports popup.
            // Fixed-string menu type-ahead is the only fallback: the resulting
            // exact Report Parameters modal and exported workbook are both
            // verified before any output is accepted.
            ExecuteLiveMutation(
                () => Keyboard.Type(PioneerRxTop500ReportSurface.ReportEntry));
            ExecuteLiveMutation(() => Keyboard.Press(
                FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN));
        }
        var parameters = WaitForExactVisibleProcessSurface(
            desktop,
            cf,
            PioneerRxTop500ReportSurface.ParametersTitle,
            ModalSurfaceControlTypes,
            _engine.ProcessId,
            ct);
        return new(true, parameters);
    }

    private bool ApplyFixedRecipe(
        AutomationElement reportSurface,
        UIA2Automation automation,
        DateOnly startDate,
        DateOnly runDate,
        CancellationToken ct)
    {
        var cf = automation.ConditionFactory;
        ct.ThrowIfCancellationRequested();
        EnsureLiveActuation();

        var rxTab = FindByIdOrExactName(
            reportSurface,
            cf,
            PioneerRxTop500ReportSurface.RxTabId,
            ControlType.TabItem,
            PioneerRxTop500ReportSurface.RxTab);
        if (rxTab is null) return false;
        ExecuteLiveMutation(() => rxTab.AsTabItem()?.Select());

        if (!ApplyRxTabRecipeFailClosed(
                () => SetText(
                    reportSurface,
                    cf,
                    PioneerRxTop500ReportSurface.CompletedFromId,
                    PioneerRxTop500ReportSurface.CompletedFromHelp,
                    PioneerRxTop500ReportRecipe.FormatDate(startDate)),
                () => SetText(
                    reportSurface,
                    cf,
                    PioneerRxTop500ReportSurface.CompletedThroughId,
                    PioneerRxTop500ReportSurface.CompletedThroughHelp,
                    PioneerRxTop500ReportRecipe.FormatDate(runDate)),
                () => SelectCombo(
                    reportSurface,
                    cf,
                    PioneerRxTop500ReportSurface.RxTransactionId,
                    PioneerRxTop500ReportSurface.RxTransactionHelp,
                    PioneerRxTop500ReportRecipe.RxTransaction),
                () => SetExactStatuses(reportSurface, cf),
                () => VerifyRxTabRecipe(reportSurface, cf, startDate, runDate)))
            return false;

        var dispensedItemTab = FindByIdOrExactName(
            reportSurface,
            cf,
            PioneerRxTop500ReportSurface.DispensedItemTabId,
            ControlType.TabItem,
            PioneerRxTop500ReportSurface.DispensedItemTab);
        if (dispensedItemTab is null) return false;
        ExecuteLiveMutation(() => dispensedItemTab.AsTabItem()?.Select());

        return SelectCombo(
                   reportSurface,
                   cf,
                   PioneerRxTop500ReportSurface.DrugClassId,
                   PioneerRxTop500ReportSurface.DrugClassHelp,
                   PioneerRxTop500ReportRecipe.DrugClass) &&
               SelectCombo(
                   reportSurface,
                   cf,
                   PioneerRxTop500ReportSurface.BrandGenericId,
                   PioneerRxTop500ReportSurface.BrandGenericHelp,
                   PioneerRxTop500ReportRecipe.BrandGeneric) &&
               SelectCombo(
                   reportSurface,
                   cf,
                   PioneerRxTop500ReportSurface.DeaScheduleId,
                   PioneerRxTop500ReportSurface.DeaScheduleHelp,
                   PioneerRxTop500ReportRecipe.DeaSchedule) &&
               VerifyDispensedItemRecipe(reportSurface, cf);
    }

    private bool VerifyFixedRecipe(
        AutomationElement reportSurface,
        UIA2Automation automation,
        DateOnly startDate,
        DateOnly runDate)
    {
        var cf = automation.ConditionFactory;
        var rxTab = FindByIdOrExactName(
            reportSurface, cf, PioneerRxTop500ReportSurface.RxTabId,
            ControlType.TabItem, PioneerRxTop500ReportSurface.RxTab);
        if (rxTab is null) return false;
        ExecuteLiveMutation(() => rxTab.AsTabItem()?.Select());
        if (!VerifyRxTabRecipe(reportSurface, cf, startDate, runDate)) return false;

        var dispensedItemTab = FindByIdOrExactName(
            reportSurface, cf, PioneerRxTop500ReportSurface.DispensedItemTabId,
            ControlType.TabItem, PioneerRxTop500ReportSurface.DispensedItemTab);
        if (dispensedItemTab is null) return false;
        ExecuteLiveMutation(() => dispensedItemTab.AsTabItem()?.Select());
        return VerifyDispensedItemRecipe(reportSurface, cf);
    }

    private static AutomationElement? FindEmbeddedReportSurface(
        AutomationElement root,
        ConditionFactory cf)
    {
        foreach (var header in root.FindAllDescendants(
                     cf.ByName(PioneerRxTop500ReportSurface.SurfaceHeader)))
        {
            try
            {
                if (header.Properties.IsOffscreen.ValueOrDefault) continue;
            }
            catch
            {
                continue;
            }

            // Return the smallest visible embedded child container that owns
            // the title and both structural tabs. Enumerating all exact title
            // matches avoids binding to a stale hidden surface retained by the
            // PioneerRx shell.
            for (var candidate = header.Parent;
                 candidate is not null && candidate.ControlType != ControlType.Window;
                 candidate = candidate.Parent)
            {
                try
                {
                    if (candidate.Properties.IsOffscreen.ValueOrDefault) continue;
                }
                catch
                {
                    continue;
                }
                var rxTab = FindByIdOrExactName(
                    candidate, cf, PioneerRxTop500ReportSurface.RxTabId,
                    ControlType.TabItem, PioneerRxTop500ReportSurface.RxTab);
                var dispensedTab = FindByIdOrExactName(
                    candidate, cf, PioneerRxTop500ReportSurface.DispensedItemTabId,
                    ControlType.TabItem, PioneerRxTop500ReportSurface.DispensedItemTab);
                if (rxTab is not null && dispensedTab is not null)
                    return candidate;
            }
        }

        return null;
    }

    internal static bool ApplyRxTabRecipeFailClosed(
        Func<bool> setCompletedFrom,
        Func<bool> setCompletedThrough,
        Func<bool> selectRxTransaction,
        Func<bool> setStatuses,
        Func<bool> verifyReadBack) =>
        setCompletedFrom() &&
        setCompletedThrough() &&
        selectRxTransaction() &&
        setStatuses() &&
        verifyReadBack();

    private bool VerifyRxTabRecipe(
        AutomationElement root,
        ConditionFactory cf,
        DateOnly startDate,
        DateOnly runDate) =>
        VerifyText(root, cf, PioneerRxTop500ReportSurface.CompletedFromId,
            PioneerRxTop500ReportSurface.CompletedFromHelp,
            PioneerRxTop500ReportRecipe.FormatDate(startDate)) &&
        VerifyText(root, cf, PioneerRxTop500ReportSurface.CompletedThroughId,
            PioneerRxTop500ReportSurface.CompletedThroughHelp,
            PioneerRxTop500ReportRecipe.FormatDate(runDate)) &&
        VerifyCombo(root, cf, PioneerRxTop500ReportSurface.RxTransactionId,
            PioneerRxTop500ReportSurface.RxTransactionHelp,
            PioneerRxTop500ReportRecipe.RxTransaction) &&
        VerifyExactStatuses(root, cf);

    private bool VerifyDispensedItemRecipe(
        AutomationElement root,
        ConditionFactory cf) =>
        VerifyCombo(root, cf, PioneerRxTop500ReportSurface.DrugClassId,
            PioneerRxTop500ReportSurface.DrugClassHelp,
            PioneerRxTop500ReportRecipe.DrugClass) &&
        VerifyCombo(root, cf, PioneerRxTop500ReportSurface.BrandGenericId,
            PioneerRxTop500ReportSurface.BrandGenericHelp,
            PioneerRxTop500ReportRecipe.BrandGeneric) &&
        VerifyCombo(root, cf, PioneerRxTop500ReportSurface.DeaScheduleId,
            PioneerRxTop500ReportSurface.DeaScheduleHelp,
            PioneerRxTop500ReportRecipe.DeaSchedule);

    private bool SetText(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        string helpText,
        string value)
    {
        var element = FindInput(root, cf, automationId, helpText, ControlType.Edit);
        var textBox = element?.AsTextBox();
        if (textBox is null || textBox.IsReadOnly) return false;
        try
        {
            ExecuteLiveMutation(() => textBox.Text = value);
            return string.Equals(textBox.Text?.Trim(), value, StringComparison.Ordinal);
        }
        catch (Top500ActuationBlockedException) { throw; }
        catch
        {
            if (element is null) return false;
            ExecuteLiveMutation(element.Focus);
            ExecuteLiveMutation(() => Keyboard.TypeSimultaneously(
                FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A));
            ExecuteLiveMutation(() => Keyboard.Type(value));
            return string.Equals(element.AsTextBox()?.Text?.Trim(), value, StringComparison.Ordinal);
        }
    }

    private bool VerifyText(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        string helpText,
        string expected)
    {
        var element = FindInput(root, cf, automationId, helpText, ControlType.Edit);
        return string.Equals(
            element?.AsTextBox()?.Text?.Trim(),
            expected,
            StringComparison.Ordinal);
    }

    private bool SelectCombo(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        string helpText,
        string value)
    {
        var combo = FindInput(root, cf, automationId, helpText, ControlType.ComboBox)?.AsComboBox();
        if (combo is null) return false;
        try
        {
            ExecuteLiveMutation(() => combo.Select(value));
            return ComboEquals(combo, value);
        }
        catch (Top500ActuationBlockedException) { throw; }
        catch
        {
            return false;
        }
    }

    private bool VerifyCombo(
        AutomationElement root,
        ConditionFactory cf,
        string automationId,
        string helpText,
        string expected)
    {
        var combo = FindInput(root, cf, automationId, helpText, ControlType.ComboBox)?.AsComboBox();
        return combo is not null && ComboEquals(combo, expected);
    }

    private static bool ComboEquals(ComboBox combo, string expected)
    {
        try
        {
            return string.Equals(combo.SelectedItem?.Text, expected, StringComparison.Ordinal) ||
                   string.Equals(combo.Value, expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool SetExactStatuses(AutomationElement root, ConditionFactory cf)
    {
        var group = FindByIdOrExactName(
            root,
            cf,
            PioneerRxTop500ReportSurface.StatusGroupId,
            ControlType.Group,
            PioneerRxTop500ReportSurface.StatusGroupName);
        if (group is null) return false;

        var checkboxes = group.FindAllDescendants(cf.ByControlType(ControlType.CheckBox));
        if (checkboxes.Length < ExactIncludedStatuses.Count ||
            checkboxes.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            return false;

        foreach (var element in checkboxes)
        {
            var checkbox = element.AsCheckBox();
            var shouldBeChecked = ExactIncludedStatuses.Contains(element.Name);
            try
            {
                if (checkbox.IsChecked != shouldBeChecked)
                    ExecuteLiveMutation(() => checkbox.IsChecked = shouldBeChecked);
            }
            catch (Top500ActuationBlockedException) { throw; }
            catch { return false; }
        }
        return VerifyExactStatuses(root, cf);
    }

    private static bool VerifyExactStatuses(AutomationElement root, ConditionFactory cf)
    {
        var group = FindByIdOrExactName(
            root,
            cf,
            PioneerRxTop500ReportSurface.StatusGroupId,
            ControlType.Group,
            PioneerRxTop500ReportSurface.StatusGroupName);
        if (group is null) return false;

        var checkboxes = group.FindAllDescendants(cf.ByControlType(ControlType.CheckBox));
        if (checkboxes.Length < ExactIncludedStatuses.Count) return false;
        var selected = checkboxes
            .Where(element => element.AsCheckBox().IsChecked == true)
            .Select(element => element.Name)
            .ToFrozenSet(StringComparer.Ordinal);
        return selected.SetEquals(ExactIncludedStatuses);
    }

    private bool ClickFixedButton(
        AutomationElement root,
        UIA2Automation automation,
        string automationId,
        string exactName)
    {
        var button = FindByIdOrExactName(
            root,
            automation.ConditionFactory,
            automationId,
            ControlType.Button,
            exactName);
        if (button is null || !button.IsEnabled) return false;
        try
        {
            ExecuteLiveMutation(() =>
            {
                if (button.Patterns.Invoke.IsSupported)
                    button.Patterns.Invoke.Pattern.Invoke();
                else
                    button.Click();
            });
            return true;
        }
        catch (Top500ActuationBlockedException) { throw; }
        catch { return false; }
    }

    private bool ApplyReportParameters(
        AutomationElement parameters,
        UIA2Automation automation)
    {
        var value = PioneerRxTop500ReportRecipe.TopCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return SetText(
                   parameters,
                   automation.ConditionFactory,
                   PioneerRxTop500ReportSurface.TopCountId,
                   PioneerRxTop500ReportSurface.TopCountHelp,
                   value) &&
               VerifyText(
                   parameters,
                   automation.ConditionFactory,
                   PioneerRxTop500ReportSurface.TopCountId,
                   PioneerRxTop500ReportSurface.TopCountHelp,
                   value);
    }

    private bool CloseExistingReportViewers(
        UIA2Automation automation,
        CancellationToken ct)
    {
        var desktop = automation.GetDesktop();
        var cf = automation.ConditionFactory;
        var deadline = DateTimeOffset.UtcNow + ElementTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            var viewer = FindExactProcessWindow(
                desktop,
                cf,
                PioneerRxTop500ReportSurface.ViewerTitle,
                _engine.ProcessId);
            if (viewer is null)
            {
                // A visible exact-title pane with no closeable top-level
                // viewer is an unknown PioneerRx surface; do not export through
                // it or close the main application window.
                return FindExactVisibleProcessSurface(
                    desktop,
                    cf,
                    PioneerRxTop500ReportSurface.ViewerTitle,
                    ViewerSurfaceControlTypes,
                    _engine.ProcessId) is null;
            }

            int processId;
            try
            {
                processId = viewer.Properties.ProcessId.ValueOrDefault;
            }
            catch
            {
                return false;
            }
            if (!IsSafeExistingReportViewerToClose(
                    processId,
                    _engine.ProcessId,
                    viewer.ControlType))
                return false;

            var window = viewer.AsWindow();
            if (window is null) return false;
            try
            {
                ExecuteLiveMutation(window.Close);
            }
            catch (Top500ActuationBlockedException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    internal static bool IsSafeExistingReportViewerToClose(
        int viewerProcessId,
        int trustedPioneerRxProcessId,
        ControlType controlType) =>
        controlType == ControlType.Window &&
        viewerProcessId > 0 &&
        viewerProcessId == trustedPioneerRxProcessId;

    private AutomationElement? WaitForReportViewer(
        UIA2Automation automation,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ElementTimeout;
        var desktop = automation.GetDesktop();
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            var viewer = FindExactVisibleProcessSurface(
                desktop,
                automation.ConditionFactory,
                PioneerRxTop500ReportSurface.ViewerTitle,
                ViewerSurfaceControlTypes,
                _engine.ProcessId);
            if (viewer is not null && VerifyReportViewer(viewer, automation.ConditionFactory))
                return viewer;
            Thread.Sleep(200);
        }
        return null;
    }

    private static bool VerifyReportViewer(
        AutomationElement viewer,
        ConditionFactory cf)
    {
        var exactTitle = string.Equals(
            viewer.Name,
            PioneerRxTop500ReportSurface.ViewerTitle,
            StringComparison.Ordinal) ||
            FindExactNavigationElement(
                viewer,
                cf,
                PioneerRxTop500ReportSurface.ViewerTitle,
                ExactAnchorControlTypes) is not null;
        var firstPage = FindExactNavigationElement(
            viewer,
            cf,
            PioneerRxTop500ReportSurface.ViewerFirstPage,
            ExactAnchorControlTypes);
        var excel = FindExactNavigationElement(
            viewer,
            cf,
            PioneerRxTop500ReportSurface.ExcelButtonName,
            ViewerExportControlTypes);

        // Lexmark's report canvas does not consistently expose its rendered
        // text to UIA. Exact report content is therefore fail-closed at the
        // downloaded XLSX boundary (title, preamble, 1..500, and 18 pages).
        return exactTitle && firstPage is not null && excel is { IsEnabled: true };
    }

    private bool ClickViewerExcel(
        AutomationElement viewer,
        UIA2Automation automation)
    {
        var excel = FindExactNavigationElement(
            viewer,
            automation.ConditionFactory,
            PioneerRxTop500ReportSurface.ExcelButtonName,
            ViewerExportControlTypes);
        if (excel is null || !excel.IsEnabled) return false;
        ActivateElement(excel);
        return true;
    }


    internal static IReadOnlyList<ControlType> SearchControlTypes { get; } =
        Array.AsReadOnly(
        [
            ControlType.MenuItem,
            ControlType.Button,
            ControlType.SplitButton,
        ]);

    internal static IReadOnlyList<string> DirectReportOpenNames { get; } =
        Array.AsReadOnly(
        [
            PioneerRxTop500ReportSurface.DirectOpenReport,
            PioneerRxTop500ReportSurface.OpenReportMenu,
        ]);

    internal static IReadOnlyList<ControlType> PopupEntryControlTypes { get; } =
        Array.AsReadOnly(
        [
            ControlType.MenuItem,
            ControlType.ListItem,
            ControlType.Button,
        ]);

    internal static IReadOnlyList<ControlType> ModalSurfaceControlTypes { get; } =
        Array.AsReadOnly(
        [
            ControlType.Window,
            ControlType.Pane,
        ]);

    internal static IReadOnlyList<ControlType> ViewerSurfaceControlTypes { get; } =
        ModalSurfaceControlTypes;

    internal static IReadOnlyList<ControlType> ExactAnchorControlTypes { get; } =
        Array.AsReadOnly(
        [
            ControlType.Text,
            ControlType.Edit,
            ControlType.Custom,
            ControlType.Group,
        ]);

    internal static IReadOnlyList<ControlType> ViewerExportControlTypes { get; } =
        Array.AsReadOnly(
        [
            ControlType.Button,
            ControlType.MenuItem,
            ControlType.Custom,
        ]);

    private static FrozenSet<string> ExactIncludedStatuses { get; } =
        PioneerRxTop500ReportRecipe.IncludedStatuses.ToFrozenSet(StringComparer.Ordinal);

    private static readonly Action<AutomationElement>[] MenuOpeners =
    [
        element =>
        {
            if (element.Patterns.ExpandCollapse.IsSupported)
                element.Patterns.ExpandCollapse.Pattern.Expand();
        },
        element =>
        {
            if (element.Patterns.Invoke.IsSupported)
                element.Patterns.Invoke.Pattern.Invoke();
        },
        element => element.Click(),
    ];
}
