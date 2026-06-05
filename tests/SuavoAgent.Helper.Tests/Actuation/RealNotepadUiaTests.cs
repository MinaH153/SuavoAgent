using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// LIVE UIA on a REAL app (Notepad) — the operational proof that the perception + signature-resolution
/// + actuation stack works on a real window, not a SimulatedApp. Runs ONLY on Windows (a no-op pass
/// elsewhere) and is exercised by the windows-uia-smoke CI job on a windows-latest runner, so it does
/// NOT need the production box. This is the closest CI proof to the live-box session: a real UIA tree,
/// real SendInput keystrokes, and a real read-back from the real app.
///
/// NB: the Win11 Store Notepad is a WinUI app whose content is NOT classic UIA2 control types, so the
/// production FlaUiElementExtractor (InterestingTypes filter) sees nothing there — the agent's real
/// targets (PioneerRx = classic Win32) are fine. This smoke therefore proves the stack via raw UIA +
/// the signature resolver + the SendInput driver + a UIA read-back, independent of that filter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealNotepadUiaTests
{
    [Fact]
    public async Task LiveUia_RealWindow_Resolve_Type_ReadBack()
    {
        if (!OperatingSystem.IsWindows()) return; // no-op on Linux/Mac CI; windows-uia-smoke runs it for real

        var logger = new LoggerConfiguration().CreateLogger();
        Process? notepad = null;
        try
        {
            notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            Assert.NotNull(notepad);

            var hwnd = await WaitForMainWindowAsync(TimeSpan.FromSeconds(25));
            Assert.NotEqual(IntPtr.Zero, hwnd); // a real top-level window exists
            await Task.Delay(1200); // let the UIA tree + focus settle

            using var automation = new UIA2Automation();

            // 1. REAL UIA is readable on a real app: the window exposes a non-trivial descendant tree.
            var window = automation.FromHandle(hwnd);
            Assert.NotNull(window);
            var descendants = window!.FindAllDescendants();
            Assert.NotEmpty(descendants); // live UIA tree from a real window

            // 2. REAL SIGNATURE RESOLUTION: if any real element exposes an AutomationId, the production
            //    UiaSignatureResolver resolves it by (ControlType, AutomationId) to a real screen point.
            var withId = descendants.FirstOrDefault(d => SafeAutomationId(d).Length > 0);
            if (withId is not null)
            {
                var controlType = withId.Properties.ControlType.ValueOrDefault.ToString();
                using var sigResolver = new UiaSignatureResolver(logger);
                var resolved = sigResolver.Resolve(controlType, SafeAutomationId(withId), null, "notepad", TimeSpan.FromSeconds(5));
                Assert.NotNull(resolved); // a real on-screen element resolved to a real point
            }

            // 3. REAL ACTUATION: the production SendInput driver types real keystrokes into the focused edit.
            var config = new ActuationConfig { Enabled = true, DryRun = false };
            var driver = new SendInputDriver(new ActuationGate(config, logger), config, logger);
            const string typed = "hello from ci uia smoke";
            var result = await driver.TypeTextAsync(
                new TypeTextRequest(typed, ClearFirst: false, PerKeyDelayMs: 8, DryRun: false), CancellationToken.None);
            Assert.True(result.Ok, $"SendInput TypeText failed: {result.RejectionCode}");

            // 4. REAL READ-BACK: the typed text actually landed in the real app, read via UIA.
            string readBack = "";
            for (var i = 0; i < 10 && !readBack.Contains("hello", StringComparison.OrdinalIgnoreCase); i++)
            {
                await Task.Delay(300);
                readBack = ReadAnyText(automation, hwnd);
            }
            Assert.Contains("hello", readBack, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (notepad is { HasExited: false }) notepad.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }

    private static string SafeAutomationId(AutomationElement el)
    {
        try { return el.Properties.AutomationId.IsSupported ? (el.Properties.AutomationId.ValueOrDefault ?? "") : ""; }
        catch { return ""; }
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var procs = Process.GetProcessesByName("notepad");
            try
            {
                var withWindow = procs.FirstOrDefault(p => { try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
                if (withWindow?.MainWindowHandle is { } h && h != IntPtr.Zero) return h;
            }
            finally { foreach (var p in procs) p.Dispose(); }
            await Task.Delay(300);
        }
        return IntPtr.Zero;
    }

    private static string ReadAnyText(UIA2Automation automation, IntPtr hwnd)
    {
        try
        {
            var window = automation.FromHandle(hwnd);
            if (window is null) return "";
            foreach (var el in window.FindAllDescendants())
            {
                try
                {
                    var v = el.Patterns.Value.PatternOrDefault?.Value?.Value;
                    if (!string.IsNullOrEmpty(v)) return v!;
                    if (el.Patterns.Text.IsSupported)
                    {
                        var t = el.Patterns.Text.Pattern.DocumentRange.GetText(4000);
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                }
                catch { /* pattern unsupported / element gone */ }
            }
        }
        catch { /* UIA unavailable */ }
        return "";
    }
}
