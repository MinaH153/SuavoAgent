using Serilog;
using Serilog.Core;
using SuavoAgent.Helper;
using Xunit;

namespace SuavoAgent.Helper.Tests;

// Trip A 2026-04-25 hard-reset prevention. ResourceBudgetGuard self-kills
// Helper when RSS or sustained CPU exceeds budget so the Watchdog can
// restart cleanly. These tests pin the suicide contract so a future
// refactor can't silently disable the Vision-On safety net.
public class ResourceBudgetGuardTests
{
    private readonly ILogger _silent = Logger.None;

    [Fact]
    public void EvaluateOnce_RssBelowHard_DoesNotExit()
    {
        var exited = -1;
        var guard = new ResourceBudgetGuard(
            new ResourceBudgetGuard.Budget
            {
                // Set hard ceiling far above any sane test process.
                HardKillRssBytes = 100L * 1024 * 1024 * 1024, // 100 GB
                SoftWarnRssBytes = 50L * 1024 * 1024 * 1024,
                SustainedCpuKillPct = 99.9,
                SustainWindow = TimeSpan.FromSeconds(60),
                PollInterval = TimeSpan.FromSeconds(5),
            },
            _silent,
            exit: code => exited = code);

        guard.EvaluateOnce();

        Assert.Equal(-1, exited);
    }

    [Fact]
    public void EvaluateOnce_RssAboveHard_ExitsWithSelfKillCode()
    {
        var exited = -1;
        var budget = new ResourceBudgetGuard.Budget
        {
            // Trigger immediately — any running process has RSS > 1 byte.
            HardKillRssBytes = 1,
            SoftWarnRssBytes = 1,
            SustainedCpuKillPct = 99.9,
            SustainWindow = TimeSpan.FromSeconds(60),
            PollInterval = TimeSpan.FromSeconds(5),
            SelfKillExitCode = 137,
        };
        var guard = new ResourceBudgetGuard(
            budget,
            _silent,
            exit: code => exited = code);

        guard.EvaluateOnce();

        Assert.Equal(137, exited);
    }

    [Fact]
    public void EvaluateOnce_FirstSample_DoesNotKillOnSustainedCpu()
    {
        // Even with a CPU threshold of 0% (every sample exceeds), the rolling
        // window requires a full window's worth of samples before declaring
        // sustained pressure. This guards against startup-spike false fires.
        var exited = -1;
        var guard = new ResourceBudgetGuard(
            new ResourceBudgetGuard.Budget
            {
                HardKillRssBytes = 100L * 1024 * 1024 * 1024,
                SoftWarnRssBytes = 50L * 1024 * 1024 * 1024,
                SustainedCpuKillPct = 0.0, // any CPU usage exceeds
                SustainWindow = TimeSpan.FromSeconds(60),
                PollInterval = TimeSpan.FromSeconds(5),
            },
            _silent,
            exit: code => exited = code);

        guard.EvaluateOnce();

        Assert.Equal(-1, exited);
    }

    [Fact]
    public void EvaluateOnce_TwoFastSamples_StillNotEnoughForSustainedKill()
    {
        // 60s window / 5s poll = 12 samples required. Two samples ≪ 12 so
        // even with a 0% threshold the kill should not fire yet.
        var exited = -1;
        var guard = new ResourceBudgetGuard(
            new ResourceBudgetGuard.Budget
            {
                HardKillRssBytes = 100L * 1024 * 1024 * 1024,
                SoftWarnRssBytes = 50L * 1024 * 1024 * 1024,
                SustainedCpuKillPct = 0.0,
                SustainWindow = TimeSpan.FromSeconds(60),
                PollInterval = TimeSpan.FromSeconds(5),
            },
            _silent,
            exit: code => exited = code);

        guard.EvaluateOnce();
        guard.EvaluateOnce();

        Assert.Equal(-1, exited);
    }

    [Fact]
    public async Task RunAsync_CancellationToken_StopsCleanly()
    {
        var guard = new ResourceBudgetGuard(
            new ResourceBudgetGuard.Budget
            {
                HardKillRssBytes = 100L * 1024 * 1024 * 1024,
                SoftWarnRssBytes = 50L * 1024 * 1024 * 1024,
                SustainedCpuKillPct = 99.9,
                SustainWindow = TimeSpan.FromMilliseconds(200),
                PollInterval = TimeSpan.FromMilliseconds(50),
            },
            _silent,
            exit: _ => Assert.Fail("guard should not exit when cancelled cleanly"));

        using var cts = new CancellationTokenSource();
        var run = guard.RunAsync(cts.Token);
        await Task.Delay(150);
        cts.Cancel();
        await run;
        // Reaching here without throwing or calling exit = success.
    }

    [Fact]
    public void Budget_Defaults_AreSensibleForVisionOn()
    {
        // Pin defaults so a future refactor doesn't silently raise them
        // back to "never fires." Codex-recommended Vision-On budget.
        var b = new ResourceBudgetGuard.Budget();
        Assert.Equal(800L * 1024 * 1024, b.HardKillRssBytes);
        Assert.Equal(500L * 1024 * 1024, b.SoftWarnRssBytes);
        Assert.Equal(80.0, b.SustainedCpuKillPct);
        Assert.Equal(TimeSpan.FromSeconds(60), b.SustainWindow);
        Assert.Equal(TimeSpan.FromSeconds(5), b.PollInterval);
        Assert.Equal(137, b.SelfKillExitCode);
    }
}
