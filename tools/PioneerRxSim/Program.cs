using System.Windows;

namespace PioneerRxSim;

/// <summary>
/// Entry point. The process MUST be named PioneerPharmacy(.exe) — PioneerRxUiaEngine attaches
/// by process name. Run `PioneerPharmacy.exe --variant faithful` (see SimOptions for flags).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--foreign-save-dialog", StringComparer.Ordinal))
        {
            var dialogApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            return dialogApp.Run(new TopDispensedSaveAsWindow(_ => { })
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
            });
        }

        SimOptions options;
        try
        {
            options = SimOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "PioneerRxSim", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new MainWindow(options));
    }
}
