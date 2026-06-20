# Presence Reasoning Bubble (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use `- [ ]` checkboxes.

**Goal:** A cursor-anchored, PHI-vetted reasoning bubble that narrates each Helper actuation ("Clicking 7") and trails the gliding cursor — deterministic labels, no LLM, no new latency.

**Architecture:** New Helper-side bubble renderer (second Win32 layered window) + a `Narrate` method on the Phase 1 `PresenceController`. Narration sourced from the action labels the Helper already receives (`ClickByLabelRequest.Label`) in `ActuationCommandHandler`. Pure text composition + PHI-vet + controller gating are unit-tested; the Win32 text card is build- + on-box-verified.

**Tech Stack:** C# / .NET 8 `net8.0-windows`, Win32 GDI layered window + `Graphics.DrawString`, xUnit (built-in asserts, hand-written fakes).

## Global Constraints
- **Cosmetic NEVER gates actuation** — every narration call is gated + try-caught; a failed/absent bubble never affects the click.
- **PHI invariant:** the bubble never renders a label that trips `PhiPatternGuard.ContainsPotentialPhi` — drop to action-kind only ("Clicking…"). Never render typed text.
- **Idle = no repaint** (same command-queue STA-thread pattern as `WindowsPresenceRenderer`).
- **Brand:** charcoal `#0F172A` card, gold `#C8A96A` accent + acting tone, cream text; wine (`Confirm` tone) for stalls.
- **Backward-compatible:** new ctor params are optional (default null) so Phase 1 wiring/tests are untouched.

---

## File Structure
**Create:**
- `src/SuavoAgent.Helper/Presence/BubbleText.cs` — pure caption composer.
- `src/SuavoAgent.Helper/Presence/IBubbleRenderer.cs` — bubble renderer interface.
- `src/SuavoAgent.Helper/Presence/WindowsBubbleRenderer.cs` — Win32 text card.
- `tests/SuavoAgent.Helper.Tests/Presence/BubbleTextTests.cs`
- `tests/SuavoAgent.Helper.Tests/Presence/PresenceNarrateTests.cs`

**Modify:**
- `src/SuavoAgent.Helper/Presence/PresenceController.cs` — add `Narrate(...)`, `IBubbleRenderer? bubble` ctor param, bubble re-anchor on `MoveTo`, hide bubble on pref-hide.
- `src/SuavoAgent.Helper/Actuation/ActuationCommandHandler.cs` — inject `PresenceController?`; `Narrate` in the click/type/press handlers + a stall line on resolution failure.
- `src/SuavoAgent.Helper/Program.cs` — build `WindowsBubbleRenderer`, pass to `PresenceController` + the controller to `ActuationCommandHandler`.

---

## Task 1: BubbleText composer

**Files:** Create `src/SuavoAgent.Helper/Presence/BubbleText.cs`; Test `tests/SuavoAgent.Helper.Tests/Presence/BubbleTextTests.cs`

**Interfaces:** Produces `static class BubbleText { static string For(string actionKind, string? label); }`

- [ ] **Step 1: Write the failing test**
```csharp
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class BubbleTextTests
{
    [Fact] public void For_WithLabel_JoinsKindAndLabel()
        => Assert.Equal("Clicking 7", BubbleText.For("Clicking", "7"));

    [Fact] public void For_NoLabel_EllipsizesKind()
        => Assert.Equal("Typing…", BubbleText.For("Typing", null));

    [Fact] public void For_EmptyKind_FallsBackToWorking()
        => Assert.Equal("Working…", BubbleText.For("  ", null));

    [Fact] public void For_LongLabel_Truncates()
    {
        var text = BubbleText.For("Clicking", new string('x', 80));
        Assert.True(text.Length <= "Clicking ".Length + 49);
        Assert.EndsWith("…", text);
    }
}
```
- [ ] **Step 2: Run → fail.** `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~BubbleTextTests"` → FAIL (BubbleText undefined).
- [ ] **Step 3: Implement**
```csharp
namespace SuavoAgent.Helper.Presence;

/// <summary>Composes a one-line bubble caption from an action kind + optional
/// (already PHI-vetted) label. Null/empty label → "Clicking…"; present → "Clicking 7".</summary>
public static class BubbleText
{
    private const int MaxLabel = 48;

    public static string For(string actionKind, string? label)
    {
        var kind = string.IsNullOrWhiteSpace(actionKind) ? "Working" : actionKind.Trim();
        if (string.IsNullOrWhiteSpace(label)) return kind + "…";
        var l = label.Trim();
        if (l.Length > MaxLabel) l = l[..MaxLabel] + "…";
        return $"{kind} {l}";
    }
}
```
- [ ] **Step 4: Run → pass** (4 tests).
- [ ] **Step 5: Commit**
```bash
git add src/SuavoAgent.Helper/Presence/BubbleText.cs tests/SuavoAgent.Helper.Tests/Presence/BubbleTextTests.cs
git commit -m "feat(presence): bubble caption composer (kind + vetted label, truncated)"
```

