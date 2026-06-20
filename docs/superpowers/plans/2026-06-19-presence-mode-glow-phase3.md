# Presence Mode Machine + FSD Glow (Phase 3) Implementation Plan

> REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** A breathing FSD-style screen-edge glow + a Driving/Observing/Idle mode machine that flips to "Observing" (sage) when the human takes the mouse, tinting the whole presence layer.

**Architecture:** Pure `PresenceModeLogic.Evaluate` (timestamps → mode) is unit-tested; `PresenceController` gains mode state, an active-tone derivation, `OnHumanInput()` (cheap, hook-safe), and an `EvaluateMode()` applied by a 1s ticker (wired in Program.cs, not in the controller). `WindowsGlowRenderer` paints a cached edge-gradient bitmap and "breathes" via blend-alpha only.

## Global Constraints
- Cosmetic NEVER gates actuation; every render/glow call gated + try-caught.
- Hook path stays fast: `OnHumanInput()` does an interlocked timestamp write ONLY.
- Glow is **non-obscuring** (edge gradient, transparent center) — never full-screen dim. Breathe via `SourceConstantAlpha`, no per-frame bitmap re-render. Idle = no repaint. Honor `GlowVisible` + `GlowIntensity`.
- Tones: Driving→`prefs.Tone` (gold), Observing→`PresenceTones.Observing` (sage), Idle→glow off.

## File Structure
**Create:** `PresenceMode.cs` (enum + `PresenceModeLogic`), `IGlowRenderer.cs`, `WindowsGlowRenderer.cs`, `PresenceModeTicker.cs`; tests `PresenceModeLogicTests.cs`, `PresenceModeControllerTests.cs`.
**Modify:** `PresenceController.cs` (mode + glow + OnHumanInput + EvaluateMode + active tone), `UserInputObserver.cs` (optional onUserInput callback), `Program.cs` (glow renderer + ticker + observer callback).

---

## Task 1: PresenceMode + PresenceModeLogic (pure)

**Files:** Create `src/SuavoAgent.Helper/Presence/PresenceMode.cs`; Test `tests/SuavoAgent.Helper.Tests/Presence/PresenceModeLogicTests.cs`

**Interfaces:** `enum PresenceMode { Idle, Driving, Observing }`; `static class PresenceModeLogic { static PresenceMode Evaluate(DateTimeOffset? lastAgent, DateTimeOffset? lastHuman, DateTimeOffset now, TimeSpan drivingWindow, TimeSpan observeWindow); }`

- [ ] **Step 1: Failing test**
```csharp
using System;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceModeLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Drive = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Observe = TimeSpan.FromSeconds(8);

    [Fact] public void RecentAgent_NoHuman_IsDriving()
        => Assert.Equal(PresenceMode.Driving, PresenceModeLogic.Evaluate(Now.AddSeconds(-1), null, Now, Drive, Observe));

    [Fact] public void RecentHuman_IsObserving_EvenWithRecentAgent()
        => Assert.Equal(PresenceMode.Observing, PresenceModeLogic.Evaluate(Now.AddSeconds(-1), Now.AddSeconds(-0.2), Now, Drive, Observe));

    [Fact] public void StaleEverything_IsIdle()
        => Assert.Equal(PresenceMode.Idle, PresenceModeLogic.Evaluate(Now.AddSeconds(-30), Now.AddSeconds(-30), Now, Drive, Observe));

    [Fact] public void HumanWithinObserveButOlderThanAgent_StaysDriving()
        => Assert.Equal(PresenceMode.Driving, PresenceModeLogic.Evaluate(Now.AddSeconds(-0.2), Now.AddSeconds(-2), Now, Drive, Observe));

    [Fact] public void Nulls_AreIdle()
        => Assert.Equal(PresenceMode.Idle, PresenceModeLogic.Evaluate(null, null, Now, Drive, Observe));
}
```
- [ ] **Step 2: Run → fail.**
- [ ] **Step 3: Implement**
```csharp
using System;

namespace SuavoAgent.Helper.Presence;

public enum PresenceMode { Idle, Driving, Observing }

/// <summary>Pure mode evaluation. Human input within the observe window AND more recent than
/// agent activity ⇒ Observing (the takeover/learning state). Else recent agent ⇒ Driving. Else Idle.</summary>
public static class PresenceModeLogic
{
    public static PresenceMode Evaluate(
        DateTimeOffset? lastAgent, DateTimeOffset? lastHuman, DateTimeOffset now,
        TimeSpan drivingWindow, TimeSpan observeWindow)
    {
        var humanFresh = lastHuman is { } h && now - h <= observeWindow;
        var agentFresh = lastAgent is { } a && now - a <= drivingWindow;
        if (humanFresh && (lastAgent is null || lastHuman!.Value >= lastAgent.Value)) return PresenceMode.Observing;
        if (agentFresh) return PresenceMode.Driving;
        return PresenceMode.Idle;
    }
}
```
- [ ] **Step 4: Run → pass (5).**
- [ ] **Step 5: Commit** `feat(presence): PresenceMode + pure mode evaluator`

