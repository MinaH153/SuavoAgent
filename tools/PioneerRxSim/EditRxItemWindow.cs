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
        string Linked,
        string InventoryGroup,
        string Supplier,
        string? SupplierAutomationName,
        string ItemNumber,
        string PackageSize,
        string CostDisplay,
        string CostPerUnitDisplay,
        string Status,
        string LastPurchase,
        bool Discontinued);

    public sealed record SearchResultRowVm(
        string Name,
        string Strength,
        string Ndc,
        string PackageSize);

    private readonly SimOptions _options;
    private readonly IReadOnlyDictionary<string, SimItem> _items;

    private readonly TextBox _quickSearch = new();
    private readonly TextBlock _itemName = new();
    private readonly TextBlock _itemNdc = new();
    private readonly TextBlock _searchMessage = new();
    private readonly DataGrid _searchResults = new();
    private readonly ObservableCollection<SearchResultRowVm> _searchResultRows = new();
    private readonly TabControl _tabs = new();
    private readonly ObservableCollection<SupplierRowVm> _rows = new();
    private readonly ComboBox _includeDiscontinued = new();
    private readonly ComboBox _inventoryGroup = new();

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
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_searchResults.Visibility == Visibility.Visible)
                CommitSelectedSearchResult();
            else
                BeginSearch(_quickSearch.Text);
        };
        searchRow.Children.Add(_quickSearch);

        _searchMessage.Margin = new Thickness(12, 0, 0, 0);
        _searchMessage.VerticalAlignment = VerticalAlignment.Center;
        _searchMessage.Foreground = Brushes.Firebrick;
        searchRow.Children.Add(_searchMessage);
        DockPanel.SetDock(searchRow, Dock.Top);
        root.Children.Add(searchRow);

        BuildQuickSearchResults();
        DockPanel.SetDock(_searchResults, Dock.Top);
        root.Children.Add(_searchResults);

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

        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };
        filters.Children.Add(new TextBlock
        {
            Text = "Include Discontinued:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        _includeDiscontinued.ItemsSource = new[] { "Yes", "No" };
        _includeDiscontinued.SelectedItem = "Yes";
        _includeDiscontinued.Width = 72;
        AutomationProperties.SetAutomationId(
            _includeDiscontinued,
            "cmbIncludeDiscontinued");
        AutomationProperties.SetHelpText(
            _includeDiscontinued,
            "Include Discontinued:");
        filters.Children.Add(_includeDiscontinued);
        filters.Children.Add(new TextBlock
        {
            Text = "Inventory Group:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 6, 0),
        });
        _inventoryGroup.ItemsSource = new[] { "All", "Rx", "340B" };
        _inventoryGroup.SelectedItem = "All";
        _inventoryGroup.Width = 82;
        AutomationProperties.SetAutomationId(_inventoryGroup, "cmbInventoryGroup");
        AutomationProperties.SetHelpText(_inventoryGroup, "Inventory Group:");
        filters.Children.Add(_inventoryGroup);
        DockPanel.SetDock(filters, Dock.Top);
        panel.Children.Add(filters);

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

        // Nadim's live grid exposes Linked as Yes/No text to UIA/OCR, not a
        // guessed boolean ordinal. Package-cost selection parses this exact cell.
        grid.Columns.Add(TextCol("Linked", nameof(SupplierRowVm.Linked), 70));
        grid.Columns.Add(TextCol(
            "Inventory Group",
            nameof(SupplierRowVm.InventoryGroup),
            110));

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
        grid.Columns.Add(TextCol("Shipping Size", nameof(SupplierRowVm.PackageSize), 110));
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
        view.Filter = value => value is SupplierRowVm row &&
            (string.Equals(
                 _includeDiscontinued.SelectedItem as string,
                 "Yes",
                 StringComparison.Ordinal) || !row.Discontinued) &&
            ((_inventoryGroup.SelectedItem as string) is not { } group ||
             group == "All" ||
             string.Equals(row.InventoryGroup, group, StringComparison.Ordinal));
        _includeDiscontinued.SelectionChanged += (_, _) => view.Refresh();
        _inventoryGroup.SelectionChanged += (_, _) => view.Refresh();
        grid.ItemsSource = view;

        panel.Children.Add(grid);
        return panel;
    }

    private void BuildQuickSearchResults()
    {
        _searchResults.AutoGenerateColumns = false;
        _searchResults.IsReadOnly = true;
        _searchResults.CanUserAddRows = false;
        _searchResults.CanUserDeleteRows = false;
        _searchResults.HeadersVisibility = DataGridHeadersVisibility.Column;
        _searchResults.SelectionMode = DataGridSelectionMode.Single;
        _searchResults.SelectionUnit = DataGridSelectionUnit.FullRow;
        _searchResults.MaxHeight = 150;
        _searchResults.Visibility = Visibility.Collapsed;
        _searchResults.ItemsSource = _searchResultRows;
        _searchResults.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Enter) return;
            eventArgs.Handled = true;
            CommitSelectedSearchResult();
        };
        AutomationProperties.SetAutomationId(_searchResults, "grdQuickSearchResults");
        _searchResults.Columns.Add(TextCol("Name", nameof(SearchResultRowVm.Name), 250));
        _searchResults.Columns.Add(TextCol("Strength", nameof(SearchResultRowVm.Strength), 120));
        _searchResults.Columns.Add(TextCol("NDC", nameof(SearchResultRowVm.Ndc), 130));
        _searchResults.Columns.Add(TextCol(
            "Package Size",
            nameof(SearchResultRowVm.PackageSize),
            110));
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
        _searchResults.Visibility = Visibility.Collapsed;
        _searchResultRows.Clear();
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
        var ndc11 = SimNdcNormalizer.TryNormalize(rawQuery);

        if (ndc11 is not null && _items.TryGetValue(ndc11, out var item))
        {
            // Enter #1 opens a realistic multi-row chooser; the active row is
            // highlighted while a same-NDC Do-Not-Use duplicate remains visible.
            _searchResultRows.Add(new SearchResultRowVm(
                item.DisplayName,
                "",
                FormatNdc542(item.Ndc11),
                "1"));
            _searchResultRows.Add(new SearchResultRowVm(
                item.DisplayName.ToUpperInvariant() + "@",
                "",
                FormatNdc542(item.Ndc11),
                "1"));
            _searchResultRows.Add(new SearchResultRowVm(
                item.DisplayName + " (Do Not Use)",
                "",
                FormatNdc542(item.Ndc11),
                "1"));
            _searchResultRows.Add(new SearchResultRowVm(
                item.DisplayName + " - alternate source",
                "",
                FormatNdc542(item.Ndc11),
                "1"));
            _searchResults.Visibility = Visibility.Visible;
            _searchResults.SelectedIndex = 0;
            _searchResults.ScrollIntoView(_searchResults.SelectedItem);
            _searchResults.Focus();
            _searchMessage.Text = "";
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

    private void CommitSelectedSearchResult()
    {
        if (_searchResults.SelectedItem is not SearchResultRowVm selected)
            return;
        var ndc = SimNdcNormalizer.TryNormalize(selected.Ndc);
        if (ndc is null) return;
        _searchResults.Visibility = Visibility.Collapsed;
        _searchResultRows.Clear();
        LoadItem(ndc, fromPersistence: false);
        if (_options.ClearSearchAfterLoad)
            _quickSearch.Clear();
        else
            _quickSearch.Focus();
    }

    private void LoadItem(string ndc11, bool fromPersistence)
    {
        if (!_items.TryGetValue(ndc11, out var item)) return;

        _itemName.Text = item.DisplayName;
        _itemNdc.Text = FormatNdc542(item.Ndc11);
        Title = $"Edit Rx Item - {item.DisplayName}";
        if (!fromPersistence) MainWindow.PersistedNdc = item.Ndc11;

        // Stage the supplier rows; they materialize in timed batches once the Pricing tab is
        // (or becomes) selected — DevExpress-style lazy load for WaitForStableRows.
        StopTimers();
        _rows.Clear();
        _batchesStarted = false;
        _stagedRows = item.Suppliers.Select(s => new SupplierRowVm(
            s.Linked ? "Yes" : "No",
            s.InventoryGroup,
            s.Supplier,
            s.TruncatedAutomationName,
            s.ItemNumber,
            s.PackageSize,
            s.Cost.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            _options.FormatCost(s.CostPerUnit),
            s.Status,
            s.LastPurchase,
            s.Discontinued || s.Status.Contains(
                "Discontinued",
                StringComparison.OrdinalIgnoreCase))).ToList();

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