---

## Task 2: IBubbleRenderer + PresenceController.Narrate

**Files:** Create `src/SuavoAgent.Helper/Presence/IBubbleRenderer.cs`; Modify `src/SuavoAgent.Helper/Presence/PresenceController.cs`; Test `tests/SuavoAgent.Helper.Tests/Presence/PresenceNarrateTests.cs`

**Interfaces:**
- Produces `interface IBubbleRenderer { void Show(string text, string tone, int x, int y); void Reanchor(int x, int y); void Hide(); }`
- Adds `PresenceController(... , IBubbleRenderer? bubble = null)` and `void Narrate(string actionKind, string? label, string? tone = null)`.

- [ ] **Step 1: Write the failing test**
```csharp
using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceNarrateTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private sealed class FakeBubble : IBubbleRenderer
    {
        public List<string> Shown { get; } = new();
        public List<string> Reanchors { get; } = new();
        public int Hides;
        public void Show(string text, string tone, int x, int y) => Shown.Add($"{text}@{x},{y}");
        public void Reanchor(int x, int y) => Reanchors.Add($"{x},{y}");
        public void Hide() => Hides++;
    }

    private sealed class NoopCursor : IPresenceRenderer
    {
        public void Glide(int fx,int fy,int tx,int ty,int d,string e,string tone,int dia){}
        public void Reticle(int x,int y,int dia,string tone){}
        public void ClickPulse(int x,int y,string tone){}
        public void Hide(){} public void Show(){}
    }

    [Fact]
    public void Narrate_WhenVisible_ShowsCaption()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "7");

        Assert.Contains(b.Shown, s => s.StartsWith("Clicking 7@"));
    }

    [Fact]
    public void Narrate_PhiLabel_RendersActionOnly()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "123-45-6789"); // SSN-shaped → must be dropped

        Assert.Contains(b.Shown, s => s.StartsWith("Clicking…@"));
        Assert.DoesNotContain(b.Shown, s => s.Contains("123-45-6789"));
    }

    [Fact]
    public void Narrate_WhenBubbleHidden_NoOp()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault() with { BubbleVisible = false });
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(), store, Log, bubble: b);

        c.Narrate("Clicking", "7");

        Assert.Empty(b.Shown);
    }

    [Fact]
    public void MoveTo_AfterNarrate_ReanchorsBubble()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "7"); // bubble showing
        c.MoveTo(10, 10);           // first place
        c.MoveTo(200, 50);          // glide → reanchor

        Assert.Contains("200,50", b.Reanchors);
    }
}
```
- [ ] **Step 2: Run → fail** (IBubbleRenderer / Narrate / bubble param undefined).
- [ ] **Step 3: Implement.** Create `IBubbleRenderer.cs`:
```csharp
namespace SuavoAgent.Helper.Presence;

/// <summary>Cursor-anchored text bubble. Win32 impl owns one layered window;
/// idle = no repaint. Tests use a fake.</summary>
public interface IBubbleRenderer
{
    void Show(string text, string tone, int x, int y);
    void Reanchor(int x, int y);
    void Hide();
}
```
Then modify `PresenceController.cs`: add `using` for nothing new (fully-qualify guard). Add field + ctor param + methods.