---

## Task 2: IGlowRenderer + PresenceController mode integration

**Files:** Create `src/SuavoAgent.Helper/Presence/IGlowRenderer.cs`; Modify `PresenceController.cs`; Test `tests/SuavoAgent.Helper.Tests/Presence/PresenceModeControllerTests.cs`

**Interfaces:** `interface IGlowRenderer { void Show(string tone, double intensity); void Hide(); }`; on `PresenceController`: ctor `IGlowRenderer? glow = null, Func<DateTimeOffset>? clock = null`; `void OnHumanInput()`; `PresenceMode EvaluateMode()`. Render calls bump agent activity + the active tone follows mode.

- [ ] **Step 1: Failing test**
```csharp
using System;
using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceModeControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();
    private sealed class FakeGlow : IGlowRenderer
    {
        public List<string> Shown { get; } = new(); public int Hides;
        public void Show(string tone, double intensity) => Shown.Add(tone);
        public void Hide() => Hides++;
    }
    private sealed class NoopCursor : IPresenceRenderer
    {
        public void Glide(int a,int b,int c,int d,int e,string f,string g,int h){}
        public void Reticle(int a,int b,int c,string d){}
        public void ClickPulse(int a,int b,string c){}
        public void Hide(){} public void Show(){}
    }

    [Fact]
    public void HumanInput_ThenEvaluate_GlowsObserving()
    {
        var now = new DateTimeOffset(2026,6,19,12,0,0,TimeSpan.Zero);
        var glow = new FakeGlow();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log,
            glow: glow, clock: () => now);

        c.OnHumanInput();
        var mode = c.EvaluateMode();

        Assert.Equal(PresenceMode.Observing, mode);
        Assert.Contains(PresenceTones.Observing, glow.Shown);
    }

    [Fact]
    public void AgentActivity_ThenEvaluate_GlowsDriving()
    {
        var now = new DateTimeOffset(2026,6,19,12,0,0,TimeSpan.Zero);
        var glow = new FakeGlow();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log,
            glow: glow, clock: () => now);

        c.MoveTo(10, 10);           // agent activity stamps now
        var mode = c.EvaluateMode();

        Assert.Equal(PresenceMode.Driving, mode);
        Assert.Contains(PresenceTones.Acting, glow.Shown);
    }

    [Fact]
    public void GlowHidden_WhenGlowVisibleFalse()
    {
        var now = new DateTimeOffset(2026,6,19,12,0,0,TimeSpan.Zero);
        var glow = new FakeGlow();
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault() with { GlowVisible = false });
        var c = new PresenceController(new NoopCursor(), store, Log, glow: glow, clock: () => now);

        c.MoveTo(10, 10);
        c.EvaluateMode();

        Assert.Empty(glow.Shown);
    }
}
```
- [ ] **Step 2: Run → fail.**
- [ ] **Step 3: Implement.** Create `IGlowRenderer.cs`:
```csharp
namespace SuavoAgent.Helper.Presence;

/// <summary>Full-screen FSD edge glow. Win32 impl breathes via blend-alpha (no per-frame
/// bitmap). Tests use a fake.</summary>
public interface IGlowRenderer
{
    void Show(string tone, double intensity);
    void Hide();
}
```
Modify `PresenceController.cs`:
- Add `using System.Threading;` if absent.
- Add fields:
```csharp
    private readonly IGlowRenderer? _glow;
    private readonly Func<DateTimeOffset> _now;
    private static readonly TimeSpan DrivingWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObserveWindow = TimeSpan.FromSeconds(8);
    private long _lastAgentTicks;
    private long _lastHumanTicks;
    private volatile int _mode = (int)PresenceMode.Idle;
```
- Ctor: add params `IGlowRenderer? glow = null, Func<DateTimeOffset>? clock = null`; assign `_glow = glow; _now = clock ?? (() => DateTimeOffset.UtcNow);`
- Add a private `StampAgent()` that sets `Interlocked.Exchange(ref _lastAgentTicks, _now().UtcTicks);` — call it at the top of `MoveTo`, `Reticle`, `Click`, `Narrate` (right after the `if (!Active) return;` guard).
- Add the active-tone helper + replace tone usages:
```csharp
    private string ActiveTone(PresencePreferences prefs)
        => (PresenceMode)_mode == PresenceMode.Observing ? PresenceTones.Observing : prefs.Tone;
```
  In `MoveTo`/`Reticle` use `ActiveTone(prefs)` instead of `prefs.Tone`; in `Click` use `ActiveTone(_store.Current)`; in `Narrate` use `tone ?? ActiveTone(prefs)`.
