using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    private const int MaxTypeAttempts = 2;
    private static readonly string[] QuickSearchRequiredHeaders =
        ["Name", "Strength", "NDC", "Package Size"];

    /// <summary>
    /// PioneerRx requires two distinct Enter presses: the first opens the
    /// result chooser; the second accepts its highlighted row. The second
    /// press is never sent until the highlighted row proves the exact NDC and
    /// contains no Do-Not-Use marker.
    /// </summary>
    private bool SearchByNdc(
        Window editWindow,
        ConditionFactory conditionFactory,
        string ndc,
        string costBasis,
        SelectorResolver resolver,
        out AutomationElement? resolvedSearchBox)
    {
        resolvedSearchBox = null;
        try
        {
            var deadline = DateTime.UtcNow + ElementTimeout;
            AutomationElement? searchBox = null;
            while (DateTime.UtcNow < deadline)
            {
                EnsureLiveActuation();
                var builtin = new AndCondition(
                    conditionFactory.ByControlType(ControlType.Edit),
                    conditionFactory.ByHelpText(QuickSearchHint));
                var (box, resolution) = resolver.FindFirst(
                    editWindow,
                    conditionFactory,
                    SelectorStepId.QuickSearchField,
                    builtin);
                searchBox = box;
                if (searchBox is not null)
                    LogIfLearned(SelectorStepId.QuickSearchField, resolution);
                if (searchBox is not null) break;
                Thread.Sleep(200);
            }

            if (searchBox is null) return false;
            resolvedSearchBox = searchBox;
            PrepareVisibleAction(
                searchBox,
                "Searching",
                costBasis == PricingApprovalContract.PackageCostBasis
                    ? "package Cost"
                    : "Cost Per Unit");

            for (var attempt = 1; attempt <= MaxTypeAttempts; attempt++)
            {
                ExecuteLiveMutation(searchBox.Focus);
                Thread.Sleep(100);
                ExecuteLiveMutation(() => Keyboard.TypeSimultaneously(
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A));
                Thread.Sleep(50);
                ExecuteLiveMutation(() => Keyboard.Type(ndc));
                Thread.Sleep(150);

                if (!SearchBoxContainsNdc(searchBox, ndc))
                {
                    _logger.Warning(
                        "PricingWorkflow: identifier search attempt {Attempt}/{Max} did not verify; retrying",
                        attempt,
                        MaxTypeAttempts);
                    Thread.Sleep(300);
                    continue;
                }

                // Enter #1: open the result chooser. It is not item selection.
                ExecuteLiveMutation(() =>
                    Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN));
                if (!WaitForExactHighlightedSearchResult(
                        editWindow,
                        conditionFactory,
                        ndc))
                {
                    _logger.Warning(
                        "PricingWorkflow: Quick Search chooser did not expose one exact eligible highlighted row");
                    return false;
                }

                // Enter #2: accept only the verified highlighted row.
                ExecuteLiveMutation(() =>
                    Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN));
                if (!WaitForQuickSearchChooserClosed(editWindow, conditionFactory))
                {
                    _logger.Warning(
                        "PricingWorkflow: Quick Search chooser remained open after selection");
                    return false;
                }

                return true;
            }

            _logger.Warning(
                "PricingWorkflow: identifier search stopped after {Max} unverified attempts",
                MaxTypeAttempts);
            return false;
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: identifier search failed locally");
            return false;
        }
    }

    private bool WaitForExactHighlightedSearchResult(
        Window editWindow,
        ConditionFactory conditionFactory,
        string ndc)
    {
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            EnsureLiveActuation();
            var grid = FindQuickSearchResultGrid(editWindow, conditionFactory);
            if (grid is not null &&
                TryReadHighlightedSearchRow(
                    grid,
                    conditionFactory,
                    out var selectedCells))
                return QuickSearchSelectionMatches(ndc, selectedCells);
            Thread.Sleep(200);
        }

        return false;
    }

    private bool WaitForQuickSearchChooserClosed(
        Window editWindow,
        ConditionFactory conditionFactory)
    {
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            EnsureLiveActuation();
            if (FindQuickSearchResultGrid(editWindow, conditionFactory) is null)
                return true;
            Thread.Sleep(200);
        }

        return false;
    }

    private static AutomationElement? FindQuickSearchResultGrid(
        AutomationElement root,
        ConditionFactory conditionFactory)
    {
        var grids = root.FindAllDescendants(
                conditionFactory.ByControlType(ControlType.Table))
            .Concat(root.FindAllDescendants(
                conditionFactory.ByControlType(ControlType.DataGrid)));
        foreach (var grid in grids)
        {
            if (TryResolveQuickSearchColumns(
                    grid,
                    conditionFactory,
                    out _))
                return grid;
        }

        return null;
    }

    private static bool TryReadHighlightedSearchRow(
        AutomationElement grid,
        ConditionFactory conditionFactory,
        out IReadOnlyList<string> selectedCells)
    {
        selectedCells = Array.Empty<string>();
        if (!TryResolveQuickSearchColumns(
                grid,
                conditionFactory,
                out var columns))
            return false;

        var rows = grid.FindAllChildren(
            conditionFactory.ByControlType(ControlType.DataItem));
        var selected = rows.Where(IsSelectedSearchRow).ToArray();
        if (selected.Length != 1) return false;

        var cells = UiaGridReader.RowCells(selected[0], conditionFactory);
        if (cells.Length <= columns.Values.Max()) return false;
        selectedCells = columns
            .OrderBy(pair => pair.Value)
            .Select(pair => UiaGridReader.GetCellText(cells[pair.Value]))
            .ToArray();
        return true;
    }

    private static bool IsSelectedSearchRow(AutomationElement row)
    {
        try
        {
            return row.Patterns.SelectionItem.IsSupported &&
                   row.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveQuickSearchColumns(
        AutomationElement grid,
        ConditionFactory conditionFactory,
        out Dictionary<string, int> columns)
    {
        columns = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var header = grid.FindFirstDescendant(
                conditionFactory.ByControlType(ControlType.Header));
            var cells = header?.FindAllDescendants(
                conditionFactory.ByControlType(ControlType.HeaderItem));
            if (cells is not { Length: > 0 }) return false;
            for (var index = 0; index < cells.Length; index++)
            {
                var name = cells[index].Name?.Trim() ?? "";
                if (QuickSearchRequiredHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    columns.TryAdd(name.ToUpperInvariant(), index);
            }

            foreach (var headerName in QuickSearchRequiredHeaders)
            {
                if (!columns.ContainsKey(headerName.ToUpperInvariant()))
                    return false;
            }

            return true;
        }
        catch
        {
            columns.Clear();
            return false;
        }
    }

    internal static bool QuickSearchSelectionMatches(
        string ndc,
        IReadOnlyList<string> selectedCells)
    {
        var expected = NdcNormalizer.TryNormalize(ndc);
        if (expected is null || selectedCells.Count == 0 ||
            selectedCells.Any(PricingGridReader.LooksLikeDoNotUse))
            return false;
        var exactMatches = selectedCells.Count(value =>
            NdcNormalizer.TryNormalize(value.Trim()) == expected);
        return exactMatches == 1;
    }

    private static bool SearchBoxContainsNdc(AutomationElement searchBox, string ndc)
    {
        try
        {
            var text = searchBox.AsTextBox()?.Text ?? searchBox.Name ?? "";
            if (string.IsNullOrEmpty(text)) return false;
            var typedDigits = new string(ndc.Where(char.IsDigit).ToArray());
            var observedDigits = new string(text.Where(char.IsDigit).ToArray());
            return observedDigits.Contains(typedDigits, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool VerifyLoadedNdc(
        Window editWindow,
        ConditionFactory conditionFactory,
        string ndc,
        AutomationElement? searchBox)
    {
        if (NdcNormalizer.TryNormalize(ndc) is null)
        {
            _logger.Warning("PricingWorkflow: requested identifier could not be normalized");
            return false;
        }

        var searchBoxRuntimeId = UiaGridReader.TryGetRuntimeId(searchBox);
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            EnsureLiveActuation();
            try
            {
                var chooserVisible = FindQuickSearchResultGrid(
                    editWindow,
                    conditionFactory) is not null;
                var texts = editWindow
                    .FindAllDescendants(conditionFactory.ByControlType(ControlType.Edit))
                    .Concat(editWindow.FindAllDescendants(
                        conditionFactory.ByControlType(ControlType.Text)))
                    .Where(element => !IsSearchBox(element, searchBoxRuntimeId))
                    .Select(SafeText)
                    .Where(value => !string.IsNullOrEmpty(value))
                    .ToArray();
                if (LoadedItemIdentityMatches(ndc, texts, chooserVisible))
                    return true;
                if (!chooserVisible && texts.Any(PricingGridReader.LooksLikeDoNotUse))
                {
                    _logger.Warning(
                        "PricingWorkflow: loaded item is marked Do Not Use; refusing to price");
                    return false;
                }
            }
            catch
            {
                // UIA trees can be transient while the selected item materializes.
            }

            Thread.Sleep(300);
        }

        _logger.Warning(
            "PricingWorkflow: loaded item did not verify within {Timeout}s",
            ElementTimeout.TotalSeconds);
        return false;
    }

    internal static bool LoadedItemIdentityMatches(
        string ndc,
        IReadOnlyList<string> candidateTexts,
        bool chooserVisible)
    {
        var expected = NdcNormalizer.TryNormalize(ndc);
        if (chooserVisible || expected is null ||
            candidateTexts.Any(PricingGridReader.LooksLikeDoNotUse))
            return false;
        foreach (var raw in candidateTexts)
        {
            if (NdcNormalizer.TryNormalize(raw.Trim()) == expected)
                return true;
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Contains(expected, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string SafeText(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern.Value;
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch
        {
            // Fall back to the UIA Name for plain Text/Label controls.
        }

        return element.Name ?? "";
    }

    private static bool IsSearchBox(
        AutomationElement element,
        int[]? searchBoxRuntimeId)
    {
        if (searchBoxRuntimeId is { Length: > 0 })
        {
            var runtimeId = UiaGridReader.TryGetRuntimeId(element);
            if (runtimeId is not null)
                return runtimeId.SequenceEqual(searchBoxRuntimeId);
        }

        return IsQuickSearchField(element);
    }

    private static bool IsQuickSearchField(AutomationElement element)
    {
        try
        {
            return string.Equals(
                element.HelpText,
                QuickSearchHint,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