Add field next to `_isSessionInteractive`:
```csharp
    private readonly IBubbleRenderer? _bubble;
    private string? _bubbleText;
```
Change ctor signature + body:
```csharp
    public PresenceController(
        IPresenceRenderer renderer,
        PresencePreferenceStore store,
        ILogger logger,
        Func<bool>? isSessionInteractive = null,
        IBubbleRenderer? bubble = null)
    {
        _renderer = renderer;
        _store = store;
        _logger = logger.ForContext<PresenceController>();
        _isSessionInteractive = isSessionInteractive ?? (() => true);
        _bubble = bubble;
        _store.Changed += OnPrefsChanged;
    }
```
Add the `Narrate` method (after `Park()`):
```csharp
    /// <summary>Show a one-line caption for the current action near the cursor. PHI-vets the
    /// label (drops to action-kind only if it trips a PHI pattern). Gated + try-caught.</summary>
    public void Narrate(string actionKind, string? label, string? tone = null)
    {
        if (_bubble is null || !Active) return;
        var prefs = _store.Current;
        if (!prefs.BubbleVisible || prefs.BubbleVerbosity == "off") return;
        try
        {
            var safeLabel = label is not null
                && SuavoAgent.Helper.Actuation.PhiPatternGuard.ContainsPotentialPhi(label, out _)
                    ? null : label;
            var text = BubbleText.For(actionKind, safeLabel);
            _bubbleText = text;
            lock (_lock) { _bubble.Show(text, tone ?? prefs.Tone, _lastX, _lastY); }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence Narrate failed (non-fatal)"); }
    }
```
In `MoveTo`, inside the glide branch right after `_lastX = x; _lastY = y;`, add bubble re-anchor:
```csharp
                _lastX = x; _lastY = y;
                if (_bubble is not null && _bubbleText is not null)
                {
                    try { _bubble.Reanchor(x, y); } catch { /* visual-only */ }
                }
```
In `OnPrefsChanged`, also hide the bubble when hidden:
```csharp
    private void OnPrefsChanged(PresencePreferences prefs)
    {
        try
        {
            if (prefs.IsCursorActive) _renderer.Show();
            else { _renderer.Hide(); _bubble?.Hide(); }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence visibility toggle failed (non-fatal)"); }
    }
```
- [ ] **Step 4: Run → pass** (4 tests). Also re-run Phase 1 controller tests: `--filter "FullyQualifiedName~PresenceControllerTests"` still PASS (ctor change is additive).
- [ ] **Step 5: Commit**
```bash
git add src/SuavoAgent.Helper/Presence/IBubbleRenderer.cs src/SuavoAgent.Helper/Presence/PresenceController.cs tests/SuavoAgent.Helper.Tests/Presence/PresenceNarrateTests.cs
git commit -m "feat(presence): PresenceController.Narrate — PHI-vetted caption, follows cursor, gated"
```

---

## Task 3: WindowsBubbleRenderer (Win32 text card)

**Files:** Create `src/SuavoAgent.Helper/Presence/WindowsBubbleRenderer.cs`

**Interfaces:** Consumes `IBubbleRenderer`. Produces `class WindowsBubbleRenderer : IBubbleRenderer, IDisposable` with `void Start()`.

**Design:** one persistent layered click-through window; command-queue STA thread (idle = no repaint). `Show` draws a rounded charcoal card with a gold left-accent bar + cream text via `Graphics.DrawString`, positioned above-right of the anchor, clamped on-screen, auto-hidden after a ~2.5s dwell. `Reanchor` repaints the stored text at a new anchor. Build- + on-box-verified.

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

