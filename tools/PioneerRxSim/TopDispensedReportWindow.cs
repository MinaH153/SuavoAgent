using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SuavoAgent.Contracts.Pricing;

namespace PioneerRxSim;

/// <summary>
/// Embedded, PHI-free Rx Transaction Search surface. Only search filters live
/// here. Report selection, the Top-X parameter, viewing, and Excel export are
/// deliberately separate surfaces because that is the field PioneerRx flow.
/// </summary>
public sealed class TopDispensedReportSurface : UserControl
{
    private readonly TextBox _from = Input(PioneerRxTop500ReportSurface.CompletedFromId,
        PioneerRxTop500ReportSurface.CompletedFromHelp, "06/01/2025");
    private readonly TextBox _through = Input(PioneerRxTop500ReportSurface.CompletedThroughId,
        PioneerRxTop500ReportSurface.CompletedThroughHelp, "06/30/2025");
    private readonly ComboBox _drugClass = Choice(PioneerRxTop500ReportSurface.DrugClassId,
        PioneerRxTop500ReportSurface.DrugClassHelp, ["Rx", "OTC"], "OTC");
    private readonly ComboBox _brandGeneric = Choice(PioneerRxTop500ReportSurface.BrandGenericId,
        PioneerRxTop500ReportSurface.BrandGenericHelp, ["Brand", "Generic"], "Brand");
    private readonly ComboBox _deaSchedule = Choice(PioneerRxTop500ReportSurface.DeaScheduleId,
        PioneerRxTop500ReportSurface.DeaScheduleHelp,
        ["No Schedule", "Schedule II", "Schedule III-V"], "Schedule II");
    private readonly ComboBox _rxTransaction = Choice(PioneerRxTop500ReportSurface.RxTransactionId,
        PioneerRxTop500ReportSurface.RxTransactionHelp,
        ["Removed From Inventory", "Added To Inventory"], "Added To Inventory");
    private readonly List<CheckBox> _statuses = [];

    public TopDispensedReportSurface()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        Content = BuildLayout();
    }

    internal bool RecipeFiltersMatch()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var selected = _statuses.Where(status => status.IsChecked == true)
            .Select(status => status.Content?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        return _from.Text == PioneerRxTop500ReportRecipe.FormatDate(
                   PioneerRxTop500ReportRecipe.StartFor(today)) &&
               _through.Text == PioneerRxTop500ReportRecipe.FormatDate(today) &&
               _rxTransaction.SelectedItem?.ToString() == PioneerRxTop500ReportRecipe.RxTransaction &&
               _drugClass.SelectedItem?.ToString() == PioneerRxTop500ReportRecipe.DrugClass &&
               _brandGeneric.SelectedItem?.ToString() == PioneerRxTop500ReportRecipe.BrandGeneric &&
               _deaSchedule.SelectedItem?.ToString() == PioneerRxTop500ReportRecipe.DeaSchedule &&
               selected.SetEquals(PioneerRxTop500ReportRecipe.IncludedStatuses);
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        AutomationProperties.SetName(root, PioneerRxTop500ReportSurface.SurfaceHeader);
        var title = new TextBlock
        {
            Text = PioneerRxTop500ReportSurface.SurfaceHeader,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var tabs = new TabControl();
        var rx = Tab(PioneerRxTop500ReportSurface.RxTab,
            PioneerRxTop500ReportSurface.RxTabId, BuildRxTab());
        tabs.Items.Add(rx);
        tabs.Items.Add(Tab("Patient", "tabPatient", Inert("Patient search")));
        tabs.Items.Add(Tab("Prescriber", "tabPrescriber", Inert("Prescriber search")));
        tabs.Items.Add(Tab("Prescribed Item", "tabPrescribedItem", Inert("Prescribed item search")));
        tabs.Items.Add(Tab(PioneerRxTop500ReportSurface.DispensedItemTab,
            PioneerRxTop500ReportSurface.DispensedItemTabId, BuildDispensedItemTab()));
        tabs.Items.Add(Tab("Compound Batch", "tabCompoundBatch", Inert("Compound batch search")));
        tabs.Items.Add(Tab("Third Party", "tabThirdParty", Inert("Third-party search")));
        tabs.Items.Add(Tab("Results", "tabResults", Inert("Results")));
        tabs.SelectedItem = rx;
        root.Children.Add(tabs);
        return root;
    }

    private UIElement BuildRxTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Row("Completed On Between", _from, "and", _through));
        panel.Children.Add(Row("Rx Transaction", _rxTransaction));
        var statusPanel = new WrapPanel { Margin = new Thickness(8) };
        foreach (var status in PioneerRxTop500ReportRecipe.IncludedStatuses
                     .Concat(["Cancelled", "Reversed"]))
        {
            var checkbox = new CheckBox
            {
                Content = status,
                IsChecked = status is "Completed" or "Cancelled" or "Reversed",
                Width = 210,
                Margin = new Thickness(3),
            };
            _statuses.Add(checkbox);
            statusPanel.Children.Add(checkbox);
        }
        var statusGroup = new GroupBox
        {
            Header = PioneerRxTop500ReportSurface.StatusGroupName,
            Content = statusPanel,
            Margin = new Thickness(0, 8, 0, 8),
        };
        AutomationProperties.SetAutomationId(statusGroup,
            PioneerRxTop500ReportSurface.StatusGroupId);
        panel.Children.Add(statusGroup);
        return new ScrollViewer { Content = panel };
    }

    private UIElement BuildDispensedItemTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Row("Drug Class", _drugClass));
        panel.Children.Add(Row("Brand/Generic", _brandGeneric));
        panel.Children.Add(Row("DEA Schedule", _deaSchedule));
        panel.Children.Add(new TextBlock
        {
            Text = "Choose the report from the Reports toolbar after setting filters.",
            Margin = new Thickness(0, 16, 0, 0),
            Foreground = Brushes.DimGray,
        });
        return new ScrollViewer { Content = panel };
    }

    private static TabItem Tab(string name, string id, UIElement content)
    {
        var tab = new TabItem { Header = name, Content = content };
        AutomationProperties.SetAutomationId(tab, id);
        return tab;
    }

    private static UIElement Inert(string text) => new TextBlock
        { Text = text, Margin = new Thickness(12) };

    private static TextBox Input(string id, string help, string value)
    {
        var input = new TextBox { Text = value, Width = 175, Margin = new Thickness(4) };
        AutomationProperties.SetAutomationId(input, id);
        AutomationProperties.SetHelpText(input, help);
        return input;
    }

    private static ComboBox Choice(
        string id, string help, IReadOnlyList<string> choices, string selected)
    {
        var combo = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = selected,
            Width = 250,
            Margin = new Thickness(4),
        };
        AutomationProperties.SetAutomationId(combo, id);
        AutomationProperties.SetHelpText(combo, help);
        return combo;
    }

    private static UIElement Row(
        string label, UIElement input, string? middle = null, UIElement? second = null)
    {
        var row = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 185,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        row.Children.Add(input);
        if (middle is not null)
            row.Children.Add(new TextBlock
                { Text = middle, Margin = new Thickness(8, 0, 8, 0) });
        if (second is not null) row.Children.Add(second);
        return row;
    }
}

