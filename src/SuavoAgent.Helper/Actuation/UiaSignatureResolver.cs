using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Resolves a GREEN-tier structural signature (ControlType + AutomationId [+ ClassName]) to a
/// clickable screen point — the read side of <c>actuation.click_by_signature</c>, mirroring
/// <see cref="UiaLabelResolver"/> but matching on structural properties rather than accessible name
/// (so learned-template replay can ground without PHI names). The click itself still goes through
/// <see cref="SendInputDriver.ClickAtAsync"/>, so the kill switch / pause gate apply uniformly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiaSignatureResolver : IDisposable
{
    public sealed record ResolvedTarget(int X, int Y, string AutomationId, string ProcessName);

    private readonly ILogger _logger;
    private UIA2Automation? _automation;

    public UiaSignatureResolver(ILogger logger)
    {
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<UiaSignatureResolver>();
    }

    public ResolvedTarget? Resolve(string controlType, string automationId, string? className, string processName, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(controlType) || string.IsNullOrWhiteSpace(automationId) || string.IsNullOrWhiteSpace(processName))
            return null;

        var deadline = DateTimeOffset.UtcNow + timeout;
        _automation ??= new UIA2Automation();

        // Packaged-app aware (same root cause as UiaLabelResolver / WindowFocusManager): "calc.exe" ->
        // [calc, Calculator, CalculatorApp] so a launcher-stub process name still resolves the real window.
        var candidates = PackagedAppAliases.CandidateProcessNames(processName);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var candidate in candidates)
            {
                var procs = Process.GetProcessesByName(candidate);
                try
                {
                    foreach (var proc in procs)
                    {
                        var resolved = TryResolveInProcess(proc, controlType, automationId, className);
                        if (resolved is not null)
                            return resolved with { ProcessName = processName };
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }

            Thread.Sleep(150);
        }

        _logger.Warning("UiaSignatureResolver: ({Control}|{AutomationId}) not found in '{Process}' within {TimeoutMs}ms",
            controlType, automationId, processName, (int)timeout.TotalMilliseconds);
        return null;
    }

    private ResolvedTarget? TryResolveInProcess(Process proc, string controlType, string automationId, string? className)
    {
        try
        {
            var window = ResolveWindow(proc);
            if (window is null) return null;

            var element = FindBySignature(window, controlType, automationId, className);
            if (element is null) return null;

            var rect = element.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;

            var cx = (int)(rect.Left + rect.Width / 2);
            var cy = (int)(rect.Top + rect.Height / 2);
            return new ResolvedTarget(cx, cy, automationId, proc.ProcessName);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaSignatureResolver: resolve failed for pid={Pid}", proc.Id);
            return null;
        }
    }

    /// <summary>
    /// Resolve the app's top-level window — Win32 MainWindowHandle, falling back to the UIA desktop
    /// root by process id when it's 0 (packaged apps intermittently report 0). Mirrors UiaLabelResolver.
    /// </summary>
    private AutomationElement? ResolveWindow(Process proc)
    {
        var auto = _automation!;
        try
        {
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                var w = auto.FromHandle(proc.MainWindowHandle);
                if (w is not null) return w;
            }
        }
        catch { /* fall through */ }

        try { return auto.GetDesktop().FindFirstChild(cf => cf.ByProcessId(proc.Id)); }
        catch { return null; }
    }

    private static AutomationElement? FindBySignature(AutomationElement root, string controlType, string automationId, string? className)
    {
        try
        {
            // Native UIA search by AutomationId (efficient, no node cap), then apply the strict
            // ControlType/ClassName equality guard below. The previous bounded BFS (MaxVisited=2000)
            // silently missed elements in deep WinUI trees, making resolution flaky — UIA's own
            // descendant walk has no arbitrary cap.
            return root.FindAllDescendants(cf => cf.ByAutomationId(automationId))
                .FirstOrDefault(node => MatchesSignature(node, controlType, automationId, className));
        }
        catch
        {
            return null; // UIA tree churned mid-walk — caller retries until the timeout.
        }
    }

    private static bool MatchesSignature(AutomationElement node, string controlType, string automationId, string? className)
    {
        try
        {
            // All requested signature fields MUST be present AND equal — never match on AutomationId
            // alone when ControlType/ClassName are unsupported (Codex P2: that risks a wrong-element
            // click on an AutomationId collision with partial provider metadata).
            if (!node.Properties.AutomationId.IsSupported) return false;
            if (!string.Equals(node.Properties.AutomationId.ValueOrDefault, automationId, StringComparison.Ordinal)) return false;

            if (!node.Properties.ControlType.IsSupported) return false;
            if (!string.Equals(node.Properties.ControlType.ValueOrDefault.ToString(), controlType, StringComparison.Ordinal)) return false;

            if (className is not null)
            {
                if (!node.Properties.ClassName.IsSupported) return false;
                if (!string.Equals(node.Properties.ClassName.ValueOrDefault, className, StringComparison.Ordinal)) return false;
            }

            return true;
        }
        catch
        {
            return false; // element gone mid-walk
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
        GC.SuppressFinalize(this);
    }
}
