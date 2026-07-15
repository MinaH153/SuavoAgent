using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

public sealed partial class SendInputDriver
{
    internal static bool TargetIdentityMatches(string expectedProcess, string establishedProcess)
    {
        var expected = PackagedAppAliases.CandidateProcessNames(expectedProcess)
            .Select(ProtectedDesktopProcessClassifier.CanonicalProcessStem)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0) return false;

        return PackagedAppAliases.CandidateProcessNames(establishedProcess)
            .Select(ProtectedDesktopProcessClassifier.CanonicalProcessStem)
            .Any(expected.Contains);
    }

    private bool TargetStillTrusted(int pid, string? expectedProcess, TargetTrustKind trustKind)
    {
        if (pid <= 0 || trustKind == TargetTrustKind.Unspecified) return true;
        return trustKind switch
        {
            TargetTrustKind.Sandbox => SandboxProcessTrustVerifier
                .VerifyResolvedProcess(pid, expectedProcess ?? string.Empty).Trusted,
            TargetTrustKind.PioneerRx => _pioneerRxTrust is not null &&
                _pioneerRxTrust.VerifyResolvedProcess(pid).Trusted,
            _ => false,
        };
    }

    private bool TargetOwnsForeground(TargetWindow target) => target.TrustKind switch
    {
        TargetTrustKind.Sandbox => SandboxWindowResolver.IsSandboxAppForeground(target.Pid),
        TargetTrustKind.PioneerRx => SystemObservers.ForegroundGuard.IsPidForeground(target.Pid),
        _ => false,
    };

    private ActuationResult? ExecuteTargetBoundMutationOrReject(
        Action mutation,
        TargetTrustKind requiredTargetKind,
        string? expectedProcess)
    {
        var identityTrusted = true;
        var ownsForeground = true;
        var targetPresent = true;
        var gateRejection = _gate.ExecuteLiveMutationOrReject(() =>
        {
            if (requiredTargetKind != TargetTrustKind.Unspecified)
            {
                var target = _activeTarget;
                targetPresent = target is not null &&
                                target.Pid > 0 &&
                                target.Hwnd != IntPtr.Zero &&
                                target.TrustKind == requiredTargetKind &&
                                (string.IsNullOrWhiteSpace(expectedProcess) ||
                                 TargetIdentityMatches(expectedProcess, target.Label));
                if (!targetPresent) return;
                identityTrusted = TargetStillTrusted(target!.Pid, target.Label, target.TrustKind);
                if (!identityTrusted) return;
                ownsForeground = TargetOwnsForeground(target);
                if (!ownsForeground) return;
            }
            mutation();
        });
        if (gateRejection is not null) return gateRejection;
        if (!targetPresent || !identityTrusted)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                "approved target identity was unavailable at the input boundary",
                dryRun: false);
        }
        if (!ownsForeground)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ForegroundNotTarget,
                "approved target did not own the foreground at the input boundary",
                dryRun: false);
        }
        return null;
    }

    private async Task<ActuationResult?> VerifyTypedTextAsync(
        string typed,
        TargetTrustKind requiredTargetKind,
        string? expectedProcess,
        CancellationToken ct)
    {
        if (_focusedValueReader is null)
            return TypeNotVerified();
        var normalizedTyped = NormalizeForVerification(typed);
        if (normalizedTyped.Length == 0) return TypeNotVerified();

        try
        {
            for (var i = 0; i < 6; i++)
            {
                await DelayWithCancel(200, ct).ConfigureAwait(false);
                var target = _activeTarget;
                if (target is null || target.TrustKind != requiredTargetKind ||
                    !TargetStillTrusted(target.Pid, target.Label, target.TrustKind) ||
                    !TargetOwnsForeground(target) ||
                    (!string.IsNullOrWhiteSpace(expectedProcess) &&
                     !TargetIdentityMatches(expectedProcess, target.Label)))
                    return TypeNotVerified();
                var readback = _focusedValueReader();
                if (!string.IsNullOrEmpty(readback) &&
                    NormalizeForVerification(readback).Contains(normalizedTyped, StringComparison.Ordinal))
                {
                    _logger.Information("Text input completion was proven by local UI read-back");
                    return null;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _logger.Warning("Text input read-back failed locally");
        }
        return TypeNotVerified();
    }

    internal static bool IsTypeReadbackVerified(string typed, IEnumerable<string?> readbacks)
    {
        var typedNorm = NormalizeForVerification(typed);
        if (typedNorm.Length == 0) return false;
        return readbacks.Any(value => !string.IsNullOrEmpty(value) &&
            NormalizeForVerification(value).Contains(typedNorm, StringComparison.Ordinal));
    }

    private static string NormalizeForVerification(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }

    private static ActuationResult TypeNotVerified() => ActuationResult.Reject(
        ActuationRejectionCodes.TypeNotVerified,
        "UI read-back did not prove that text input completed",
        dryRun: false);

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
    /// Returns null if none resolve; callers must fail closed and never use PATH resolution.
    /// </summary>
    private string? ResolveTrustedSystemPath(string processName)
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
                _logger.Information("ResolveTrustedSystemPath resolved an approved sandbox executable");
                return appPaths;
            }
        }
        catch { /* fail closed below */ }
        return null;
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
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
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
        var move = BuildAbsoluteMouseMove(x, y);
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

    private static INPUT BuildAbsoluteMouseMove(int x, int y)
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

        var dx = NormalizeAbsoluteCoordinate(x, vx, vw);
        var dy = NormalizeAbsoluteCoordinate(y, vy, vh);

        return new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_ABSOLUTE,
                },
            },
        };
    }

    internal static int NormalizeAbsoluteCoordinate(int coordinate, int origin, int length)
    {
        if (length <= 1) return 0;
        var boundedOffset = Math.Clamp((long)coordinate - origin, 0L, length - 1L);
        return (int)Math.Round(
            boundedOffset * 65535d / (length - 1d),
            MidpointRounding.AwayFromZero);
    }
}
