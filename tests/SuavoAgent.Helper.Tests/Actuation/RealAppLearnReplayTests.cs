using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Replication;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// THE WATCH-&-LEARN LOOP, end-to-end on a REAL app, in CI: observe a real classic-Win32 app → mine a
/// template with the production learning pipeline → replay that LEARNED template on a fresh instance.
///
///   1. OBSERVE  — drive the real harness ("learn" 3-step workflow) and perceive each step with the
///                 production FlaUiElementExtractor, confirming its real (ControlType, AutomationId).
///   2. LEARN    — record those observations as behavioral events (6 repetitions, as a real session
///                 would), then run the REAL RoutineDetector + WorkflowTemplateExtractor to mine a
///                 WorkflowTemplate. Nothing hand-authored — the template is produced by the pipeline.
///   3. REPLICATE— compile + replay the LEARNED template on a FRESH harness instance with the production
///                 UiaSignatureResolver + SendInputDriver → it drives the real app to completion.
///
/// This is way-in #1 ("watch &amp; learn ... gather structural + workflow knowledge, and can replicate")
/// proven on a real app without the box. Runs only on Windows when SUAVOAGENT_UIA_HARNESS is set.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("RealDesktopUia")]
public sealed class RealAppLearnReplayTests
{
    private static readonly (string Id, string Label)[] Steps =
    {
        ("openButton", "Open"), ("searchButton", "Search"), ("approveButton", "Approve"),
    };

    [Fact]
    public async Task WatchLearnReplicate_OnRealClassicWin32App()
    {
        if (!OperatingSystem.IsWindows()) return;
        var harnessExe = Environment.GetEnvironmentVariable("SUAVOAGENT_UIA_HARNESS");
        if (string.IsNullOrWhiteSpace(harnessExe) || !File.Exists(harnessExe)) return;

        var serilog = new LoggerConfiguration().CreateLogger();
        var processName = Path.GetFileNameWithoutExtension(harnessExe);
        var extractor = new FlaUiElementExtractor(serilog);
        var config = new ActuationConfig { Enabled = true, DryRun = false };
        var driver = new SendInputDriver(new ActuationGate(config, serilog), config, serilog);
        using var sigResolver = new UiaSignatureResolver(serilog);

        // ── 1. OBSERVE: drive the real harness through its 3 steps, perceiving each (real UIA). ──
        var observed = new List<(string Tree, string Elem)>();
        Process? watch = null;
        try
        {
            watch = Process.Start(new ProcessStartInfo(harnessExe, "learn") { UseShellExecute = false });
            var hwnd = await WaitForWindowAsync(watch!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd);
            await Task.Delay(900);
            var perceiver = new RealUiaPerceiver(extractor, hwnd);

            foreach (var (id, _) in Steps)
            {
                var sig = $"Button|{id}";
                PerceivedScreen? screen = null;
                for (var i = 0; i < 15 && (screen?.Signatures?.Contains(sig) != true); i++)
                {
                    await Task.Delay(200);
                    screen = await perceiver.PerceiveAsync(CancellationToken.None);
                }
                Assert.True(screen?.Signatures?.Contains(sig) == true, $"watch: {sig} never appeared");
                observed.Add((screen!.ContentHash, id)); // real screen hash + real AutomationId

                var target = sigResolver.Resolve("Button", id, null, processName, TimeSpan.FromSeconds(5));
                Assert.NotNull(target);
                var click = await driver.ClickAtAsync(target!.X, target.Y, dryRun: false, CancellationToken.None);
                Assert.True(click.Ok, $"watch: click {id} failed: {click.RejectionCode}");
            }
        }
        finally { try { if (watch is { HasExited: false }) watch.Kill(entireProcessTree: true); } catch { } }

        Assert.Equal(3, observed.Count); // we genuinely observed all three steps on the real app

        // ── 2. LEARN: record the observations as a real session would, then mine a template. ──
        using var db = new AgentStateDb(":memory:");
        const string session = "watch-learn-session";
        db.CreateLearningSession(session, "pharm-test-001");

        var baseTime = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        var seq = 1;
        for (var rep = 0; rep < 6; rep++) // six repetitions — what a real operator's session looks like
        {
            var t0 = baseTime.AddMinutes(rep * 2); // >30s between reps → no spurious cross-rep edges
            for (var s = 0; s < observed.Count; s++)
            {
                db.InsertBehavioralEvent(session, seq++, "interaction", "invoked",
                    observed[s].Tree, observed[s].Elem, "Button", "WinForms.Button",
                    null, null, null, null, null, 1, t0.AddSeconds(s * 5).ToString("o"));
            }
        }

        new RoutineDetector(db, session).DetectAndPersist();
        Assert.NotEmpty(db.GetLearnedRoutines(session)); // the pipeline detected the routine from observation

        var fingerprint = new PmsVersionFingerprint("SuavoAgent.UiaHarness", "schema", "dialect", "1.0");
        var extractorPipeline = new WorkflowTemplateExtractor(
            db, session, "learned", $"{processName}*", () => fingerprint, WorkflowTemplateThresholds.Default);
        var learned = extractorPipeline.ExtractAndPersist();

        Assert.NotEmpty(learned); // a template was LEARNED (mined), not hand-authored
        var template = learned[0];
        Assert.Equal(3, template.Steps.Count);
        Assert.Equal(Steps.Select(s => s.Id), template.Steps.Select(s => s.Target.AutomationId)); // the observed workflow

        // ── 3. REPLICATE: replay the LEARNED template on a FRESH harness instance. ──
        Process? replay = null;
        try
        {
            replay = Process.Start(new ProcessStartInfo(harnessExe, "learn") { UseShellExecute = false });
            var hwnd2 = await WaitForWindowAsync(replay!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd2);
            await Task.Delay(900);

            var plan = TemplatePlanCompiler.Compile(template);
            var replayer = new TemplateReplayer(
                new RealUiaPerceiver(extractor, hwnd2),
                new DirectUiaActuator(new UiaLabelResolver(serilog), sigResolver, driver),
                new AllowGate(),
                (_, ct) => Task.Delay(200, ct)); // real settle delay so the UI repaints between clicks

            var result = await replayer.ReplayAsync(
                plan,
                new AgentObjective("replay the learned workflow", "task.learn-replay", "pharm-test-001"),
                new ActuationContext("pharm-test-001", "task.learn-replay", DryRun: false),
                new TemplateReplayOptions { SettleStableReads = 1 },
                CancellationToken.None);

            Assert.Equal(ReplayOutcome.Completed, result.Outcome); // the learned template drove the real app to done
        }
        finally { try { if (replay is { HasExited: false }) replay.Kill(entireProcessTree: true); } catch { } }
    }

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