/// <summary>Exact modal opened after Reports -> Top X Most Dispensed.</summary>
public sealed class TopDispensedReportParametersWindow : Window
{
    private readonly Func<bool> _filtersMatch;
    private readonly Action _showViewer;
    private readonly TextBox _topCount = new()
    {
        Text = "500",
        Width = 80,
        Margin = new Thickness(8),
    };

    public TopDispensedReportParametersWindow(Func<bool> filtersMatch, Action showViewer)
    {
        _filtersMatch = filtersMatch;
        _showViewer = showViewer;
        Title = PioneerRxTop500ReportSurface.ParametersTitle;
        Width = 330;
        Height = 160;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, PioneerRxTop500ReportSurface.ParametersTitle);
        AutomationProperties.SetAutomationId(_topCount,
            PioneerRxTop500ReportSurface.TopCountId);
        AutomationProperties.SetHelpText(_topCount,
            PioneerRxTop500ReportSurface.TopCountHelp);
        Content = BuildLayout();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.F12) TryView();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Top X:",
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(_topCount);
        root.Children.Add(row);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var view = new Button
        {
            Content = PioneerRxTop500ReportSurface.ViewButtonName,
            Width = 110,
            Margin = new Thickness(4),
        };
        AutomationProperties.SetAutomationId(view,
            PioneerRxTop500ReportSurface.ViewButtonId);
        view.Click += (_, _) => TryView();
        buttons.Children.Add(view);
        buttons.Children.Add(new Button
        {
            Content = "Cancel - ESC",
            Width = 110,
            Margin = new Thickness(4),
        });
        root.Children.Add(buttons);
        return root;
    }

    private void TryView()
    {
        var expected = PioneerRxTop500ReportRecipe.TopCount.ToString(
            CultureInfo.InvariantCulture);
        if (!_filtersMatch() || !string.Equals(_topCount.Text, expected, StringComparison.Ordinal))
            return;
        _showViewer();
        Close();
    }
}

/// <summary>Exact report viewer boundary; Excel exists only on this surface.</summary>
public sealed class TopDispensedReportViewerWindow : Window
{
    private readonly Func<bool> _filtersMatch;
    private readonly SimVariant _variant;
    private readonly TextBlock _state = new();

