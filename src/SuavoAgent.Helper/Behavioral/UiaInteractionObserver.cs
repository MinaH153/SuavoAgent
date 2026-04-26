using FlaUI.Core.AutomationElements;
using FlaUI.Core.AutomationElements.Infrastructure;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA2;
using FlaUI.UIA2.Patterns;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Subscribes to UIA FocusChanged and StructureChanged events on PMS windows.
/// Emits PHI-free Interaction events to the behavioral buffer.
/// Does NOT subscribe to TextChanged, ValuePattern, or SelectionPattern (RED tier).
/// </summary>
public sealed class UiaInteractionObserver : IDisposable
{
    private readonly UIA2Automation _automation;
    private readonly string _pharmacySalt;
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly Action _triggerTreeResnapshot;

    private string? _currentTreeHash;
    private Window? _subscribedWindow;
    private int? _subscribedProcessId;

    // Keep handler references alive for unregister
    private FocusChangedEventHandlerBase? _focusHandler;
    private StructureChangedEventHandlerBase? _structureHandler;
    private AutomationEventHandlerBase? _invokeHandler;

    private bool _disposed;

    // Trip A 2026-04-25 hard-reset prevention. Subtree event subscriptions
    // on PioneerRx (TreeScope.Subtree on FocusChanged + StructureChanged +
    // InvokePattern.Invoked) can fire dozens-per-sec on a busy pharmacist
    // session. Each fire walks element properties, scrubs PHI, and enqueues
    // a buffer event — under UIA2 that adds up fast enough to spike CPU.
    // Token bucket caps sustained throughput at 5 events/sec with burst
    // capacity of 25 so a flurry of clicks still records the first 25 but
    // a runaway loop can't dominate the dispatcher thread. The limiter is
    // extracted into TokenBucketRateLimiter so the throttle contract is
    // unit-testable without spinning up UIA2Automation.
    private const int RateLimitMaxBurst = 25;
    private static readonly TimeSpan RateLimitRefillInterval = TimeSpan.FromMilliseconds(200); // 5/sec
    private readonly TokenBucketRateLimiter _throttle = new(RateLimitMaxBurst, RateLimitRefillInterval);
    private long _droppedEvents;
    private long _lastDropLogTicks;

    public UiaInteractionObserver(
        UIA2Automation automation,
        string pharmacySalt,
        BehavioralEventBuffer buffer,
        ILogger logger,
        Action triggerTreeResnapshot)
    {
        _automation = automation;
        _pharmacySalt = pharmacySalt;
        _buffer = buffer;
        _logger = logger.ForContext<UiaInteractionObserver>();
        _triggerTreeResnapshot = triggerTreeResnapshot;
    }

    /// <summary>Called by UiaTreeObserver when hash changes.</summary>
    public void SetCurrentTreeHash(string treeHash)
    {
        Volatile.Write(ref _currentTreeHash, treeHash);
    }

    /// <summary>
    /// Registers FocusChanged (global, filtered to PMS process) and
    /// StructureChanged (on window subtree) events.
    /// Unsubscribes from any previous window first.
    /// </summary>
    public void Subscribe(Window window)
    {
        Unsubscribe();

        try
        {
            int processId;
            try { processId = window.Properties.ProcessId.Value; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "UiaInteractionObserver: could not read ProcessId from window");
                return;
            }

            _subscribedWindow = window;
            _subscribedProcessId = processId;

            // FocusChanged is global — filter to PMS process in the handler
            _focusHandler = _automation.RegisterFocusChangedEvent(OnFocusChanged);

            // StructureChanged on the window subtree — catches grid/tab reflows
            // RegisterStructureChangedEvent is on AutomationElement; Window inherits it
            _structureHandler = window.RegisterStructureChangedEvent(
                TreeScope.Subtree,
                OnStructureChanged);

            // InvokePattern.Invoked on the window subtree — catches button/menu activations.
            // Wrapped in try/catch: registration may fail on machines that restrict UIA event
            // subscriptions (e.g., elevated processes, locked desktops).
            try
            {
                _invokeHandler = window.RegisterAutomationEvent(
                    InvokePattern.InvokedEvent,
                    TreeScope.Subtree,
                    (element, _) => RecordInvocation(element, depth: -1, childIndex: -1));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "UiaInteractionObserver: InvokePattern.Invoked registration failed — button clicks will not be correlated");
            }

