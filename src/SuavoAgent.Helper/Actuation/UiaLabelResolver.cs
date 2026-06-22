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

    // Pid (QA wave2 agentic): the resolved process, so the click path can re-assert this process still
    // owns the foreground at click time (TOCTOU guard). Defaulted so older constructions stay valid.
    public sealed record ResolvedTarget(int X, int Y, string ResolvedFromLabel, string ProcessName, int Pid = 0);

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
            var window = ResolveWindow(proc);
            if (window is null) return null;

            var element = FindByName(window, label, mode);
            if (element is null) return null;

            var rect = element.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;

            var cx = (int)(rect.Left + rect.Width / 2);
            var cy = (int)(rect.Top + rect.Height / 2);
            return new ResolvedTarget(cx, cy, label, proc.ProcessName, proc.Id);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaLabelResolver: resolve failed for pid={Pid}", proc.Id);
            return null;
        }
    }

    /// <summary>
    /// Resolve the app's top-level window. Prefer the Win32 MainWindowHandle, but fall back to the UIA
    /// desktop root filtered by process id when it's 0 — packaged apps (the Windows 11 Calculator)
    /// intermittently report MainWindowHandle==0 even with the window up, which made resolution FLAKY
    /// (same label succeeded one run, failed the next). The desktop-root path doesn't depend on the
    /// Win32 handle.
    /// </summary>
    private AutomationElement? ResolveWindow(Process proc)
    {
        var auto = _automation!;
        // 1) Win32 MainWindowHandle — classic Win32/WPF apps, and sometimes packaged ones.
        try
        {
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                var w = auto.FromHandle(proc.MainWindowHandle);
                if (w is not null) return w;
            }
        }
        catch { /* fall through to the desktop-root search */ }

        try
        {
            var desktop = auto.GetDesktop();
            // 2) A top-level window owned DIRECTLY by the process (self-hosted WinUI/Win32) — covers the
            //    intermittent MainWindowHandle==0 case.
            var direct = desktop.FindFirstChild(cf => cf.ByProcessId(proc.Id));
            if (direct is not null) return direct;

            // 3) ApplicationFrameHost-hosted UWP (e.g. the Windows 11 Calculator): the FRAME window is
            //    owned by ApplicationFrameHost.exe, NOT the app, so (1)/(2) miss it entirely — the root
            //    cause of the intermittent "label not found". Find the desktop window whose hosted child
            //    (the app's CoreWindow) belongs to the app process; its UIA subtree spans the app content.
            return desktop.FindAllChildren()
                .FirstOrDefault(w =>
                {
                    try { return w.FindFirstChild(cf => cf.ByProcessId(proc.Id)) is not null; }
                    catch { return false; }
                });
        }
        catch { return null; }
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
    /// Read the VALUE of the currently FOCUSED UIA element — the read-back used to VERIFY a type
    /// actually landed. Reads ONLY the field value via ValuePattern (classic edits) or TextPattern
    /// (rich-edit / document controls like the Win11 Notepad). Deliberately does NOT fall back to the
    /// accessible Name: Name is a LABEL ("Text editor"), not the typed value, and trusting it would
    /// cause a FALSE verification mismatch. Returns null when no value is readable — the caller treats
    /// null as "unverified" (type allowed), never as a mismatch, so verification never false-fails.
    /// </summary>
    public string? ReadFocusedElementValue()
    {
        try
        {
            _automation ??= new UIA2Automation();
            var focused = _automation.FocusedElement();
            if (focused is null) return null;

            // ValuePattern — classic edit / text-box controls (Win32, WinForms, WPF TextBox).
            try
            {
                var v = focused.Patterns.Value.PatternOrDefault?.Value?.Value;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { /* pattern unsupported */ }

            // TextPattern — rich-edit / document controls (e.g. the Windows 11 WinUI Notepad), which do
            // NOT expose ValuePattern.
            try
            {
                if (focused.Patterns.Text.IsSupported)
                {
                    var t = focused.Patterns.Text.Pattern.DocumentRange.GetText(8000);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { /* pattern unsupported */ }

            return null; // no field VALUE readable → unverified (never the Name label)
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the value of a SPECIFIC element (located by AutomationId, accessible Name, or ControlType)
    /// inside an allowlisted process — the read behind the <c>assert_element</c> verification verb.
    /// Retries until the value is readable or the timeout elapses (UIA values lag a frame or two after
    /// the action that set them). Unlike <see cref="ReadFocusedElementValue"/>, this DOES fall back to
    /// the accessible Name: display-only controls (the Calculator result, AutomationId
    /// "CalculatorResults") expose their value ONLY via Name ("Display is 12"), and the caller compares
    /// against a workflow-supplied <c>expected</c> (not the locator), so reading Name is correct here.
    /// Returns null when the element or a value cannot be read within the timeout.
    /// </summary>
    public string? ReadElementValue(string processName, string? automationId, string? name, string? controlType, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        _automation ??= new UIA2Automation();
        var candidates = PackagedAppAliases.CandidateProcessNames(processName);
        var ctFilter = ParseControlType(controlType);

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var candidate in candidates)
            {
                var procs = Process.GetProcessesByName(candidate);
                try
                {
                    foreach (var proc in procs)
                    {
                        try
                        {
                            var window = ResolveWindow(proc);
                            if (window is null) continue;
                            var (element, matchedByName) = FindElement(window, automationId, name, ctFilter);
                            if (element is null) continue;
                            // Suppress the Name fallback when we located the element BY name: reading its
                            // Name back would just echo the locator, a circular PASS that verifies nothing
                            // (Codex). Only a real Value/Text counts in that case.
                            var val = ReadValueOf(element, allowNameFallback: !matchedByName);
                            if (val is not null) return val;
                        }
                        catch { /* UIA tree churned mid-walk — retry until the deadline */ }
                    }
                }
                finally { foreach (var p in procs) p.Dispose(); }
            }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>Locate the element by AutomationId (preferred) → Name → ControlType. Returns whether
    /// the match was BY NAME, so the caller can suppress the circular Name-as-value read-back.</summary>
    private static (AutomationElement? element, bool matchedByName) FindElement(
        AutomationElement root, string? automationId, string? name, ControlType? ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                var e = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (e is not null) return (e, false);
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var e = root.FindFirstDescendant(cf => cf.ByName(name));
                if (e is not null) return (e, true);
            }
            if (ct is { } c)
            {
                return (root.FindFirstDescendant(cf => cf.ByControlType(c)), false);
            }
            return (null, false);
        }
        catch { return (null, false); }
    }

    /// <summary>Read an element's value: ValuePattern (edit boxes) → TextPattern (documents) → Name
    /// (display-only controls, e.g. the Calculator result). <paramref name="allowNameFallback"/> is
    /// false when the element was located BY name — reading its Name back would be circular, a PASS
    /// that confirms nothing, so only a real Value/Text counts there.</summary>
    private static string? ReadValueOf(AutomationElement e, bool allowNameFallback)
    {
        try { var v = e.Patterns.Value.PatternOrDefault?.Value?.Value; if (!string.IsNullOrEmpty(v)) return v; } catch { /* unsupported */ }
        try { if (e.Patterns.Text.IsSupported) { var t = e.Patterns.Text.Pattern.DocumentRange.GetText(8000); if (!string.IsNullOrEmpty(t)) return t; } } catch { /* unsupported */ }
        if (allowNameFallback)
        {
            try { var n = e.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { /* unavailable */ }
        }
        return null;
    }

    private static ControlType? ParseControlType(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Enum.TryParse<ControlType>(s, ignoreCase: true, out var ct) ? ct : null;

    /// <summary>A PHI-safe descriptor of one UIA element — structural identity only.</summary>
    public sealed record DiscoveredElement(string ControlType, string AutomationId, string Name);

    /// <summary>
    /// Enumerate an allowlisted app's actionable UIA elements so the agent can SEE the UI and ground
    /// its clicks/asserts on REAL elements instead of guessing. Returns the controlType + automationId
    /// (developer-assigned, not PHI) + accessible name (PHI-SCRUBBED via PhiPatternGuard + length-
    /// capped). Only elements with an automationId or a name are returned; capped at <paramref name="max"/>.
    /// </summary>
    public IReadOnlyList<DiscoveredElement> DiscoverElements(string processName, int max)
    {
        var found = new List<DiscoveredElement>();
        if (string.IsNullOrWhiteSpace(processName)) return found;
        _automation ??= new UIA2Automation();
        var cap = max <= 0 ? 60 : Math.Min(max, 200);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in PackagedAppAliases.CandidateProcessNames(processName))
        {
            var procs = Process.GetProcessesByName(candidate);
            try
            {
                foreach (var proc in procs)
                {
                    AutomationElement? window;
                    try { window = ResolveWindow(proc); } catch { continue; }
                    if (window is null) continue;

                    AutomationElement[] descendants;
                    try { descendants = window.FindAllDescendants(); } catch { continue; }
                    foreach (var e in descendants)
                    {
                        if (found.Count >= cap) return found;
                        var ct = SafeControlType(e);
                        var aid = SafeAutomationId(e);
                        var rawName = SafeName(e);
                        var name = ScrubName(rawName);
                        // Skip purely structural elements with no handle for an action.
                        if (string.IsNullOrEmpty(aid) && string.IsNullOrEmpty(name)) continue;
                        var key = $"{ct}|{aid}|{name}";
                        if (!seen.Add(key)) continue;
                        found.Add(new DiscoveredElement(ct, aid, name));
                    }
                    if (found.Count > 0) return found; // one window's tree is enough
                }
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return found;
    }

    private static string SafeControlType(AutomationElement e)
    {
        try { return e.ControlType.ToString(); } catch { return "Unknown"; }
    }

    private static string SafeAutomationId(AutomationElement e)
    {
        try { return e.AutomationId ?? string.Empty; } catch { return string.Empty; }
    }

    // Accessible names can carry PHI on a real PMS — drop any name that matches a PHI pattern, and
    // cap length so a long free-text field can't smuggle content. Structural names (button labels,
    // "Document") pass through.
    private static string ScrubName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        if (PhiPatternGuard.ContainsPotentialPhi(name, out _)) return string.Empty;
        var trimmed = name.Trim();
        return trimmed.Length > 48 ? trimmed[..48] : trimmed;
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
                        var window = ResolveWindow(proc);
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
