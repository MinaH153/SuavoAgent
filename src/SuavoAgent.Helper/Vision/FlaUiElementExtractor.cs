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
/// FlaUI-backed UIA element extractor. Walks the current foreground window's
/// UIA tree and emits a VisualElement for each meaningful control (button,
/// input, menu item, tab, checkbox, etc.).
///
/// UIA is the OS-level accessibility API — same surface screen readers use.
/// No hooks, no DLL injection, vendor-invisible from a PMS perspective.
///
/// Fail-closed: any error during tree walk returns an empty list rather than
/// throwing. The vision pipeline still emits a frame, just without UIA
/// elements for this capture.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FlaUiElementExtractor : IUiaElementExtractor
{
    private readonly ILogger _logger;

    public FlaUiElementExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Control types we keep. Everything else (pure layout containers, static text) is skipped.</summary>
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

    public async Task<IReadOnlyList<VisualElement>> ExtractAsync(int maxElements, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<VisualElement>();
        if (maxElements <= 0) return Array.Empty<VisualElement>();

        return await Task.Run<IReadOnlyList<VisualElement>>(() =>
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    _logger.Debug("FlaUiElementExtractor: no foreground window");
                    return Array.Empty<VisualElement>();
                }

                using var automation = new UIA2Automation();
                var window = automation.FromHandle(hwnd);
                if (window == null)
                {
                    _logger.Debug("FlaUiElementExtractor: automation returned null for hwnd {Handle}", hwnd);
                    return Array.Empty<VisualElement>();
                }

                var results = new List<VisualElement>(Math.Min(maxElements, 256));
                var queue = new Queue<AutomationElement>();
                queue.Enqueue(window);

                while (queue.Count > 0 && results.Count < maxElements)
                {
                    if (ct.IsCancellationRequested) break;
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

                                // Skip zero-size or off-screen elements — likely
                                // invisible/clipped, not useful to reasoning.
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                                results.Add(new VisualElement
                                {
                                    Role = role,
                                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                                    Bounds = new VisionRect(
                                        (int)bounds.X, (int)bounds.Y,
                                        (int)bounds.Width, (int)bounds.Height),
                                    Confidence = 1.0, // UIA is deterministic, not probabilistic
                                });
                            }
                        }

                        // Breadth-first walk through children. Cap depth via queue size.
                        var children = el.FindAllChildren();
                        foreach (var c in children)
                        {
                            if (queue.Count + results.Count >= maxElements * 4) break;
                            queue.Enqueue(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Individual element failures shouldn't abort the whole walk.
                        _logger.Debug(ex, "FlaUiElementExtractor: element walk skipped one node");
                    }
                }

                _logger.Debug(
                    "FlaUiElementExtractor: extracted {Count} elements (max={Max})",
                    results.Count, maxElements);
                return results;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "FlaUiElementExtractor: UIA walk failed");
                return Array.Empty<VisualElement>();
            }
        }, ct);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
