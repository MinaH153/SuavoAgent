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
public sealed partial class SendInputDriver
{
    public enum TargetTrustKind
    {
        Unspecified = 0,
        Sandbox = 1,
        PioneerRx = 2,
    }

    private readonly ActuationGate _gate;
    private readonly ActuationConfig _config;
    private readonly ILogger _logger;
    private readonly PioneerRxProcessTrustVerifier? _pioneerRxTrust;
    private readonly Func<string?>? _focusedValueReader;
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

    private sealed record TargetWindow(int Pid, IntPtr Hwnd, string Label, TargetTrustKind TrustKind);

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
        SuavoAgent.Helper.Presence.PresenceController? presence = null,
        PioneerRxProcessTrustVerifier? pioneerRxTrust = null,
        Func<string?>? focusedValueReader = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<SendInputDriver>();
        _intentCursor = intentCursor;
        _presence = presence;
        _pioneerRxTrust = pioneerRxTrust;
        _focusedValueReader = focusedValueReader;
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

    public async Task<ActuationResult> TypeTextAsync(
        TypeTextRequest req,
        CancellationToken ct,
        TargetTrustKind requiredTargetKind = TargetTrustKind.Unspecified)
    {
        ArgumentNullException.ThrowIfNull(req);
        // Bug 21 effective-OR: either flag forces dry-run. Real input only fires
        // when BOTH the cloud workflow AND the local gate agree it's live.
        var effectiveDryRun = req.DryRun || _gate.IsDryRun;
        if (req.Text is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "text is null", effectiveDryRun);

        if (PhiPatternGuard.ContainsPotentialPhi(req.Text, out _))
        {
            _logger.Warning("TypeText rejected by local PHI pattern policy");
            return ActuationResult.Reject(
                ActuationRejectionCodes.PhiPatternDetected,
                "input rejected by local PHI pattern policy",
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
            var fgReject = await EnsureTargetForegroundOrRejectAsync(
                "type", focusClick: true, requiredTargetKind, req.ProcessName, ct).ConfigureAwait(false);
            if (fgReject is not null) return fgReject;

            if (req.ClearFirst)
            {
                var clearSelectReject = ExecuteTargetBoundMutationOrReject(
                    () => SendChord(new[] { VirtualKey.Control }, VirtualKey.A),
                    requiredTargetKind,
                    req.ProcessName);
                if (clearSelectReject is not null) return clearSelectReject;
                await DelayWithCancel(_config.DefaultPerKeyDelayMs, ct).ConfigureAwait(false);
                var clearDeleteReject = ExecuteTargetBoundMutationOrReject(
                    () => SendChord(Array.Empty<VirtualKey>(), VirtualKey.Delete),
                    requiredTargetKind,
                    req.ProcessName);
                if (clearDeleteReject is not null) return clearDeleteReject;
                await DelayWithCancel(_config.DefaultPerKeyDelayMs, ct).ConfigureAwait(false);
            }

            var perKeyDelay = req.PerKeyDelayMs > 0 ? req.PerKeyDelayMs : _config.DefaultPerKeyDelayMs;
            foreach (var ch in req.Text)
            {
                ct.ThrowIfCancellationRequested();
                var characterReject = ExecuteTargetBoundMutationOrReject(
                    () => SendUnicodeChar(ch),
                    requiredTargetKind,
                    req.ProcessName);
                if (characterReject is not null) return characterReject;
                await DelayWithCancel(perKeyDelay, ct).ConfigureAwait(false);
            }

            if (requiredTargetKind != TargetTrustKind.Unspecified)
            {
                var verification = await VerifyTypedTextAsync(
                    req.Text,
                    requiredTargetKind,
                    req.ProcessName,
                    ct).ConfigureAwait(false);
                if (verification is not null) return verification;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(
                "TypeText failed mid-execution ({ErrorType})",
                ex.GetType().Name);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "text input failed locally",
                dryRun: false);
        }

        return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
    }

    public async Task<ActuationResult> PressKeysAsync(
        PressKeysRequest req,
        CancellationToken ct,
        TargetTrustKind requiredTargetKind = TargetTrustKind.Unspecified)
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
                    "one or more key chords were invalid",
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
                "PressKeys DRY-RUN: chordCount={ChordCount} evidence={Evidence} requestDryRun={ReqDR} gateDryRun={GateDR}",
                req.Chords.Count, evidence, req.DryRun, _gate.IsDryRun);
            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: true, evidence);
        }

        try
        {
            // Fail-closed focus guard: same reasoning as TypeText — verify the target owns foreground
            // before sending any chord, else refuse so keystrokes can't land in the wrong window.
            // No focus-click: chords route to the foreground window, and a content click could trip a
            // control (e.g. a Calculator button).
            var fgReject = await EnsureTargetForegroundOrRejectAsync(
                "press_keys", focusClick: false, requiredTargetKind, req.ProcessName, ct).ConfigureAwait(false);
            if (fgReject is not null) return fgReject;

            var interDelay = req.InterChordDelayMs > 0 ? req.InterChordDelayMs : _config.DefaultInterChordDelayMs;
            for (var i = 0; i < parsed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chordReject = ExecuteTargetBoundMutationOrReject(
                    () => SendChord(parsed[i].Modifiers, parsed[i].MainKey),
                    requiredTargetKind,
                    req.ProcessName);
                if (chordReject is not null) return chordReject;
                if (i < parsed.Count - 1) await DelayWithCancel(interDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(
                "PressKeys failed mid-execution ({ErrorType})",
                ex.GetType().Name);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "key input failed locally",
                dryRun: false);
        }

        return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
    }

    public Task<ActuationResult> ClickAtAsync(
        int x,
        int y,
        bool dryRun,
        CancellationToken ct,
        int expectedPid = 0,
        string? expectedProcess = null,
        TargetTrustKind targetTrustKind = TargetTrustKind.Unspecified)
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
        // Uses the UWP-aware SandboxWindowResolver (not the raw ForegroundGuard): for a Win11 UWP app the
        // foreground HWND is the ApplicationFrameHost frame, so a naive foreground-PID compare would
        // false-reject a legitimate Calculator click. EffectiveAppPid drills AFH→CoreWindow and returns
        // the frame PID for classic Win32 (PioneerRx, Notepad), so this matches the resolver's proc.Id
        // for both UWP and classic apps.
        if (expectedPid > 0 && targetTrustKind == TargetTrustKind.Sandbox)
        {
            var trust = SandboxProcessTrustVerifier.VerifyResolvedProcess(expectedPid, expectedProcess ?? string.Empty);
            if (!trust.Trusted)
            {
                return Task.FromResult(ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "resolved process failed sandbox path/publisher identity verification",
                    dryRun: false));
            }
        }

        if (expectedPid > 0 && targetTrustKind == TargetTrustKind.PioneerRx &&
            (_pioneerRxTrust is null || !_pioneerRxTrust.VerifyResolvedProcess(expectedPid).Trusted))
        {
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                "resolved PMS process failed local approval identity verification",
                dryRun: false));
        }

        if (expectedPid > 0 && !SandboxWindowResolver.IsSandboxAppForeground(expectedPid))
        {
            _logger.Warning(
                "ClickAt refused because the approved target no longer owns the foreground");
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                "target window lost foreground between resolve and click; refusing to click stale coordinates",
                dryRun: false));
        }

        try
        {
            var targetTrustedAtMutation = true;
            var targetForegroundAtMutation = true;
            var mutationReject = _gate.ExecuteLiveMutationOrReject(() =>
            {
                targetTrustedAtMutation = TargetStillTrusted(expectedPid, expectedProcess, targetTrustKind);
                targetForegroundAtMutation = expectedPid <= 0 || SandboxWindowResolver.IsSandboxAppForeground(expectedPid);
                if (!targetTrustedAtMutation || !targetForegroundAtMutation) return;
                TryGlow(x, y); // glide the presence cursor in + land the reticle at the click point
                MoveAndClick(x, y);
            });
            if (mutationReject is not null) return Task.FromResult(mutationReject);
            if (!targetTrustedAtMutation)
            {
                return Task.FromResult(ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "approved target identity changed before pointer input",
                    dryRun: false));
            }
            if (!targetForegroundAtMutation)
            {
                return Task.FromResult(ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    "approved target lost foreground before pointer input",
                    dryRun: false));
            }
            try { _presence?.Click(x, y); } catch { /* visual-only — never break actuation */ }
            if (expectedPid > 0 && targetTrustKind != TargetTrustKind.Unspecified)
            {
                var hwnd = SandboxWindowResolver.ForegroundWindowForProcess(expectedPid);
                _activeTarget = new TargetWindow(
                    expectedPid,
                    hwnd,
                    expectedProcess ?? "target",
                    targetTrustKind);
            }
            // Record the click so a TYPE arriving shortly after (the click_by_label → type field-entry
            // flow) does NOT re-focus the window centre and undo the focus this click just set.
            System.Threading.Interlocked.Exchange(ref _lastClickUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            return Task.FromResult(ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence));
        }
        catch (Exception ex)
        {
            _logger.Warning("ClickAt failed ({ErrorType})", ex.GetType().Name);
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "pointer input failed locally",
                dryRun: false));
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
                "requested app is not in the immutable sandbox allowlist",
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

            // Resolve to an absolute path under a trusted machine location before launching. A bare
            // process name is resolved by Win32 against the app dir + CWD first, so a planted
            // "notepad.exe" could hijack the launch. Failure to resolve is terminal; PATH is never used.
            var launchPath = ResolveTrustedSystemPath(processName);
            if (launchPath is null)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "sandbox executable could not be resolved to a trusted system location",
                    dryRun: false);
            }
            var launchTrust = SandboxProcessTrustVerifier.VerifyExecutablePath(launchPath, processName);
            if (!launchTrust.Trusted)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "sandbox executable failed path/publisher identity verification",
                    dryRun: false);
            }

            Process? startedProcess = null;
            var launchIdentityTrusted = true;
            var launchReject = _gate.ExecuteLiveMutationOrReject(() =>
            {
                launchIdentityTrusted = SandboxProcessTrustVerifier
                    .VerifyExecutablePath(launchPath, processName).Trusted;
                if (!launchIdentityTrusted) return;
                startedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = launchPath,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = Environment.SystemDirectory,
                });
            });
            if (launchReject is not null) return launchReject;
            if (!launchIdentityTrusted)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "sandbox executable identity changed before launch",
                    dryRun: false);
            }
            using var p = startedProcess;

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
                var resolvedTrust = SandboxProcessTrustVerifier.VerifyResolvedProcess(rw.Pid, processName);
                if (!resolvedTrust.Trusted)
                {
                    _activeTarget = new TargetWindow(
                        0, IntPtr.Zero, processName, TargetTrustKind.Sandbox);
                    return ActuationResult.Reject(
                        ActuationRejectionCodes.ProcessIdentityUntrusted,
                        "launched window failed sandbox path/publisher identity verification",
                        dryRun: false);
                }
                var resolvedTrustedAtFocus = true;
                var focusReject = _gate.ExecuteLiveMutationOrReject(() =>
                {
                    resolvedTrustedAtFocus = SandboxProcessTrustVerifier
                        .VerifyResolvedProcess(rw.Pid, processName).Trusted;
                    if (resolvedTrustedAtFocus)
                        WindowFocusManager.ForceForeground(rw.Hwnd, _logger);
                });
                if (focusReject is not null) return focusReject;
                if (!resolvedTrustedAtFocus)
                {
                    return ActuationResult.Reject(
                        ActuationRejectionCodes.ProcessIdentityUntrusted,
                        "sandbox target identity changed before focus",
                        dryRun: false);
                }
                _activeTarget = new TargetWindow(rw.Pid, rw.Hwnd, processName, TargetTrustKind.Sandbox);
                if (WindowFocusManager.GetClientCenterScreen(rw.Hwnd) is { } c)
                    TryGlow(c.X, c.Y); // glow on the freshly-launched window so the operator sees the open
                _logger.Information("LaunchSandboxApp resolved and foregrounded an approved sandbox target");
            }
            else
            {
                // Unresolved target sentinel: the launch may have succeeded, but we cannot prove which
                // window is the app's. The next type/press will fail closed rather than risk a leak.
                _activeTarget = new TargetWindow(0, IntPtr.Zero, processName, TargetTrustKind.Sandbox);
                _logger.Warning("LaunchSandboxApp started but its approved window could not be resolved");
            }

            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "LaunchSandboxApp failed locally ({ErrorType})",
                ex.GetType().Name);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "sandbox launch failed locally",
                dryRun: false);
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
    private async Task<ActuationResult?> EnsureTargetForegroundOrRejectAsync(
        string verb,
        bool focusClick,
        TargetTrustKind requiredTargetKind,
        string? expectedProcess,
        CancellationToken ct)
    {
        var target = _activeTarget;
        if (target is null)
        {
            if (requiredTargetKind != TargetTrustKind.Unspecified)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    $"no {requiredTargetKind} target was established before {verb}",
                    dryRun: false);
            }
            _logger.Warning("{Verb}: no actuation target established (no preceding launch) — using current foreground window", verb);
            return null;
        }

        if (requiredTargetKind != TargetTrustKind.Unspecified && target.TrustKind != requiredTargetKind)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                $"active target class does not match required {requiredTargetKind} scope",
                dryRun: false);
        }

        if (!string.IsNullOrWhiteSpace(expectedProcess) &&
            !TargetIdentityMatches(expectedProcess, target.Label))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                $"active target does not match the process bound to {verb}",
                dryRun: false);
        }

        if (target.Pid <= 0 || target.Hwnd == IntPtr.Zero)
        {
            _logger.Warning("{Verb} refused: launch could not resolve the approved target window", verb);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                $"launch could not resolve the approved target window; refusing to {verb} to avoid keystroke leak",
                dryRun: false);
        }

        if (target.TrustKind == TargetTrustKind.Sandbox)
        {
            var trust = SandboxProcessTrustVerifier.VerifyResolvedProcess(target.Pid, target.Label);
            if (!trust.Trusted)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "active sandbox target no longer satisfies path/publisher identity policy",
                    dryRun: false);
            }
        }
        else if (target.TrustKind == TargetTrustKind.PioneerRx &&
                 (_pioneerRxTrust is null || !_pioneerRxTrust.VerifyResolvedProcess(target.Pid).Trusted))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                "active PMS target no longer satisfies local approval identity policy",
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
            if (TargetOwnsForeground(target)) { acquired = true; break; }
            var trustedAtFocus = true;
            var focusReject = _gate.ExecuteLiveMutationOrReject(() =>
            {
                trustedAtFocus = TargetStillTrusted(
                    target.Pid,
                    target.Label,
                    target.TrustKind);
                if (trustedAtFocus)
                    WindowFocusManager.ForceForeground(target.Hwnd, _logger);
            });
            if (focusReject is not null) return focusReject;
            if (!trustedAtFocus)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "approved target identity changed before focus",
                    dryRun: false);
            }
            await DelayWithCancel(150, ct).ConfigureAwait(false);
        }

        if (!acquired)
        {
            // PID stays in the LOCAL log for diagnosis but NOT in the cloud-facing reason: a 5-digit
            // PID trips the cloud's ZIP-code PHI filter, which would null the result and mask this
            // legitimate operational failure as an opaque "result_rejected_phi_validation".
            _logger.Warning("{Verb} refused because the approved target could not be foregrounded", verb);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                "approved target could not be brought to the foreground",
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
            if (!TargetOwnsForeground(target))
            {
                _logger.Warning("{Verb} refused because the approved target lost foreground", verb);
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    "approved target lost foreground before focus input",
                    dryRun: false);
            }

            var center = WindowFocusManager.GetClientCenterScreen(target.Hwnd);
            if (center is not { } pt)
            {
                // A focusClick verb (type) NEEDS a focused edit control. If we can't locate the target's
                // client area we can't guarantee that, so fail closed rather than type blind — a dropped
                // or partial value is worse than none ("one line of truth must be real").
                _logger.Warning("{Verb} refused because the approved target client area was unavailable", verb);
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    "approved target client area was unavailable",
                    dryRun: false);
            }

            var trustedAtFocus = true;
            var focusClickReject = _gate.ExecuteLiveMutationOrReject(() =>
            {
                trustedAtFocus = TargetStillTrusted(target.Pid, target.Label, target.TrustKind);
                if (!trustedAtFocus || !TargetOwnsForeground(target)) return;
                TryGlow(pt.X, pt.Y);
                MoveAndClick(pt.X, pt.Y);
            });
            if (focusClickReject is not null) return focusClickReject;
            if (!trustedAtFocus)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "approved target identity changed before focus input",
                    dryRun: false);
            }
            await DelayWithCancel(FocusSettleMs, ct).ConfigureAwait(false);
        }

        // STEP 3 — final re-confirm the target is still foreground after the focus-click (a click can't
        // change which window is foreground here, but verify rather than assume). Fail closed otherwise.
        if (TargetOwnsForeground(target)) return null;

        // PID stays in the LOCAL log only (see note above) — the cloud-facing reason omits it so it
        // doesn't trip the ZIP-code PHI filter and mask this operational failure.
        _logger.Warning("{Verb} refused because the approved target did not retain foreground", verb);
        return ActuationResult.Reject(
            ActuationRejectionCodes.ForegroundNotTarget,
            "approved target did not retain foreground",
            dryRun: false);
    }

    // Focus settle after the focus-click: a short pause so the click's WM_SETFOCUS is processed and the
    // edit control is genuinely input-ready before the first keystroke. The acquire timeout bounds the
    // fail-closed wait while we try to bring the target to the foreground.
    private const int FocusSettleMs = 250;
    private const int ForegroundAcquireTimeoutMs = 6000;

}
