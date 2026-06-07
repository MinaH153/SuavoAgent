using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
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

    public SendInputDriver(ActuationGate gate, ActuationConfig config, ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<SendInputDriver>();
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

    public Task<ActuationResult> ClickAtAsync(int x, int y, bool dryRun, CancellationToken ct)
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

        try
        {
            MoveAndClick(x, y);
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
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = processName,
                UseShellExecute = false,
                CreateNoWindow = false,
            });

            // WINDOW-FOCUS: Process.Start returns before the app's window exists, so without waiting +
            // foregrounding it the NEXT workflow step (type/click) lands in whatever window was already
            // focused — observed live: a "type" leaked into a PowerShell prompt instead of the launched
            // Notepad. Wait for the GUI to be input-ready, then bring its main window to the foreground
            // so the launched app reliably owns subsequent keystrokes. Best-effort: a flaky foreground
            // call must never fail an otherwise-successful launch.
            if (p is not null)
            {
                try { p.WaitForInputIdle(5000); } catch { /* console / non-GUI apps don't support this */ }
                var hwnd = await WaitForMainWindowAsync(p, 5000, ct).ConfigureAwait(false);
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    _logger.Information("LaunchSandboxApp: {Process} foregrounded (hwnd=0x{Hwnd:X})", processName, hwnd.ToInt64());
                }
                else
                {
                    _logger.Warning(
                        "LaunchSandboxApp: {Process} started but no main window appeared within 5s — focus not guaranteed",
                        processName);
                }
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
    /// Poll a freshly-started process until its main window handle exists (it is not available
    /// synchronously from <see cref="Process.Start(ProcessStartInfo)"/>), or the timeout elapses /
    /// the process exits. Returns <see cref="IntPtr.Zero"/> if no window ever appears.
    /// </summary>
    private static async Task<IntPtr> WaitForMainWindowAsync(Process p, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            try
            {
                p.Refresh();
                if (p.HasExited) return IntPtr.Zero;
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero; // process handle gone
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

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
