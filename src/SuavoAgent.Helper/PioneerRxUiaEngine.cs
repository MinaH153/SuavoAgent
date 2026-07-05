using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
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

            // Bind to the first LIVE process that yields a UIA main window. GetProcessesByName can return
            // an exited/inaccessible entry (a transient self-extract stub, an updater/splash helper, or a
            // crashed instance) — skip those.
            //
            // Resolve the window via the UIA TREE (desktop root → ByProcessId), NOT FlaUI's
            // Application.GetMainWindow. GetMainWindow reads Process.MainWindowHandle (a Win32/EnumWindows
            // path); in the Helper's launch context — CreateProcessAsUser with a SecurityImpersonation
            // primary token (SessionWatcher/NativeProcess.LaunchInSession) — that read throws
            // InvalidOperationException on every attempt, so attach NEVER succeeded on the field box
            // (Queen, 2026-07-05: a plain interactive process reads the same sim's MainWindowHandle fine +
            // finds it via UIA; the service-launched Helper cannot use MainWindowHandle). The Helper's own
            // actuation already resolves this exact window with ByProcessId off the desktop root
            // (UiaLabelResolver / UiaSignatureResolver) and that path works from this context — attach now
            // uses the same one. Retry covers a WPF app still painting its first window.
            foreach (var p in processes)
            {
                UIA2Automation? automation = null;
                try
                {
                    if (p.HasExited) continue;

                    automation = new UIA2Automation();
                    var desktop = automation.GetDesktop();
                    var window = Retry.WhileNull(
                        () => desktop.FindFirstChild(cf => cf.ByProcessId(p.Id))?.AsWindow(),
                        timeout: TimeSpan.FromSeconds(3),
                        interval: TimeSpan.FromMilliseconds(250)).Result;
                    if (window != null)
                    {
                        _app = Application.Attach(p); // kept for ProcessId + lifecycle; Attach() does not touch MainWindowHandle
                        _automation = automation;
                        _mainWindow = window;
                        _logger.Information("Attached to PioneerRx PID {Pid}", p.Id);
                        return true;
                    }

                    // This instance has no UIA window (yet) — release and try the next.
                    automation.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug("Skipping PioneerPharmacy process ({Type})", ex.GetType().Name);
                    automation?.Dispose();
                }
            }

            _logger.Warning(
                "PioneerPharmacy running ({Count} process(es)) but none yielded a UIA main window",
                processes.Length);
            return false;
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