/// <summary>Persistent click-through GDI text card for agent narration. One layered
/// window; commands run on an STA thread that blocks when idle (no repaint at rest).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBubbleRenderer : IBubbleRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private string _text = string.Empty;
    private string _tone = PresenceTones.Acting;
    private const int W = 360, H = 56, CursorPad = 26;

    public WindowsBubbleRenderer(ILogger logger) => _logger = logger.ForContext<WindowsBubbleRenderer>();

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
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "STATIC", string.Empty, WS_POPUP, 0, 0, W, H,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            ShowWindow(_hwnd, SW_HIDE);
            foreach (var cmd in _commands.GetConsumingEnumerable())
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "bubble cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "bubble renderer loop ended"); }
        finally { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Show(string text, string tone, int x, int y) => Enqueue(() =>
    {
        _text = text ?? string.Empty;
        _tone = tone ?? PresenceTones.Acting;
        Paint(x, y);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    });

    public void Reanchor(int x, int y) => Enqueue(() => { if (_text.Length > 0) Paint(x, y); });

    public void Hide() => Enqueue(() => { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });

    private void Paint(int anchorX, int anchorY)
    {
        if (_hwnd == IntPtr.Zero) return;
        var accent = ToneColor(_tone);
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var card = new Rectangle(6, 6, W - 12, H - 12);
            using (var path = RoundedRect(card, 12))
            using (var fill = new SolidBrush(Color.FromArgb(232, 15, 23, 42)))   // charcoal glass
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillPath(fill, path);
                g.FillRectangle(accentBrush, card.X, card.Y + 6, 4, card.Height - 12); // gold left bar
            }
            using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            using var text = new SolidBrush(Color.FromArgb(245, 234, 224));         // cream
            var textRect = new RectangleF(card.X + 16, card.Y, card.Width - 22, card.Height);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_text, font, text, textRect, fmt);
        }

        // Position above-right of the cursor, clamped to the virtual screen.
        var px = anchorX + CursorPad;
        var py = anchorY - H - CursorPad / 2;
        var vsX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vsY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vsW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vsH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        px = Math.Clamp(px, vsX, vsX + vsW - W);
        py = Math.Clamp(py, vsY, vsY + vsH - H);

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new PointNative(px, py);
            var sz = new SizeNative(W, H);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color ToneColor(string tone) => tone switch
    {
        PresenceTones.Acting => Color.FromArgb(200, 169, 106),
        PresenceTones.Observing => Color.FromArgb(122, 158, 126),
        PresenceTones.Confirm => Color.FromArgb(140, 40, 50),
        _ => Color.FromArgb(200, 169, 106),
    };

    public void Dispose()
    {
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
- [ ] **Step 2: Build** `dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release` → `0 Error(s)`.
- [ ] **Step 3: Commit**
```bash
git add src/SuavoAgent.Helper/Presence/WindowsBubbleRenderer.cs
git commit -m "feat(presence): Win32 text-card bubble renderer (charcoal/gold, idle-no-repaint)"
```

---

## Task 4: Wire narration into actuation + DI + on-box

**Files:** Modify `ActuationCommandHandler.cs`, `Program.cs`

- [ ] **Step 1: Inject PresenceController into ActuationCommandHandler.** Add a ctor param `SuavoAgent.Helper.Presence.PresenceController? presence = null` (last) + field `_presence`. In `HandleClickByLabelAsync` (after `var req = data.Value.Deserialize<ClickByLabelRequest>();`, ~line 76) add — before resolution:
```csharp
        _presence?.Narrate("Clicking", req?.Label);
```
On the resolution-failure return path in that method, add a stall line:
```csharp
        _presence?.Narrate("Couldn't find", req?.Label, SuavoAgent.Helper.Presence.PresenceTones.Confirm);
```
In `HandleTypeTextAsync` (after deserialize): `_presence?.Narrate("Typing", null);` (never the text).
In `HandlePressKeysAsync` (after deserialize): `_presence?.Narrate("Pressing keys", null);`
In `HandleClickBySignatureAsync` (after deserialize): `_presence?.Narrate("Clicking", null);`

- [ ] **Step 2: Build the bubble + wire in Program.cs.** In the Windows block (after `presenceRenderer.Start();`), before constructing `presenceController`:
```csharp
        var bubbleRenderer = new SuavoAgent.Helper.Presence.WindowsBubbleRenderer(Log.Logger);
        bubbleRenderer.Start();
```
Change the `presenceController = new PresenceController(...)` call to pass the bubble:
```csharp
        presenceController = new SuavoAgent.Helper.Presence.PresenceController(
            presenceRenderer, presenceStore, Log.Logger,
            isSessionInteractive: () => Environment.UserInteractive,
            bubble: bubbleRenderer);
```
Change the `actuationHandler = new ActuationCommandHandler(...)` line to pass the controller:
```csharp
        actuationHandler = new ActuationCommandHandler(actuationGate, sendInputDriver, uiaResolver, actuationConfig, Log.Logger, presenceController);
```

- [ ] **Step 3: Build + full presence tests**
```bash
dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release
dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj -c Release --filter "FullyQualifiedName~Presence|FullyQualifiedName~BubbleText"
```
Expected: build `0 Error(s)`; all presence + bubble tests PASS.

- [ ] **Step 4: Commit**
```bash
git add src/SuavoAgent.Helper/Actuation/ActuationCommandHandler.cs src/SuavoAgent.Helper/Program.cs
git commit -m "feat(presence): narrate each actuation into the bubble + DI wire-up + stall line"
```

- [ ] **Step 5: On-box verification (Joshua, no PioneerRx)** — OTA, `run_workflow calc_verified`: each click shows a bubble ("Clicking 7" …) that trails the gliding cursor + fades; `BubbleVisible=false` suppresses it (cursor still glides); Ctrl+Alt+H hides cursor + bubble; a PHI-shaped label renders action-kind only.

---

## Self-Review
- **Spec coverage:** deterministic bubble (Tasks 1-4 ✓), PHI-vet drop-to-action-only (Task 2 test ✓), follows cursor (Task 2 Reanchor ✓), stall line (Task 4 ✓), BubbleVisible honored (Task 2 ✓), idle-no-repaint (Task 3 ✓), zero Core changes ✓, cosmetic-never-gates (gated + try-caught ✓).
- **Deferred (logged):** Core step-`Description` via `presence.narrate` IPC → Phase 2.5; LLM "Explain more" lane + step-log rail → Phase 3.
- **Placeholder scan:** none. **Type consistency:** `IBubbleRenderer.Show/Reanchor/Hide`, `PresenceController.Narrate(actionKind,label,tone)`, `BubbleText.For` used consistently.
- **Implementer note (Task 4 Step 1):** confirm the exact early-return line in `HandleClickByLabelAsync` for the resolution-failure stall placement (the method resolves via `UiaLabelResolver` then `_driver.ClickAtAsync` at ~line 119; the stall goes on the not-found return before that).
