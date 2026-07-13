using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Visible uninstall boundary. Local intent alone is not deletion authority:
/// cleanup can start only from the exact cloud-signed claim already accepted
/// by Broker, and success is terminal only after the signed cleanup ticket is
/// acknowledged by the cloud finalizer.
/// </summary>
internal sealed class UninstallOrchestrator
{
    public enum Phase { Authorize, StopServices, PreserveEvidence, Finalize, Done }

    public sealed record PhaseEvent(Phase Phase, string Message);

    public const string DefaultInstallDir = @"C:\Program Files\Suavo\Agent";
    public const string DefaultDataDir = @"C:\ProgramData\SuavoAgent";

    private readonly string _installDir;
    private readonly string _dataDir;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<
        string,
        string,
        string,
        CancellationToken,
        Task<SelfUninstallFinalizationResult>> _finalize;

    public UninstallOrchestrator(
        string? installDir = null,
        string? dataDir = null,
        Func<string, bool>? fileExists = null,
        Func<string, string, string, CancellationToken,
            Task<SelfUninstallFinalizationResult>>? finalize = null)
    {
        _installDir = string.IsNullOrWhiteSpace(installDir) ? DefaultInstallDir : installDir!;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDir : dataDir!;
        _fileExists = fileExists ?? File.Exists;
        _finalize = finalize ?? SelfUninstallCompletionFinalizer.ExecuteProductionAsync;
    }

    public async Task<SelfUninstallFinalizationResult> RunAsync(
        IProgress<PhaseEvent> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new PhaseEvent(
            Phase.Authorize,
            "Confirming signed dashboard removal authority"));

        var claimPath = Path.Combine(
            _dataDir,
            SelfUninstallContract.RequestFileName + ".claimed");
        if (!_fileExists(claimPath))
        {
            ConsoleUI.WriteWarn(
                "Removal is pending signed approval from the Suavo dashboard. " +
                "No service, credential, evidence, or program file was changed.");
            return SelfUninstallFinalizationResult.Pending(
                "signed_cloud_authority_required");
        }

        progress.Report(new PhaseEvent(
            Phase.StopServices,
            "Removing the authorized runtime"));
        progress.Report(new PhaseEvent(
            Phase.PreserveEvidence,
            "Preserving protected compliance evidence"));
        var result = await _finalize(
                claimPath,
                _installDir,
                _dataDir,
                cancellationToken)
            .ConfigureAwait(false);

        progress.Report(new PhaseEvent(
            Phase.Finalize,
            "Confirming signed cloud completion"));
        if (!result.IsFinalized || result.Cleanup is not { FullyClean: true })
        {
            ConsoleUI.WriteWarn(
                $"Removal is safely pending ({result.Code}). " +
                "SuavoAgent will not report completion until cloud finalization and zero-residue proof both succeed.");
            return result.IsFinalized
                ? SelfUninstallFinalizationResult.Pending(
                    "cleanup_proof_incomplete",
                    result.Cleanup)
                : result;
        }

        ConsoleUI.WriteOk(
            "SuavoAgent runtime removed; signed cloud completion and retained evidence were verified.");
        progress.Report(new PhaseEvent(Phase.Done, "Removal finalized"));
        return result;
    }
}
