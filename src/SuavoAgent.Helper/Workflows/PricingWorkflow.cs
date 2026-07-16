using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.SystemObservers;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Executes the PioneerRx pricing lookup workflow via UIA:
///   Item menu → Rx Item → Quick Search (NDC) → Pricing tab → read supplier grid
///
/// Navigation path confirmed from field screenshots (Apr 4, 2026): Item → Rx Item opens Edit Rx Item.
///   - Quick Search field at top accepts NDC
///   - Pricing tab shows supplier catalog with Cost and Cost Per Unit columns
/// </summary>
public sealed partial class PricingWorkflow
{
    private readonly PioneerRxUiaEngine _engine;
    private readonly ActuationGate _actuationGate;
    private readonly ILogger _logger;
    // Vision-primary grid reader (reads "the one on top" by sight via OCR). Null = vision off →
    // UIA-only, exactly today's behavior. When present, the vision read drives and the UIA read
    // verifies the exact cost (VisionExactReconciler) so a misread never writes wrong pricing.
    private readonly VisionPricingGridReader? _visionReader;
    private readonly SendInputDriver? _pointerDriver;
    private readonly object _screenContractLock = new();
    private string? _screenContractJobId;
    private string? _screenContractPmsFingerprint;
    private string? _screenContractSignature;
    private long _screenContractVerifiedAtTicks = -1;
    private static readonly long ScreenContractCacheMilliseconds =
        (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

    // UIA element identifiers confirmed from screenshots
    private const string ItemMenuName = "Item";
    private const string RxItemMenuName = "Rx Item";
    private const string QuickSearchHint = "Quick Search";
    private const string PricingTabName = "Pricing";
    private const string EditRxItemWindowTitle = "Edit Rx Item";

    // How long to wait for UI elements to appear after navigation. Sized for a LOADED box: PioneerRx
    // under real dispensing load (or a slow VM) can take >8s to open the Edit window / load an item
    // after an NDC search — an 8s cap timed out those rows and mislabeled them NO_MATCH. These are
    // upper bounds on the happy path (elements normally resolve in <2s); a genuinely missing item still
    // fails fast via the value checks, not by burning the whole window.
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GridLoadTimeout = TimeSpan.FromSeconds(10);
    // Bound the sighted read (capture + OCR) per NDC so a stuck OCR can't wedge a 500-item batch.
    private static readonly TimeSpan VisionReadTimeout = TimeSpan.FromSeconds(20);

    public PricingWorkflow(
        PioneerRxUiaEngine engine,
        ActuationGate actuationGate,
        ILogger logger,
        VisionPricingGridReader? visionReader = null,
        SendInputDriver? pointerDriver = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _actuationGate = actuationGate ?? throw new ArgumentNullException(nameof(actuationGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _visionReader = visionReader;
        _pointerDriver = pointerDriver;
    }

    /// <summary>
    /// Looks up pricing for a single NDC. Returns the cheapest supplier row.
    /// Leaves PioneerRx in a usable state (closes Edit Rx Item dialog after read).
    /// </summary>
    public SupplierPriceResult Lookup(NdcPricingRequest request)
    {
        // M2a capture: GREEN-tier selector-resolution telemetry per step. Builtin source only
        // (no learned patches yet). Candidate scans run ONLY on the rare failure path, so the
        // happy path is unchanged. Never records element Name/Value/Text — PHI-negative by type.
        var observations = new List<SelectorObservation>();
        void Observe(SelectorStepId step, SelectorOutcome outcome, SelectorFailureKind kind,
                     IReadOnlyList<ObservedElement>? candidates = null) =>
            observations.Add(new SelectorObservation(
                step, SelectorResolvedVia.Builtin, outcome, kind, null,
                candidates ?? Array.Empty<ObservedElement>()));
        SupplierPriceResult Done(SupplierPriceResult r) => r with { Observations = observations };

        // M2b: the resolver tries any active learned selector patch before the hardcoded builtin.
        // With no patches (the case until M2c distributes one) it IS the builtin find — today's
        // behavior unchanged. The builtin always backstops a missed learned selector per element.
        var resolver = new SelectorResolver(
            request.Patches,
            request.PmsFingerprint,
            request.ScreenSignatureV1);

        try
        {
            if (request.ScreenSignatureV1 is not null &&
                !AdmitScreenContract(request))
            {
                return Done(Fail(request, "pricing_screen_identity_changed"));
            }

            // Pricing drives a live PMS and has no truthful simulation mode.  Refuse
            // before touching UIA when disabled, paused, killed, compromised, or dry-run.
            EnsureLiveActuation();

            var mainWindow = _engine.MainWindow;
            if (mainWindow == null)
                return Done(Fail(request, "PioneerRx main window not available"));

            // The cockpit is often foreground when the pharmacist starts a run. Bring the locally
            // approved PMS forward before any pointer or keyboard action. Each visible movement
            // below re-verifies that the same approved process still owns the foreground.
            BringPmsToForeground(mainWindow);
            Thread.Sleep(200);

            using var automation = new UIA2Automation();
            var cf = automation.ConditionFactory;

            // Step 1: Open Item → Rx Item from the menu bar
            if (!OpenRxItemDialog(mainWindow, cf, resolver))
            {
                Observe(SelectorStepId.OpenRxItem, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                    ScanCandidates(mainWindow, cf, ControlType.MenuItem));
                return Done(Fail(request, "Could not open Item → Rx Item menu"));
            }
            Observe(SelectorStepId.OpenRxItem, SelectorOutcome.Resolved, SelectorFailureKind.None);

            // Step 2: Find the Edit Rx Item window
            var editWindow = WaitForWindow(automation);
            if (editWindow == null)
            {
                Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Failed, SelectorFailureKind.Timeout);
                return Done(Fail(request, "Edit Rx Item window did not appear"));
            }

            try
            {
                // Step 3: Type NDC into Quick Search and press Enter
                if (!SearchByNdc(
                        editWindow,
                        cf,
                        request.Ndc,
                        request.CostBasis,
                        resolver,
                        out var searchBox))
                {
                    Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                        ScanCandidates(editWindow, cf, ControlType.Edit));
                    return Done(Fail(request, $"Could not enter NDC {request.Ndc} in Quick Search"));
                }
                Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Resolved, SelectorFailureKind.None);

                // [C-3] Verify the loaded item's NDC matches the requested NDC before reading pricing.
                // Prevents returning pricing data for the previously-selected item when Quick Search
                // is slow or finds no match. (Selector resolved; the mismatch is semantic, so no
                // candidate scan — and we must not read element values here.)
                if (!VerifyLoadedNdc(editWindow, cf, request.Ndc, searchBox))
                {
                    Observe(SelectorStepId.VerifyNdc, SelectorOutcome.Failed, SelectorFailureKind.VerifyMismatch);
                    return Done(Fail(request, "Loaded item did not match the requested identifier"));
                }
                Observe(SelectorStepId.VerifyNdc, SelectorOutcome.Resolved, SelectorFailureKind.None);

                // Step 4: Navigate to Pricing tab
                if (!ClickPricingTab(editWindow, cf, resolver))
                {
                    Observe(SelectorStepId.PricingTab, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                        ScanCandidates(editWindow, cf, ControlType.TabItem));
                    return Done(Fail(request, "Could not click Pricing tab"));
                }
                Observe(SelectorStepId.PricingTab, SelectorOutcome.Resolved, SelectorFailureKind.None);

                if (request.CostBasis == PricingApprovalContract.PackageCostBasis)
                {
                    NarrateVisibleRead("Cost");
                    var packageResult = ReadPackageCostResult(request, editWindow, cf, out _);
                    Observe(SelectorStepId.SupplierGrid, packageResult.Found ? SelectorOutcome.Resolved : SelectorOutcome.Failed,
                        packageResult.Found ? SelectorFailureKind.None : SelectorFailureKind.GridEmpty);
                    return Done(packageResult);
                }
                if (request.CostBasis != PricingApprovalContract.CostPerUnitBasis)
                    return Done(Fail(request, "pricing_cost_basis_unsupported"));

                // Step 5: Read the supplier grid. VISION-PRIMARY (SEE "the one on top" via OCR) +
                // EXACT-VERIFY (UIA cell value) so an OCR misread never writes a wrong cost. When
                // vision is off this collapses to exactly today's UIA-only read.
                NarrateVisibleRead("Cost Per Unit");
                var uiaCheapest = ReadCheapestSupplier(editWindow, cf, out var gridFailure);

                if (_visionReader is { IsAvailable: true })
                {
                    VisionSupplierGridParser.VisionGridReading? visionReading = null;
                    try
                    {
                        var hwnd = editWindow.Properties.NativeWindowHandle.ValueOrDefault;
                        using var visionCts = new CancellationTokenSource(VisionReadTimeout);
                        visionReading = _visionReader
                            .TryReadCheapestAsync(hwnd, _engine.ProcessId, visionCts.Token)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("PricingWorkflow: vision read errored ({Type}) — using exact-only", ex.GetType().Name);
                    }

                    var decision = VisionExactReconciler.Reconcile(
                        vision: visionReading is { } vr
                            ? new VisionExactReconciler.Reading(vr.Supplier, vr.CostPerUnit, vr.Confidence)
                            : null,
                        uia: uiaCheapest is { } uc
                            ? new VisionExactReconciler.Reading(uc.supplier, uc.costPerUnit, 1.0)
                            : null);

                    if (!decision.Accept)
                    {
                        Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Failed, SelectorFailureKind.GridEmpty);
                        _logger.Warning("PricingWorkflow: pricing read was not confirmed ({Reason})",
                            decision.RejectReason);
                        return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                            false, null, null, gridFailure ?? decision.RejectReason ?? "Pricing read not confirmed"));
                    }

                    Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Resolved, SelectorFailureKind.None);
                    _logger.Debug("PricingWorkflow: pricing read confirmed from {Source}", decision.Source);
                    return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                        true, decision.Supplier, decision.CostPerUnit, null));
                }

                // Vision disabled → UIA-only (unchanged).
                if (uiaCheapest == null)
                {
                    Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Failed, SelectorFailureKind.GridEmpty,
                        ScanCandidates(editWindow, cf, ControlType.Table)
                            .Concat(ScanCandidates(editWindow, cf, ControlType.DataGrid)).Take(5).ToList());
                    return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                        false, null, null, gridFailure ?? "No supplier rows found in Pricing tab"));
                }
                Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Resolved, SelectorFailureKind.None);

                _logger.Debug("PricingWorkflow: exact pricing read confirmed");

                return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                    true, uiaCheapest.Value.supplier, uiaCheapest.Value.costPerUnit, null));
            }
            finally
            {
                // Always close the Edit Rx Item dialog — press Escape
                TryCloseEditWindow(editWindow, cf);
            }
        }
        catch (PricingActuationGateClosedException ex)
        {
            _logger.Warning(
                "PricingWorkflow: live UIA halted because actuation gate closed ({Code})",
                ex.RejectionCode);
            return Done(Fail(request, PricingSafetyErrors.ActuationGateClosed(ex.RejectionCode)));
        }
        catch (Exception)
        {
            _logger.Error("PricingWorkflow failed locally");
            // Compliance: never upload a raw exception message (uncontrolled free text — possible
            // leak vector). The real exception is in the local log above; the corpus/cloud get a
            // generic reason + the structured observations only.
            return Done(Fail(request, "Pricing lookup failed (unhandled error)"));
        }
    }

    internal PricingScreenObservationContext? CaptureObservationContext()
    {
        if (!_engine.VerifyAttachedProcessIdentity().Trusted)
            return null;
        var mainWindow = _engine.MainWindow;
        if (mainWindow is null)
            return null;
        var handle = mainWindow.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == 0 || _engine.ProcessId <= 0)
            return null;
        var snapshot = new IsolatedWindowStructureSnapshotProvider().Capture(
            handle,
            _engine.ProcessId,
            WindowStructureCaptureProfile.Pms);
        return _engine.VerifyAttachedProcessIdentity().Trusted &&
               snapshot.Success && !snapshot.Truncated &&
               snapshot.TreeHash is { Length: 64 } signature
            ? new PricingScreenObservationContext(_engine.ProcessId, signature)
            : null;
    }

    private bool AdmitScreenContract(NdcPricingRequest request)
    {
        lock (_screenContractLock)
        {
            var nowTicks = Environment.TickCount64;
            if (string.Equals(
                    _screenContractJobId,
                    request.JobId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _screenContractPmsFingerprint,
                    request.PmsFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    _screenContractSignature,
                    request.ScreenSignatureV1,
                    StringComparison.Ordinal) &&
                _screenContractVerifiedAtTicks >= 0 &&
                nowTicks - _screenContractVerifiedAtTicks <=
                    ScreenContractCacheMilliseconds)
                return true;

            var currentScreen = CaptureObservationContext();
            if (currentScreen is null ||
                !string.Equals(
                    currentScreen.ScreenSignatureV1,
                    request.ScreenSignatureV1,
                    StringComparison.Ordinal))
                return false;

            _screenContractJobId = request.JobId;
            _screenContractPmsFingerprint = request.PmsFingerprint;
            _screenContractSignature = request.ScreenSignatureV1;
            _screenContractVerifiedAtTicks = nowTicks;
            return true;
        }
    }

    private void EnsureLiveActuation()
    {
        var rejection = _actuationGate.CheckLiveOrReject();
        if (rejection is not null)
            throw new PricingActuationGateClosedException(
                rejection.RejectionCode ?? ActuationRejectionCodes.GateDisabled);

        var processTrust = _engine.VerifyAttachedProcessIdentity();
        if (!processTrust.Trusted)
        {
            _logger.Warning(
                "PricingWorkflow: attached PioneerRx process identity rejected ({Code})",
                processTrust.Code);
            throw new PricingActuationGateClosedException(
                ActuationRejectionCodes.ProcessIdentityUntrusted);
        }
    }

    private void ExecuteLiveMutation(Action mutation)
    {
        var identityRejected = false;
        var rejection = _actuationGate.ExecuteLiveMutationOrReject(() =>
        {
            if (!_engine.VerifyAttachedProcessIdentity().Trusted)
            {
                identityRejected = true;
                return;
            }
            mutation();
        });
        if (rejection is not null)
            throw new PricingActuationGateClosedException(
                rejection.RejectionCode ?? ActuationRejectionCodes.GateDisabled);
        if (identityRejected)
            throw new PricingActuationGateClosedException(
                ActuationRejectionCodes.ProcessIdentityUntrusted);
    }

    // GREEN-tier candidate scan for the failure path: records ControlType / AutomationId /
    // ClassName of up to 5 elements of the given type so the fleet corpus can learn what was
    // actually on screen when a builtin selector missed. NEVER reads Name/Value/Text (PHI tier).
    private static IReadOnlyList<ObservedElement> ScanCandidates(
        AutomationElement root, ConditionFactory cf, ControlType type)
    {
        try
        {
            return root.FindAllDescendants(cf.ByControlType(type))
                .Take(5)
                .Select(ToObserved)
                .Where(element => element is not null)
                .Cast<ObservedElement>()
                .ToList();
        }
        catch
        {
            return Array.Empty<ObservedElement>();
        }
    }

    private static ObservedElement? ToObserved(AutomationElement el)
    {
        string controlType;
        try { controlType = el.ControlType.ToString(); } catch { controlType = "Unknown"; }
        string? automationId;
        try { automationId = NullIfEmpty(el.AutomationId); } catch { automationId = null; }
        string? className;
        try { className = NullIfEmpty(el.ClassName); } catch { className = null; }
        if (automationId is not null &&
                !StructuralIdentifierSanitizer.IsAllowed(automationId) ||
            className is not null &&
                !StructuralIdentifierSanitizer.IsAllowed(className))
            return null;
        return new ObservedElement(controlType, automationId, className);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Visibility into learned-patch usage during a run. The FallbackUsed line is the early
    // signal that a learned selector has drifted (input to later auto-retirement / M2c-M2d).
    private void LogIfLearned(SelectorStepId step, SelectorResolver.Resolution res)
    {
        if (res.ResolvedVia == SelectorResolvedVia.Learned)
            _logger.Information("PricingWorkflow: step {Step} resolved via an approved learned patch", step);
        else if (res.Outcome == SelectorOutcome.FallbackUsed)
            _logger.Warning("PricingWorkflow: step {Step} learned patch missed; used builtin fallback", step);
    }

    private bool OpenRxItemDialog(Window mainWindow, ConditionFactory cf, SelectorResolver resolver)
    {
        try
        {
            // Click "Item" in the menu bar. Win32/WinForms/DevExpress bars report ControlType.MenuBar;
            // a WPF-native menu reports ControlType.Menu. Try MenuBar first, then Menu, so the menu
            // opens regardless of the vendor's UI toolkit — the real PioneerRx control type is unknown
            // from screenshots, and this handles both. (Surfaced on the WPF PioneerRxSim: faithful runs
            // failed every row at "Could not open Item → Rx Item menu" because only MenuBar was tried.)
            var menuBar = mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.MenuBar))
                ?? mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.Menu));
            _logger.Debug("OpenRxItemDialog: menuBar found={Found} ct={Ct}", menuBar != null, menuBar?.ControlType);
            if (menuBar == null) return false;

            var (itemMenu, itemRes) = resolver.FindFirst(menuBar, cf, SelectorStepId.OpenItemMenu, cf.ByName(ItemMenuName));
            if (itemMenu == null)
            {
                // The bar's automation peer may not expose its MenuItem children as walkable descendants
                // of the bar element. Search the whole window for the "Item" MenuItem by name — robust to
                // odd menu nesting. The count+names log tells us whether the items are in the tree at all.
                var windowMenuItems = mainWindow.FindAllDescendants(cf.ByControlType(ControlType.MenuItem));
                _logger.Debug("OpenRxItemDialog: bar-scope miss; window MenuItems={Count}",
                    windowMenuItems.Length);
                itemMenu = windowMenuItems.FirstOrDefault(
                    m => string.Equals(m.Name, ItemMenuName, StringComparison.OrdinalIgnoreCase));
            }
            _logger.Debug("OpenRxItemDialog: structural menu target found={Found}", itemMenu != null);
            if (itemMenu == null) return false;
            LogIfLearned(SelectorStepId.OpenItemMenu, itemRes);
            PrepareVisibleAction(itemMenu, "Opening", "Item menu");

            // Open the "Item" submenu, then find "Rx Item" inside it. Rather than assume which pattern
            // this toolkit's menu honors, try each opener and keep whichever actually makes "Rx Item"
            // appear: menus open on ExpandCollapse (WPF ControlType.Menu), on Invoke, or only on a
            // physical mouse click (some MenuBar automation peers) — and the real PioneerRx menu's
            // control type is a screenshot-unanswerable unknown. (The WPF sim proved it: the stock-Menu
            // variant opens on Expand; the MenuBar-peer variant needed the physical click. Trying all
            // three, and stopping the moment "Rx Item" is findable, makes the workflow menu-toolkit-
            // agnostic.) The submenu renders in a POPUP (a separate top-level UIA element), so search
            // the DESKTOP ROOT first, then fall back to the main window.
            var searchRoot = mainWindow.Automation.GetDesktop();
            AutomationElement? rxItemEntry = null;
            for (var i = 0; i < MenuOpeners.Length; i++)
            {
                try
                {
                    ExecuteLiveMutation(() => MenuOpeners[i](itemMenu));
                }
                catch (PricingActuationGateClosedException) { throw; }
                catch { /* try the next opener */ }
                Thread.Sleep(300);
                var (found, res) = resolver.FindFirst(searchRoot, cf, SelectorStepId.OpenRxItem, cf.ByName(RxItemMenuName));
                if (found == null)
                    (found, res) = resolver.FindFirst(mainWindow, cf, SelectorStepId.OpenRxItem, cf.ByName(RxItemMenuName));
                _logger.Debug("OpenRxItemDialog: opener {N}/{Max} → rxItem found={Found}",
                    i + 1, MenuOpeners.Length, found != null);
                if (found != null) { rxItemEntry = found; LogIfLearned(SelectorStepId.OpenRxItem, res); break; }
            }
            if (rxItemEntry == null) return false;

            // "Rx Item" is a LEAF that opens the Edit Rx Item window — Invoke() fires it; expanding it
            // would be a no-op, so don't prefer Expand here. The caller's WaitForWindow confirms it opened.
            PrepareVisibleAction(rxItemEntry, "Opening", "Rx Item");
            OpenMenuElement(rxItemEntry, expandToOpenSubmenu: false);
            Thread.Sleep(300);
            return true;
        }
        catch (PricingActuationGateClosedException) { throw; }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: OpenRxItemDialog failed locally");
            return false;
        }
    }

    // Openers tried in order until the "Item" submenu appears — a menu may honor ExpandCollapse (WPF
    // stock Menu), Invoke, or only a physical mouse click (some MenuBar automation peers, incl. the
    // sim's PrxMenuBar). el.Click() is a real click at the element's center. Each call site guards throws.
    private static readonly Action<AutomationElement>[] MenuOpeners =
    {
        el => { if (el.Patterns.ExpandCollapse.IsSupported) el.Patterns.ExpandCollapse.Pattern.Expand(); },
        el => { if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); },
        el => el.Click(),
    };

    /// <summary>
    /// Activates a menu element by the UIA pattern it actually exposes, in order, so the menu works
    /// across UI toolkits (Win32 / WinForms / DevExpress / WPF) whose menu items respond to different
    /// patterns. A submenu-owning bar item ("Item") needs <c>ExpandCollapse.Expand()</c> to unfold; a
    /// leaf entry ("Rx Item") needs <c>Invoke()</c>; some only accept a synthesized Click(). Each attempt
    /// is guarded so an unsupported/throwing pattern falls through to the next rather than aborting.
    /// </summary>
    private void OpenMenuElement(AutomationElement el, bool expandToOpenSubmenu)
    {
        if (expandToOpenSubmenu)
        {
            try
            {
                if (el.Patterns.ExpandCollapse.IsSupported)
                {
                    ExecuteLiveMutation(() => el.Patterns.ExpandCollapse.Pattern.Expand());
                    return;
                }
            }
            catch (PricingActuationGateClosedException) { throw; }
            catch { /* fall through to Invoke/Click */ }
        }

        try
        {
            if (el.Patterns.Invoke.IsSupported)
            {
                ExecuteLiveMutation(() => el.Patterns.Invoke.Pattern.Invoke());
                return;
            }
        }
        catch (PricingActuationGateClosedException) { throw; }
        catch { /* fall through to Click */ }

        ExecuteLiveMutation(() => el.AsMenuItem()?.Click());
    }

}
