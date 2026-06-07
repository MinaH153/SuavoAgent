using System.Diagnostics;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Walks the UIA tree of a target process's top-level window and resolves
/// an accessible-name label to a clickable point in screen coordinates.
///
/// This is the read side of <c>actuation.click_by_label</c>. The actual
/// click goes through <see cref="SendInputDriver.ClickAtAsync"/> so the
/// kill switch / pause gate apply uniformly. Notably we do NOT call
/// <c>element.Click()</c> directly — that would bypass the gate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiaLabelResolver : IDisposable
{
    public enum MatchMode
    {
        Exact,
        ContainsCaseInsensitive,
    }

    public sealed record ResolvedTarget(int X, int Y, string ResolvedFromLabel, string ProcessName);

    private readonly ILogger _logger;
    private UIA2Automation? _automation;

    public UiaLabelResolver(ILogger logger)
    {
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<UiaLabelResolver>();
    }

    public ResolvedTarget? Resolve(string label, string processName, MatchMode mode, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(processName))
            return null;

        var deadline = DateTimeOffset.UtcNow + timeout;
        _automation ??= new UIA2Automation();

        // Search every candidate process name, not just the bare launcher name: Windows 11 packaged
        // apps (Calculator, etc.) are launched via a stub (calc.exe) but the window-owning process has
        // a different name (Calculator / CalculatorApp), so GetProcessesByName("calc") finds nothing.
        // PackagedAppAliases expands "calc.exe" -> [calc, Calculator, CalculatorApp]. (Same root cause
        // the SendInputDriver launch path hit — see WindowFocusManager.)
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
                        var resolved = TryResolveInProcess(proc, label, mode);
                        if (resolved is not null)
                        {
                            return resolved with { ProcessName = processName };
                        }
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }

            Thread.Sleep(150);
        }

        _logger.Warning("UiaLabelResolver: '{Label}' not found in '{Process}' within {TimeoutMs}ms",
            label, processName, (int)timeout.TotalMilliseconds);
        return null;
    }

    private ResolvedTarget? TryResolveInProcess(Process proc, string label, MatchMode mode)
    {
        try
        {
            if (proc.MainWindowHandle == IntPtr.Zero) return null;

            var auto = _automation!;
            var window = auto.FromHandle(proc.MainWindowHandle);
            if (window is null) return null;

            var element = FindByName(window, label, mode);
            if (element is null) return null;

            var rect = element.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;

            var cx = (int)(rect.Left + rect.Width / 2);
            var cy = (int)(rect.Top + rect.Height / 2);
            return new ResolvedTarget(cx, cy, label, proc.ProcessName);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaLabelResolver: resolve failed for pid={Pid}", proc.Id);
            return null;
        }
    }

    private AutomationElement? FindByName(AutomationElement root, string label, MatchMode mode)
    {
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var visited = 0;
        const int MaxVisited = 2000; // budget — keep traversal bounded.

        while (queue.Count > 0 && visited < MaxVisited)
        {
            visited++;
            var node = queue.Dequeue();
            string? name = null;
            try { name = node.Name; } catch { /* element gone */ }

            if (name is not null && Matches(name, label, mode))
            {
                return node;
            }

            AutomationElement[] children;
            try { children = node.FindAllChildren(); }
            catch { continue; }

            foreach (var c in children) queue.Enqueue(c);
        }
        return null;
    }

    private static bool Matches(string actual, string expected, MatchMode mode) =>
        mode switch
        {
            MatchMode.Exact => string.Equals(actual, expected, StringComparison.Ordinal),
            MatchMode.ContainsCaseInsensitive => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
        GC.SuppressFinalize(this);
    }
}
