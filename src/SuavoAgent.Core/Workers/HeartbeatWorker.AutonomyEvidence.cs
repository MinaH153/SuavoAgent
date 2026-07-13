using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    internal sealed record PricingAutonomyAdmission(
        bool Allowed,
        AutonomyEvidenceScope Scope,
        bool TrustedIdentity);

    internal sealed record PricingAutonomyTerminalIdentity(
        AutonomyEvidenceScope EvidenceScope,
        bool Stable);

    private AutonomyEvidenceScope? BuildTrustedPricingAutonomyScope()
    {
        var identity = _serviceProvider
            .GetService<IPioneerRxAutonomyIdentityProvider>()?
            .Current(DateTimeOffset.UtcNow);
        if (identity is null) return null;
        var binding = _serviceProvider
            .GetService<IActivePmsAdapterRegistry>()?
            .CurrentBinding();
        return BuildPricingAutonomyScope(identity, binding, _options.PricingExecutor);
    }

    internal static AutonomyEvidenceScope BuildPricingAutonomyScope(
        PioneerRxAutonomyIdentity identity,
        ActivePmsAdapterBinding? binding,
        PricingExecutorMode executorMode)
    {
        var actionClass = executorMode == PricingExecutorMode.SqlFirst
            ? "pricing_sql_read"
            : "pricing_live_uia";
        var selectorDigest = TaskAutonomyScope.ComponentDigest(
            "pricing-selector-v1",
            actionClass,
            executorMode.ToString(),
            identity.FileVersion,
            identity.ExecutableSha256,
            identity.SignerCertificateSha256,
            identity.ApprovalReceiptDigest,
            identity.AuthorityDigest,
            identity.ApprovalCounter.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var templateDigest = binding?.TemplateDigest ?? TaskAutonomyScope.ComponentDigest(
            "builtin-pricing-template-v1",
            actionClass,
            identity.FileVersion,
            identity.ApprovalReceiptDigest);
        var modelDigest = binding?.ModelDigest ?? TaskAutonomyScope.ComponentDigest(
            "builtin-pricing-model-v1",
            executorMode.ToString(),
            identity.FileVersion,
            identity.ApprovalReceiptDigest);
        return TaskAutonomyScope.Create(
            "pricing",
            "pricing",
            "pioneerrx",
            identity.FileVersion,
            selectorDigest,
            templateDigest,
            modelDigest,
            executorMode);
    }

    internal static AutonomyExecutionMode ReadAutonomyExecutionMode(JsonElement data)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty("autonomyExecutionMode", out var value) &&
           value.ValueKind == JsonValueKind.String &&
           string.Equals(value.GetString(), "auto", StringComparison.Ordinal)
            ? AutonomyExecutionMode.Auto
            : AutonomyExecutionMode.Supervised;

    private PricingAutonomyAdmission CapturePricingAutonomyAdmission(
        AutonomyExecutionMode mode)
    {
        // This is the single admission read. The exact scope returned here is
        // carried through the run and is the only scope terminal evidence may
        // use; a later identity read can demote/latch the run but can never
        // replace its admitted identity with a new or unverified scope.
        var trustedScope = BuildTrustedPricingAutonomyScope();
        var scope = trustedScope ?? BuildUnprovenPricingScope();
        var allowed = mode == AutonomyExecutionMode.Supervised ||
            (_taskAutonomy is not null &&
             !string.IsNullOrWhiteSpace(_options.PharmacyId) &&
             trustedScope is not null &&
             IsPricingAutonomyCommandAllowed(
                 mode, _taskAutonomy, trustedScope, _options.PharmacyId));
        return new(allowed, scope, trustedScope is not null);
    }

    internal static bool IsPricingAutonomyCommandAllowed(
        AutonomyExecutionMode mode,
        TaskAutonomyLedger ledger,
        AutonomyEvidenceScope scope,
        string pharmacyId)
        => mode == AutonomyExecutionMode.Supervised ||
           ledger.MayRunUnsupervised(
               scope,
               pharmacyId,
               // This exact server-owned value is signed with the command and
               // emitted only from the exact operator setting. It is the
               // pricing deployment enable; the legacy global flag remains for
               // workflow and replay paths.
               unsupervisedExecutionEnabled: true);

    internal static PricingAutonomyTerminalIdentity EvaluatePricingTerminalIdentity(
        PricingAutonomyAdmission admission,
        AutonomyEvidenceScope? currentTrustedScope)
    {
        var stable = !admission.TrustedIdentity ||
            (currentTrustedScope is not null &&
             string.Equals(
                 currentTrustedScope.ScopeDigest,
                 admission.Scope.ScopeDigest,
                 StringComparison.Ordinal));
        // The evidence scope is deliberately always the admission scope. The
        // current read exists only to detect drift and demote/latch the run.
        return new(admission.Scope, stable);
    }

    internal static bool EnforcePricingAdmissionIdentity(
        PricingAutonomyAdmission admission,
        Func<string, bool> latch)
    {
        return admission.TrustedIdentity || latch("admission_identity_unproven");
    }

    private bool EnforcePricingAdmissionIdentity(PricingAutonomyAdmission admission) =>
        EnforcePricingAdmissionIdentity(admission, LatchPricingAutonomy);

    /// <summary>
    /// Feed device-signed, exact-scope semantic evidence after every pricing run.
    /// Missing evidence can only withhold or demote autonomy, never grant it.
    /// </summary>
    private AutonomyEvidenceScope BuildUnprovenPricingScope()
    {
        var digest = TaskAutonomyScope.ComponentDigest(
            "pioneerrx-identity-unproven-v1",
            _options.PricingExecutor.ToString());
        return TaskAutonomyScope.Create(
            "pricing", "pricing", "pioneerrx", "unverified",
            digest, digest, digest, _options.PricingExecutor);
    }

    private bool TryRecordPricingAutonomy(
        string runId,
        PricingAutonomyAdmission admission,
        PricingJobExecutionResult? execution,
        AutonomyExecutionMode executionMode,
        AutonomySemanticResult? forcedResult = null,
        string reasonCode = "execution_terminal")
    {
        if (_taskAutonomy is null)
        {
            LatchPricingAutonomy("autonomy_ledger_unavailable");
            return false;
        }
        try
        {
            var terminalScope = admission.TrustedIdentity
                ? BuildTrustedPricingAutonomyScope()
                : null;
            var terminalIdentity = EvaluatePricingTerminalIdentity(
                admission, terminalScope);
            var identityStable = terminalIdentity.Stable;
            if (admission.TrustedIdentity && !identityStable)
                LatchPricingAutonomy("admitted_identity_changed");

            var progress = execution?.Progress;
            var postconditionSatisfied = forcedResult is null &&
                admission.TrustedIdentity && identityStable &&
                execution?.Ok == true && progress is not null &&
                progress.Status == PricingJobStatus.Completed &&
                progress.TotalItems > 0 &&
                progress.CompletedItems == progress.TotalItems &&
                progress.FailedItems == 0;
            var semanticResult = !identityStable
                ? AutonomySemanticResult.Failed
                : forcedResult ?? (postconditionSatisfied
                ? AutonomySemanticResult.Completed
                : (progress?.CompletedItems ?? 0) > 0
                    ? AutonomySemanticResult.Partial
                    : AutonomySemanticResult.Failed);
            var postconditionDigest = TaskAutonomyScope.ComponentDigest(
                "pricing-postcondition-v1",
                progress?.Status ?? "not_started",
                (progress?.TotalItems ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (progress?.CompletedItems ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (progress?.FailedItems ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                execution?.Mode ?? _options.PricingExecutor.ToString(),
                reasonCode,
                !admission.TrustedIdentity ? "identity_unproven" :
                    identityStable ? "identity_verified" : "admitted_identity_changed");
            _taskAutonomy.RecordRun(new AutonomyRunEvidence(
                runId,
                terminalIdentity.EvidenceScope,
                Supervised: executionMode == AutonomyExecutionMode.Supervised,
                WorkItemCount: progress?.CompletedItems ?? 0,
                SemanticResult: semanticResult,
                PostconditionSatisfied: postconditionSatisfied,
                PostconditionDigest: postconditionDigest,
                CompletedAt: DateTimeOffset.UtcNow));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "TaskAutonomy: terminal evidence failed ({ErrorType}); autonomy latched off",
                ex.GetType().Name);
            LatchPricingAutonomy("terminal_evidence_persistence_failed");
            return false;
        }
    }

    private bool LatchPricingAutonomy(string reasonCode)
    {
        try
        {
            if (_taskAutonomy is not null)
                _taskAutonomy.LatchDisabled(reasonCode);
            else
                _stateDb.LatchAutonomyDisabled("pricing", reasonCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "TaskAutonomy: durable disable latch failed ({ErrorType}); runtime remains fail-closed",
                ex.GetType().Name);
            return false;
        }
    }
}
