using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Startup smoke test for the TieredBrain. Runs one synthetic decision at
/// Core startup to verify the full wiring path (rule catalog → engine →
/// verifier → orchestrator) resolves cleanly without actually executing
/// any action. Logs a SINGLE structured line so operators can eyeball health
/// at install time.
///
/// Intentionally uses a skill id that should have no matching rules, so the
/// probe cannot accidentally trigger real behavior. Tier 2 is exercised only
/// if already enabled; the probe never forces Tier 2 on.
///
/// This is observation-only — the orchestrator returns a BrainDecision but
/// the probe does NOT execute actions. Safe to call at every startup.
/// </summary>
public static class BrainStartupProbe
{
    public const string ProbeSkill = "__startup_probe__";

    public static async Task RunAsync(
        TieredBrain brain,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            var ctx = new RuleContext
            {
                SkillId = ProbeSkill,
                ProcessName = "probe.exe",
                WindowTitle = "startup",
                VisibleElements = new HashSet<string>(),
                OperatorIdleMs = 0,
                Flags = new Dictionary<string, string>(),
            };

            // Use a timeout budget of 2 s — enough for rules + a fast Tier-2
            // pass, but not enough to block startup if the model is broken.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var decideTask = brain.DecideAsync(
                ctx,
                allowedTier2Actions: new HashSet<RuleActionType> { RuleActionType.Log },
                shadowMode: true, // extra guard — any Tier-2 approval returns NoMatch
                ct: cts.Token);

            // Hard wall-clock guard. The 2 s cts above is cooperative, but a
            // grammar-constrained llama.cpp generation can spin in native code
            // without yielding a cancellable point, so DecideAsync may not
            // return even after cancellation fires. WhenAny lets the probe
            // RETURN on the deadline regardless (the orphaned inference finishes
            // on its own and the model idle-unloads). Critical so any caller —
            // even one that accidentally awaits the probe — can never be blocked
            // past the budget. (Live failure on Mina's box 2026-06-05: an
            // awaited probe blocked host.Run() and Core never reached Running.)
            // Observe the task even if the guard wins below, so a late fault on
            // the orphaned inference can never surface as an unobserved task
            // exception (Codex P2). OnlyOnFaulted is a no-op on normal completion.
            _ = decideTask.ContinueWith(
                t => logger.LogWarning(
                    "core.brain_startup_probe.orphaned_failure exception_type={ExceptionType}",
                    t.Exception?.GetType().Name ?? "UnknownException"),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            var guard = Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (await Task.WhenAny(decideTask, guard) != decideTask)
            {
                logger.LogWarning(
                    "BrainStartupProbe: exceeded 3s budget — Tier-2 inference did not honor cancellation (model slow or grammar/model mismatch). Wiring resolved; not blocking startup.");
                return;
            }

            var decision = await decideTask;
            logger.LogInformation(
                "BrainStartupProbe: tier={Tier} outcome={Outcome} reason=\"{Reason}\"",
                decision.Tier, decision.Outcome, decision.Reason);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("BrainStartupProbe: timed out — likely Tier-2 model load stuck");
        }
        catch (Exception ex)
        {
            // Probe failures must not crash startup. Log and keep going.
            logger.LogSafeError(ex);
        }
    }
}
