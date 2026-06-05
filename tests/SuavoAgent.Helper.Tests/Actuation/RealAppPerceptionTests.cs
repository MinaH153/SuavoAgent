using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// REAL-APP perceive → resolve → act → re-perceive on a classic-Win32 app (the SuavoAgent.UiaHarness
/// WinForms app — the same control shape as the agent's real target, PioneerRx). Unlike the WinUI Store
/// Notepad (whose content the production FlaUiElementExtractor's classic-control filter can't see), this
/// proves the WHOLE production perception + signature-resolution + actuation cycle that BOTH ways-in
/// depend on, against a real window, in CI — no production box:
///   • the NL loop's perception + verify (FlaUiElementExtractor reads the real tree, before AND after),
///   • the watch-&-learn replay's grounding (UiaSignatureResolver resolves a real element by signature),
///   • the shared actuation (SendInputDriver clicks the real point and the real app advances).
/// Runs only on Windows when the harness exe path is provided (SUAVOAGENT_UIA_HARNESS); the
/// windows-uia-smoke CI job builds the harness and sets it. No-op skip otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealAppPerceptionTests
{
    [Fact]
    public async Task ProductionStack_PerceiveResolveActReperceive_OnRealClassicWin32App()
    {
        if (!OperatingSystem.IsWindows()) return;
        var harnessExe = Environment.GetEnvironmentVariable("SUAVOAGENT_UIA_HARNESS");
        if (string.IsNullOrWhiteSpace(harnessExe) || !File.Exists(harnessExe)) return; // harness not built (local)

        var logger = new LoggerConfiguration().CreateLogger();
        var processName = Path.GetFileNameWithoutExtension(harnessExe); // "SuavoAgent.UiaHarness"
        var extractor = new FlaUiElementExtractor(logger);
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(harnessExe) { UseShellExecute = false });
            Assert.NotNull(proc);

            var hwnd = await WaitForWindowAsync(proc!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd);
            await Task.Delay(1000); // let the UIA tree settle

            // 1. PERCEIVE — the production extractor reads the real classic-Win32 tree (the loop's eyes).
            var before = await PerceiveAsync(extractor, hwnd);
            Assert.Contains(before, e => e.AutomationId == "saveButton" && e.Role == "Button");

            // 2. RESOLVE BY SIGNATURE — the production resolver grounds a real element (replay's grounding).
            using var sigResolver = new UiaSignatureResolver(logger);
            var target = sigResolver.Resolve("Button", "saveButton", null, processName, TimeSpan.FromSeconds(5));
            Assert.NotNull(target); // resolved to a real on-screen point

            // 3. ACT — the production SendInput driver clicks the resolved point (shared actuation).
            var config = new ActuationConfig { Enabled = true, DryRun = false };
            var driver = new SendInputDriver(new ActuationGate(config, logger), config, logger);
            var click = await driver.ClickAtAsync(target!.X, target.Y, dryRun: false, CancellationToken.None);
            Assert.True(click.Ok, $"SendInput click failed: {click.RejectionCode}");

            // 4. RE-PERCEIVE — the click advanced the REAL app; the production extractor sees the new state
            //    (the loop's verify / replay's postcondition check, closed on a real window).
            IReadOnlyList<VisualElement> after = Array.Empty<VisualElement>();
            for (var i = 0; i < 12 && !after.Any(e => e.AutomationId == "doneButton"); i++)
            {
                await Task.Delay(300);
                after = await PerceiveAsync(extractor, hwnd);
            }
            Assert.Contains(after, e => e.AutomationId == "doneButton");   // the workflow completed on a real app
            Assert.DoesNotContain(after, e => e.AutomationId == "saveButton"); // and the Save step was consumed
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }

    private static Task<IReadOnlyList<VisualElement>> PerceiveAsync(FlaUiElementExtractor extractor, IntPtr hwnd) =>
        extractor.ExtractAsync(
            new ScreenBytes(Array.Empty<byte>(), 0, 0, DateTimeOffset.UtcNow, (long)hwnd),
            maxElements: 80,
            CancellationToken.None);

    private static async Task<IntPtr> WaitForWindowAsync(Process proc, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { proc.Refresh(); if (proc.MainWindowHandle != IntPtr.Zero) return proc.MainWindowHandle; }
            catch { /* not ready */ }
            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }
}
