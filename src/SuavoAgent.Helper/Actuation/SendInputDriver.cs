using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Wraps Win32 <c>SendInput</c> for the four actuation primitives the
/// Phase-5.2 sandbox workflows need: type Unicode text, press a single key
/// chord, click at a point in screen coordinates, launch an allowlisted
/// sandbox app.
///
/// Every driver method:
///   1. Consults <see cref="ActuationGate"/> first. If the gate is closed,
///      returns immediately with the rejection envelope — Win32 is never
///      touched.
///   2. If <see cref="ActuationGate.IsDryRun"/> is true, computes the
///      evidence hash (so audit can prove what WOULD have been pressed) and
///      returns success without invoking SendInput.
///   3. Otherwise actually drives the OS, then returns the same evidence
///      hash for the audit row.
///
/// The driver itself does NOT decide whether actuation is safe — it asks
/// the gate. That separation is what lets the kill switch be authoritative.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SendInputDriver
{
    private readonly ActuationGate _gate;
    private readonly ActuationConfig _config;
    private readonly ILogger _logger;
    // Visual-only "agent is acting here" glow. Fired (fire-and-forget) at every real cursor move so the
    // operator can WATCH the agent work — the click point AND the type focus-click both flow through
    // MoveAndClick, so one call covers both. Null when the intent-cursor overlay isn't available; a
    // glow can never block, slow, or fail actuation (it only paints).
    private readonly SuavoAgent.Helper.IntentCursor.IntentCursorController? _intentCursor;

    // Persistent "agent presence" cursor — glides to the target and lands a pre-click reticle, then
    // pulses on click, and never disappears between actions. Preferred over the one-shot IntentCursor
    // flash when wired. Like the glow, it is purely visual and can never block/slow/fail actuation.
    private readonly SuavoAgent.Helper.Presence.PresenceController? _presence;

    // The window a preceding launch_sandbox_app established as the actuation target. type/press
    // re-assert + VERIFY this is foreground immediately before injecting input (they arrive as
    // separate IPC commands, seconds later — focus can drift in between). Set ONCE per launch on
    // this process-lifetime singleton; an unresolved launch stores the sentinel (Pid<=0 / Hwnd=0)
    // so the next type/press fails closed instead of leaking keystrokes. volatile: written on a
    // launch command thread, read on a later type/press command thread.
    private volatile TargetWindow? _activeTarget;

    private sealed record TargetWindow(int Pid, IntPtr Hwnd, string Label);

    /// <summary>
    /// PID of the window the last launch_sandbox_app established, or 0 if none / unresolved.
    /// Read-only snapshot of the volatile target — used ONLY by the sandbox capture path to
    /// validate the foreground before PrintWindow (never for keystroke injection).
    /// </summary>
    public int ActiveTargetPid => _activeTarget?.Pid ?? 0;

    /// <summary>
    /// HWND of the window the last launch_sandbox_app established, or IntPtr.Zero if none.
    /// Used by the sandbox capture path to construct a window-scoped PrintWindow capturer.
    /// A slightly-stale HWND is harmless: WindowScopedScreenCapture re-checks IsWindowVisible.
    /// </summary>
    public IntPtr ActiveTargetHwnd => _activeTarget?.Hwnd ?? IntPtr.Zero;

    // Ticks (UtcNow) of the last successful live click (click_by_label / click_by_signature →
    // ClickAtAsync). A real click establishes keyboard focus on the clicked control, so a type that
    // arrives shortly after must NOT re-focus the window centre (which would undo the click_by_label
    // and type into the wrong control — the PMS "click Quick Search then type the NDC" flow). volatile
    // long: written on a click command thread, read on a later type command thread. 0 = no click yet.
    private long _lastClickUtcTicks;
    private static readonly TimeSpan ClickFocusFreshWindow = TimeSpan.FromSeconds(15);

    public SendInputDriver(
        ActuationGate gate,
        ActuationConfig config,
        ILogger logger,
        SuavoAgent.Helper.IntentCursor.IntentCursorController? intentCursor = null,
        SuavoAgent.Helper.Presence.PresenceController? presence = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<SendInputDriver>();
        _intentCursor = intentCursor;
        _presence = presence;
    }

    /// <summary>
    /// Paint the "agent is acting here" glow at a screen point — fire-and-forget so it never blocks,
    /// slows, or can fail the actuation it accompanies (the overlay is purely visual). This is what
    /// makes the agent's work watchable: a brief accent-toned halo lands where the agent is about to
    /// click or set focus. No-op when the overlay isn't wired.
    /// </summary>
    private void TryGlow(int x, int y)
    {
        // Persistent presence cursor: glide to the target and land a reticle BEFORE the click,
        // so intent is shown before action (the beat one-shot flashes lack). Preferred when wired.
        var pres = _presence;
        if (pres is not null)
        {
            try { pres.MoveTo(x, y); pres.Reticle(x, y); }
            catch { /* visual-only — never break actuation */ }
            return;
        }

        // Legacy fallback: one-shot IntentCursor flash.
        var ic = _intentCursor;
        if (ic is null) return;
        try
        {
            _ = ic.ShowAsync(
                new IntentCursorRequest(
                    X: x, Y: y,
                    CoordinateSpace: IntentCursorCoordinateSpaces.Screen,
                    DurationMs: 1500, DiameterPx: 48, Opacity: 0.85,
                    Tone: IntentCursorTones.Agent),
                CancellationToken.None);
        }
        catch { /* visual-only — a glow must never break actuation */ }
    }

    public async Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        // Bug 21 effective-OR: either flag forces dry-run. Real input only fires
        // when BOTH the cloud workflow AND the local gate agree it's live.
        var effectiveDryRun = req.DryRun || _gate.IsDryRun;
        if (req.Text is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "text is null", effectiveDryRun);

        if (PhiPatternGuard.ContainsPotentialPhi(req.Text, out var matched))
        {
            _logger.Warning("TypeText rejected: PHI pattern matched={Pattern}", matched);
            return ActuationResult.Reject(
                ActuationRejectionCodes.PhiPatternDetected,
                $"input matched PHI pattern '{matched}' — sandbox actuation must not type PHI",
                effectiveDryRun);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var evidence = ComputeEvidenceHash("type_text", req.Text);
        var sw = Stopwatch.StartNew();

        if (effectiveDryRun)
        {
            _logger.Information(
                "TypeText DRY-RUN: chars={Length} evidence={Evidence} requestDryRun={ReqDR} gateDryRun={GateDR}",
                req.Text.Length, evidence, req.DryRun, _gate.IsDryRun);
            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: true, evidence);
        }

        try
        {
            // Fail-closed focus guard: confirm the launch-established target owns the foreground
            // BEFORE any keystroke (including ClearFirst's Ctrl+A/Delete), else refuse — never leak.
            // focusClick: type needs a focused edit control, so click the client area to set it.
            var fgReject = await EnsureTargetForegroundOrRejectAsync("type", focusClick: true, ct).ConfigureAwait(false);
            if (fgReject is not null) return fgReject;

            if (req.ClearFirst)
            {
                SendChord(new[] { VirtualKey.Control }, VirtualKey.A);
                await DelayWithCancel(_config.DefaultPerKeyDelayMs, ct).ConfigureAwait(false);
                SendChord(Array.Empty<VirtualKey>(), VirtualKey.Delete);
                await DelayWithCancel(_config.DefaultPerKeyDelayMs, ct).ConfigureAwait(false);
            }

            var perKeyDelay = req.PerKeyDelayMs > 0 ? req.PerKeyDelayMs : _config.DefaultPerKeyDelayMs;
            foreach (var ch in req.Text)
            {
                ct.ThrowIfCancellationRequested();
                if (_gate.CheckOrReject() is not null)
                {
                    return ActuationResult.Reject(
                        ActuationRejectionCodes.GatePaused,
                        "gate closed mid-type",
                        dryRun: false);
                }
                SendUnicodeChar(ch);
                await DelayWithCancel(perKeyDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "TypeText failed mid-execution");
            return ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, dryRun: false);
        }

        return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
    }

    public async Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var effectiveDryRun = req.DryRun || _gate.IsDryRun;
        if (req.Chords is null || req.Chords.Count == 0)
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "chords list empty", effectiveDryRun);

        var parsed = new List<KeyChord>(req.Chords.Count);
        foreach (var chordRaw in req.Chords)
        {
            if (!KeyChord.TryParse(chordRaw, out var chord) || chord is null)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ChordParseFailure,
                    $"could not parse chord '{chordRaw}'",
                    effectiveDryRun);
            }
            parsed.Add(chord);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var evidence = ComputeEvidenceHash("press_keys", string.Join(",", req.Chords));
        var sw = Stopwatch.StartNew();

        if (effectiveDryRun)
        {
            _logger.Information(
                "PressKeys DRY-RUN: chords=[{Chords}] evidence={Evidence} requestDryRun={ReqDR} gateDryRun={GateDR}",
                string.Join(",", req.Chords), evidence, req.DryRun, _gate.IsDryRun);
            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: true, evidence);
        }

        try
        {
            // Fail-closed focus guard: same reasoning as TypeText — verify the target owns foreground
            // before sending any chord, else refuse so keystrokes can't land in the wrong window.
            // No focus-click: chords route to the foreground window, and a content click could trip a
            // control (e.g. a Calculator button).
            var fgReject = await EnsureTargetForegroundOrRejectAsync("press_keys", focusClick: false, ct).ConfigureAwait(false);
            if (fgReject is not null) return fgReject;

            var interDelay = req.InterChordDelayMs > 0 ? req.InterChordDelayMs : _config.DefaultInterChordDelayMs;
            for (var i = 0; i < parsed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_gate.CheckOrReject() is not null)
                    return ActuationResult.Reject(ActuationRejectionCodes.GatePaused, "gate closed mid-chord", false);

                SendChord(parsed[i].Modifiers, parsed[i].MainKey);
                if (i < parsed.Count - 1) await DelayWithCancel(interDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PressKeys failed mid-execution");
            return ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, dryRun: false);
        }

        return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
    }

    public Task<ActuationResult> ClickAtAsync(int x, int y, bool dryRun, CancellationToken ct, int expectedPid = 0)
    {
        var effectiveDryRun = dryRun || _gate.IsDryRun;
        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return Task.FromResult(rejection with { DryRun = effectiveDryRun });

        var evidence = ComputeEvidenceHash("click_at", $"{x},{y}");
        var sw = Stopwatch.StartNew();

        if (effectiveDryRun)
        {
            _logger.Information(
                "ClickAt DRY-RUN: x={X} y={Y} evidence={Evidence} requestDryRun={ReqDR} gateDryRun={GateDR}",
                x, y, evidence, dryRun, _gate.IsDryRun);
            return Task.FromResult(ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: true, evidence));
        }

        // QA wave2 (agentic) TOCTOU guard: the element was resolved inside the allowlisted process, but
        // the window can move/close/be covered between resolve and this click. Unlike type/press, the
        // click path had NO re-assert, so it could land at stale coordinates in whatever is now there — a
        // wrong-target click into a PHI app once a PMS process is click-allowlisted. Re-confirm the
        // resolved process still owns the foreground at click time; fail closed otherwise. (expectedPid=0
        // from legacy callers / dry tooling skips the check, preserving existing behavior.)
        if (expectedPid > 0 && !SystemObservers.ForegroundGuard.IsPidForeground(expectedPid))
        {
            _logger.Warning(
                "ClickAt refused: resolved process (pid={Pid}) no longer owns the foreground at click time — failing closed (TOCTOU). {ForegroundOwner}",
                expectedPid, WindowFocusManager.DescribeForegroundWindow());
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                "target window lost foreground between resolve and click; refusing to click stale coordinates",
                dryRun: false));
        }

        try
        {
            TryGlow(x, y); // glide the presence cursor in + land the reticle at the click point
            MoveAndClick(x, y);
            try { _presence?.Click(x, y); } catch { /* visual-only — never break actuation */ }
            // Record the click so a TYPE arriving shortly after (the click_by_label → type field-entry
            // flow) does NOT re-focus the window centre and undo the focus this click just set.
            System.Threading.Interlocked.Exchange(ref _lastClickUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            return Task.FromResult(ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ClickAt failed");
            return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, dryRun: false));
        }
    }

    public async Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var effectiveDryRun = req.DryRun || _gate.IsDryRun;
        if (string.IsNullOrWhiteSpace(req.AppKey))
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "appKey is required", effectiveDryRun);

        if (!ActuationAllowlistedSandboxApps.ProcessNames.TryGetValue(req.AppKey, out var processName))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.AppNotInAllowlist,
                $"appKey '{req.AppKey}' is not in the sandbox allowlist (notepad, calculator)",
                effectiveDryRun);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var evidence = ComputeEvidenceHash("launch_sandbox_app", processName);
        var sw = Stopwatch.StartNew();

        if (effectiveDryRun)
        {
            _logger.Information(
                "LaunchSandboxApp DRY-RUN: process={Process} evidence={Evidence} requestDryRun={ReqDR} gateDryRun={GateDR}",
                processName, evidence, req.DryRun, _gate.IsDryRun);
            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: true, evidence);
        }

        try
        {
            // Snapshot visible windows BEFORE launch so a window that appears afterward can be told
            // apart from pre-existing ones (resolution fallback #3).
            var preLaunch = WindowFocusManager.CaptureVisibleTopLevelWindows();

            // Resolve to an absolute path under a trusted system location (System32, then Windows)
            // before launching. A bare process name is resolved by Win32 against the app dir + CWD
            // FIRST, so a planted "notepad.exe" could hijack the launch; pinning the real system path
            // closes that. Falls back to the bare name (PATH resolution) only if not found there.
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ResolveTrustedSystemPath(processName),
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Environment.SystemDirectory,
            });

            // WINDOW-FOCUS: Process.Start returns before the app's window exists, and for Windows 11
            // packaged apps (Notepad/Calculator) the started exe is a launcher STUB whose own
            // MainWindowHandle never populates — so the old "foreground p.MainWindowHandle" approach
            // silently did nothing and a later type/press leaked into whatever window was focused
            // (observed live: keystrokes into a PowerShell prompt). Resolve the REAL window by process
            // name / new-window heuristics, then force it foreground. We still record the target either
            // way: type/press re-verify foreground before injecting and FAIL CLOSED if it isn't the
            // target, so an imperfect launch can never leak keystrokes.
            // Offload the blocking wait + poll-for-window to a worker thread so the IPC dispatcher
            // isn't held for up to ~8s. `p` outlives the awaited task (disposed at method end).
            var resolved = await Task.Run(() =>
            {
                try { p?.WaitForInputIdle(3000); } catch { /* console / non-GUI apps don't support this */ }
                return WindowFocusManager.ResolveAppWindow(p, processName, preLaunch, 8000, ct, _logger);
            }, ct).ConfigureAwait(false);
            // A launch starts a fresh interaction: clear any prior click recency so a following type
            // takes the centre focus-click path (a stale click from an earlier workflow must not make
            // launch→type skip it, which would type into an unfocused control).
            System.Threading.Interlocked.Exchange(ref _lastClickUtcTicks, 0);
            if (resolved is { } rw && rw.Hwnd != IntPtr.Zero)
            {
                WindowFocusManager.ForceForeground(rw.Hwnd, _logger);
                _activeTarget = new TargetWindow(rw.Pid, rw.Hwnd, processName);
                if (WindowFocusManager.GetClientCenterScreen(rw.Hwnd) is { } c)
                    TryGlow(c.X, c.Y); // glow on the freshly-launched window so the operator sees the open
                _logger.Information(
                    "LaunchSandboxApp: {Process} resolved + foregrounded (pid={Pid} hwnd=0x{Hwnd:X})",
                    processName, rw.Pid, rw.Hwnd.ToInt64());
            }
            else
            {
                // Unresolved target sentinel: the launch may have succeeded, but we cannot prove which
                // window is the app's. The next type/press will fail closed rather than risk a leak.
                _activeTarget = new TargetWindow(0, IntPtr.Zero, processName);
                _logger.Warning(
                    "LaunchSandboxApp: {Process} started but its window could not be resolved — subsequent type/press will fail closed to avoid keystroke leak",
                    processName);
            }

            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LaunchSandboxApp failed for {Process}", processName);
            return ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, dryRun: false);
        }
    }

    /// <summary>
    /// Re-assert and VERIFY that the launch-established target window owns the foreground right
    /// before injecting input. Returns a rejection envelope (caller returns it unchanged) when the
    /// target cannot be confirmed foreground — fail-closed so keystrokes never land in the wrong
    /// window. Returns <c>null</c> to proceed. No active target means no preceding launch this
    /// session: legacy behaviour (type into the current foreground) is preserved with a warning,
    /// since the sandbox workflow always launches first and that is the only live actuation path.
    /// </summary>
    private async Task<ActuationResult?> EnsureTargetForegroundOrRejectAsync(string verb, bool focusClick, CancellationToken ct)
    {
        var target = _activeTarget;
        if (target is null)
        {
            _logger.Warning("{Verb}: no actuation target established (no preceding launch) — using current foreground window", verb);
            return null;
        }

        if (target.Pid <= 0 || target.Hwnd == IntPtr.Zero)
        {
            _logger.Warning("{Verb} refused: launch could not resolve a target window for '{Label}'", verb, target.Label);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                $"launch could not resolve a target window for '{target.Label}'; refusing to {verb} to avoid keystroke leak",
                dryRun: false);
        }

        // STEP 1 — bring the target to the top and confirm it owns the foreground. Fail closed if it
        // can't be acquired, so keystrokes never land in the wrong window.
        var sw = Stopwatch.StartNew();
        var acquired = false;
        while (sw.ElapsedMilliseconds < ForegroundAcquireTimeoutMs && !ct.IsCancellationRequested)
        {
            // QA wave2 (agentic): honor a mid-acquire gate pause/kill. The per-key/per-chord loops
            // already re-check the gate, but this up-to-6s foreground-acquire loop did not — so a
            // pharmacist who grabbed focus during the acquire window got it yanked back every 150ms
            // (the user-input pause was set but never consulted here). Abort the focus-steal instead.
            var pausedReject = _gate.CheckOrReject();
            if (pausedReject is not null)
            {
                _logger.Information("{Verb} aborted mid-foreground-acquire: gate closed (user activity / kill)", verb);
                return pausedReject;
            }
            if (SystemObservers.ForegroundGuard.IsPidForeground(target.Pid)) { acquired = true; break; }
            WindowFocusManager.ForceForeground(target.Hwnd, _logger);
            await DelayWithCancel(150, ct).ConfigureAwait(false);
        }

        if (!acquired)
        {
            // PID stays in the LOCAL log for diagnosis but NOT in the cloud-facing reason: a 5-digit
            // PID trips the cloud's ZIP-code PHI filter, which would null the result and mask this
            // legitimate operational failure as an opaque "result_rejected_phi_validation".
            _logger.Warning(
                "{Verb} refused: target '{Label}' (pid={Pid}) not brought to foreground within {Timeout}ms — failing closed; {ForegroundOwner}",
                verb, target.Label, target.Pid, ForegroundAcquireTimeoutMs,
                WindowFocusManager.DescribeForegroundWindow());
            return ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                $"target '{target.Label}' could not be brought to the foreground within {ForegroundAcquireTimeoutMs}ms; refusing to {verb} to avoid keystroke leak into the wrong window",
                dryRun: false);
        }

        // STEP 2 — give the target's editable control REAL keyboard focus (TYPE only). A window
        // force-foregrounded via AttachThreadInput is visually on top, but its child edit control's
        // keyboard focus is TRANSIENT — observed live: typing immediately landed a few chars, but after
        // a settle every keystroke dropped (focus had decayed) and the text went nowhere. A
        // physical-style click in the client area sets genuine, stable focus exactly the way a human
        // clicks before typing. Gated to TYPE: press_keys sends chords that route to the foreground
        // window (and a content click could trigger a control, e.g. a Calculator button), so it skips
        // the click and relies on the foreground + SetFocus established above.
        //
        // BUT skip the centre focus-click when a real click just landed (click_by_label → type): that
        // click already focused the SPECIFIC control the workflow targeted, and re-clicking the window
        // centre would move focus to the wrong control (wrong-field data entry). The foreground is still
        // verified above + below; we only skip the extra centre click.
        if (focusClick && ClickRecentlyEstablishedFocus())
        {
            _logger.Information(
                "{Verb}: a click established field focus <{Window}s ago — skipping centre focus-click to preserve the targeted control",
                verb, (int)ClickFocusFreshWindow.TotalSeconds);
        }
        else if (focusClick)
        {
            // Re-confirm the target STILL owns the foreground FIRST, then read its client-area coords and
            // click adjacently — so the coordinates are the freshest possible read right before the
            // physical click and a window that moved/closed since STEP 1 can't catch a stray click.
            if (!SystemObservers.ForegroundGuard.IsPidForeground(target.Pid))
            {
                _logger.Warning("{Verb} refused: '{Label}' lost foreground before focus-click — failing closed", verb, target.Label);
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    $"target '{target.Label}' lost the foreground before focus-click; refusing to {verb} to avoid a stray click",
                    dryRun: false);
            }

            var center = WindowFocusManager.GetClientCenterScreen(target.Hwnd);
            if (center is not { } pt)
            {
                // A focusClick verb (type) NEEDS a focused edit control. If we can't locate the target's
                // client area we can't guarantee that, so fail closed rather than type blind — a dropped
                // or partial value is worse than none ("one line of truth must be real").
                _logger.Warning("{Verb} refused: could not locate client area of '{Label}' to set input focus — failing closed", verb, target.Label);
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    $"could not locate target '{target.Label}' client area to set input focus; refusing to {verb}",
                    dryRun: false);
            }

            TryGlow(pt.X, pt.Y); // glow at the focus-click point (TYPE flow) so typing is watchable too
            try { MoveAndClick(pt.X, pt.Y); }
            catch (Exception ex) { _logger.Warning(ex, "{Verb}: focus-click failed for '{Label}'", verb, target.Label); }
            await DelayWithCancel(FocusSettleMs, ct).ConfigureAwait(false);
        }

        // STEP 3 — final re-confirm the target is still foreground after the focus-click (a click can't
        // change which window is foreground here, but verify rather than assume). Fail closed otherwise.
        if (SystemObservers.ForegroundGuard.IsPidForeground(target.Pid)) return null;

        // PID stays in the LOCAL log only (see note above) — the cloud-facing reason omits it so it
        // doesn't trip the ZIP-code PHI filter and mask this operational failure.
        _logger.Warning(
            "{Verb} refused: target '{Label}' (pid={Pid}) lost foreground after focus-click — failing closed; {ForegroundOwner}",
            verb, target.Label, target.Pid, WindowFocusManager.DescribeForegroundWindow());
        return ActuationResult.Reject(
            ActuationRejectionCodes.ForegroundNotTarget,
            $"target '{target.Label}' did not hold the foreground; refusing to {verb} to avoid keystroke leak into the wrong window",
            dryRun: false);
    }

    // Focus settle after the focus-click: a short pause so the click's WM_SETFOCUS is processed and the
    // edit control is genuinely input-ready before the first keystroke. The acquire timeout bounds the
    // fail-closed wait while we try to bring the target to the foreground.
    private const int FocusSettleMs = 250;
    private const int ForegroundAcquireTimeoutMs = 6000;

    /// <summary>
    /// True if a real click (click_by_label/click_by_signature → ClickAtAsync) landed within the last
    /// <see cref="ClickFocusFreshWindow"/>. When so, a TYPE skips its centre focus-click so it doesn't
    /// move focus off the control the click just targeted (the click→type field-entry flow).
    /// </summary>
    private bool ClickRecentlyEstablishedFocus()
    {
        var ticks = System.Threading.Interlocked.Read(ref _lastClickUtcTicks);
        if (ticks == 0) return false;
        var since = DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero);
        return since >= TimeSpan.Zero && since <= ClickFocusFreshWindow;
    }

    /// <summary>
    /// Resolve a bare process file name (e.g. "notepad.exe", "msedge.exe") to its absolute path,
    /// defeating the app-dir/CWD launch-hijack a bare name is subject to. Resolution order, each
    /// step trusted (admin-write-only) so none reopens the hijack hole a CWD/PATH lookup would:
    ///   1. System32, then the Windows dir — where in-box system apps (notepad, calc, explorer) live.
    ///   2. HKLM <c>App Paths\&lt;exe&gt;</c> (64- then 32-bit view) — the canonical Windows registry for
    ///      "where is this exe" that per-machine installers (Edge, Chrome) write. HKLM is
    ///      admin-write-only, so resolving from it preserves the anti-hijack guarantee while letting
    ///      non-System32 apps launch. The path is File.Exists-verified before it is trusted.
    /// Returns the bare name unchanged if none resolve (best-effort PATH resolution).
    /// </summary>
    private string ResolveTrustedSystemPath(string processName)
    {
        try
        {
            var sys32 = System.IO.Path.Combine(Environment.SystemDirectory, processName);
            if (System.IO.File.Exists(sys32)) return sys32;
            var win = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), processName);
            if (System.IO.File.Exists(win)) return win;

            var appPaths = ResolveViaAppPaths(processName);
            if (appPaths is not null)
            {
                _logger.Information(
                    "ResolveTrustedSystemPath: {Process} resolved via HKLM App Paths -> {Path}",
                    processName, appPaths);
                return appPaths;
            }
        }
        catch { /* fall through to bare name → PATH resolution */ }
        return processName;
    }

    /// <summary>
    /// Look up an exe's absolute path from HKLM <c>SOFTWARE\Microsoft\Windows\CurrentVersion\App
    /// Paths\&lt;exe&gt;</c> (default value), checking both the 64-bit and 32-bit registry views (Edge
    /// is a 32-bit-view per-machine install). Only HKLM is consulted — never HKCU — because HKCU is
    /// user-writable and would reopen the launch-hijack vector this whole method exists to close.
    /// Returns null if the key is absent or the recorded path no longer exists on disk.
    /// </summary>
    private static string? ResolveViaAppPaths(string processName)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(subKey + processName);
                if (key?.GetValue(null) is string path)
                {
                    var trimmed = path.Trim().Trim('"');
                    if (trimmed.Length > 0 && System.IO.File.Exists(trimmed)) return trimmed;
                }
            }
            catch { /* try the other view */ }
        }
        return null;
    }

    public static string ComputeEvidenceHash(string verb, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes($"{verb}|{payload}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    private static async Task DelayWithCancel(int ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    // ── Win32 surface ───────────────────────────────────────────────────────

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKeyCode;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static int InputSize => Marshal.SizeOf<INPUT>();

    // Wrap every SendInput call so a partial/blocked injection becomes a thrown
    // Win32Exception instead of silent acceptance — Codex adversarial review of
    // SuavoAgent#65 (Bug 22) flagged that swallowing the return value was the
    // diagnostic gap that hid the Identification-vs-Impersonation token bug for
    // weeks. Exceptions bubble into the existing try/catch in the public verbs.
    private static void SendInputOrThrow(string verb, INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, InputSize);
        SendInputValidator.EnsureFullyInjected(verb, sent, inputs.Length, Marshal.GetLastWin32Error());
    }

    private static void SendUnicodeChar(char ch)
    {
        var down = new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion { Keyboard = new KEYBDINPUT { ScanCode = ch, Flags = KEYEVENTF_UNICODE } },
        };
        var up = down;
        up.U.Keyboard.Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        var inputs = new[] { down, up };
        SendInputOrThrow("type_text_char", inputs);
    }

    private static void SendChord(IReadOnlyList<VirtualKey> modifiers, VirtualKey mainKey)
    {
        var total = (modifiers.Count * 2) + 2;
        var buffer = new INPUT[total];
        var idx = 0;
        for (var i = 0; i < modifiers.Count; i++)
        {
            buffer[idx++] = KeyDown(modifiers[i]);
        }
        buffer[idx++] = KeyDown(mainKey);
        buffer[idx++] = KeyUp(mainKey);
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            buffer[idx++] = KeyUp(modifiers[i]);
        }
        SendInputOrThrow("send_chord", buffer);
    }

    private static INPUT KeyDown(VirtualKey vk) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKeyCode = (ushort)vk } },
    };

    private static INPUT KeyUp(VirtualKey vk) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKeyCode = (ushort)vk, Flags = KEYEVENTF_KEYUP } },
    };

    private static void MoveAndClick(int x, int y)
    {
        // Use absolute virtual-screen coordinates so the click lands on the
        // exact requested point regardless of DPI / multimon configuration.
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        var dx = (int)(((double)(x - vx) / Math.Max(1, vw)) * 65535);
        var dy = (int)(((double)(y - vy) / Math.Max(1, vh)) * 65535);

        var move = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion { Mouse = new MOUSEINPUT { Dx = dx, Dy = dy, Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } },
        };
        var down = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion { Mouse = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTDOWN } },
        };
        var up = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion { Mouse = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTUP } },
        };

        var inputs = new[] { move, down, up };
        SendInputOrThrow("move_and_click", inputs);
    }
}
