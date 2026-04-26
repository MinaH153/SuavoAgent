using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using FlaUI.UIA3;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class PioneerRxUiaEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly bool _useUia3;
    private Application? _app;
    private AutomationBase? _automation;
    private Window? _mainWindow;
    private string _activeFramework = "uia2";

    public string? WindowTitle => _mainWindow?.Title;
    public Window? MainWindow => _mainWindow;
    public int ProcessId => _app?.ProcessId ?? -1;

    /// <summary>
    /// Which UIA framework actually attached. Useful for telemetry +
    /// dashboard surface so the operator can see whether UIA3 was used
    /// or fell back to UIA2.
    /// </summary>
    public string ActiveFramework => _activeFramework;

    /// <summary>
    /// AutomationBase reference for callers that need to subscribe to
    /// events via this exact instance (Subscribe path on UIA observers
    /// uses event handlers tied to a specific AutomationBase).
    /// </summary>
    public AutomationBase? Automation => _automation;

    public PioneerRxUiaEngine(ILogger logger, bool useUia3 = false)
    {
        _logger = logger;
        _useUia3 = useUia3;
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

            // Codex 2026-04-26: try UIA3 first if the operator opted in,
            // then fall back to UIA2 if the modern framework returns no
            // useful elements within the attach budget. PioneerRx is
            // WinForms — some custom controls may only show up in UIA2's
            // legacy interop layer. Auto-fallback ensures observation
            // still works at every pharmacy regardless of the flag.
            if (_useUia3 && TryAttachWith(new UIA3Automation(), "uia3", processes[0]))
            {
                return true;
            }

            // Drop the UIA3 instance if it failed; UIA2 is the production path.
            _automation?.Dispose();
            _automation = null;
            _mainWindow = null;

            return TryAttachWith(new UIA2Automation(), "uia2", processes[0]);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to attach to PioneerRx");
            return false;
        }
    }

    private bool TryAttachWith(AutomationBase candidate, string frameworkLabel, Process targetProcess)
    {
        try
        {
            _automation = candidate;
            _mainWindow = _app!.GetMainWindow(candidate, TimeSpan.FromSeconds(5));

            if (_mainWindow == null)
            {
                _logger.Warning("UIA framework {Framework}: could not get PioneerRx main window",
                    frameworkLabel);
                candidate.Dispose();
                _automation = null;
                return false;
            }

            // Smoke-check the tree — UIA3 sometimes attaches but exposes
            // an empty subtree on legacy WinForms apps. CheckHealth runs
            // the same menu-bar lookup the steady-state observers do, so
            // if it returns 0 menu items we know UIA3 isn't going to be
            // useful here and we should fall back. UIA2 attempt skips
            // this check (it IS the fallback — accept whatever it finds).
            if (frameworkLabel == "uia3")
            {
                var probe = CheckHealth();
                if (!probe.MenuBarFound || probe.MenuItems.Length == 0)
                {
                    _logger.Warning(
                        "UIA3: attached but menu bar empty (items={Count}, found={Found}). " +
                        "Falling back to UIA2 — likely WinForms control visibility issue.",
                        probe.MenuItems.Length, probe.MenuBarFound);
                    candidate.Dispose();
                    _automation = null;
                    _mainWindow = null;
                    return false;
                }
            }

            _activeFramework = frameworkLabel;
            _logger.Information(
                "Attached to PioneerRx PID {Pid} via {Framework}",
                targetProcess.Id, frameworkLabel);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "UIA framework {Framework}: attach attempt failed", frameworkLabel);
            try { candidate.Dispose(); } catch { /* ignore */ }
            _automation = null;
            _mainWindow = null;
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
