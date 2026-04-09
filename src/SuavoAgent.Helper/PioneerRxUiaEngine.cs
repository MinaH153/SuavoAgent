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

            _app = Application.Attach(processes[0]);
            _automation = new UIA2Automation();
            _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(5));

            if (_mainWindow == null)
            {
                _logger.Warning("Could not get PioneerRx main window");
                return false;
            }

            _logger.Information("Attached to PioneerRx PID {Pid}, window: {Title}",
                processes[0].Id, _mainWindow.Title);
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
