using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper;

public sealed class PioneerRxUiaEngine : IDisposable
{
    private readonly ILogger _logger;
    private Application? _app;
    private UIA2Automation? _automation;
    private Window? _mainWindow;
    private readonly PioneerRxProcessTrustVerifier _processTrust;

    public Window? MainWindow => _mainWindow;
    public int ProcessId => _app?.ProcessId ?? -1;

    public PioneerRxUiaEngine(
        ILogger logger,
        PioneerRxProcessTrustVerifier? processTrust = null)
    {
        _logger = logger;
        _processTrust = processTrust ?? new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved"));
    }

    public bool TryAttach()
    {
        try
        {
            if (!_processTrust.IsApproved)
            {
                _logger.Warning(
                    "PioneerRx attach refused: local process approval is unavailable ({Code})",
                    _processTrust.ApprovalCode);
                return false;
            }

            var approvedName = SuavoAgent.Contracts.Ipc.ProtectedDesktopProcessClassifier
                .CanonicalProcessStem(_processTrust.ApprovedProcessName);
            var processes = Process.GetProcessesByName(approvedName);
            if (processes.Length == 0)
            {
                _logger.Warning("Approved PioneerRx process was not running");
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
                    var trust = _processTrust.VerifyResolvedProcess(p.Id);
                    if (!trust.Trusted)
                    {
                        _logger.Warning(
                            "Skipping PioneerRx process whose approved identity did not verify ({Code})",
                            trust.Code);
                        continue;
                    }

                    automation = new UIA2Automation();
                    var desktop = automation.GetDesktop();
                    var window = Retry.WhileNull(
                        () => desktop.FindFirstChild(cf => cf.ByProcessId(p.Id))?.AsWindow(),
                        timeout: TimeSpan.FromSeconds(3),
                        interval: TimeSpan.FromMilliseconds(250)).Result;
                    if (window != null)
                    {
                        // Revalidate after the UIA lookup so PID exit/reuse or an
                        // image-path race cannot cross the attach boundary.
                        trust = _processTrust.VerifyResolvedProcess(p.Id);
                        if (!trust.Trusted)
                        {
                            _logger.Warning(
                                "PioneerRx identity changed during attach ({Code})",
                                trust.Code);
                            automation.Dispose();
                            continue;
                        }
                        _app = Application.Attach(p); // kept for ProcessId + lifecycle; Attach() does not touch MainWindowHandle
                        _automation = automation;
                        _mainWindow = window;
                        _logger.Information("Attached to an approved PioneerRx process");
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
        catch (Exception)
        {
            _logger.Error("Failed to attach to the approved PioneerRx process");
            return false;
        }
    }

    public PioneerRxProcessTrustVerifier.Verdict VerifyAttachedProcessIdentity() =>
        ProcessId > 0
            ? _processTrust.VerifyResolvedProcess(ProcessId)
            : PioneerRxProcessTrustVerifier.Verdict.Deny("pioneerrx_not_attached");

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

            return (true, true, Array.Empty<string>());
        }
        catch (Exception)
        {
            _logger.Debug("PioneerRx health check failed locally");
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
        catch (Exception)
        {
            _logger.Debug("PioneerRx structural element lookup failed locally");
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
        catch (Exception)
        {
            _logger.Debug("PioneerRx element value read failed locally");
            return null;
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _app?.Dispose();
    }
}
