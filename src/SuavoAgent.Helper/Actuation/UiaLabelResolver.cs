using System.Diagnostics;
using System.Linq;
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
        LogAvailableNames(candidates, processName, label);
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

    /// <summary>
    /// Resolve a label via UIA's OWN descendant search rather than a hand-rolled BFS. The previous
    /// bounded BFS (MaxVisited=2000) silently missed elements in deep WinUI trees (the Windows 11
    /// Calculator): it found some buttons but not others depending on traversal order, so resolution
    /// was FLAKY — observed live: "Seven" resolved one run, "Five" failed the next. UIA's native
    /// FindFirstDescendant walks the full tree efficiently with no arbitrary node cap.
    /// </summary>
    private static AutomationElement? FindByName(AutomationElement root, string label, MatchMode mode)
    {
        try
        {
            if (mode == MatchMode.Exact)
                return root.FindFirstDescendant(cf => cf.ByName(label));

            // contains_ci: UIA has no native substring match, so enumerate descendants (native, no
            // 2000-node cap) and filter. Bounded only by the app's real element count.
            return root.FindAllDescendants()
                .FirstOrDefault(e => SafeName(e) is { } n && n.Contains(label, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null; // UIA tree churned mid-walk — caller retries until the timeout.
        }
    }

    private static string? SafeName(AutomationElement e)
    {
        try { return e.Name; } catch { return null; }
    }

    /// <summary>
    /// Discovery aid: when a label can't be resolved, log the accessible names ACTUALLY present so the
    /// real control names are visible instead of guessed. Names are PHI-scrubbed (PhiPatternGuard) and
    /// this writes to the LOCAL log only — never cloud telemetry — so it is safe on a PMS box. Capped
    /// to keep the line bounded. This is the "name the field you blocked" principle applied to UIA.
    /// </summary>
    private void LogAvailableNames(IReadOnlyList<string> candidates, string processName, string label)
    {
        try
        {
            foreach (var candidate in candidates)
            {
                var procs = Process.GetProcessesByName(candidate);
                try
                {
                    foreach (var proc in procs)
                    {
                        if (proc.MainWindowHandle == IntPtr.Zero) continue;
                        var window = _automation!.FromHandle(proc.MainWindowHandle);
                        if (window is null) continue;
                        var names = window.FindAllDescendants()
                            .Select(SafeName)
                            .Where(n => !string.IsNullOrWhiteSpace(n)
                                && !PhiPatternGuard.ContainsPotentialPhi(n!, out _))
                            .Distinct()
                            .Take(80)
                            .ToArray();
                        _logger.Warning(
                            "UiaLabelResolver discovery: '{Label}' absent; {Count} PHI-safe named elements in '{Process}': [{Names}]",
                            label, names.Length, processName, string.Join(" | ", names));
                        return; // one window is enough
                    }
                }
                finally { foreach (var p in procs) p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaLabelResolver: discovery dump failed");
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
        GC.SuppressFinalize(this);
    }
}