- Add:
```csharp
    public void OnHumanInput() => Interlocked.Exchange(ref _lastHumanTicks, _now().UtcTicks);

    /// <summary>Recompute the mode from activity timestamps and apply it (glow + tone). Called by the
    /// 1s ticker and inline after agent activity. Returns the current mode.</summary>
    public PresenceMode EvaluateMode()
    {
        DateTimeOffset? la = _lastAgentTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _lastAgentTicks), TimeSpan.Zero);
        DateTimeOffset? lh = _lastHumanTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _lastHumanTicks), TimeSpan.Zero);
        var mode = PresenceModeLogic.Evaluate(la, lh, _now(), DrivingWindow, ObserveWindow);
        if ((int)mode != _mode)
        {
            _mode = (int)mode;
            ApplyGlow(mode);
        }
        return mode;
    }

    private void ApplyGlow(PresenceMode mode)
    {
        if (_glow is null) return;
        var prefs = _store.Current;
        try
        {
            if (!prefs.GlowVisible || mode == PresenceMode.Idle) { _glow.Hide(); return; }
            var tone = mode == PresenceMode.Observing ? PresenceTones.Observing : prefs.Tone;
            _glow.Show(tone, prefs.GlowIntensity);
        }
        catch (Exception ex) { _logger.Debug(ex, "presence glow apply failed (non-fatal)"); }
    }
```
- At the end of `MoveTo`/`Click`/`Narrate` (after the render call), call `StampAgent(); EvaluateMode();` — simplest: put `StampAgent();` first thing and `EvaluateMode();` last. (Reticle: StampAgent only, no need to force eval.)
- In `OnPrefsChanged`, also hide glow when cursor inactive: in the `else` branch add `_glow?.Hide();`.
- [ ] **Step 4: Run → pass (3).** Re-run Phase 1/2 controller tests (`PresenceControllerTests`, `PresenceNarrateTests`) → still PASS (additive params).
- [ ] **Step 5: Commit** `feat(presence): mode machine in controller — glow + tone follow Driving/Observing/Idle`

---

## Task 3: WindowsGlowRenderer (Win32 FSD edge glow)

**Files:** Create `src/SuavoAgent.Helper/Presence/WindowsGlowRenderer.cs`

**Design:** full-virtual-desktop layered click-through window; render the edge-gradient bitmap once per tone (cached), then a breathing loop calls `UpdateLayeredWindow` with oscillating `SourceConstantAlpha` only. `Hide` stops the breath + hides. Build-verified.

