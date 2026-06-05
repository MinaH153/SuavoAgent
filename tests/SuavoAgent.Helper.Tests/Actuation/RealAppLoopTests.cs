using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// FULL AGENTIC LOOP driving a REAL classic-Win32 app (the SuavoAgent.UiaHarness) end-to-end in CI:
/// the production AgenticLoopRunner perceives the real window with FlaUiElementExtractor, reasons with
/// the REAL TieredBrainReasoner (rule brain — the LLM tiers can't run in CI), acts with the production
/// UiaLabelResolver + SendInputDriver on the real screen, and verifies by re-perceiving — driving the
/// real app to Done. This is [loop → real UIA] one-shot on a real app, no production box.
///
/// The actuator is wired DIRECTLY to the Helper resolvers (not through the VerbDispatcher), on purpose:
/// the dispatcher's process allowlist (notepad/calc only) is a SECURITY boundary that correctly blocks
/// a non-sandbox app, and the full VerbDispatcher path is already proven end-to-end in the Core e2e
/// suite. This test's job is the loop's ORCHESTRATION over real perception + real actuation.
/// Runs only on Windows when SUAVOAGENT_UIA_HARNESS points at the built harness exe (set by the
/// windows-uia-smoke CI job); no-op skip otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("RealDesktopUia")]
public sealed class RealAppLoopTests
{
    [Fact]
    public async Task FullAgenticLoop_DrivesRealClassicWin32App_ToDone()
    {
        if (!OperatingSystem.IsWindows()) return;
        var harnessExe = Environment.GetEnvironmentVariable("SUAVOAGENT_UIA_HARNESS");
        if (string.IsNullOrWhiteSpace(harnessExe) || !File.Exists(harnessExe)) return;

        var serilog = new LoggerConfiguration().CreateLogger();
        var processName = Path.GetFileNameWithoutExtension(harnessExe); // "SuavoAgent.UiaHarness"
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(harnessExe) { UseShellExecute = false });
            Assert.NotNull(proc);
            var hwnd = await WaitForWindowAsync(proc!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd);
            await Task.Delay(1000);

            var extractor = new FlaUiElementExtractor(serilog);
            var config = new ActuationConfig { Enabled = true, DryRun = false };
            var driver = new SendInputDriver(new ActuationGate(config, serilog), config, serilog);
            using var sigResolver = new UiaSignatureResolver(serilog);
            var labelResolver = new UiaLabelResolver(serilog);

            var perceiver = new RealUiaPerceiver(extractor, hwnd);
            var actuator = new DirectUiaActuator(labelResolver, sigResolver, driver);

            // Real Tier-1 rule brain: while "Button:Save" is shown, click it; once "Button:Done" shows, finish.
            var rules = new[]
            {
                new Rule
                {
                    Id = "realloop.click-save", SkillId = NavigateReasoning.NavigateSkillId,
                    When = new RulePredicate { VisibleElements = new[] { "Button:Save" } },
                    Then = new[]
                    {
                        new RuleActionSpec
                        {
                            Type = RuleActionType.Click,
                            Parameters = new Dictionary<string, string> { ["label"] = "Save", ["process_name"] = processName },
                        },
                    },
                },
                new Rule
                {
                    Id = "realloop.done", SkillId = NavigateReasoning.NavigateSkillId,
                    When = new RulePredicate { VisibleElements = new[] { "Button:Done" } },
                    Then = new[]
                    {
                        new RuleActionSpec
                        {
                            Type = RuleActionType.Log,
                            Parameters = new Dictionary<string, string> { ["status"] = NavigateReasoning.CompleteStatusValue },
                        },
                    },
                },
            };
            var reasoner = new TieredBrainReasoner(
                new RuleEngine(rules, NullLogger<RuleEngine>.Instance),
                new NullLocalInference(),
                new ActionVerifier(autoExecuteDestructive: true),
                new NullCloudReasoning(),
                NullLogger<TieredBrain>.Instance);

            // Default delay = real Task.Delay, so settle polls give the real UI time to repaint after the click.
            var runner = new AgenticLoopRunner(perceiver, reasoner, actuator, new AllowGate());

