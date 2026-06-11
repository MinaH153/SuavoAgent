using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PioneerRxSim;

/// <summary>
/// The "Edit Rx Item" window — the exact automation surface PricingWorkflow drives:
///   - top-level Window titled "Edit Rx Item" (found via desktop FindFirstDescendant(ByName))
///   - Quick Search: ControlType.Edit with HelpText "Quick Search"; Enter executes the search
///   - item panel: Text elements carrying the item name (incl. "(Do Not Use)" markers) and the
///     NDC in hyphenated 5-4-2 form (VerifyLoadedNdc normalizes any shape)
///   - TabControl with a TabItem named "Pricing"
///   - a virtualized WPF DataGrid (ControlType.DataGrid) whose rows are DataItem children,
///     cells are Custom children with ValuePattern, headers are Header/HeaderItem with the
///     real column names — rows arrive in timed batches to exercise WaitForStableRows
///   - Escape closes the window (TryCloseEditWindow)
/// </summary>
public sealed class EditRxItemWindow : Window
{
    public sealed record SupplierRowVm(
        string Supplier,
        string? SupplierAutomationName,
        string ItemNumber,
        string PackageSize,
        string CostDisplay,
        string CostPerUnitDisplay,
        string Status,
        string LastPurchase);

    private readonly SimOptions _options;
    private readonly IReadOnlyDictionary<string, SimItem> _items;

    private readonly TextBox _quickSearch = new();
    private readonly TextBlock _itemName = new();
    private readonly TextBlock _itemNdc = new();
    private readonly TextBlock _searchMessage = new();
    private readonly TabControl _tabs = new();
    private readonly ObservableCollection<SupplierRowVm> _rows = new();

    private IReadOnlyList<SupplierRowVm>? _stagedRows;
    private bool _batchesStarted;
    private readonly List<DispatcherTimer> _timers = new();

