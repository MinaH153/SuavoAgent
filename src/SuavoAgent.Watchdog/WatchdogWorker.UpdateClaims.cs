using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

public sealed partial class WatchdogWorker
{
    private readonly UpdateClaimMonitor _updateClaimMonitor = new();
    private WatchdogUpdateActivationTelemetry? _lastUpdateActivation;

    private void ProcessActiveUpdateClaim(DateTimeOffset now)
    {
        var maintenanceRoot = _options.MaintenanceRoot
                              ?? UpdateActivationContract.DefaultMaintenanceRoot();
        var activeClaimPath = _options.ActiveClaimPath
                              ?? Path.Combine(
                                  maintenanceRoot,
                                  UpdateActivationContract.ActiveClaimFileName);
        var completionPath = _options.ActivationCompletionPath
                             ?? Path.Combine(
                                 maintenanceRoot,
                                 UpdateActivationContract.CompletionFileName);
        var inspection = _updateClaimMonitor.Inspect(
            maintenanceRoot,
            activeClaimPath,
            completionPath,
            now);
        if (inspection.State == UpdateClaimState.None)
        {
            _lastUpdateActivation = null;
            return;
        }

        var pointer = inspection.Pointer;
        switch (inspection.State)
        {
            case UpdateClaimState.Invalid:
                _logger.LogCritical(
                    "SYSTEM update durable claim is invalid; refusing blind resume: {Code}",
                    inspection.Code);
                RecordUpdateActivation(now, pointer, "invalid", inspection.Code);
                return;

            case UpdateClaimState.Completed:
                var outcome = inspection.Completion!.Outcome;
                var targetVersion = pointer?.TargetVersion
                                    ?? inspection.Completion.TargetVersion;
                if (string.Equals(outcome, "committed", StringComparison.Ordinal))
                    _logger.LogInformation(
                        "SYSTEM update v{Version} has durable completion",
                        targetVersion);
                else
                    _logger.LogCritical(
                        "SYSTEM update v{Version} terminated with {Outcome}",
                        targetVersion,
                        outcome);
                RecordUpdateActivation(
                    now,
                    pointer,
                    "completed",
                    outcome,
                    inspection.Completion);
                return;

            case UpdateClaimState.AwaitingHeartbeat:
                RecordUpdateActivation(now, pointer, "running", inspection.Code);
                return;

            case UpdateClaimState.ResumeRequired:
                break;

            default:
                return;
        }

        var resumeLeaseId = RemoteCommandTrust.ComputeSha256Hex(
            "resume|" + pointer!.ReplayId);
        try
        {
            if (_updateReplayLedger.Contains(resumeLeaseId, now) ||
                !_updateReplayLedger.TryReserve(resumeLeaseId, now))
            {
                RecordUpdateActivation(now, pointer, "resume_leased", inspection.Code);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeCritical(ex);
            RecordUpdateActivation(now, pointer, "resume_blocked", "resume_ledger_unavailable");
            return;
        }

        var terminateRunner = _options.TerminateStaleUpdateRunner
                              ?? ((root, stagingId) =>
                                  StaleUpdateRunnerTerminator.TerminateExact(
                                      root,
                                      stagingId,
                                      _logger));
        if (!terminateRunner(maintenanceRoot, pointer.StagingId))
        {
            ReleaseResumeLease(resumeLeaseId);
            _logger.LogCritical(
                "SYSTEM update claim is stale but the exact durable-claim runner could not be terminated");
            RecordUpdateActivation(
                now,
                pointer,
                "resume_blocked",
                "stale_runner_termination_failed");
            return;
        }

        if (!_command.InvokeUpdateCoordinatorResume(activeClaimPath))
        {
            ReleaseResumeLease(resumeLeaseId);
            _logger.LogCritical(
                "SYSTEM update claim heartbeat expired and trusted resume launch failed");
            RecordUpdateActivation(now, pointer, "resume_failed", inspection.Code);
            return;
        }

        _logger.LogWarning(
            "SYSTEM update claim heartbeat expired; relaunched trusted coordinator for v{Version}",
            pointer.TargetVersion);
        RecordUpdateActivation(now, pointer, "resume_launched", inspection.Code);
    }

    private void ReleaseResumeLease(string resumeLeaseId)
    {
        try { _updateReplayLedger.Release(resumeLeaseId); }
        catch (Exception ex)
        {
            _logger.LogSafeCritical(ex);
        }
    }

    private void RecordUpdateActivation(
        DateTimeOffset now,
        UpdateActivationClaimPointer? pointer,
        string state,
        string detail,
        UpdateActivationCompletion? completion = null)
    {
        _lastUpdateActivation = new WatchdogUpdateActivationTelemetry(
            Present: true,
            TargetVersion: pointer?.TargetVersion ?? completion?.TargetVersion,
            StagingId: pointer?.StagingId ?? completion?.StagingId,
            LastHeartbeatAt: pointer?.LastHeartbeatAtUtc,
            ObservedAt: now.ToString("O"),
            State: state,
            Detail: detail);
    }
}

internal sealed record WatchdogUpdateActivationTelemetry(
    bool Present,
    string? TargetVersion,
    string? StagingId,
    string? LastHeartbeatAt,
    string ObservedAt,
    string State,
    string Detail);
