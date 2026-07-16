using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PioneerRxSim;

/// <summary>
/// The simulated PioneerRx main window. Automation surface the workflow drives:
///   - a menu bar for item maintenance
///   - an Actions / Tools / Search / Reports toolbar matching the field build
///   - MenuItem "Item" → MenuItem "Rx Item" → opens the "Edit Rx Item" window
/// Everything else is inert dressing so the window reads as a plausible PMS shell.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly SimOptions _options;

    /// <summary>Last successfully loaded NDC — models PioneerRx reopening the Edit Rx Item
    /// screen on the previously viewed item (--no-persist-last-item disables).</summary>
    internal static string? PersistedNdc;

    private EditRxItemWindow? _editWindow;
    private TopDispensedReportSurface? _top500Surface;
    private TopDispensedReportParametersWindow? _top500Parameters;
    private TopDispensedReportViewerWindow? _top500Viewer;
    private readonly ContentControl _bodyHost = new();

    public MainWindow(SimOptions options)
    {
        _options = options;

        Title = "PioneerRx — Synthetic Test Pharmacy #001  [SIMULATOR — synthetic data]";
        Width = 1180;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Same precedent as tests/SuavoAgent.UiaHarness: the workflow's menu interaction is a
        // REAL mouse click at screen coordinates — a console window overlapping the menu would
        // swallow it. Topmost keeps the rehearsal deterministic on a busy VM desktop.
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));

        var root = new DockPanel();

        // ── Menu bar ─────────────────────────────────────────────────────────
        // Default: PrxMenuBar (reports ControlType.MenuBar — what the workflow asserts).
        // wpf-menu variant: stock Menu (reports ControlType.Menu — the WPF-native truth).
        Menu menu = options.Variant == SimVariant.WpfMenu ? new Menu() : new PrxMenuBar();
        menu.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xEE));

        var itemMenu = new MenuItem { Header = "Item" };
        var rxItem = new MenuItem { Header = "Rx Item" };
        rxItem.Click += (_, _) => OpenEditRxItem();
        itemMenu.Items.Add(rxItem);
        itemMenu.Items.Add(new MenuItem { Header = "Inventory" });
        itemMenu.Items.Add(new MenuItem { Header = "Price Tables" });

        menu.Items.Add(new MenuItem { Header = "Patient" });
        menu.Items.Add(new MenuItem { Header = "Rx" });
        menu.Items.Add(itemMenu);
        menu.Items.Add(new MenuItem { Header = "Inventory" });
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        // The real PioneerRx shell presents Search as a toolbar dropdown, not
        // necessarily as a MenuBar/Menu descendant. A WPF Button + ContextMenu
        // deliberately exposes Search as ControlType.Button and its opened
        // Rx Binoculars entry as a popup MenuItem.
        var toolbar = new ToolBar
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)),
        };
        var findRx = new Button
        {
            Content = "Find Rx",
            Padding = new Thickness(10, 3, 10, 3),
        };
        findRx.Click += (_, _) => OpenTopDispensedReport();
        toolbar.Items.Add(findRx);
        toolbar.Items.Add(new Button { Content = "Actions", Padding = new Thickness(10, 3, 10, 3) });
        toolbar.Items.Add(new Button { Content = "Tools", Padding = new Thickness(10, 3, 10, 3) });
        var searchButton = new Button
        {
            Content = "Search",
            Padding = new Thickness(10, 3, 10, 3),
        };
        var searchPopup = new ContextMenu();
        var rxBinoculars = new MenuItem { Header = "Rx Binoculars" };
        rxBinoculars.Click += (_, _) => OpenTopDispensedReport();
        searchPopup.Items.Add(rxBinoculars);
        searchPopup.Items.Add(new MenuItem { Header = "Patient Search" });
        searchButton.Click += (_, _) =>
        {
            searchPopup.PlacementTarget = searchButton;
            searchPopup.IsOpen = true;
        };
        toolbar.Items.Add(searchButton);
        var reportsButton = new Button
        {
            Content = "Reports",
            Padding = new Thickness(10, 3, 10, 3),
        };
        var reportsPopup = new ContextMenu();
        var topDispensed = new MenuItem { Header = "Top X Most Dispensed" };
        topDispensed.Click += (_, _) => OpenTopDispensedParameters();
        reportsPopup.Items.Add(new MenuItem { Header = "Drug Log" });
        reportsPopup.Items.Add(new MenuItem { Header = "Patient Volume" });
        reportsPopup.Items.Add(topDispensed);
        reportsButton.Click += (_, _) =>
        {
            reportsPopup.PlacementTarget = reportsButton;
            reportsPopup.IsOpen = true;
        };
        toolbar.Items.Add(reportsButton);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // ── Footer: sim configuration banner (also human-verifiable on the VM) ──
        var footer = new TextBlock
        {
            Text = $"PioneerRxSim  •  variant={options.VariantFlag}  •  clearSearchAfterLoad={options.ClearSearchAfterLoad}  " +
                   $"•  persistLastItem={options.PersistLastItem}  •  gridBatches={options.GridTiming.Batch1Ms}ms/{options.GridTiming.Batch2Ms}ms",
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x78)),
            FontSize = 12,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── Inert body ───────────────────────────────────────────────────────
        var body = new TextBlock
        {
            Text = "Workflow Queue\n\n(simulated shell — drive via Item → Rx Item or Search → Rx Binoculars)",
            Margin = new Thickness(24),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x46)),
        };
        _bodyHost.Content = body;
        root.Children.Add(_bodyHost);

        Content = root;
    }

    private void OpenEditRxItem()
    {
        // PioneerRx behaves like a dialog here: one Edit Rx Item screen at a time.
        if (_editWindow is { IsLoaded: true })
        {
            _editWindow.Activate();
            return;
        }

        _editWindow = new EditRxItemWindow(_options) { Owner = this };
        _editWindow.Closed += (_, _) => _editWindow = null;
        _editWindow.Show();
    }

    private void OpenTopDispensedReport()
    {
        _top500Surface = new TopDispensedReportSurface();
        _bodyHost.Content = _top500Surface;
    }

    private void OpenTopDispensedParameters()
    {
        if (_top500Surface is null) return;
        if (_top500Parameters is { IsLoaded: true })
        {
            _top500Parameters.Activate();
            return;
        }

        _top500Parameters = new TopDispensedReportParametersWindow(
            _top500Surface.RecipeFiltersMatch,
            OpenTopDispensedViewer)
        {
            Owner = this,
        };
        _top500Parameters.Closed += (_, _) => _top500Parameters = null;
        _top500Parameters.Show();
    }

    private void OpenTopDispensedViewer()
    {
        if (_top500Surface is null) return;
        if (_top500Viewer is { IsLoaded: true })
        {
            _top500Viewer.Activate();
            return;
        }

        _top500Viewer = new TopDispensedReportViewerWindow(
            _top500Surface.RecipeFiltersMatch,
            _options.Variant)
        {
            Owner = this,
        };
        _top500Viewer.Closed += (_, _) => _top500Viewer = null;
        _top500Viewer.Show();
    }
}
