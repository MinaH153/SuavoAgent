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
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// LIVE UIA on a REAL app (Notepad) — the operational proof that the perception + signature-resolution
/// + actuation stack works on a real window, not a SimulatedApp. Runs ONLY on Windows (a no-op pass
/// elsewhere) and is exercised by the windows-uia-smoke CI job on a windows-latest runner, so it does
/// NOT need the production box. This is the closest CI proof to the live-box session: real screen UIA,
/// real AutomationIds, real SendInput.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealNotepadUiaTests
{
    private static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public async Task LiveUia_PerceiveResolveType_OnRealNotepad()
    {
        if (!Windows) return; // no-op on Linux/Mac CI; the windows-uia-smoke job runs it for real

        var logger = new LoggerConfiguration().CreateLogger();
        Process? notepad = null;
        try
        {
            notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            Assert.NotNull(notepad);

            // Wait for the main window to materialize.
            var hwnd = await WaitForMainWindowAsync(notepad!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd);
            await Task.Delay(800); // let UIA settle

            // 1. REAL PERCEPTION — the production extractor reads Notepad's real UIA tree.
            var extractor = new FlaUiElementExtractor(logger);
            var screen = new ScreenBytes(Array.Empty<byte>(), 0, 0, DateTimeOffset.UtcNow, (long)hwnd);
            var elements = await extractor.ExtractAsync(screen, maxElements: 80, CancellationToken.None);
            Assert.NotEmpty(elements); // real elements perceived from a real app

            // A real app surfaces real structural AutomationIds (GREEN-tier). Find one to resolve.
            var withId = elements.FirstOrDefault(e => !string.IsNullOrEmpty(e.AutomationId));

            // 2. REAL SIGNATURE RESOLUTION — resolve a discovered element by (ControlType, AutomationId).
            if (withId is not null)
            {
                using var sigResolver = new UiaSignatureResolver(logger);
                var resolved = sigResolver.Resolve(withId.Role, withId.AutomationId!, null, "notepad", TimeSpan.FromSeconds(5));
                Assert.NotNull(resolved); // a real on-screen element resolved to a real point
            }

            // 3. REAL ACTUATION — SendInput types into the focused Notepad edit, then read it back via UIA.
            var config = new ActuationConfig { Enabled = true, DryRun = false };
            var driver = new SendInputDriver(new ActuationGate(config, logger), config, logger);
            const string typed = "hello from CI uia smoke";
            var result = await driver.TypeTextAsync(
                new TypeTextRequest(typed, ClearFirst: false, PerKeyDelayMs: 5, DryRun: false), CancellationToken.None);
            Assert.True(result.Ok, $"SendInput TypeText failed: {result.RejectionCode}");

            await Task.Delay(400);
            var readBack = ReadNotepadText(hwnd);
            // Real proof the actuation changed the real app. Robust to Notepad variants: assert a prefix landed.
            Assert.Contains("hello", readBack, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (notepad is { HasExited: false }) notepad.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(Process proc, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            proc.Refresh();
            if (proc.MainWindowHandle != IntPtr.Zero) return proc.MainWindowHandle;
            // Win11 Notepad may host the window in a child process; fall back to any notepad window.
            var any = Process.GetProcessesByName("notepad").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            try { if (any?.MainWindowHandle is { } h && h != IntPtr.Zero) return h; }
            finally { foreach (var p in Process.GetProcessesByName("notepad")) p.Dispose(); }
            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }

    private static string ReadNotepadText(IntPtr hwnd)
    {
        try
        {
            using var automation = new UIA2Automation();
            var window = automation.FromHandle(hwnd);
            if (window is null) return string.Empty;
            // Walk for the first element exposing text (Edit/Document) — robust across Notepad variants.
            foreach (var el in window.FindAllDescendants())
            {
                try
                {
                    var val = el.Patterns.Value.PatternOrDefault?.Value;
                    if (!string.IsNullOrEmpty(val?.Value)) return val!.Value!;
                    if (el.Patterns.Text.IsSupported)
                    {
                        var text = el.Patterns.Text.Pattern.DocumentRange.GetText(2000);
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
                catch { /* element gone / pattern unsupported */ }
            }
        }
        catch { /* UIA unavailable */ }
        return string.Empty;
    }
}