            var result = await runner.RunAsync(
                new AgentObjective("click save then finish", "task.realloop", "pharm-test-001"),
                new AgenticLoopOptions
                {
                    MaxSteps = 8,
                    SettleStableReads = 2,
                    SettlePollInterval = TimeSpan.FromMilliseconds(200),
                    AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey", "Log" },
                },
                CancellationToken.None);

            Assert.Equal(TerminationReason.Done, result.Termination);

            var final = await perceiver.PerceiveAsync(CancellationToken.None);
            Assert.Contains("Button:Done", final!.ElementSummary);      // the loop advanced the real app
            Assert.DoesNotContain("Button:Save", final.ElementSummary); // and consumed the Save step
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
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

    /// <summary>Production perception: FlaUiElementExtractor on the real window → the loop's PerceivedScreen.</summary>
    private sealed class RealUiaPerceiver : IPerceiver
    {
        private readonly FlaUiElementExtractor _extractor;
        private readonly IntPtr _hwnd;
        public RealUiaPerceiver(FlaUiElementExtractor extractor, IntPtr hwnd) { _extractor = extractor; _hwnd = hwnd; }

        public async Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
        {
            var els = await _extractor.ExtractAsync(
                new ScreenBytes(Array.Empty<byte>(), 0, 0, DateTimeOffset.UtcNow, (long)_hwnd), 80, ct);
            var summary = new List<string>();
            var signatures = new List<string>();
            foreach (var e in els)
            {
                summary.Add(string.IsNullOrEmpty(e.Name) ? e.Role : $"{e.Role}:{e.Name}");
                if (!string.IsNullOrEmpty(e.AutomationId)) signatures.Add($"{e.Role}|{e.AutomationId}");
            }
            var hash = string.Join(",", summary.OrderBy(s => s, StringComparer.Ordinal));
            return new PerceivedScreen(hash, Scrubbed: true, summary, WindowTitle: "harness", Signatures: signatures);
        }
    }

    /// <summary>Production actuation wired directly to the Helper resolvers (see class remarks re: allowlist).</summary>
    private sealed class DirectUiaActuator : IActuator
    {
        private readonly UiaLabelResolver _label;
        private readonly UiaSignatureResolver _sig;
        private readonly SendInputDriver _driver;
        public DirectUiaActuator(UiaLabelResolver label, UiaSignatureResolver sig, SendInputDriver driver)
        { _label = label; _sig = sig; _driver = driver; }

        public async Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (action.Kind != NextActionKind.Act || action.Parameters is null)
                return new ActOutcome(ActStatus.Failed, "not_an_actuating_action");

            var p = action.Parameters;
            string Param(string k) => p.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
            var proc = Param("process_name");

            if (action.Verb == "click_by_label")
            {
                var t = _label.Resolve(Param("label"), proc, UiaLabelResolver.MatchMode.Exact, TimeSpan.FromSeconds(5));
                if (t is null) return new ActOutcome(ActStatus.Failed, "label_not_resolved");
                var r = await _driver.ClickAtAsync(t.X, t.Y, dryRun: false, ct);
                return r.Ok ? new ActOutcome(ActStatus.Success, EvidenceHash: r.EvidenceHash) : new ActOutcome(ActStatus.Failed, r.RejectionCode);
            }
            if (action.Verb == "click_by_signature")
            {
                var t = _sig.Resolve(Param("controlType"), Param("automationId"), null, proc, TimeSpan.FromSeconds(5));
                if (t is null) return new ActOutcome(ActStatus.Failed, "signature_not_resolved");
                var r = await _driver.ClickAtAsync(t.X, t.Y, dryRun: false, ct);
                return r.Ok ? new ActOutcome(ActStatus.Success, EvidenceHash: r.EvidenceHash) : new ActOutcome(ActStatus.Failed, r.RejectionCode);
            }
            return new ActOutcome(ActStatus.Failed, "unsupported_verb");
        }
    }

    private sealed class AllowGate : ISafetyGate
    {
        public SafetyVerdict Preflight(AgentObjective objective) => SafetyVerdict.Allow;
        public SafetyVerdict GateAction(NextAction action, AgentObjective objective) => SafetyVerdict.Allow;
        public bool AssertScrubbed(PerceivedScreen screen) => true; // the harness has no PHI; perception is GREEN-tier
    }
}