- [ ] **Step 1: Implement**
```csharp
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Full-screen FSD edge glow. One layered click-through window covering the virtual
/// desktop; the edge-gradient bitmap is rendered once per tone, then "breathes" by varying
/// UpdateLayeredWindow's SourceConstantAlpha only (no per-frame bitmap re-render).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlowRenderer : IGlowRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hBitmap = IntPtr.Zero;     // cached edge bitmap for the current tone
    private string? _bitmapTone;
    private int _vx, _vy, _vw, _vh;
    private volatile bool _breathing;
    private double _intensity = 0.6;

    public WindowsGlowRenderer(ILogger logger) => _logger = logger.ForContext<WindowsGlowRenderer>();

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            _vx = GetSystemMetrics(SM_XVIRTUALSCREEN); _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)); _vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "STATIC", string.Empty, WS_POPUP, _vx, _vy, _vw, _vh,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            ShowWindow(_hwnd, SW_HIDE);
            foreach (var cmd in _commands.GetConsumingEnumerable())
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "glow cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "glow renderer loop ended"); }
        finally { ReleaseBitmap(); if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Show(string tone, double intensity) => Enqueue(() =>
    {
        _intensity = Math.Clamp(intensity, 0.05, 1.0);
        EnsureBitmap(tone ?? PresenceTones.Acting);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        if (_breathing) return;          // already breathing; tone/intensity updated for next frame
        _breathing = true;
        Breathe();
    });

    public void Hide() => Enqueue(() => { _breathing = false; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });

    private void Breathe()
    {
        var startedAt = Environment.TickCount64;
        while (_breathing)
        {
            var t = ((Environment.TickCount64 - startedAt) % 3000) / 3000.0;          // 3s cycle
            var wave = 0.5 - 0.5 * Math.Cos(t * 2 * Math.PI);                          // 0..1..0
            var alpha = (byte)Math.Round(255 * (0.35 + (_intensity - 0.35) * Math.Max(0, _intensity > 0.35 ? wave : 0)));
            Blend(alpha);
            // Drain any queued commands (tone change / hide) without blocking the breath.
            while (_commands.TryTake(out var cmd)) { try { cmd(); } catch { } }
            if (!_breathing) break;
            Thread.Sleep(80);                                                          // ~12fps
        }
    }

    private void EnsureBitmap(string tone)
    {
        if (_bitmapTone == tone && _hBitmap != IntPtr.Zero) return;
        ReleaseBitmap();
        var color = ToneColor(tone);
        using var bmp = new Bitmap(_vw, _vh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var band = Math.Max(24, Math.Min(_vw, _vh) / 12); // edge thickness
            DrawEdge(g, color, new Rectangle(0, 0, _vw, band), 90f, false);            // top
            DrawEdge(g, color, new Rectangle(0, _vh - band, _vw, band), 90f, true);    // bottom
            DrawEdge(g, color, new Rectangle(0, 0, band, _vh), 0f, false);             // left
            DrawEdge(g, color, new Rectangle(_vw - band, 0, band, _vh), 0f, true);     // right
        }
        _hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        _bitmapTone = tone;
    }

    private static void DrawEdge(Graphics g, Color color, Rectangle r, float angle, bool reverse)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var c0 = Color.FromArgb(150, color);
        var c1 = Color.FromArgb(0, color);
        using var brush = new LinearGradientBrush(r, reverse ? c1 : c0, reverse ? c0 : c1, angle);
        g.FillRectangle(brush, r);
    }

    private void Blend(byte alpha)
    {
        if (_hwnd == IntPtr.Zero || _hBitmap == IntPtr.Zero) return;
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var old = SelectObject(memDc, _hBitmap);
        try
        {
            var dst = new PointNative(_vx, _vy);
            var sz = new SizeNative(_vw, _vh);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally { SelectObject(memDc, old); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    private void ReleaseBitmap() { if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; _bitmapTone = null; } }

    private static Color ToneColor(string tone) => tone switch
    {
        PresenceTones.Acting => Color.FromArgb(200, 169, 106),
        PresenceTones.Observing => Color.FromArgb(122, 158, 126),
        PresenceTones.Confirm => Color.FromArgb(140, 40, 50),
        _ => Color.FromArgb(200, 169, 106),
    };

    public void Dispose()
    {
        _breathing = false;
        try { _commands.CompleteAdding(); } catch { }
        _thread?.Join(500);
        _commands.Dispose();
    }

    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, ULW_ALPHA = 0x2;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int X, Y; public SizeNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PointNative dst, ref SizeNative sz, IntPtr dcSrc, ref PointNative src, int crKey, ref BlendFunction blend, int flags);
}
```
- [ ] **Step 2: Build** → `0 Error(s)`.
- [ ] **Step 3: Commit** `feat(presence): Win32 FSD edge-glow renderer (cached bitmap, blend-alpha breath)`

