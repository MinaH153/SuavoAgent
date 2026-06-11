using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PioneerRxSim;

/// <summary>
/// The simulated PioneerRx main window. Automation surface the workflow drives:
///   - a menu bar (ControlType.MenuBar by default; stock Menu under --variant wpf-menu)
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

    public MainWindow(SimOptions options)
    {
        _options = options;

        Title = "PioneerRx — Better Life Pharmacy #001  [SIMULATOR — synthetic data]";
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
        menu.Items.Add(new MenuItem { Header = "Reports" });
        menu.Items.Add(new MenuItem { Header = "Tools" });
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

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
            Text = "Workflow Queue\n\n(simulated shell — drive via Item → Rx Item)",
            Margin = new Thickness(24),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x46)),
        };
        root.Children.Add(body);

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
}