    public TopDispensedReportViewerWindow(
        Func<bool> filtersMatch,
        SimVariant variant)
    {
        _filtersMatch = filtersMatch;
        _variant = variant;
        Title = PioneerRxTop500ReportSurface.ViewerTitle;
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, PioneerRxTop500ReportSurface.ViewerTitle);
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(8) };
        var toolbar = new ToolBar();
        toolbar.Items.Add(new Button { Content = "Print Immediately" });
        toolbar.Items.Add(new Button { Content = "Print..." });
        var excel = new Button { Content = PioneerRxTop500ReportSurface.ExcelButtonName };
        AutomationProperties.SetAutomationId(excel,
            PioneerRxTop500ReportSurface.ExcelButtonId);
        excel.Click += (_, _) => ExportReport();
        toolbar.Items.Add(excel);
        toolbar.Items.Add(new Button { Content = "Text" });
        toolbar.Items.Add(new Button { Content = "PDF" });
        toolbar.Items.Add(new Button { Content = "Design" });
        toolbar.Items.Add(new Button { Content = "Fax" });
        var page = new TextBlock
        {
            Text = PioneerRxTop500ReportSurface.ViewerFirstPage,
            Margin = new Thickness(24, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(page, PioneerRxTop500ReportSurface.ViewerFirstPage);
        toolbar.Items.Add(page);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        _state.Text = "The report has completed";
        _state.Margin = new Thickness(8);
        DockPanel.SetDock(_state, Dock.Bottom);
        root.Children.Add(_state);

        var report = new StackPanel { Margin = new Thickness(24) };
        var contentTitle = new TextBlock
        {
            Text = PioneerRxTop500ReportSurface.ViewerContentTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(
            contentTitle,
            PioneerRxTop500ReportSurface.ViewerContentTitle);
        report.Children.Add(contentTitle);
        report.Children.Add(new TextBlock
        {
            Text = "Synthetic Test Pharmacy\nDispensed Item Brand/Generic: Generic\n" +
                   "Dispensed Item DEA Schedule: No Schedule\nRx Transaction: Removed From Inventory",
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 16),
        });
        report.Children.Add(new TextBlock
        {
            Text = "Rank     Drug                         Strength       NDC          Total Dispensed",
            FontFamily = new FontFamily("Consolas"),
        });
        root.Children.Add(report);
        return root;
    }

    private void ExportReport()
    {
        if (!_filtersMatch()) return;
        if (_variant == SimVariant.Top500SaveAs)
        {
            new TopDispensedSaveAsWindow(WriteSelectedPath) { Owner = this }.Show();
            return;
        }
        if (_variant == SimVariant.Top500ForeignSaveAs)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--foreign-save-dialog");
            Process.Start(start)?.Dispose();
            return;
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloads);
        var path = SyntheticTop500XlsxWriter.Write(downloads, DateTimeOffset.Now);
        _state.Text = "Excel export complete.";
        AutomationProperties.SetHelpText(_state, Path.GetFileName(path));
    }

    private void WriteSelectedPath(string path)
    {
        var written = SyntheticTop500XlsxWriter.WriteToPath(path, DateTimeOffset.Now);
        _state.Text = "Excel export complete.";
        AutomationProperties.SetHelpText(_state, Path.GetFileName(written));
    }
}

/// <summary>Minimal same-process common-dialog-shaped Save As simulator.</summary>
public sealed class TopDispensedSaveAsWindow : Window
{
    private readonly Action<string> _save;
    private readonly TextBox _fileName = new() { Width = 390, Margin = new Thickness(8) };

    public TopDispensedSaveAsWindow(Action<string> save)
    {
        _save = save;
        Title = PioneerRxTop500ReportSurface.SaveAsTitle;
        Width = 560;
        Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, PioneerRxTop500ReportSurface.SaveAsTitle);
        AutomationProperties.SetAutomationId(
            _fileName,
            PioneerRxTop500ReportSurface.SaveAsFileNameId);
        AutomationProperties.SetName(
            _fileName,
            PioneerRxTop500ReportSurface.SaveAsFileNameHelp);
        AutomationProperties.SetHelpText(
            _fileName,
            PioneerRxTop500ReportSurface.SaveAsFileNameHelp);
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal };
        fileRow.Children.Add(new TextBlock
        {
            Text = PioneerRxTop500ReportSurface.SaveAsFileNameHelp,
            Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
        });
        fileRow.Children.Add(_fileName);
        root.Children.Add(fileRow);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var save = new Button
        {
            Content = PioneerRxTop500ReportSurface.SaveAsButtonName,
            Width = 90,
            Margin = new Thickness(4),
        };
        AutomationProperties.SetAutomationId(
            save,
            PioneerRxTop500ReportSurface.SaveAsButtonId);
        save.Click += (_, _) => TrySave();
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 90, Margin = new Thickness(4) });
        root.Children.Add(buttons);
        return root;
    }

    private void TrySave()
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(_fileName.Text); }
        catch { return; }
        var downloads = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), downloads,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(fullPath))
            return;

        _save(fullPath);
        Close();
    }
}
