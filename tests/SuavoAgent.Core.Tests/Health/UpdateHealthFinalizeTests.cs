using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// Closes the OTA-telemetry-never-closes gap: a clean self-update writes "applying" then exits, and on
/// restart nothing flips it to success, so the cloud rollout reading update-health.json stays "applying"
/// forever (observed live on 3.24.0 — watchdog confirmed the restart, but update-health stayed
/// "applying"). IsCompletedApplyingUpdate is the milestone predicate the first healthy heartbeat uses to
/// finalize the record.
/// </summary>
public class UpdateHealthFinalizeTests
{
    private static UpdateHealthPayload Applying(string? target) =>
        new(
            Present: true,
            Status: "applying",
            TargetVersion: target,
            LastAttemptAt: "2026-06-07T05:17:23Z",
            LastSuccessAt: null,
            ConsecutiveFailures: 0,
            LastErrorKind: null,
            Channel: "stable");

    [Fact]
    public void Finalizes_WhenApplyingTargetMatchesRunningVersion()
    {
        Assert.True(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("3.24.0"), "3.24.0"));
    }

    [Fact]
    public void Finalizes_AcrossVersionPrefix_BridgesVForm()
    {
        // The agent reports bare semver "3.24.0"; a manifest/tag may carry "v3.24.0" — normalization
        // must bridge them, else the loop would never close (the MKM#1181 prefix-mismatch class).
        Assert.True(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("v3.24.0"), "3.24.0"));
        Assert.True(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("3.24.0"), "v3.24.0"));
    }

    [Fact]
    public void DoesNotFinalize_WhenStillRunningOlderVersion()
    {
        // Update mid-flight or a failed swap: still on the old binary → must stay "applying", never a
        // false success.
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("3.24.0"), "3.23.0"));
    }

    [Fact]
    public void DoesNotFinalize_WhenStatusIsNotApplying()
    {
        var current = new UpdateHealthPayload(true, "current", "3.24.0", "t", "t", 0, null, "stable");
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(current, "3.24.0"));

        var failed = new UpdateHealthPayload(true, "failed", "3.24.0", "t", null, 1, "boom", "stable");
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(failed, "3.24.0"));
    }

    [Fact]
    public void DoesNotFinalize_WhenTargetOrRunningMissing()
    {
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying(null), "3.24.0"));
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("3.24.0"), null));
        Assert.False(RuntimeHealthEvidence.IsCompletedApplyingUpdate(Applying("3.24.0"), ""));
    }
}
