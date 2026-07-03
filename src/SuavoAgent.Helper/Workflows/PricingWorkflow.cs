using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Executes the PioneerRx pricing lookup workflow via UIA:
///   Item menu → Rx Item → Quick Search (NDC) → Pricing tab → read supplier grid
///
/// Navigation path confirmed from field screenshots (Apr 4, 2026):
///   - Top menu: Item → Rx Item opens "Edit Rx Item" window
///   - Quick Search field at top accepts NDC
///   - Pricing tab shows supplier catalog with Cost, Cost Per Unit columns
///   - Cheapest = row with lowest Cost Per Unit (sorted ascending by default)
/// </summary>
public sealed class PricingWorkflow
{
    private readonly PioneerRxUiaEngine _engine;
    private readonly ILogger _logger;
    // Vision-primary grid reader (reads "the one on top" by sight via OCR). Null = vision off →
    // UIA-only, exactly today's behavior. When present, the vision read drives and the UIA read
    // verifies the exact cost (VisionExactReconciler) so a misread never writes wrong pricing.
    private readonly VisionPricingGridReader? _visionReader;

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

    public PricingWorkflow(PioneerRxUiaEngine engine, ILogger logger, VisionPricingGridReader? visionReader = null)
    {
        _engine = engine;
        _logger = logger;
        _visionReader = visionReader;
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
        var resolver = new SelectorResolver(request.Patches);

        var mainWindow = _engine.MainWindow;
        if (mainWindow == null)
            return Done(Fail(request, "PioneerRx main window not available"));

        try
        {
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
            var editWindow = WaitForWindow(automation, EditRxItemWindowTitle);
            if (editWindow == null)
            {
                Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Failed, SelectorFailureKind.Timeout);
                return Done(Fail(request, "Edit Rx Item window did not appear"));
            }

            try
            {
                // Step 3: Type NDC into Quick Search and press Enter
                if (!SearchByNdc(editWindow, cf, request.Ndc, resolver, out var searchBox))
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
                    return Done(Fail(request, $"Loaded item NDC does not match {request.Ndc} — item may not exist or search timed out"));
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

                // Step 5: Read the supplier grid. VISION-PRIMARY (SEE "the one on top" via OCR) +
                // EXACT-VERIFY (UIA cell value) so an OCR misread never writes a wrong cost. When
                // vision is off this collapses to exactly today's UIA-only read.
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
                        _logger.Warning("PricingWorkflow: NDC {Ndc} pricing read NOT confirmed: {Reason}",
                            request.Ndc, decision.RejectReason);
                        return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                            false, null, null, gridFailure ?? decision.RejectReason ?? "Pricing read not confirmed"));
                    }

                    Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Resolved, SelectorFailureKind.None);
                    _logger.Debug("PricingWorkflow: NDC {Ndc} → {Supplier} @ {Cost}/unit (source {Src})",
                        request.Ndc, decision.Supplier, decision.CostPerUnit, decision.Source);
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

                _logger.Debug("PricingWorkflow: NDC {Ndc} → {Supplier} @ {Cost}/unit",
                    request.Ndc, uiaCheapest.Value.supplier, uiaCheapest.Value.costPerUnit);

                return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                    true, uiaCheapest.Value.supplier, uiaCheapest.Value.costPerUnit, null));
            }
            finally
            {
                // Always close the Edit Rx Item dialog — press Escape
                TryCloseEditWindow(editWindow, cf);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PricingWorkflow: unhandled error for NDC {Ndc}", request.Ndc);
            // Compliance: never upload a raw exception message (uncontrolled free text — possible
            // leak vector). The real exception is in the local log above; the corpus/cloud get a
            // generic reason + the structured observations only.
            return Done(Fail(request, "Pricing lookup failed (unhandled error)"));
        }
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
                .ToList();
        }
        catch
        {
            return Array.Empty<ObservedElement>();
        }
    }

    private static ObservedElement ToObserved(AutomationElement el)
    {
        string controlType;
        try { controlType = el.ControlType.ToString(); } catch { controlType = "Unknown"; }
        string? automationId;
        try { automationId = NullIfEmpty(el.AutomationId); } catch { automationId = null; }
        string? className;
        try { className = NullIfEmpty(el.ClassName); } catch { className = null; }
        return new ObservedElement(controlType, automationId, className);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Visibility into learned-patch usage during a run. The FallbackUsed line is the early
    // signal that a learned selector has drifted (input to later auto-retirement / M2c-M2d).
    private void LogIfLearned(SelectorStepId step, SelectorResolver.Resolution res)
    {
        if (res.ResolvedVia == SelectorResolvedVia.Learned)
            _logger.Information("PricingWorkflow: step {Step} resolved via learned patch {PatchId}", step, res.PatchId);
        else if (res.Outcome == SelectorOutcome.FallbackUsed)
            _logger.Warning("PricingWorkflow: step {Step} learned patch {PatchId} missed — used builtin fallback", step, res.PatchId);
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
                _logger.Debug("OpenRxItemDialog: bar-scope miss; window MenuItems={Count} names=[{Names}]",
                    windowMenuItems.Length, string.Join(",", windowMenuItems.Select(m => m.Name)));
                itemMenu = windowMenuItems.FirstOrDefault(
                    m => string.Equals(m.Name, ItemMenuName, StringComparison.OrdinalIgnoreCase));
            }
            _logger.Debug("OpenRxItemDialog: itemMenu '{Name}' found={Found}", ItemMenuName, itemMenu != null);
            if (itemMenu == null) return false;
            LogIfLearned(SelectorStepId.OpenItemMenu, itemRes);

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
                try { MenuOpeners[i](itemMenu); } catch { /* try the next opener */ }
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
            OpenMenuElement(rxItemEntry, expandToOpenSubmenu: false);
            Thread.Sleep(300);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: OpenRxItemDialog failed");
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
    private static void OpenMenuElement(AutomationElement el, bool expandToOpenSubmenu)
    {
        if (expandToOpenSubmenu)
        {
            try
            {
                if (el.Patterns.ExpandCollapse.IsSupported)
                {
                    el.Patterns.ExpandCollapse.Pattern.Expand();
                    return;
                }
            }
            catch { /* fall through to Invoke/Click */ }
        }

        try
        {
            if (el.Patterns.Invoke.IsSupported)
            {
                el.Patterns.Invoke.Pattern.Invoke();
                return;
            }
        }
        catch { /* fall through to Click */ }

        el.AsMenuItem()?.Click();
    }

    private Window? WaitForWindow(UIA2Automation automation, string title)
    {
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desktop = automation.GetDesktop();
                var cf = automation.ConditionFactory;
                var win = desktop.FindFirstDescendant(cf.ByName(title))?.AsWindow();
                if (win != null) return win;
            }
            catch { }
            Thread.Sleep(200);
        }
        return null;
    }

    // SearchByNdc was Codex-flagged as brittle for 500-row batches:
    // the original sequence was Focus -> Sleep(100) -> Ctrl+A -> Sleep(50) ->
    // Type(ndc) -> Sleep(100) -> Enter -> Sleep(800) with no verification.
    // Window focus changes mid-row (toast popups, antivirus dialogs, or just
    // OS desktop redraws) would silently drop keystrokes or fire Enter against
    // the wrong control. Now we:
    //   (1) find the search box,
    //   (2) attempt the type-and-press sequence up to MaxTypeAttempts times,
    //       verifying after each that the box actually contains the NDC we
    //       intended to send before pressing Enter,
    //   (3) bail with false (caller writes a failure result for the row) if
    //       all attempts fail rather than firing Enter against an unknown state.
    private const int MaxTypeAttempts = 2;

    private bool SearchByNdc(Window editWindow, ConditionFactory cf, string ndc, SelectorResolver resolver,
        out AutomationElement? resolvedSearchBox)
    {
        resolvedSearchBox = null;
        try
        {
            // Quick Search is a text box near the top of the Edit Rx Item window
            var deadline = DateTime.UtcNow + ElementTimeout;
            AutomationElement? searchBox = null;
            while (DateTime.UtcNow < deadline)
            {
                // Learned patch first, then the builtin (ControlType=Edit + HelpText "Quick Search").
                var builtin = new FlaUI.Core.Conditions.AndCondition(
                    cf.ByControlType(ControlType.Edit),
                    cf.ByHelpText(QuickSearchHint));
                var (box, res) = resolver.FindFirst(editWindow, cf, SelectorStepId.QuickSearchField, builtin);
                searchBox = box;
                if (searchBox != null) LogIfLearned(SelectorStepId.QuickSearchField, res);

                if (searchBox == null)
                {
                    // Fallback: first Edit control at the top of the window
                    var edits = editWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit));
                    searchBox = edits.FirstOrDefault();
                }

                if (searchBox != null) break;
                Thread.Sleep(200);
            }

            if (searchBox == null) return false;

            // Capture the resolved box so VerifyLoadedNdc can exclude this exact
            // element by identity (RuntimeId) — robust even when HelpText is absent.
            resolvedSearchBox = searchBox;

            for (int attempt = 1; attempt <= MaxTypeAttempts; attempt++)
            {
                searchBox.Focus();
                Thread.Sleep(100);

                // Clear existing text then type NDC
                Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                Thread.Sleep(50);
                Keyboard.Type(ndc);
                Thread.Sleep(150);

                // Before firing Enter, confirm the search box actually contains
                // the NDC we just typed. A common failure mode is that the OS
                // shifted focus between Focus() and Type() — Ctrl+A was a no-op
                // and Type() landed in some other window. Polling the textbox
                // value here catches that without nuking the user's foreground.
                if (SearchBoxContainsNdc(searchBox, ndc))
                {
                    Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                    Thread.Sleep(800);
                    return true;
                }

                _logger.Warning(
                    "PricingWorkflow: SearchByNdc attempt {Attempt}/{Max} — search box does not contain {Ndc} after type; retrying",
                    attempt, MaxTypeAttempts, ndc);
                // Brief backoff before next attempt — gives a transient focus
                // thief (notification, modal) time to clear.
                Thread.Sleep(300);
            }

            _logger.Warning(
                "PricingWorkflow: SearchByNdc giving up on {Ndc} after {Max} attempts — never confirmed text landed in Quick Search",
                ndc, MaxTypeAttempts);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: SearchByNdc failed for {Ndc}", ndc);
            return false;
        }
    }

    private static bool SearchBoxContainsNdc(AutomationElement searchBox, string ndc)
    {
        try
        {
            var text = searchBox.AsTextBox()?.Text ?? searchBox.Name ?? "";
            if (string.IsNullOrEmpty(text)) return false;
            // Match permissively — PioneerRx may apply input masks (5-4-2) or
            // strip non-digits. Compare digit-only forms.
            var typedDigits = new string(ndc.Where(char.IsDigit).ToArray());
            var observedDigits = new string(text.Where(char.IsDigit).ToArray());
            return observedDigits.Contains(typedDigits, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies that the Edit Rx Item window contains the expected NDC after Quick Search loads.
    /// Scans the loaded item's text-bearing elements for the normalized NDC (hyphens removed,
    /// 11 digits) — EXCLUDING the Quick Search box, which retains the typed query and would
    /// tautologically "verify" any NDC (see <see cref="IsQuickSearchField"/>).
    /// Returns false if the NDC is not found within the element timeout, indicating the wrong
    /// item was loaded or no result was returned.
    /// </summary>
    private bool VerifyLoadedNdc(Window editWindow, ConditionFactory cf, string ndc, AutomationElement? searchBox)
    {
        // Caller already normalized to 11-digit canonical form upstream (ExcelPricingReader).
        // If this invariant breaks we'd silently match shorter substrings, so assert + fall back.
        var normalizedNdc = NdcNormalizer.TryNormalize(ndc);
        if (string.IsNullOrEmpty(normalizedNdc))
        {
            _logger.Warning("PricingWorkflow: cannot normalize NDC '{Ndc}' for verification", ndc);
            return false;
        }

        // Identity of the Quick Search box we typed into, read once. VerifyLoadedNdc
        // excludes that exact element so the tautology (matching the box's own echoed
        // query) can't pass — by identity, not HelpText, so it holds with no HelpText.
        var searchBoxRid = TryGetRuntimeId(searchBox);

        var lastSeen = new List<string>();
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Materialize once. [C-3] NEVER verify against the Quick Search box: it still holds
                // the NDC we just typed, so matching it is tautological. Exclude it by element
                // IDENTITY (the exact box SearchByNdc typed into) so the exclusion holds even when
                // PioneerRx exposes no HelpText; verify only the loaded item.
                var texts = editWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit))
                    .Concat(editWindow.FindAllDescendants(cf.ByControlType(ControlType.Text)))
                    .Where(el => !IsSearchBox(el, searchBoxRid))
                    .Select(SafeText)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
                lastSeen = texts;

                // Do-Not-Use must be a FULL-PASS check BEFORE any NDC match. PioneerRx returns a red
                // "(Do Not Use)" duplicate sharing the active item's NDC; the NDC lives in an Edit
                // field (enumerated first) while the "(Do Not Use)" marker is a separate Text label.
                // A per-element early-return on the NDC match would accept the item before the marker
                // element is ever inspected — the exact hole this guard exists to close. Scan ALL
                // candidates for the marker first; if any hits, refuse to price.
                if (texts.Any(PricingGridReader.LooksLikeDoNotUse))
                {
                    _logger.Warning(
                        "PricingWorkflow: loaded item for NDC {Ndc} is marked Do Not Use — refusing to price",
                        ndc);
                    return false;
                }

                foreach (var raw in texts)
                {
                    // PioneerRx may display the NDC in any supported shape; normalize before compare
                    // to avoid false negatives on 4-4-2 / 5-3-2 layouts.
                    if (NdcNormalizer.TryNormalize(raw.Trim()) == normalizedNdc)
                        return true;

                    // Fallback: substring check against digit-only form, for cases where the NDC
                    // is embedded inside a longer descriptor ("NDC 50242-0041-21 — OMEPRAZOLE …")
                    var digitsOnly = new string(raw.Where(char.IsDigit).ToArray());
                    if (digitsOnly.Contains(normalizedNdc, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            Thread.Sleep(300);
        }

        _logger.Warning("PricingWorkflow: NDC {Ndc} not found in loaded item after {Timeout}s",
            ndc, ElementTimeout.TotalSeconds);
        _logger.Debug("PricingWorkflow: verify miss for {Ndc} — Edit window texts=[{Seen}]",
            ndc, string.Join(" | ", lastSeen.Take(15)));
        return false;
    }

    /// <summary>
    /// Reads an element's text WITHOUT throwing. <c>el.AsTextBox().Text</c> reaches the Value pattern,
    /// which THROWS on a plain Text/Label control that doesn't implement it — and that exception,
    /// caught by the scan's try/catch, aborted the ENTIRE <see cref="VerifyLoadedNdc"/> pass before it
    /// could match (the loaded item's NDC lives in a Text label, e.g. PioneerRx's "NDC: 00093-5056-98").
    /// Prefer the Value pattern only when supported; otherwise use the element Name (a Text control's
    /// Name IS its rendered text). Never throws, so one unreadable control can't sink the whole scan.
    /// </summary>
    private static string SafeText(AutomationElement el)
    {
        try
        {
            if (el.Patterns.Value.IsSupported)
            {
                var v = el.Patterns.Value.Pattern.Value;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { /* Value not really available — fall back to Name */ }
        return el.Name ?? "";
    }

    /// <summary>
    /// True if <paramref name="el"/> is the Quick Search box that <see cref="VerifyLoadedNdc"/>
    /// must NOT verify against (it retains the typed query, so matching it confirms the requested
    /// NDC regardless of what actually loaded — a tautology).
    /// <para>
    /// PRIMARY: element identity. <paramref name="searchBoxRid"/> is the RuntimeId of the exact box
    /// <see cref="SearchByNdc"/> typed into; comparing RuntimeIds excludes that one element whether
    /// or not PioneerRx exposes HelpText, and never false-skips a different field that merely shares
    /// a HelpText string. FALLBACK: only when the box's identity is unknown (RuntimeId unreadable,
    /// e.g. the resolver never captured it) do we fall back to the HelpText marker — best effort,
    /// matching legacy behaviour.
    /// </para>
    /// </summary>
    private static bool IsSearchBox(AutomationElement el, int[]? searchBoxRid)
    {
        if (searchBoxRid is { Length: > 0 })
        {
            var rid = TryGetRuntimeId(el);
            if (rid != null) return RuntimeIdEquals(rid, searchBoxRid);
            // RuntimeId unreadable on this candidate — fall through to the HelpText marker.
        }
        return IsQuickSearchField(el);
    }

    /// <summary>RuntimeId of <paramref name="el"/>, or null if unavailable or the read throws.</summary>
    private static int[]? TryGetRuntimeId(AutomationElement? el)
    {
        if (el == null) return null;
        try { return el.Properties.RuntimeId.ValueOrDefault; }
        catch { return null; }
    }

    private static bool RuntimeIdEquals(int[] a, int[] b) => a.SequenceEqual(b);

    /// <summary>
    /// Degraded fallback identity check: matches the HelpText marker <see cref="SearchByNdc"/>
    /// resolves the box by (<see cref="QuickSearchHint"/>). Used only when RuntimeId identity is
    /// unavailable. The property fetch is guarded — a UIA read can throw, treated as "not the box".
    /// </summary>
    private static bool IsQuickSearchField(AutomationElement el)
    {
        try { return string.Equals(el.HelpText, QuickSearchHint, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private bool ClickPricingTab(Window editWindow, ConditionFactory cf, SelectorResolver resolver)
    {
        try
        {
            var deadline = DateTime.UtcNow + ElementTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var (pricingTab, res) = resolver.FindFirst(editWindow, cf, SelectorStepId.PricingTab, cf.ByName(PricingTabName));
                if (pricingTab != null)
                {
                    LogIfLearned(SelectorStepId.PricingTab, res);
                    pricingTab.AsTabItem()?.Select();
                    Thread.Sleep(500);
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: ClickPricingTab failed");
            return false;
        }
    }

    /// <summary>
    /// Reads the supplier catalog DataGrid on the Pricing tab and returns the entry
    /// with the lowest Cost Per Unit.
    ///
    /// Columns are resolved by header NAME, not ordinal (Codex M-4). If PioneerRx
    /// reorders, hides, or adds columns, the lookup still finds the right fields.
    /// On schema miss, fails closed with a distinct failure reason — the prior
    /// fallback-to-hardcoded-ordinals path was Codex-flagged as risking wrong
    /// supplier/cost data on a UI revision (data integrity is precedence 1).
    /// </summary>
    private (string supplier, decimal costPerUnit)? ReadCheapestSupplier(
        Window editWindow, ConditionFactory cf, out string? failureReason)
    {
        failureReason = null;
        try
        {
            var deadline = DateTime.UtcNow + GridLoadTimeout;
            AutomationElement? grid = null;
            while (DateTime.UtcNow < deadline)
            {
                grid = editWindow.FindFirstDescendant(cf.ByControlType(ControlType.Table))
                    ?? editWindow.FindFirstDescendant(cf.ByControlType(ControlType.DataGrid));
                if (grid != null) break;
                Thread.Sleep(200);
            }

            if (grid == null)
            {
                _logger.Debug("PricingWorkflow: no DataGrid found on Pricing tab");
                failureReason = "Pricing tab DataGrid not found";
                return null;
            }

            // Virtualized DevExpress grids load rows lazily — reading once can
            // catch a partial set and miss the true cheapest. Wait until the row
            // count stops changing before reading.
            var rows = WaitForStableRows(grid, cf, out var expectedRowCount);
            if (rows.Length == 0)
            {
                _logger.Debug("PricingWorkflow: Pricing grid has no rows");
                failureReason = "Pricing grid has no rows";
                return null;
            }

            // Precedence-1 (data integrity): if the grid reports more logical rows than we could
            // realize by scrolling, we're ranking a PARTIAL set and the true cheapest may be off
            // screen. Refuse rather than write a possibly non-cheapest supplier to the operator's
            // sheet — the row gets a clear status instead of plausible-looking wrong data.
            if (expectedRowCount > rows.Length)
            {
                _logger.Warning(
                    "PricingWorkflow: realized {Read}/{Total} pricing rows (virtualized) — refusing to rank a partial set",
                    rows.Length, expectedRowCount);
                failureReason = $"Pricing grid virtualized: read {rows.Length} of {expectedRowCount} supplier rows — refusing to rank a partial set";
                return null;
            }

            var cols = ResolvePricingColumns(grid, cf);
            if (cols is null)
            {
                // Fail-fast (Codex review): the schema didn't resolve, so we
                // cannot safely read cells by ordinal without risking a wrong-
                // column write back to the operator's Excel. Surface a clear
                // failure reason so the row's Status cell explains the miss.
                failureReason = "Pricing grid schema not recognized — Supplier/Cost columns missing or renamed";
                return null;
            }
            var (supplierIdx, costIdx, statusIdx) = cols.Value;

            var parsed = new List<PricingGridReader.SupplierRow>(rows.Length);
            foreach (var row in rows)
            {
                var cells = row.FindAllChildren(cf.ByControlType(ControlType.Custom))
                    .Concat(row.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
                    .ToArray();

                var needed = Math.Max(supplierIdx, Math.Max(costIdx, statusIdx));
                if (cells.Length <= needed) continue;

                // Read FULL cell text (Value / LegacyIAccessible pattern), not the
                // rendered Name — supplier names truncate in the grid
                // ("Mckesson Geri…") and a truncated name would be written back.
                var supplierText = GetCellText(cells[supplierIdx]);
                var costText = GetCellText(cells[costIdx]);
                var statusText = statusIdx >= 0 ? GetCellText(cells[statusIdx]) : "";

                if (!PricingGridReader.TryParseCost(costText, out var cost)) continue;
                parsed.Add(new PricingGridReader.SupplierRow(supplierText, cost, statusText));
            }

            _logger.Debug("ReadCheapest: {GridRows} grid rows → {Parsed} usable: [{Detail}]",
                rows.Length, parsed.Count,
                string.Join(" | ", parsed.Select(p => $"{p.Supplier}={p.CostPerUnit}/{p.Status}")));

            // Cheapest = min Cost across ALL usable rows (sort is user-toggleable;
            // never trust row 1) — discontinued/unavailable rows excluded.
            var cheapest = PricingGridReader.SelectCheapest(parsed);
            if (cheapest == null)
            {
                failureReason = "No usable supplier rows in Pricing tab";
                return null;
            }
            return cheapest;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: ReadCheapestSupplier error");
            failureReason = "Pricing grid read error";
            return null;
        }
    }

    /// <summary>
    /// Resolves the Supplier and Cost Per Unit column indices by header name.
    /// WPF DataGrid exposes headers as Header/HeaderItem control types.
    ///
    /// Codex review (2026-05-18) flagged the prior fallback-to-hardcoded-ordinals
    /// behavior: if PioneerRx ships a UI revision that reorders or renames the
    /// pricing grid columns, hardcoded ordinals (5, 10) would silently write
    /// the wrong cell values back to the operator's Excel sheet — a correctness
    /// bug masquerading as success. Pricing data integrity is precedence 1 here.
    ///
    /// Now: header miss returns null. Caller treats null as a typed failure
    /// ("Pricing grid schema not recognized") so the row gets a clear error in
    /// the Excel output instead of plausible-looking wrong data.
    /// </summary>
    private (int supplierIdx, int costIdx, int statusIdx)? ResolvePricingColumns(AutomationElement grid, ConditionFactory cf)
    {
        try
        {
            // Look for a Header descendant (WPF DataGrid exposes column headers as Header control)
            var header = grid.FindFirstDescendant(cf.ByControlType(ControlType.Header));
            if (header == null)
            {
                _logger.Warning("PricingWorkflow: no Header found in grid — failing closed (no ordinal fallback)");
                return null;
            }

            var headerCells = header.FindAllDescendants(cf.ByControlType(ControlType.HeaderItem));
            if (headerCells.Length == 0)
            {
                _logger.Warning("PricingWorkflow: Header has no HeaderItems — failing closed (no ordinal fallback)");
                return null;
            }

            // statusIdx is OPTIONAL — when present we honor "Include Discontinued
            // = No" defensively by skipping discontinued/unavailable rows even if
            // the grid's own filter bar wasn't pinned. Absence (-1) is fine.
            int supplierIdx = -1, costIdx = -1, statusIdx = -1;
            for (int i = 0; i < headerCells.Length; i++)
            {
                var name = headerCells[i].Name?.Trim() ?? "";
                if (supplierIdx == -1 && name.Equals("Supplier", StringComparison.OrdinalIgnoreCase))
                    supplierIdx = i;
                else if (costIdx == -1 &&
                         (name.Equals("Cost Per Unit", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals("Cost (per unit)", StringComparison.OrdinalIgnoreCase)))
                    costIdx = i;
                else if (statusIdx == -1 && name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    statusIdx = i;
            }

            if (supplierIdx == -1 || costIdx == -1)
            {
                _logger.Warning(
                    "PricingWorkflow: could not resolve Supplier/Cost columns by header name " +
                    "(Supplier={Sup}, Cost={Cost}) — failing closed (no ordinal fallback)",
                    supplierIdx, costIdx);
                return null;
            }

            _logger.Debug("PricingWorkflow: resolved columns — Supplier=col {Sup}, Cost Per Unit=col {Cost}, Status=col {Status}",
                supplierIdx, costIdx, statusIdx);
            return (supplierIdx, costIdx, statusIdx);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PricingWorkflow: column resolution error — failing closed (no ordinal fallback)");
            return null;
        }
    }

    /// <summary>
    /// Polls the grid's row count until it stabilizes (two consecutive equal,
    /// non-zero reads) or the load timeout elapses, then returns the rows.
    /// DevExpress grids virtualize — a single read can catch a partial set and
    /// miss the true cheapest supplier.
    /// </summary>
    private AutomationElement[] WaitForStableRows(AutomationElement grid, ConditionFactory cf, out int expectedRowCount)
    {
        expectedRowCount = TryGetGridRowCount(grid); // -1 when the grid exposes no logical count

        var deadline = DateTime.UtcNow + GridLoadTimeout;
        var scroll = grid.Patterns.Scroll.PatternOrDefault;
        bool canScroll = false;
        try { canScroll = scroll != null && scroll.VerticallyScrollable.ValueOrDefault; } catch { }

        // Accumulate realized rows across scroll positions, keyed by RuntimeId. A virtualized
        // DevExpress/WPF grid only exposes RENDERED rows via UIA, so a single-viewport read misses
        // off-screen rows — and the true cheapest supplier can be one of them. Scroll to the bottom
        // in increments, unioning rows, until we've realized the logical row count (or growth stops
        // / the load budget elapses). Without scroll support this degrades to the old stable read.
        var byId = new Dictionary<string, AutomationElement>();
        void Harvest()
        {
            int i = 0;
            foreach (var r in grid.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
            {
                var rid = TryGetRuntimeId(r);
                byId[rid is { Length: > 0 } ? string.Join(",", rid) : "ord:" + i] = r;
                i++;
            }
        }

        Harvest();
        int lastCount = byId.Count, stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (expectedRowCount > 0 && byId.Count >= expectedRowCount) break;

            bool scrolled = false;
            if (canScroll)
            {
                double pct = 100;
                try { pct = scroll!.VerticalScrollPercent.ValueOrDefault; } catch { }
                if (pct < 99.9)
                {
                    try { scroll!.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); scrolled = true; } catch { }
                }
            }

            Thread.Sleep(250);
            Harvest();

            if (byId.Count == lastCount)
            {
                // A lazily-loaded DevExpress/WPF grid materializes rows in TIMED BATCHES (the true
                // cheapest can be in a LATER batch — the sim proves this with a winner in the 2nd
                // batch at ~850ms). Breaking after ~500ms of stability caught only batch 1 and mis-
                // ranked. Require a longer no-growth window (~1.75s) so a late batch has time to land
                // before we conclude the set is complete. Still bounded by GridLoadTimeout.
                if (++stable >= 7 && !scrolled) break; // stopped growing and nothing left to scroll
            }
            else { stable = 0; lastCount = byId.Count; }
        }
        _logger.Debug("WaitForStableRows: harvested {Count} rows (canScroll={Scroll}, expectedCount={Expected})",
            byId.Count, canScroll, expectedRowCount);
        return byId.Values.ToArray();
    }

    /// <summary>Logical row count from the grid's Grid pattern, or -1 when unavailable. Used to
    /// tell a fully-realized read from a partial (still-virtualized) one so ranking can fail closed.</summary>
    private static int TryGetGridRowCount(AutomationElement grid)
    {
        try { return grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault ?? -1; }
        catch { return -1; }
    }

    /// <summary>
    /// Reads a cell's FULL value rather than its rendered Name. Grid cells
    /// truncate long text in the Name property ("Mckesson Geri…"); the
    /// ValuePattern / LegacyIAccessible value carries the complete string.
    /// Falls back to Name (the prior behavior) when no value pattern exists.
    /// </summary>
    private static string GetCellText(AutomationElement el)
    {
        try
        {
            var text = el.AsTextBox()?.Text;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { /* not a value-bearing element */ }

        try
        {
            var legacy = el.Patterns.LegacyIAccessible.PatternOrDefault;
            var v = legacy?.Value?.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch { /* legacy pattern unsupported */ }

        return el.Name?.Trim() ?? "";
    }

    private void TryCloseEditWindow(Window editWindow, ConditionFactory cf)
    {
        try
        {
            // Press Escape to dismiss — PioneerRx uses Escape to close dialogs
            editWindow.Focus();
            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: could not close Edit Rx Item window");
        }
    }

    private static SupplierPriceResult Fail(NdcPricingRequest req, string error) =>
        new(req.JobId, req.RowIndex, req.Ndc, false, null, null, error);
}