            _logger.Information(
                "UiaInteractionObserver: subscribed to PID {Pid}", processId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "UiaInteractionObserver: Subscribe failed");
        }
    }

    /// <summary>
    /// Records an invocation event for the given element.
    /// Called externally (e.g. when InvokedEvent fires via a separate handler).
    /// </summary>
    public void RecordInvocation(AutomationElement element, int depth, int childIndex)
    {
        EmitInteractionEvent("Invoked", element, depth, childIndex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private void Unsubscribe()
    {
        try
        {
            if (_focusHandler is not null)
            {
                _automation.UnregisterFocusChangedEvent(_focusHandler);
                _focusHandler = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: UnregisterFocusChangedEvent warning");
        }

        try
        {
            if (_structureHandler is not null && _subscribedWindow is not null)
            {
                // IAutomationElementEventUnsubscriber is implemented by AutomationElement
                if (_subscribedWindow is IAutomationElementEventUnsubscriber unsub)
                    unsub.UnregisterStructureChangedEventHandler(_structureHandler);
                _structureHandler = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: UnregisterStructureChangedEvent warning");
        }

        try
        {
            if (_invokeHandler is not null && _subscribedWindow is not null)
            {
                if (_subscribedWindow is IAutomationElementEventUnsubscriber unsub)
                    unsub.UnregisterAutomationEventHandler(_invokeHandler);
                _invokeHandler = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: UnregisterAutomationEventHandler (invoke) warning");
        }

        _subscribedWindow = null;
        _subscribedProcessId = null;
    }

    private void OnFocusChanged(AutomationElement element)
    {
        if (_subscribedProcessId is null) return;

        try
        {
            int elementPid;
            try { elementPid = element.Properties.ProcessId.Value; }
            catch { return; }

            if (elementPid != _subscribedProcessId.Value) return;

            EmitInteractionEvent("FocusChanged", element, depth: -1, childIndex: -1);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: OnFocusChanged error");
        }
    }

    private void OnStructureChanged(AutomationElement element, StructureChangeType changeType, int[] runtimeId)
    {
        try
        {
            // StructureChanged signals new grid/panel — trigger tree re-snapshot
            _triggerTreeResnapshot();

            var subtype = $"StructureChanged.{changeType}";
            EmitInteractionEvent(subtype, element, depth: -1, childIndex: -1);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: OnStructureChanged error");
        }
    }

    private void EmitInteractionEvent(
        string subtype,
        AutomationElement element,
        int depth,
        int childIndex)
    {
        // Rate-limit BEFORE doing any UIA RPC work — every property read
        // here (ControlType, AutomationId, etc.) is a separate UIA2
        // round-trip that can saturate the dispatcher under burst load.
        // Drop the event entirely if no token is available so the rest of
        // the observer + buffer pipeline never feels the back-pressure.
        if (!TryAcquireToken())
        {
            return;
        }

        try
        {
            var controlType = TryGetControlType(element);
            var automationId = TryGet(() => element.AutomationId);
            var className = TryGet(() => element.ClassName);
            var name = TryGet(() => element.Name);
            var boundingRect = TryGet(() => element.BoundingRectangle.ToString());

            var raw = new RawElementProperties(
                ControlType: controlType,
                AutomationId: automationId,
                ClassName: className,
                Name: name,
                BoundingRect: boundingRect,
                Depth: depth,
                ChildIndex: childIndex);

            var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
            if (scrubbed is null) return;

            var elementId = UiaPropertyScrubber.BuildElementId(raw);
            var treeHash = Volatile.Read(ref _currentTreeHash);

            var ev = BehavioralEvent.Interaction(
                subtype: subtype,
                treeHash: treeHash,
                elementId: elementId,
                controlType: scrubbed.ControlType,
                className: scrubbed.ClassName,
                nameHash: scrubbed.NameHash);

            _buffer.Enqueue(ev);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "UiaInteractionObserver: EmitInteractionEvent error for {Subtype}", subtype);
        }
    }

    /// <summary>
    /// Throttle wrapper that handles the periodic-log side effect. Logic
    /// lives in <see cref="TokenBucketRateLimiter"/>; this method records
    /// drops and emits a periodic warning so ops can see throttling is
    /// active versus the agent being silent for other reasons.
    /// </summary>
    private bool TryAcquireToken()
    {
        if (_throttle.TryAcquire()) return true;

        var now = Environment.TickCount64;
        var dropped = Interlocked.Increment(ref _droppedEvents);
        if (now - Interlocked.Read(ref _lastDropLogTicks) > 5000)
        {
            _logger.Warning(
                "UiaInteractionObserver: {Dropped} interaction events dropped by rate limiter " +
                "(burst={Max}/refill={Hz}Hz). High-frequency UIA event source — review subtree subscription scope.",
                dropped, RateLimitMaxBurst, 1000.0 / RateLimitRefillInterval.TotalMilliseconds);
            Interlocked.Exchange(ref _lastDropLogTicks, now);
            Interlocked.Exchange(ref _droppedEvents, 0);
        }
        return false;
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
