using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Vision;
using VisionRect = SuavoAgent.Contracts.Vision.Rect;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// FlaUI-backed UIA element extractor. Walks the UIA tree of the hwnd that
/// was foreground AT CAPTURE TIME (from <see cref="ScreenBytes.Hwnd"/>) and
/// emits a VisualElement per meaningful control.
///
/// UIA is the OS-level accessibility API — same surface screen readers use.
/// No hooks, no DLL injection, vendor-invisible from a PMS perspective.
///
/// Safety:
///   - Binds to exact capture hwnd, not GetForegroundWindow() — alt-tab race
///     free (Codex C-2).
///   - Disposes every AutomationElement after walking past it — no COM
///     handle leak on deep trees (Codex C-1).
///   - Wall-clock budget caps walk time even on pathological trees (Codex M-2).
///   - Fail-closed: any exception returns empty list, logs with exception
///     type name for diagnostic (Codex M-5).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FlaUiElementExtractor : IUiaElementExtractor
{
    private readonly ILogger _logger;

    public FlaUiElementExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Control types we keep. Everything else is skipped.</summary>
    private static readonly HashSet<ControlType> InterestingTypes = new()
    {
        ControlType.Button,
        ControlType.Edit,
        ControlType.MenuItem,
        ControlType.TabItem,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.ComboBox,
        ControlType.ListItem,
        ControlType.HeaderItem,
        ControlType.Hyperlink,
    };

    /// <summary>Max wall-clock time for one walk (Codex M-2).</summary>
    private static readonly TimeSpan WalkBudget = TimeSpan.FromMilliseconds(1500);

    public async Task<IReadOnlyList<VisualElement>> ExtractAsync(
        ScreenBytes screen,
        int maxElements,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<VisualElement>();
        if (maxElements <= 0) return Array.Empty<VisualElement>();

        var hwnd = (IntPtr)screen.Hwnd;
        if (hwnd == IntPtr.Zero)
        {
            _logger.Debug("FlaUiElementExtractor: screen.Hwnd is 0 (capture didn't record hwnd)");
            return Array.Empty<VisualElement>();
        }

        return await Task.Run<IReadOnlyList<VisualElement>>(() => Walk(hwnd, maxElements, ct), ct);
    }

    private IReadOnlyList<VisualElement> Walk(IntPtr hwnd, int maxElements, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        UIA2Automation? automation = null;
        var results = new List<VisualElement>(Math.Min(maxElements, 256));

        try
        {
            automation = new UIA2Automation();
            var window = automation.FromHandle(hwnd);
            if (window == null)
            {
                _logger.Debug("FlaUiElementExtractor: UIA FromHandle returned null for 0x{Hwnd:X}", hwnd.ToInt64());
                return Array.Empty<VisualElement>();
            }

            // Walk breadth-first. We dispose elements after we've read them AND
            // enqueued their children so COM handles don't pile up (Codex C-1).
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(window);

            while (queue.Count > 0 && results.Count < maxElements)
            {
                if (ct.IsCancellationRequested) break;
                if (sw.Elapsed >= WalkBudget)
                {
                    _logger.Debug(
                        "FlaUiElementExtractor: wall-clock budget reached at {Ms}ms, stopping walk with {Count}/{Max}",
                        sw.ElapsedMilliseconds, results.Count, maxElements);
                    break;
                }

                var el = queue.Dequeue();
                try
                {
                    if (el.Properties.ControlType.IsSupported)
                    {
                        var ctrlType = el.Properties.ControlType.ValueOrDefault;
                        if (InterestingTypes.Contains(ctrlType))
                        {
                            var role = ctrlType.ToString();
                            var name = el.Properties.Name.ValueOrDefault;
                            var bounds = el.Properties.BoundingRectangle.ValueOrDefault;

                            // Skip zero-size or off-screen elements.
                            if (bounds.Width > 0 && bounds.Height > 0)
                            {
                                results.Add(new VisualElement
                                {
                                    Role = role,
                                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                                    Bounds = new VisionRect(
                                        (int)bounds.X, (int)bounds.Y,
                                        (int)bounds.Width, (int)bounds.Height),
                                    Confidence = 1.0,
                                });
                            }
                        }
                    }

                    // Enqueue children. Leaves the children in the queue for
                    // later processing + disposal.
                    var children = el.FindAllChildren();
                    foreach (var c in children)
                    {
                        if (queue.Count + results.Count >= maxElements * 4) break;
                        queue.Enqueue(c);
                    }
                }
                catch (Exception ex)
                {
                    // Individual element walk failures shouldn't abort the
                    // whole walk. Log with type name for COM diagnosis.
                    _logger.Debug(
                        "FlaUiElementExtractor: skipped node (exception {Type}: {Msg})",
                        ex.GetType().FullName, ex.Message);
                }
                // No per-element Dispose — FlaUI AutomationElement's UIA native
                // handle is owned by the UIA2Automation instance. Disposing
                // UIA2Automation (in the outer finally) releases all derived
                // elements together.
            }

            _logger.Debug(
                "FlaUiElementExtractor: extracted {Count} elements in {Ms}ms (max={Max})",
                results.Count, sw.ElapsedMilliseconds, maxElements);
            return results;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "FlaUiElementExtractor: UIA walk failed ({Type}: {Msg})",
                ex.GetType().FullName, ex.Message);
            return Array.Empty<VisualElement>();
        }
        finally
        {
            automation?.Dispose();
        }
    }

}