    public EditRxItemWindow(SimOptions options)
    {
        _options = options;
        _items = SimCatalog.Items(options.Variant);

        Title = "Edit Rx Item";
        Width = 980;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Closed += (_, _) => StopTimers();

        Content = BuildLayout();

        // PioneerRx-style persistence: reopening Edit Rx Item shows the last viewed item.
        if (_options.PersistLastItem && MainWindow.PersistedNdc is { } persisted)
            LoadItem(persisted, fromPersistence: true);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // Quick Search row
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        searchRow.Children.Add(new TextBlock
        {
            Text = "Quick Search:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
        });

        _quickSearch.Width = 280;
        _quickSearch.Height = 26;
        _quickSearch.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHelpText(_quickSearch, "Quick Search");
        AutomationProperties.SetAutomationId(_quickSearch, "txtQuickSearch");
        _quickSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) BeginSearch(_quickSearch.Text);
        };
        searchRow.Children.Add(_quickSearch);

        _searchMessage.Margin = new Thickness(12, 0, 0, 0);
        _searchMessage.VerticalAlignment = VerticalAlignment.Center;
        _searchMessage.Foreground = Brushes.Firebrick;
        searchRow.Children.Add(_searchMessage);
        DockPanel.SetDock(searchRow, Dock.Top);
        root.Children.Add(searchRow);

        // Item identity panel — the Text elements VerifyLoadedNdc scans.
        // Tree order matters: the item NAME (where "(Do Not Use)" lives) precedes the NDC.
        var identity = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        _itemName.FontSize = 17;
        _itemName.FontWeight = FontWeights.Bold;
        _itemName.Foreground = new SolidColorBrush(Color.FromRgb(0x21, 0x26, 0x2E));
        identity.Children.Add(_itemName);

        var ndcRow = new StackPanel { Orientation = Orientation.Horizontal };
        ndcRow.Children.Add(new TextBlock { Text = "NDC:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
        ndcRow.Children.Add(_itemNdc);
        identity.Children.Add(ndcRow);
        DockPanel.SetDock(identity, Dock.Top);
        root.Children.Add(identity);

        // Tabs
        var generalTab = new TabItem
        {
            Header = "General",
            Content = new TextBlock { Text = "Item configuration (simulated)", Margin = new Thickness(12) },
        };
        var pricingTab = new TabItem { Header = "Pricing", Content = BuildPricingTab() };
        _tabs.Items.Add(generalTab);
        _tabs.Items.Add(pricingTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged bubbles (DataGrid row selection raises it too) — only react to
            // the TabControl's own tab switches.
            if (!ReferenceEquals(e.Source, _tabs)) return;
            if (ReferenceEquals(_tabs.SelectedItem, pricingTab))
                StartBatchesIfReady();
        };
        root.Children.Add(_tabs);

        return root;
    }

    private UIElement BuildPricingTab()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };

        var caption = new TextBlock
        {
            Text = "Supplier Catalog",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            // Virtualization ON (WPF default, made explicit): unrealized rows are the heart of
            // the UIA2-vs-virtualization question. Height bounds realization to ~8 rows.
            EnableRowVirtualization = true,
            EnableColumnVirtualization = false,
            MaxHeight = 248,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        AutomationProperties.SetAutomationId(grid, "grdSupplierCatalog");

        // Supplier column: narrow + ellipsis, with the DevExpress-style behavior of exposing the
        // TRUNCATED render text as the cell's UIA Name while ValuePattern carries the full value.
        var supplierCol = new DataGridTextColumn
        {
            Header = "Supplier",
            Binding = new Binding(nameof(SupplierRowVm.Supplier)),
            Width = new DataGridLength(150),
        };
        var supplierElementStyle = new Style(typeof(TextBlock));
        supplierElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        supplierCol.ElementStyle = supplierElementStyle;
        var supplierCellStyle = new Style(typeof(DataGridCell));
        supplierCellStyle.Setters.Add(new Setter(
            AutomationProperties.NameProperty,
            new Binding(nameof(SupplierRowVm.SupplierAutomationName)) { TargetNullValue = string.Empty }));
        supplierCol.CellStyle = supplierCellStyle;
        grid.Columns.Add(supplierCol);

        grid.Columns.Add(TextCol("Item #", nameof(SupplierRowVm.ItemNumber), 90));
        grid.Columns.Add(TextCol("Package Size", nameof(SupplierRowVm.PackageSize), 110));
        grid.Columns.Add(TextCol("Cost", nameof(SupplierRowVm.CostDisplay), 90));
        // The header PricingWorkflow.ResolvePricingColumns resolves by NAME — renamed under
        // the renamed-cost variant to prove the fail-closed path.
        grid.Columns.Add(TextCol(_options.CostPerUnitHeader, nameof(SupplierRowVm.CostPerUnitDisplay), 110));
        grid.Columns.Add(TextCol("Status", nameof(SupplierRowVm.Status), 110));
        grid.Columns.Add(TextCol("Last Purchase", nameof(SupplierRowVm.LastPurchase), 110));

        // Supplier-ascending sort: the user-toggleable sort PioneerRx allows. Guarantees the
        // cheapest row is NOT row 1 for the seeded data — "never trust row 1".
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.SortDescriptions.Add(new SortDescription(nameof(SupplierRowVm.Supplier), ListSortDirection.Ascending));
        grid.ItemsSource = view;

        panel.Children.Add(grid);
        return panel;
    }

    private static DataGridTextColumn TextCol(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = new DataGridLength(width),
    };

    // ── Search + item loading ────────────────────────────────────────────────

    private void BeginSearch(string rawQuery)
    {
        _searchMessage.Text = "Searching…";
        _searchMessage.Foreground = Brushes.DimGray;

        // Realistic PMS search latency, then resolve on the UI thread.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timers.Add(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ExecuteSearch(rawQuery);
        };
        timer.Start();
    }

    private void ExecuteSearch(string rawQuery)
    {
        var digits = new string(rawQuery.Where(char.IsDigit).ToArray());

        if (digits.Length == 11 && _items.TryGetValue(digits, out var item))
        {
            LoadItem(item.Ndc11, fromPersistence: false);
            _searchMessage.Text = "";

            if (_options.ClearSearchAfterLoad)
                _quickSearch.Clear();
            // else: the typed NDC stays visible in the Quick Search box — the adversarial
            // default that makes VerifyLoadedNdc's Edit-scan tautological (see SimOptions).
        }
        else
        {
            // IMPORTANT: this message must NOT echo the typed digits. If it did, the no-match
            // probe would have a second accidental NDC source in a Text element and the
            // tautology finding would no longer be attributable to the search box alone.
            _searchMessage.Text = "No matching items found.";
            _searchMessage.Foreground = Brushes.Firebrick;
            // The previously loaded item (if any) stays on screen — PioneerRx does not blank
            // the editor on a failed quick search. Stale-grid probe substrate.
        }
    }

    private void LoadItem(string ndc11, bool fromPersistence)
    {
        if (!_items.TryGetValue(ndc11, out var item)) return;

        _itemName.Text = item.DisplayName;
        _itemNdc.Text = FormatNdc542(item.Ndc11);
        if (!fromPersistence) MainWindow.PersistedNdc = item.Ndc11;

        // Stage the supplier rows; they materialize in timed batches once the Pricing tab is
        // (or becomes) selected — DevExpress-style lazy load for WaitForStableRows.
        StopTimers();
        _rows.Clear();
        _batchesStarted = false;
        _stagedRows = item.Suppliers.Select(s => new SupplierRowVm(
            s.Supplier,
            s.TruncatedAutomationName,
            s.ItemNumber,
            s.PackageSize,
            s.Cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            _options.FormatCost(s.CostPerUnit),
            s.Status,
            s.LastPurchase)).ToList();

        StartBatchesIfReady();
    }

    private void StartBatchesIfReady()
    {
        if (_batchesStarted || _stagedRows is null) return;
        if (_tabs.SelectedItem is not TabItem { Header: "Pricing" }) return;

        _batchesStarted = true;
        var (batch1Ms, batch2Ms) = _options.GridTiming;
        var staged = _stagedRows;
        var firstCount = batch2Ms > batch1Ms ? Math.Max(1, staged.Count / 2) : staged.Count;

        ScheduleBatch(batch1Ms, staged.Take(firstCount));
        if (firstCount < staged.Count)
            ScheduleBatch(batch2Ms, staged.Skip(firstCount));
    }

    private void ScheduleBatch(int delayMs, IEnumerable<SupplierRowVm> rows)
    {
        var batch = rows.ToList();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timers.Add(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var row in batch) _rows.Add(row);
        };
        timer.Start();
    }

    private void StopTimers()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
    }

    private static string FormatNdc542(string ndc11) =>
        $"{ndc11[..5]}-{ndc11[5..9]}-{ndc11[9..]}";
}