---

## Task 4: Wire the ticker + takeover callback + DI

**Files:** Create `PresenceModeTicker.cs`; Modify `UserInputObserver.cs`, `Program.cs`

- [ ] **Step 1: PresenceModeTicker** — a 1s timer calling `controller.EvaluateMode()`:
```csharp
using System;
using System.Threading;

namespace SuavoAgent.Helper.Presence;

/// <summary>Drives PresenceController.EvaluateMode every second so the mode demotes to
/// Observing/Idle when the agent goes quiet or the human takes over.</summary>
public sealed class PresenceModeTicker : IDisposable
{
    private readonly Timer _timer;
    public PresenceModeTicker(PresenceController controller)
        => _timer = new Timer(_ => { try { controller.EvaluateMode(); } catch { } }, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    public void Dispose() => _timer.Dispose();
}
```
- [ ] **Step 2: UserInputObserver callback.** Add an optional ctor param + invoke it in `NotifyCoalesced`:
  - Ctor: add `Action? onUserInput = null`; field `_onUserInput`.
  - In `NotifyCoalesced`, after `_gate.NotifyUserInputDetected(source);` add `try { _onUserInput?.Invoke(); } catch { }`.
- [ ] **Step 3: Program.cs DI.** In the Windows block, after `bubbleRenderer.Start();`:
```csharp
        var glowRenderer = new SuavoAgent.Helper.Presence.WindowsGlowRenderer(Log.Logger);
        glowRenderer.Start();
```
  Add `glow: glowRenderer` to the `PresenceController` ctor call (alongside `bubble:`). After the controller is built, add the ticker:
```csharp
        var presenceModeTicker = new SuavoAgent.Helper.Presence.PresenceModeTicker(presenceController);
```
  Change the observer construction (`var observer = new UserInputObserver(actuationGate, Log.Logger);`) to:
```csharp
        var observer = new UserInputObserver(actuationGate, Log.Logger, onUserInput: () => presenceController.OnHumanInput());
```
  (Check the actual `UserInputObserver` ctor param order — it has `TimeSpan? coalesceWindow = null` before our new `onUserInput`; pass `onUserInput:` by name.)
- [ ] **Step 4: Build + full presence tests**
```bash
dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release
dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj -c Release --filter "FullyQualifiedName~Presence|FullyQualifiedName~BubbleText"
```
Expected: build `0 Error(s)`; all PASS.
- [ ] **Step 5: Commit** `feat(presence): wire FSD glow + mode ticker + human-takeover callback`
- [ ] **Step 6: On-box** — gold glow breathes while clicking; move mouse → sage + agent pauses; idle → glow fades; `GlowVisible=false` → no glow; bounded CPU.

## Self-Review
- Spec coverage: mode machine (T1 pure + T2 controller), FSD glow (T3), tone cohesion (T2 ActiveTone + ApplyGlow), takeover→Observing (T4 callback), prefs honored (T2 GlowVisible/Intensity). ✓
- Deferred: AwaitingConfirm/Resume + confirm dialogs (3b); observe LEARNING (4); LLM lane.
- Placeholders: none. Type consistency: `PresenceMode`, `PresenceModeLogic.Evaluate`, `IGlowRenderer.Show/Hide`, `PresenceController.OnHumanInput/EvaluateMode` consistent.
- **Implementer note:** confirm `UserInputObserver` ctor param order before Step 2 (named arg `onUserInput:` is safest); confirm `Program.cs` observer line text before editing.
