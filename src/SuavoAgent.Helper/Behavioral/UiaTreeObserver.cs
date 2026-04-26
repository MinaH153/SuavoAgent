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

    // Trip A 2026-04-25 hard-reset prevention. PioneerRx is a deep WinForms
    // tree; with UIA2 marshalling overhead a single walk on a busy install
    // can pin a CPU core for 30+ seconds. Bound the walk so a single tree
    // walk can't dominate the window between walks, and skip the next walk
    // if the previous one was already that slow — letting the observer
    // breathe instead of stacking back-to-back walks.
    private const int MaxElementsPerWalk = 5000;
    private static readonly TimeSpan SlowWalkSkipThreshold = TimeSpan.FromSeconds(30);

    private readonly string _pharmacySalt;
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private TimeSpan _lastWalkDuration = TimeSpan.Zero;

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
        _logger.Information(
            "UiaTreeObserver started (interval={IntervalSec}s, maxElements={MaxElems}, slowSkipThreshold={SkipSec}s)",
            WalkInterval.TotalSeconds, MaxElementsPerWalk, SlowWalkSkipThreshold.TotalSeconds);

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

            // Back-pressure: if the last walk burned more than the skip
            // threshold, take this cycle off so we don't stack walks back
            // to back. A single walk that takes 35s on a 60s cadence
            // already eats >half the breathing room — two in a row would
            // saturate the CPU.
            if (_lastWalkDuration > SlowWalkSkipThreshold)
            {
                _logger.Warning(
                    "UiaTreeObserver: skipping walk — previous walk took {Sec:F1}s (threshold {ThreshSec}s). " +
                    "PioneerRx tree may be unusually deep or UIA2 marshalling slow.",
                    _lastWalkDuration.TotalSeconds,
                    SlowWalkSkipThreshold.TotalSeconds);
                _lastWalkDuration = TimeSpan.Zero;
                continue;
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
    /// Walks the window depth-first (max 8 levels, max 5000 elements),
    /// scrubs each element, computes a SHA-256 tree hash, and enqueues a
    /// TreeSnapshot event. Records the wall-clock duration so the next
    /// cycle can skip if this one was slow.
    /// </summary>
    public void WalkTree(Window window)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scrubbedElements = new List<ScrubbedElement>();
        var hashParts = new List<string>();

        // Window inherits AutomationElement — pass directly. WalkElement
        // checks scrubbedElements.Count against MaxElementsPerWalk on every
        // recursion so a runaway tree truncates rather than stalls.
        WalkElement(window, depth: 0, scrubbedElements, hashParts);

        var treeHash = ComputeTreeHash(hashParts);
        _buffer.Enqueue(BehavioralEvent.TreeSnapshot(treeHash));

        sw.Stop();
        _lastWalkDuration = sw.Elapsed;

        var truncated = scrubbedElements.Count >= MaxElementsPerWalk;
        if (truncated || sw.Elapsed > SlowWalkSkipThreshold)
        {
            _logger.Warning(
                "UiaTreeObserver: walk slow — {Count} elements in {Ms}ms (truncated={Trunc})",
                scrubbedElements.Count, sw.ElapsedMilliseconds, truncated);
        }
        else
        {
            _logger.Debug("UiaTreeObserver: walked {Count} elements in {Ms}ms, hash={Hash}",
                scrubbedElements.Count, sw.ElapsedMilliseconds, treeHash[..8]);
        }
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private void WalkElement(
        AutomationElement element,
        int depth,
        List<ScrubbedElement> output,
        List<string> hashParts)
    {
        if (depth > MaxDepth) return;
        // Element budget — prevents UIA enumeration from running unbounded
        // on a degenerate tree (the kind of failure mode that took 5
        // hours to diagnose at Nadim's). Truncation is logged in WalkTree.
        if (output.Count >= MaxElementsPerWalk) return;

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
