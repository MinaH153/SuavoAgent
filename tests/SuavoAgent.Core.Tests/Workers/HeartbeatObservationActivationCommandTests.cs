using System.Text.Json;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class HeartbeatWorkerTests
{
    [Fact]
    public async Task DormantAuthority_DeniesObservationCommandAndBurnsNonce()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _observationAuthority.RevokeLocalAuthority();
        var data = BindLiveCommandExpiry(
            "approve_candidate",
            new { correlationKey = "dormant-denial" });
        var signed = Sign("approve_candidate", JsonSerializer.Serialize(data));

        await InvokeProcessAsync(BuildResponseJson(signed, data));

        Assert.Empty(_db.GetPendingFeedbackEvents(sessionId));
        Assert.False(_db.TryRecordNonce(signed.Nonce));
    }

    [Fact]
    public async Task UnknownCommand_IsCentrallyDeniedAndBurnsNonce()
    {
        var data = BindLiveCommandExpiry(
            "future_desktop_command",
            new { objective = "inspect the desktop" });
        var signed = Sign("future_desktop_command", JsonSerializer.Serialize(data));
        var auditBefore = _db.GetAuditEntryCount();

        await InvokeProcessAsync(BuildResponseJson(signed, data));

        Assert.Equal(auditBefore, _db.GetAuditEntryCount());
        Assert.False(_db.TryRecordNonce(signed.Nonce));
        Assert.Equal(
            ObservationActivationCommandClass.Unknown,
            ObservationActivationCommandPolicy.Classify(signed.Command));
    }

    [Fact]
    public async Task AuditedMaintenance_RemainsAvailableWhileDormant()
    {
        _observationAuthority.RevokeLocalAuthority();
        var data = new
        {
            commandId = "33333333-3333-4333-8333-333333333333",
            reason = "operator_requested",
        };
        var signed = Sign("collect_health_probe", JsonSerializer.Serialize(data));
        var auditBefore = _db.GetAuditEntryCount();

        await InvokeProcessAsync(BuildResponseJson(signed, data));

        Assert.Equal(auditBefore + 1, _db.GetAuditEntryCount());
        Assert.False(_db.TryRecordNonce(signed.Nonce));
        Assert.Equal(
            ObservationActivationCommandClass.MaintenanceControlPlane,
            ObservationActivationCommandPolicy.Classify(signed.Command));
    }

    [Fact]
    public async Task ExpiredAuthority_DeniesObservationCommand()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _observationClock.Advance(TimeSpan.FromMinutes(3));
        var data = BindLiveCommandExpiry(
            "approve_candidate",
            new { correlationKey = "expired-denial" });
        var signed = Sign("approve_candidate", JsonSerializer.Serialize(data));

        await InvokeProcessAsync(BuildResponseJson(signed, data));

        Assert.Empty(_db.GetPendingFeedbackEvents(sessionId));
        Assert.False(_observationAuthority.ObservationEnabled);
        Assert.False(_db.TryRecordNonce(signed.Nonce));
    }

    [Fact]
    public void AuthorityLoss_CancelsAdmittedCommandLease()
    {
        using var admission = ObservationActivationCommandPolicy.Admit(
            "navigate_pricing",
            _observationAuthority,
            CancellationToken.None);
        Assert.True(admission.Admitted);
        Assert.False(admission.Token.IsCancellationRequested);

        _observationAuthority.RevokeLocalAuthority();

        Assert.True(admission.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ExactApprovedPioneerRxCommand_RunsWithCurrentAuthority()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        var data = BindLiveCommandExpiry(
            "approve_candidate",
            new { correlationKey = "active-authority" });
        var signed = Sign("approve_candidate", JsonSerializer.Serialize(data));

        await InvokeProcessAsync(BuildResponseJson(signed, data));

        var feedback = Assert.Single(_db.GetPendingFeedbackEvents(sessionId));
        Assert.Equal("active-authority", feedback.TargetId);
        Assert.False(_db.TryRecordNonce(signed.Nonce));
        Assert.Equal(
            ObservationActivationCommandClass.ApprovedPioneerRxObservation,
            ObservationActivationCommandPolicy.Classify(signed.Command));
    }

    [Fact]
    public void CommandPolicy_ExhaustivelyClassifiesVerifierSurface()
    {
        Assert.Equal(
            SignedCommandVerifier.ExplicitCommands.Order(StringComparer.Ordinal),
            ObservationActivationCommandPolicy.ExplicitCommands.Order(StringComparer.Ordinal));
        Assert.All(
            SignedCommandVerifier.ExplicitCommands,
            command => Assert.NotEqual(
                ObservationActivationCommandClass.Unknown,
                ObservationActivationCommandPolicy.Classify(command)));
        Assert.Equal(
            ObservationActivationCommandClass.ReleaseProhibited,
            ObservationActivationCommandPolicy.Classify("computer_use_observe"));
        Assert.Equal(
            ObservationActivationCommandClass.ReleaseProhibited,
            ObservationActivationCommandPolicy.Classify("navigate_app"));
        Assert.Equal(
            ObservationActivationCommandClass.ReleaseProhibited,
            ObservationActivationCommandPolicy.Classify("decommission"));
        Assert.Equal(
            ObservationActivationCommandClass.MaintenanceControlPlane,
            ObservationActivationCommandPolicy.Classify("abort_navigation"));
        Assert.Equal(
            ObservationActivationCommandClass.ApprovedPioneerRxObservation,
            ObservationActivationCommandPolicy.Classify("navigate_pricing"));
    }
}
