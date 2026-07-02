using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class PioneerRxUiaEngine : IDisposable
{
    private readonly ILogger _logger;
    private Application? _app;
    private UIA2Automation? _automation;
    private Window? _mainWindow;

    public string? WindowTitle => _mainWindow?.Title;
    public Window? MainWindow => _mainWindow;
    public int ProcessId => _app?.ProcessId ?? -1;

    public PioneerRxUiaEngine(ILogger logger)
    {
        _logger = logger;
    }

    public bool TryAttach()
    {
        try
        {
            var processes = Process.GetProcessesByName("PioneerPharmacy");
            if (processes.Length == 0)
            {
                _logger.Warning("PioneerPharmacy.exe not found");
                return false;
            }

            // Pick the first LIVE process that owns a top-level window. GetProcessesByName can return a
            // process that has already exited or is otherwise inaccessible — a transient self-extract
            // stub, an updater/splash helper, or a crashed instance. Accessing .Id/.MainWindowHandle on
            // such an entry throws "No process is associated with this object", and blindly using
            // processes[0] then fails the whole attach (and the workflow reports "main window not
            // available" for every NDC). Skip the dead/inaccessible/windowless ones and attach to a real
            // instance — robust on the box where PioneerRx spawns auxiliary processes.
            Process? target = null;
            foreach (var p in processes)
            {
                try
                {
                    if (p.HasExited) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue; // no top-level window (yet)
                    target = p;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Debug("Skipping inaccessible PioneerPharmacy process ({Type})", ex.GetType().Name);
                }
            }
            if (target is null)
            {
                _logger.Warning(
                    "PioneerPharmacy running ({Count} process(es)) but none is a live, windowed, accessible instance",
                    processes.Length);
                return false;
            }

            _app = Application.Attach(target);
            _automation = new UIA2Automation();
            _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(5));

            if (_mainWindow == null)
            {
                _logger.Warning("Could not get PioneerRx main window");
                return false;
            }

            _logger.Information("Attached to PioneerRx PID {Pid}", target.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to attach to PioneerRx");
            return false;
        }
    }

    public (bool WindowFound, bool MenuBarFound, string[] MenuItems) CheckHealth()
    {
        if (_mainWindow == null || _automation == null)
            return (false, false, Array.Empty<string>());

        try
        {
            var cf = _automation.ConditionFactory;
            var menuBar = _mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.MenuBar));

            if (menuBar == null)
                return (true, false, Array.Empty<string>());

            var items = menuBar.FindAllChildren(cf.ByControlType(ControlType.MenuItem))
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();

            return (true, true, items);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Health check error");
            return (true, false, Array.Empty<string>());
        }
    }

    public AutomationElement? FindElement(ControlType type, string name)
    {
        if (_mainWindow == null || _automation == null) return null;

        try
        {
            var cf = _automation.ConditionFactory;
            return _mainWindow.FindFirstDescendant(
                new AndCondition(cf.ByControlType(type), cf.ByName(name)));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "FindElement failed: {Type} {Name}", type, name);
            return null;
        }
    }

    public string? ReadElementValue(string name)
    {
        if (_mainWindow == null || _automation == null) return null;

        try
        {
            var cf = _automation.ConditionFactory;
            var element = _mainWindow.FindFirstDescendant(cf.ByName(name));
            if (element == null) return null;

            var patterns = element.GetSupportedPatterns();
            if (patterns.Any(p => p.ToString()!.Contains("Value")))
            {
                var valuePattern = element.AsTextBox();
                return valuePattern?.Text;
            }

            return element.Name;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ReadElementValue failed: {Name}", name);
            return null;
        }
    }

    public bool ClickElement(string name)
    {
        if (_mainWindow == null || _automation == null) return false;

        try
        {
            var cf = _automation.ConditionFactory;
            var element = _mainWindow.FindFirstDescendant(cf.ByName(name));
            if (element == null)
            {
                _logger.Debug("ClickElement: {Name} not found", name);
                return false;
            }

            var button = element.AsButton();
            button?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ClickElement failed: {Name}", name);
            return false;
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _app?.Dispose();
    }
}
