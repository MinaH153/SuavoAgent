using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Replication;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// "GETS SMARTER EVERY INSTALL" + "no demo to copy", on a real app, in CI: a NEW install that has
/// NEVER observed this workflow receives it as a FLEET SEED (the same wire path another install's
/// learned template travels), applies it via the production SeedApplicator, and replicates it on the
/// real harness — no local watch-&-learn required. This is the cross-install compounding: knowledge
/// learned at install A drives install B. (The transfer gates are unit-tested in Core; this proves a
/// SEEDED template drives a real app.)
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("RealDesktopUia")]
public sealed class RealAppSeedReplayTests
{
    private static ElementSignature Sig(string id) => new("Button", id, "WinForms.Button");

    private static TemplateStep Step(int ord, string id) =>
        new(ord, TemplateStepKind.Click, Sig(id), new[] { Sig(id) },
            MinElementsRequired: 1, ExpectedAfter: null, IsWrite: false,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);

    [Fact]
    public async Task FleetSeededTemplate_ReplicatesOnRealApp_WithoutLocalObservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var harnessExe = Environment.GetEnvironmentVariable("SUAVOAGENT_UIA_HARNESS");
        if (string.IsNullOrWhiteSpace(harnessExe) || !File.Exists(harnessExe)) return;

        var serilog = new LoggerConfiguration().CreateLogger();
        var processName = Path.GetFileNameWithoutExtension(harnessExe);

        // ── A fleet seed: the 3-step workflow as learned at ANOTHER install, arriving over the wire. ──
        var fingerprint = new PmsVersionFingerprint(processName, "schema", "dialect", "1.0");
        var steps = new[] { Step(0, "openButton"), Step(1, "searchButton"), Step(2, "approveButton") };
        var seedTemplate = new SeedWorkflowTemplate(
            TemplateId: "fleet-seed-open-search-approve",
            TemplateVersion: "1.0.0",
            SkillId: "learned",
            ProcessNameGlob: $"{processName}*",
            PmsVersionRange: new[] { fingerprint },
            ScreenSignatureV1: WorkflowTemplate.ComputeScreenSignature(steps[0].ExpectedVisible),
            StepsHash: WorkflowTemplate.ComputeStepsHash(steps),
            Steps: steps,
            AggregateConfidence: 0.9,
            ContributorCount: 4, FleetMatchCount: 12, FleetMismatchCount: 0,
            HasWriteback: false);

        var response = new SeedResponse(
            SeedDigest: "fleet-seed-digest", SeedVersion: 1, Phase: "pattern",
            GatesPassed: new[] { "schema" }, UiOverlap: null, Correlations: null,
            QueryShapes: Array.Empty<SeedQueryShape>(),
            StatusMappings: Array.Empty<SeedStatusMapping>(),
            WorkflowHints: null,
            WorkflowTemplates: new[] { seedTemplate });

        // ── A fresh install (DB) that has observed NOTHING — apply the fleet seed. ──
        using var db = new AgentStateDb(":memory:");
        const string session = "fleet-seed-session";
        db.CreateLearningSession(session, "pharm-test-001");

        var applicator = new SeedApplicator(db);
        var result = applicator.ApplyWorkflowTemplates(session, response, fingerprint);
        Assert.Equal(1, result.TemplatesApplied); // the fleet knowledge landed

        var stored = db.GetWorkflowTemplate(seedTemplate.TemplateId);
        Assert.NotNull(stored);
        Assert.StartsWith("seed:", stored!.ExtractedBy); // it was SEEDED from the fleet, not locally observed

        // The domain template to replay carries the seed's steps (== what SeedApplicator persisted above).
        var template = new WorkflowTemplate(
            TemplateId: seedTemplate.TemplateId, TemplateVersion: seedTemplate.TemplateVersion,
            SkillId: seedTemplate.SkillId, ProcessNameGlob: seedTemplate.ProcessNameGlob,
            PmsVersionRange: seedTemplate.PmsVersionRange, ScreenSignatureV1: seedTemplate.ScreenSignatureV1,
            StepsHash: seedTemplate.StepsHash, RoutineHashOrigin: null, Steps: seedTemplate.Steps,
            AggregateConfidence: seedTemplate.AggregateConfidence, ObservationCount: seedTemplate.FleetMatchCount,
            HasWriteback: seedTemplate.HasWriteback, ExtractedAt: "2026-06-04T00:00:00Z",
            ExtractedBy: "seed:fleet", RetiredAt: null, RetirementReason: null);

        // ── REPLICATE the seeded template on the real harness. ──
        var extractor = new FlaUiElementExtractor(serilog);
        var config = new ActuationConfig { Enabled = true, DryRun = false };
        var driver = new SendInputDriver(new ActuationGate(config, serilog), config, serilog);
        using var sigResolver = new UiaSignatureResolver(serilog);

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(harnessExe, "learn") { UseShellExecute = false });
            var hwnd = await WaitForWindowAsync(proc!, TimeSpan.FromSeconds(20));
            Assert.NotEqual(IntPtr.Zero, hwnd);
            await Task.Delay(900);

            var plan = TemplatePlanCompiler.Compile(template);
            var replayer = new TemplateReplayer(
                new RealUiaPerceiver(extractor, hwnd),
                new DirectUiaActuator(new UiaLabelResolver(serilog), sigResolver, driver),
                new AllowGate(),
                (_, ct) => Task.Delay(200, ct));

            var replay = await replayer.ReplayAsync(
                plan,
                new AgentObjective("replay the fleet-seeded workflow", "task.seed-replay", "pharm-test-001"),
                new ActuationContext("pharm-test-001", "task.seed-replay", DryRun: false),
                new TemplateReplayOptions { SettleStableReads = 1 },
                CancellationToken.None);

            Assert.Equal(ReplayOutcome.Completed, replay.Outcome); // fleet knowledge drove a real app it never watched
        }
        finally { try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { } }
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
