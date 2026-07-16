using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

internal sealed record PackageCostApprovalBootstrapResult(
    string Code,
    PricingObservationContract? Observation = null,
    PricingCostBasisAuthority? Authority = null);

/// <summary>
/// Establishes the exact, read-only UIA observation contract that a PIC must
/// approve before a package-cost command can be queued. This path never opens
/// an item, reads a price, clicks, types, or invokes the actuation gateway. Its
/// only Helper command captures the already-visible PioneerRx window structure.
/// </summary>
internal sealed class PackageCostApprovalBootstrapper
{
    private static readonly TimeSpan ReadinessMaximumAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    private readonly AgentOptions _options;
    private readonly AgentStateDb _db;
    private readonly IIpcCommandClient _commandClient;
    private readonly PricingUiaActivityGate _activityGate;
    private readonly ActuationReadinessTracker _readiness;
    private readonly IPioneerRxAutonomyIdentityProvider _pmsIdentityProvider;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;
    private readonly ILogger<PackageCostApprovalBootstrapper> _logger;

    internal PackageCostApprovalBootstrapper(
        IOptions<AgentOptions> options,
        AgentStateDb db,
        IIpcCommandClient commandClient,
        PricingUiaActivityGate activityGate,
        ActuationReadinessTracker readiness,
        IPioneerRxAutonomyIdentityProvider pmsIdentityProvider,
        ILogger<PackageCostApprovalBootstrapper> logger,
        IReadOnlyDictionary<string, string>? trustedApprovalKeys = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _pmsIdentityProvider = pmsIdentityProvider ??
            throw new ArgumentNullException(nameof(pmsIdentityProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _trustedApprovalKeys = trustedApprovalKeys ??
            RemoteCommandTrust.CreateProductionKeyRegistry();
    }

    internal async Task<PackageCostApprovalBootstrapResult> TryStageAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        now = now.ToUniversalTime();
        if (_options.PricingExecutor is not (
                PricingExecutorMode.UiaFirst or PricingExecutorMode.VisionFirst))
            return new("pricing_package_bootstrap_executor_unavailable");

        var readiness = _readiness.Current;
        if (readiness is not { Ready: true } ||
            readiness.LastConclusiveCheckAtUtc is not { } checkedAt ||
            checkedAt < now - ReadinessMaximumAge ||
            checkedAt > now + TimeSpan.FromMinutes(1))
            return new("pricing_package_bootstrap_helper_not_ready");

        if (!_db.TryAdmitPricingCloudAuthority(now, out var leaseCode))
            return new(leaseCode);

        var livePmsIdentity = _pmsIdentityProvider.Current(now);
        if (livePmsIdentity is null)
            return new("pricing_live_pms_identity_unavailable");

        using var activityLease = _activityGate.TryEnterBootstrap();
        if (activityLease is null)
            return new("pricing_package_bootstrap_run_active");

        if (_commandClient.IsBusy)
            return new("pricing_package_bootstrap_pipe_busy");

        if (!_commandClient.IsConnected &&
            !await _commandClient.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false))
            return new("pricing_package_bootstrap_helper_unreachable");

        var screen = await CaptureObservationContextAsync(ct).ConfigureAwait(false);
        if (screen is null)
            return new("pricing_live_screen_identity_unavailable");

        var pmsFingerprint = PricingObservationPolicy.Digest(
            "pioneerrx_live_process_identity_v1",
            livePmsIdentity.FileVersion,
            livePmsIdentity.ExecutableSha256,
            livePmsIdentity.SignerCertificateSha256,
            livePmsIdentity.ApprovalReceiptDigest,
            livePmsIdentity.AuthorityDigest,
            livePmsIdentity.ApprovalCounter.ToString(CultureInfo.InvariantCulture));
        var activePatches = _db.GetActiveSelectorPatches().ToArray();
        PricingObservationContract observation;
        try
        {
            observation = PricingObservationPolicy.CreateUia(
                "uia",
                pmsFingerprint,
                screen.ScreenSignatureV1,
                activePatches,
                PricingApprovalContract.PackageCostBasis);
        }
        catch (ArgumentException)
        {
            return new("pricing_live_screen_identity_invalid");
        }

        var authority = PricingApprovalAuthorityResolver.ResolveOrStageProposal(
            _db,
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            observation,
            now,
            _trustedApprovalKeys,
            out var code);
        if (authority is not null)
            return new(code, observation, authority);

        if (code == "pricing_cost_basis_approval_pending")
            _logger.LogInformation("core.pricing.package_approval_proposal_ready");
        return new(code, observation);
    }

    private async Task<PricingScreenObservationContext?> CaptureObservationContextAsync(
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        IpcResponse? response;
        try
        {
            response = await _commandClient.SendAsync(
                    new IpcRequest(
                        id,
                        IpcCommands.PricingObservationContext,
                        1,
                        Data: null),
                    ObservationTimeout,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogSafeWarning(exception);
            return null;
        }

        if (response is null || response.Id != id ||
            response.Command != IpcCommands.PricingObservationContext ||
            response.Status != IpcStatus.Ok || response.Error is not null ||
            response.Data is null)
            return null;
        try
        {
            var parsed = response.Data.Value.Deserialize<PricingScreenObservationContext>();
            return parsed is { ProcessId: > 0, ScreenSignatureV1.Length: 64 } &&
                   parsed.ScreenSignatureV1.All(character =>
                       character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
