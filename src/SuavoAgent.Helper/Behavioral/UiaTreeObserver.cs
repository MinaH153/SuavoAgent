using System.Security.Cryptography;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Periodic depth-first tree walker. Snapshots PMS window structure every 60 seconds.
/// Reads GREEN+YELLOW properties only — NEVER Value, Text, Selection, HelpText.
/// </summary>
public sealed class UiaTreeObserver
{
    private const int MaxDepth = 8;
    private static readonly TimeSpan WalkInterval = TimeSpan.FromSeconds(60);

    private readonly string _pharmacySalt;
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;

    public UiaTreeObserver(
        string pharmacySalt,
        BehavioralEventBuffer buffer,
        ILogger logger)
    {
        _pharmacySalt = pharmacySalt;
        _buffer = buffer;
        _logger = logger.ForContext<UiaTreeObserver>();
    }

    /// <summary>
    /// Loops every 60 seconds, walking the window returned by <paramref name="getWindow"/>.
    /// Exits cleanly on cancellation.
    /// </summary>
    public async Task RunAsync(Func<Window?> getWindow, CancellationToken ct)
    {
        _logger.Information("UiaTreeObserver started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WalkInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var window = getWindow();
                if (window is null)
                {
                    _logger.Debug("UiaTreeObserver: window not available, skipping walk");
                    continue;
                }

                WalkTree(window);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "UiaTreeObserver: walk failed");
            }
        }

        _logger.Information("UiaTreeObserver stopped");
    }

    /// <summary>
    /// Walks the window depth-first (max 8 levels), scrubs each element,
    /// computes a SHA-256 tree hash, and enqueues a TreeSnapshot event.
    /// </summary>
    public void WalkTree(Window window)
    {
        var scrubbedElements = new List<ScrubbedElement>();
        var hashParts = new List<string>();

        // Window inherits AutomationElement — pass directly
        WalkElement(window, depth: 0, scrubbedElements, hashParts);

        var treeHash = ComputeTreeHash(hashParts);
        _buffer.Enqueue(BehavioralEvent.TreeSnapshot(treeHash));

        _logger.Debug("UiaTreeObserver: walked {Count} elements, hash={Hash}",
            scrubbedElements.Count, treeHash[..8]);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private void WalkElement(
        AutomationElement element,
        int depth,
        List<ScrubbedElement> output,
        List<string> hashParts)
    {
        if (depth > MaxDepth) return;

        try
        {
            // GREEN tier only — ControlType, AutomationId, ClassName, Name, BoundingRect
            var controlType = TryGetControlType(element);
            var automationId = TryGet(() => element.AutomationId);
            var className = TryGet(() => element.ClassName);
            var name = TryGet(() => element.Name);
            var boundingRect = TryGet(() => element.BoundingRectangle.ToString());

            // Track child index per ControlType among siblings at this level
            // (passed in via depth; actual sibling position is handled by WalkChildren)
            var raw = new RawElementProperties(
                ControlType: controlType,
                AutomationId: automationId,
                ClassName: className,
                Name: name,
                BoundingRect: boundingRect,
                Depth: depth,
                ChildIndex: 0); // overridden in WalkChildren

            var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
            if (scrubbed is not null)
            {
                output.Add(scrubbed);
                // Hash contribution: structural identity only (no Name)
                hashParts.Add($"{controlType}|{automationId}|{className}");
            }

            WalkChildren(element, depth, output, hashParts);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaTreeObserver: error reading element at depth {Depth}", depth);
        }
    }

    private void WalkChildren(
        AutomationElement element,
        int depth,
        List<ScrubbedElement> output,
        List<string> hashParts)
    {
        if (depth >= MaxDepth) return;

        try
        {
            var children = element.FindAllChildren();
            if (children is null) return;

            // Track child index per ControlType for positional fallback ID
            var countByControlType = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var child in children)
            {
                try
                {
                    var controlType = TryGetControlType(child);
                    var automationId = TryGet(() => child.AutomationId);
                    var className = TryGet(() => child.ClassName);
                    var name = TryGet(() => child.Name);
                    var boundingRect = TryGet(() => child.BoundingRectangle.ToString());

                    var ctKey = controlType ?? "Unknown";
                    countByControlType.TryGetValue(ctKey, out var childIndex);
                    countByControlType[ctKey] = childIndex + 1;

                    var raw = new RawElementProperties(
                        ControlType: controlType,
                        AutomationId: automationId,
                        ClassName: className,
                        Name: name,
                        BoundingRect: boundingRect,
                        Depth: depth + 1,
                        ChildIndex: childIndex);

                    var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
                    if (scrubbed is not null)
                    {
                        output.Add(scrubbed);
                        hashParts.Add($"{controlType}|{automationId}|{className}");
                    }

                    WalkChildren(child, depth + 1, output, hashParts);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "UiaTreeObserver: error on child at depth {Depth}", depth + 1);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaTreeObserver: FindAllChildren failed at depth {Depth}", depth);
        }
    }

    private static string ComputeTreeHash(List<string> parts)
    {
        if (parts.Count == 0)
            return "empty";

        var combined = string.Join('\n', parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? TryGetControlType(AutomationElement el)
    {
        try { return el.ControlType.ToString(); }
        catch { return null; }
    }

    private static string? TryGet(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }
}
