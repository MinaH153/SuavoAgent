using System.Diagnostics;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

public sealed partial class SendInputDriver
{
    /// <summary>
    /// Moves the real Windows pointer to a locally resolved target without clicking it.
    /// The same live gate, process identity check, foreground check, and injected-input
    /// path used by clicks apply here. The persistent presence layer is painted first so
    /// the pharmacist sees intent before the pointer arrives.
    /// </summary>
    public ActuationResult MovePointerTo(
        int x,
        int y,
        int expectedPid,
        string expectedProcess,
        TargetTrustKind targetTrustKind)
    {
        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection;
        if (_gate.IsDryRun)
            return ActuationResult.Reject(
                ActuationRejectionCodes.GateDryRun,
                "live pointer movement is unavailable while the local gate is dry-run",
                dryRun: true);
        if (expectedPid <= 0 || string.IsNullOrWhiteSpace(expectedProcess) ||
            targetTrustKind == TargetTrustKind.Unspecified)
            return ActuationResult.Reject(
                ActuationRejectionCodes.MalformedRequest,
                "a trusted target process is required for pointer movement",
                dryRun: false);

        var evidence = ComputeEvidenceHash("move_pointer", $"{x},{y}");
        var sw = Stopwatch.StartNew();
        try
        {
            var identityTrusted = true;
            var ownsForeground = true;
            var mutationRejection = _gate.ExecuteLiveMutationOrReject(() =>
            {
                identityTrusted = TargetStillTrusted(expectedPid, expectedProcess, targetTrustKind);
                if (!identityTrusted) return;
                ownsForeground = TargetProcessOwnsForeground(expectedPid, targetTrustKind);
                if (!ownsForeground) return;
                TryGlow(x, y);
                MovePointerOnly(x, y);
            });
            if (mutationRejection is not null) return mutationRejection;
            if (!identityTrusted)
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "approved target identity changed before pointer movement",
                    dryRun: false);
            if (!ownsForeground)
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ForegroundNotTarget,
                    "approved target did not own the foreground before pointer movement",
                    dryRun: false);
            return ActuationResult.Success(sw.ElapsedMilliseconds, dryRun: false, evidence);
        }
        catch (Exception ex)
        {
            _logger.Warning("Pointer movement failed locally ({ErrorType})", ex.GetType().Name);
            return ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "pointer movement failed locally",
                dryRun: false);
        }
    }

    /// <summary>Shows a fixed, PHI-screened action caption. Never affects actuation.</summary>
    public void NarratePresence(string actionKind, string? safeLabel)
    {
        try { _presence?.Narrate(actionKind, safeLabel); }
        catch { /* visual-only */ }
    }

    private static bool TargetProcessOwnsForeground(int pid, TargetTrustKind targetTrustKind) =>
        targetTrustKind switch
        {
            TargetTrustKind.Sandbox => SandboxWindowResolver.IsSandboxAppForeground(pid),
            TargetTrustKind.PioneerRx => SystemObservers.ForegroundGuard.IsPidForeground(pid),
            _ => false,
        };

    private static void MovePointerOnly(int x, int y) =>
        SendInputOrThrow("move_pointer", new[] { BuildAbsoluteMouseMove(x, y) });
}
